# Multi-user Uncloud

How one Uncloud host serves several people, each with their own files and their own
connected accounts, without any of them being able to reach anyone else's.

This document is the design. Phase 1 — accounts, sign-in, per-user storage and per-user
Dropbox connections — is implemented. Later phases are marked as such.

## The shape of it

Uncloud v0 was one person on one Mac: a singleton `LibraryService` holding one folder, one
`dropbox.json` next to the preferences, one import at a time, and no authentication at all
because binding to `127.0.0.1` *was* the authentication.

A host serving several people inverts that. There is still exactly one process and one
storage volume, but almost everything that used to be a singleton is now per-user:

```text
                    ┌─────────────────────────────────────┐
   browser ────────▶│  host allowlist → CSRF → session    │  Program.cs pipeline
                    └────────────────┬────────────────────┘
                                     │ resolves a UserAccount, or 401
                                     ▼
                    ┌─────────────────────────────────────┐
                    │  UserWorkspaces.For(userId)         │  the isolation boundary
                    └────────────────┬────────────────────┘
                                     │
          ┌──────────────────────────┼──────────────────────────┐
          ▼                          ▼                          ▼
   LibraryService            IDropboxConnection            ImportJobs
   (root = users/<id>)       (that user's token)           (that user's queue)
```

The one rule everything else rests on: **a user's root directory is derived from the
authenticated session and never from anything in the request.** A handler cannot be handed a
root; it asks `UserWorkspaces` for the workspace belonging to the user the middleware
authenticated, and that workspace's root is `<host root>/users/<user id>`. Path traversal is
then the same problem v0 already solved — `PathPolicy.Resolve` rejects `..`, absolute paths,
backslashes, NULs, dot-prefixed segments and symbolic links at every level — except that the
root it resolves against is now the user's rather than the host's.

## Storage layout

The host operator picks one directory. Everything lives under it, and everyone draws from
the same volume.

```text
<host root>/
  .homebase/                    v0's cache, if this folder was a v0 library (harmless, unused)
  users/
    2f6c…/                      one user, named by opaque id
      .homebase/index.db          that user's metadata cache
      Files/Dropbox/…             that user's imports
    9ab1…/                      another user; nothing above can see into it
```

Directories are named by an opaque 32-character id, not by username, so renaming an account
never rewrites a path and a username can never collide with a path segment.

Nothing about *who* a user is lives under the host root. Accounts, sessions and connector
tokens live in a control database beside the host's preferences:

```text
<config dir>/            ~/Library/Application Support/Homebase on macOS
  homebase.db              users, sessions, connectors, host settings
  host.key                 32 random bytes, owner-only: the connector-token key
```

This keeps v0's rule — a credential must never be written into the library folder, where it
would travel alongside files — and extends it: the control plane is not in any user's reach
even if the isolation boundary were breached, because it is not under the host root at all.

## Control database

```sql
host_settings(key, value)                       -- root_path, and future host-wide settings

users(id, username, display_name, password_hash, is_admin, created_at, disabled_at)

sessions(token_hash, user_id → users.id, created_at, expires_at, seen_at)

connectors(user_id → users.id, provider, account_name, secret, connected_at)
  PRIMARY KEY (user_id, provider)
```

Both child tables cascade on user delete, so removing an account cannot strand a live session
or an orphaned refresh token. Deleting an account deliberately does **not** delete that
user's files: the directory is left on disk and the admin is told where it is.

## Sign-in

Password, then a session cookie.

- **Hashing.** PBKDF2-HMAC-SHA512, 210,000 iterations, 16-byte random salt, 32-byte output,
  stored as `pbkdf2-sha512$iterations$salt$hash`. The iteration count travels with the hash,
  so raising it later doesn't invalidate existing passwords. Verification is a fixed-time
  comparison, and a sign-in attempt for a username that doesn't exist still performs a hash
  so that a missing account and a wrong password take the same time to refuse.
- **Sessions.** 32 random bytes, base64url, handed to the browser as a cookie. Only the
  SHA-256 of that value is stored, so a stolen copy of the database yields no live sessions.
  Thirty-day expiry, refreshed no more often than daily. Changing a password revokes every
  other session for that account.
