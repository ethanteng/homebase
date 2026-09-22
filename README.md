# Uncloud v0

[![CI](https://github.com/ethanteng/homebase/actions/workflows/ci.yml/badge.svg)](https://github.com/ethanteng/homebase/actions/workflows/ci.yml)

A self-hosted file library for a household. One computer holds everyone's files on storage you own; each person signs in and sees only their own folder and their own connected accounts. ASP.NET Core + React/TypeScript + SQLite. No cloud account required.

## Run

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [Node.js](https://nodejs.org/) 22.12+ (an LTS release recommended).

```sh
cd /Users/ethanteng/Projects/homebase
./scripts/run.sh
```

Open **http://127.0.0.1:5210**. The first visit asks you to make an account; that first account looks after this Uncloud. Then choose an existing folder in Finder, or enter its absolute path (`~/Uncloud` also works), and select **Use this folder**. Create a new folder in Finder first if needed. Stop the app with Ctrl+C.

Everyone you add under **People** gets a folder of their own underneath that one, and their own Dropbox connection. Nobody can reach anybody else's. [`docs/multi-user.md`](docs/multi-user.md) is the design; the short version is below.

The run script installs frontend dependencies when needed, builds the UI, and starts the backend serving both the app and API. First-time dependency restore requires internet; the app itself works offline. `scripts/dotnet.sh` finds an SDK matching the major version in `global.json`, checking PATH, `~/.dotnet`, then `~/.cache/homebase/dotnet`. An older `dotnet` on PATH is skipped rather than used.

For development, use two terminals:

```sh
./scripts/dotnet.sh run --project src/Homebase.Server
# In another terminal:
npm --prefix src/homebase-web ci
npm --prefix src/homebase-web run dev
```

Open **http://127.0.0.1:5173** for live frontend updates. Vite proxies `/api` to the backend; neither server listens on the network.

## This milestone

- Accounts with passwords and sessions; the first one is the administrator.
- One host folder, chosen once, holding a folder per account under `users/`.
- Imports from folders on the host's computer and from per-account Dropbox connections, with a metadata index.
- In-app Dropbox setup, per account or host-wide, and host-managed places to import from, so nothing needs a terminal.
- Native macOS folder chooser, with a manual path fallback.
- Nested folder navigation, breadcrumbs, browser back/forward, folder-local filtering and sorting, file details, and downloads.
- Live filesystem reads on navigation/refresh; a transactional SQLite metadata cache at `<user folder>/.homebase/index.db`.
- Empty, loading, missing-folder, permission, sign-in, and connection error states.

Uncloud doesn’t move, modify, or delete your existing files. Hidden entries (including `.homebase`) stay out of the browser. Symbolic links are skipped; traversal and direct hidden-path requests are rejected. Arbitrary file content is downloaded as an attachment, never executed on the app’s origin. The API restricts host/origin, requires a custom header for mutations, and refuses everything but sign-in without a session.

## Architecture

```text
src/Homebase.Core/       Filesystem boundaries, SQLite cache
  Accounts/             Accounts, sessions, sealed connector tokens, per-user workspaces
  Importing/            Contract for future importer adapters
src/Homebase.Server/     ASP.NET Core API, macOS picker, built React assets
src/homebase-web/        React + TypeScript + Vite
tests/Homebase.Tests/    Integration tests against real temporary files and SQLite
```

The filesystem is the source of truth. Browsing scans one directory and atomically replaces that directory’s cached entries. There is no recursive scan, file watcher, content index, or global search yet. Metadata for unvisited or removed nested folders may be stale until visited; the UI always reads disk. Filtering covers the current folder only. Keep v0 to reasonably sized individual directories; pagination is a later step.

Accounts, sessions, host settings and sealed provider tokens live in `~/Library/Application Support/Homebase/homebase.db`, beside a 32-byte `host.key`, outside the storage root and so outside the boundary they define. Each account's file metadata lives in its own `.homebase`. Stop Uncloud and remove a `.homebase` to reset that rebuildable cache; browse the folder again to recreate it. This cache is not a backup.

`Homebase__Dropbox__AppKey` supplies the host's Dropbox app key for a host that would rather not use
the settings screen; an account with its own key ignores it. `Homebase__ConfigDirectory` overrides the preference directory for isolated testing. `Homebase__Port` overrides port 5210 (also update Vite's proxy for development). Run a single Uncloud process per preference directory. Uncloud is not a sandbox: one process runs as one operating-system user and can read every account's folder, so isolation is enforced in Uncloud, not by the kernel, and anyone with a shell on the host can read everything.

### Reaching it from other computers

By default Uncloud binds `127.0.0.1`, regardless of `ASPNETCORE_URLS`. `Homebase__Bind` moves it; binding anywhere else **requires** `Homebase__AllowedHosts`, and the process refuses to start without it, because the host-header allowlist is what stops a name in somebody's DNS from turning a browser on the network into a way in.

```sh
Homebase__Bind=0.0.0.0 \
Homebase__AllowedHosts=uncloud.local,192.168.1.10 \
Homebase__Certificate__Path=/path/to/uncloud.pfx \
Homebase__PublicUrl=https://uncloud.local:5210 \
./scripts/run.sh
```

Without a certificate, passwords and files cross the network in the clear; Uncloud says so at startup rather than leaving you to notice. `Homebase__Certificate__Path` (with `Homebase__Certificate__Password`) serves HTTPS directly. `Homebase__PublicUrl` sets the address Dropbox returns to, which must match the redirect URI registered with your Dropbox app.

Behind a reverse proxy that terminates TLS, name it in `Homebase__TrustedProxies` (a comma-separated list of the addresses it connects from):

```sh
Homebase__Bind=127.0.0.1 \
Homebase__AllowedHosts=uncloud.local \
Homebase__TrustedProxies=127.0.0.1 \
Homebase__PublicUrl=https://uncloud.local \
./scripts/run.sh
```

Uncloud otherwise sees plain HTTP on a loopback address while the browser sent an `https://` origin, and refuses every sign-in as cross-site. Only the addresses listed here are believed, and only about the scheme and host — trusting those headers from anyone would let any client claim HTTPS or any hostname it liked. A forwarded host must still be one of `Homebase__AllowedHosts`.

### Upgrading a single-user library

The folder a previous version used becomes the host folder, and nothing in it is moved. Files sitting at its top level are listed under **Storage settings**, and **Move them into my folder** renames each one into the administrator's folder — nothing is copied, and anything whose name is already taken is reported rather than overwritten.

### Future importers and desktop packaging

`IImportSource` is what the import engine sees: metadata, a listing, and a stream. A Dropbox account
and a folder on this computer both implement it, so nothing in the engine, the panel, or the log
knows which is which. A new service is that interface plus whatever it takes to authenticate, and it
chooses the folder under `Files/` its imports land in. An implementation is responsible for refusing
any path that reaches outside the place it stands for. `IHomebaseImporter` remains an unimplemented
contract for adapters that want to write into the library directly, such as Evernote notes and
attachments under `Notes/Evernote/`; these folders aren’t created until something needs them.

Core logic has no web or desktop dependency. ASP.NET serves the compiled UI and can be started by a future desktop shell. To create a self-contained macOS build without adding a desktop framework:

```sh
./scripts/publish-macos.sh            # Apple Silicon
./scripts/publish-macos.sh osx-x64    # Intel
./artifacts/osx-arm64/Homebase.Server
```

Open http://127.0.0.1:5210. This produces a local executable and its assets, not a signed `.app` or `.dmg` yet. No billing, AI, or photo management is included, and there are no per-account storage quotas: everyone draws on the same drive, so one account can fill it for everyone.

## Bringing files in

Uncloud copies files and folders onto storage you own. An import happens **once**: after a file is
here, this copy is the one that counts, and Uncloud never goes back for it. Nothing already on disk
is ever overwritten.

There are two ways in, and they are the same import underneath — the same walk, the same room check,
the same never-overwrite rule, the same log. Open **Bring files in** in the sidebar and choose where
from.

### A folder on this computer

The easy one, and the one to reach for first: nothing to sign up for, nothing to configure, and it
works for every service at once. If a desktop app already syncs a folder onto this machine —
Dropbox, Google Drive, OneDrive, iCloud Drive — point Uncloud at that folder. An old external drive
or a `Documents` folder works exactly the same way.

An administrator adds the folders under **Where files come from**, which offers whatever it finds on
this computer (`~/Dropbox`, `~/Google Drive`, `~/Library/CloudStorage/*`, `Documents`, `Pictures`…)
as one-click suggestions, with a folder chooser and a path box for anything else. Files brought in
from a place called *Dropbox* land in `Files/Dropbox/`.

Adding a place shares it with **every account on this host**, which is why only an administrator can
do it: Uncloud runs as one operating-system user and can read whatever that user can, so letting a
member name a folder would be a way around the isolation between accounts rather than a feature. For
the same reason a place can never be, contain, or sit inside the host's storage folder or Uncloud's
preference directory, and that is re-checked every time a place is used rather than only when it is
added — moving the host's folder afterwards doesn't open a way in. Paths inside a place go through
the same policy the library uses: no traversal, no hidden entries, and symbolic links are left out of
listings rather than followed.

Removing a place stops anyone bringing anything else in from it. Nothing already brought home is
touched: those are ordinary files in somebody's folder now.

### A Dropbox account online

For Dropbox files that aren't synced to this computer. Each account connects its own Dropbox, and
the connection it authorises is the signing-in person's alone. Uncloud asks only for read-only
permissions, so it cannot change anything in anybody's Dropbox.

Connecting goes through a Dropbox *app*, and there are two places one can come from:

- **Your own**, under **My account**. Anybody signed in can set this up for themselves, and it wins
  over the host's. Nobody's Dropbox waits on anybody else.
- **The host's**, under **Where files come from**. An administrator who sets one here saves everyone
  else the trouble: with a host key in place, connecting Dropbox is one click for every account.

Both screens show the same four steps, the exact redirect URI to register, and the three permissions
to tick. An app key is not a secret — Uncloud signs in with PKCE precisely because a program on
somebody's own computer cannot keep one, and there is no Dropbox app secret anywhere in Uncloud.
That is what makes it safe for a member to set their own rather than reserving it to an
administrator.

`Homebase__Dropbox__AppKey` still works as the host's key when nothing has been set in the app, so a
host started that way keeps working untouched.

```sh
# Still supported, no longer necessary.
Homebase__Dropbox__AppKey=your-app-key ./scripts/run.sh
```

Changing an app key signs out the connections that were made through it, because each was authorised
against the old app and its refresh token cannot be used against a new one — Uncloud clears them
rather than leaving connections that fail on a later refresh with nothing on screen to explain why.
Changing the host's key leaves anybody connecting through their own alone, which is the whole point
of having one.

### Either way

Files land in `Files/` as ordinary files, and the normal browser shows them. Importing a folder again
brings only what is new; anything already imported, hidden, or blocked by an existing file is listed
as skipped rather than silently passed over, and one unreadable file doesn't abandon the rest. A
folder Dropbox refuses to list is retried before Uncloud gives up on it, and giving up is reported at
the top of the panel and written to the log rather than left to be noticed.

An import runs on its own rather than inside the request that started it, so the panel shows how
far it has got — the file it is on, how many of how many, and how much has arrived — and **Stop**
ends it. Whatever already arrived stays; only the rest is dropped. Leaving the page or reloading
doesn't cancel anything: the import carries on and the panel picks it back up.

The sidebar shows the space left on the drive your folder lives on. A file's size is listed beside
it; a folder's costs a walk of its whole tree, so **Check size** asks for it and reports what the
folder holds, how much of that isn't home yet, and whether it fits. Every import measures the same
way before downloading anything, and refuses outright when the files wouldn't fit — running the
drive out of room halfway through a folder is worse than not starting. Some room is always left
over, so the local index still has somewhere to write.

Dropbox sign-in uses the authorization-code flow with PKCE, so there is no client secret — an app
key is not one, which is why it is safe to set from a settings screen. The refresh token is sealed
with AES-256-GCM under the host key and stored against the account that connected it, never in the
library folder where it would travel alongside imported files; the account and provider are
authenticated alongside it, so a sealed token moved between accounts fails its tag check rather than
handing over somebody else's Dropbox. Because Dropbox returns the browser by a cross-site redirect
that may not carry a session, the PKCE state names the account instead — and the address the person
was on, so a sign-in started at `127.0.0.1` comes back there instead of landing on `localhost` and
reading as a sign-out. Only an address this host already answers to is accepted as a return address.

Each file is written beside its destination and moved into place, so an interrupted transfer can't
leave a half-written file, and the move never overwrites — a file that appears mid-transfer wins. The
revision (or, from a folder, the size and modification time) and a SHA-256 of each import are
recorded in `.homebase` as provenance: what came from where, and when. A folder on this computer is
copied, never moved: the originals stay exactly where they were.

## Keeping a copy

Uncloud doesn't replicate your files anywhere. One host, one drive: if that drive dies, everything
on it is gone. Set up a backup before you put anything you care about here.

Anything that copies a directory works, because the library is ordinary files in ordinary folders —
nothing has to understand Uncloud to back it up. On macOS, include the host folder in Time Machine.
Otherwise [restic](https://restic.net) or [rclone](https://rclone.org) to a second drive or an
offsite target does the job:

```sh
restic -r /Volumes/Backup/uncloud backup ~/Uncloud
```

Prefer something that keeps **versions** rather than a live mirror. A mirror propagates a deletion
as faithfully as it propagates a new file, so it protects you against a failed drive and not at all
against the far more common way people lose things, which is deleting them and noticing later.

`.homebase/index.db` is a live SQLite database and a rebuildable cache, not a backup — it is fine
if it comes along, and fine if it doesn't. The control database that holds accounts and connector
tokens lives in the preference directory, so back that up separately if you want the accounts
themselves to survive, not just the files.

Reaching your files while away from the host is not solved yet; see the [design
notes](docs/multi-user.md).

## Landing page

The standalone **Uncloud** messaging page for **uncloud.life** is in [`landing/`](landing/README.md). Preview it with `npm --prefix src/homebase-web run dev:landing` at **http://127.0.0.1:5174**. Build it with `npm --prefix src/homebase-web run build:landing`; the static output goes to `artifacts/landing/` and contains no file-browser API. The application and its storage paths still use the internal name Homebase.

## Check

```sh
./scripts/check.sh
```

Builds/type-checks the frontend and runs backend integration tests for accounts (first-run setup, sign-in refusals that say nothing about who exists, throttled guessing, password changes that sign out everywhere else, disabling and deleting accounts, and keeping the last administrator), isolation (separate folders, every path by which one account might name another's files, administrator-only endpoints, nothing readable without signing in, per-account provider connections and import queues, sealed tokens refused under another account, and shared free space with private usage), the upgrade path from a single-user library, host binding rules, persistence, indexing, file integrity/downloads, host folder switching, unavailable folders, symlinks, traversal, request boundaries, imports (single files, whole folders, skipping what's already here, refusing to overwrite anything it didn't write, carrying on past a file that fails or times out while still stopping when cancelled, retrying a listing Dropbox rate-limits, refusing a folder that wouldn't fit on the drive, and running an import as a job that reports its progress, refuses a second one and stops when asked), places on the host's computer (bringing a folder home, the refusals that keep a place from reaching the host's folder or the preference directory — including after the host's folder moves — only an administrator adding one, paths that try to walk out of one, links left unfollowed, and two places kept from sharing a folder in the library), and in-app Dropbox setup (setting the host's app key and an account's own, an account's key winning over the host's and falling back to it when cleared, the environment variable behind both, signing out only the connections a changed key would have broken, the redirect address to register, and a sign-in returning to the address it started from). Tests use disposable fixtures and isolated settings, never your real library or accounts.

GitHub Actions runs this same script on every pull request and push to `main`, on both macOS and Linux. The tests cover filesystem, indexing, and request-boundary behavior; the native macOS folder chooser isn't automatable and still needs a manual pass.

If macOS blocks a folder, check **System Settings → Privacy & Security → Files and Folders** for the app or terminal launching Uncloud. If a drive disconnects, reconnect it and refresh, or choose another root in Storage settings.