- **Cookie flags.** `HttpOnly`, `Path=/`, `SameSite=Lax`, and `Secure` whenever the request
  arrived over HTTPS.

`SameSite=Lax` rather than `Strict` is deliberate. Dropbox returns the browser by a top-level
cross-site redirect; under `Strict` the cookie is withheld for that navigation *and* for the
redirect that follows it back into the app, so connecting an account would appear to sign you
out. Lax is not a weakening here, because the CSRF defence does not rest on it: every
state-changing request is a non-GET, must carry a same-origin `Origin`, and must carry the
`X-Homebase-Request: 1` header, which cross-site form posts and image loads cannot set.

- **Throttling.** Failed sign-ins are counted per username and per client address in memory.
  Ten failures inside fifteen minutes refuses further attempts for that window.

## The Dropbox return trip

The OAuth callback is the one endpoint that must work without a session, because it is a
navigation from dropbox.com. Rather than trust a cookie that may not be sent, the PKCE
exchange carries its own identity: `DropboxAuthFlow` records the pending verifier **against
the user who started it**, keyed so that a returning `state` names the account to connect.
The state is 512 bits of randomness, compared in fixed time, valid for ten minutes, and
consumed exactly once. Two people connecting Dropbox at the same moment no longer overwrite
each other's pending exchange, which the single-slot v0 design would have done.

Refresh tokens are sealed with AES-256-GCM under the host key, with the user id and provider
name as additional authenticated data. A sealed token therefore cannot be decrypted after
being moved to another user's row — the tampering fails the tag check rather than yielding
someone else's account.

## Roles

**Admin** — the first account created, and anyone an admin promotes. Chooses the host root,
creates and disables accounts, resets passwords, and sees each user's disk usage. The last
active admin cannot be deleted, disabled or demoted, so a host cannot be locked out of itself.

**Member** — browses and downloads their own files, connects and disconnects their own
provider accounts, runs their own imports, changes their own password. A member cannot see
that other accounts exist.

## Storage accounting

Everyone draws from the same volume, as intended: `/api/storage` reports that volume's free
and total space to everyone, plus the caller's own usage. Usage is a walk of the user's tree,
cached for a minute, because files arrive by routes Uncloud doesn't mediate (a copy in
Finder) and an incrementally maintained number would drift.

**Per-user quotas are deliberately not implemented.** A shared pool is what was asked for, and
it is the right default for a household. It does mean one account can fill the volume for
everyone, which is worth knowing before opening a host to people you don't live with. The
schema has room for a `quota_bytes` column, and `ImportService.RequireRoomFor` is already the
single place every import passes through, so adding it later is contained.

## Network exposure

A multi-user host is not a loopback service. `Homebase__Bind` sets the address (default
`127.0.0.1`, unchanged). Binding anywhere else requires `Homebase__AllowedHosts` to be set,
and the process refuses to start otherwise — the host-header allowlist is what stops a DNS
rebinding attack from turning a browser on the LAN into a proxy into the host.

Over plain HTTP, passwords cross the network in the clear. `Homebase__Certificate__Path` (with
`Homebase__Certificate__Password`) makes Kestrel serve HTTPS directly; a reverse proxy
terminating TLS works too. The server warns loudly at startup when it is bound beyond loopback
without a certificate.

## What this is not

- **Not an OS sandbox.** One process runs as one operating-system user and can read every
  user's directory. Isolation is enforced in Uncloud, not by the kernel. Anyone with a shell
  on the host, or any other program running as that OS user, can read everything.
- **Not a backup.** Syncing with your own computers (below) copies
  files to them, but it mirrors: a deletion on a laptop deletes the host's copy too, and the 30
  days of versions the host keeps are for undoing that, not for surviving a dead drive. Backup
  belongs to a tool built for it; the README says which.
- **No invitations, e-mail, or password reset by mail.** An admin sets a password and hands it
  over. Self-service recovery needs a mail path Uncloud doesn't have.
- **No audit log** of who read what.

## Syncing with your own computers

People keep their files on their own laptops too, and expect adding, editing or deleting one there
to reach the host. That is Syncthing's job, not a thing to rebuild: rename detection, conflict
copies, retries, NAT traversal and offline catch-up are all hard and all solved there.

An earlier, administrator-only version did this and was taken out, for two reasons. Its
configuration was global with no notion of accounts, so it could only be offered to administrators
without handing every account a list of everybody's shared folders; and it mirrored without
versioning, so a deletion on a laptop was simply gone. This version answers both.

**Ownership.** There is still one Syncthing with one configuration for the whole host. Two tables
in the control database say who owns what: `sync_devices(device_id PRIMARY KEY, user_id, …)` and
`sync_folders(folder_id PRIMARY KEY, user_id, path, …)`, both cascading with the account.
`SyncService` answers every call from these rows, filtered by the id from the session, and only
then consults Syncthing for live state. So:

- A computer belongs to one account. Pairing one another account has is refused, without saying
  whose. A computer that could be claimed twice would let one person send folders to another's
  laptop, or see the folders it offers.
- Folders are stored as a path relative to the account's own root, resolved through `PathPolicy`
  like any browse, and only ever sent to that account's own computers. Folder ids hash the account
  id with the path, so two people's `Documents` are two folders.
- Offers (folders a computer proposes) are shown only to the account that owns the computer, and
  taking one up is the only way a laptop's folder comes into existence here. Paired devices are
  added with `autoAcceptFolders` and `introducer` off, so a laptop can't create folders or pair
  other computers by itself.
- Anything naming a computer or folder another account owns gets the same `not_found` as one that
  doesn't exist.

**Keeping `.homebase` out.** An account's whole folder can be synced, which means its live SQLite
index is inside a synced folder. Uncloud writes `.stignore` excluding `.homebase` *before* adding
the folder to Syncthing, so no first scan ever sees it; the file is added to, never replaced.

**Deletions.** Every folder is `sendreceive` with staggered versioning (30 days) on the host, so a
file a laptop deletes or replaces is kept under the folder's hidden `.stversions`. The browser
hides dot-entries, so versions don't clutter anybody's files; there is no restore UI yet.

**Keeping Syncthing honest.** `ReconcileAsync` runs when Syncthing first answers and whenever the
host's folder moves. It points each folder at `<host root>/users/<id>/<path>` — if the files
aren't there, Syncthing's folder marker is missing and it stops the folder rather than propagating
an empty directory as mass deletion — makes sure every folder is versioned and guarded, pauses the
computers of disabled accounts, and gives folders left by the administrator-only version to the
account whose folder they are in. Disabling an account pauses its computers immediately when
Syncthing is up; deleting one removes them, and is refused while Syncthing can't be reached, so a
laptop can't keep writing into a deleted account's folder.

## Migrating a v0 library

A v0 install has files sitting directly in its root. On first start, that root is adopted as
the host root, and nothing is moved: pre-existing top-level entries stay exactly where they
are, visible to nobody, while accounts get fresh directories under `users/`.

`GET /api/host/claim` lists what is sitting at the top level; `POST /api/host/claim` moves it
into the calling admin's folder. Each entry is moved by rename — atomic, on the same volume —
and an entry whose name is already taken in the destination is reported rather than
overwritten, keeping v0's promise that Uncloud never overwrites a file it didn't write. The
leftover `<host root>/.homebase/index.db` is a rebuildable cache and is left alone.

## Phases

**Phase 1 — implemented.** Control database, accounts, sign-in and sessions, admin role,
per-user roots, per-user Dropbox connections, per-user imports, per-user usage, host root
selection and v0 adoption, LAN binding with a host allowlist and optional TLS, the admin
Users panel, per-account syncing with each person's own computers, and tests that a member cannot
reach another member's files — or computers, or synced folders — by any route.

**Phase 2.** Per-user quotas. Sharing a folder between accounts on the same host. An audit
log. Session listing and revocation from the account page. Restoring a file from `.stversions` in the browser.

**Phase 3.** Google Drive and iCloud connectors. The per-user connector model already
generalises: `connectors` is keyed by `(user_id, provider)` and `IProviderTokens` is the only
seam a new provider needs, so a second provider is an importer and an OAuth flow, not another
round of this work.
