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
- Per-account Dropbox connections, imports, and metadata index.
- Per-account syncing with your own computers through Syncthing, so changes made on a laptop — additions, edits and deletions — reach the host, with deleted and replaced files kept for 30 days.
- Native macOS folder chooser, with a manual path fallback.
- Nested folder navigation, breadcrumbs, browser back/forward, folder-local filtering and sorting, file details, and downloads.
- Live filesystem reads on navigation/refresh; a transactional SQLite metadata cache at `<user folder>/.homebase/index.db`.
- Empty, loading, missing-folder, permission, sign-in, and connection error states.

Uncloud doesn’t move, modify, or delete your existing files — except in a folder you choose to sync with your own computers, where a change made on one of them, deletion included, is made here too. Hidden entries (including `.homebase`) stay out of the browser. Symbolic links are skipped; traversal and direct hidden-path requests are rejected. Arbitrary file content is downloaded as an attachment, never executed on the app’s origin. The API restricts host/origin, requires a custom header for mutations, and refuses everything but sign-in without a session.

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

`Homebase__Dropbox__AppKey` supplies the Dropbox app key. `Homebase__Syncthing__*` configures the Syncthing process described under [Your own computers](#your-own-computers). `Homebase__ConfigDirectory` overrides the preference directory for isolated testing. `Homebase__Port` overrides port 5210 (also update Vite's proxy for development). Run a single Uncloud process per preference directory. Uncloud is not a sandbox: one process runs as one operating-system user and can read every account's folder, so isolation is enforced in Uncloud, not by the kernel, and anyone with a shell on the host can read everything.

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

`IHomebaseImporter` is a small, unimplemented adapter contract. Future Dropbox and Google Drive importers can write to `Files/Dropbox/` and `Files/Google Drive/`; Evernote can write notes and attachments to `Notes/Evernote/`. These folders aren’t created until an importer needs them. Adapters should preserve source IDs and import checkpoints in `.homebase`, avoid overwriting files, and pass destinations through the same filesystem boundary checks. The UI doesn’t need to know the provider.

Core logic has no web or desktop dependency. ASP.NET serves the compiled UI and can be started by a future desktop shell. To create a self-contained macOS build without adding a desktop framework:

```sh
./scripts/publish-macos.sh            # Apple Silicon
./scripts/publish-macos.sh osx-x64    # Intel
./artifacts/osx-arm64/Homebase.Server
```

Open http://127.0.0.1:5210. This produces a local executable and its assets, not a signed `.app` or `.dmg` yet. No billing, AI, or photo management is included, and there are no per-account storage quotas: everyone draws on the same drive, so one account can fill it for everyone.

## Dropbox

Uncloud copies files and folders out of Dropbox onto storage you own. An import happens **once**:
after a file is here, this copy is the one that counts, and Uncloud never goes back to Dropbox for
it. Nothing already on disk is ever overwritten. Uncloud asks only for read-only permissions, so it
cannot change anything in your Dropbox account either.

Each account connects its own Dropbox; the app key below is the host's, and the connection it
authorises is the signing-in person's alone. Create an app at
[dropbox.com/developers/apps](https://www.dropbox.com/developers/apps) with the
`account_info.read`, `files.metadata.read` and `files.content.read` permissions and the redirect URI
`http://localhost:5210/api/providers/dropbox/callback`, then start Uncloud with its app key:

```sh
Homebase__Dropbox__AppKey=your-app-key ./scripts/run.sh
```

Open **Dropbox** in the sidebar, connect the account, and bring a file or a whole folder home. Files
land in `Files/Dropbox/` as ordinary files, and the normal browser shows them. Importing a folder
again brings only what is new; anything already imported, hidden, or blocked by an existing file is
listed as skipped rather than silently passed over, and one unreadable file doesn't abandon the rest.
A folder Dropbox refuses to list is retried before Uncloud gives up on it, and giving up is reported
at the top of the panel and written to the log rather than left to be noticed.

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

Sign-in uses the authorization-code flow with PKCE, so there is no client secret. The refresh token
is sealed with AES-256-GCM under the host key and stored against the account that connected it,
never in the library folder where it would travel alongside imported files; the account and
provider are authenticated alongside it, so a sealed token moved between accounts fails its tag
check rather than handing over somebody else's Dropbox. Because Dropbox returns the browser by a
cross-site redirect that may not carry a session, the PKCE state names the account instead. Each download is written beside its destination and
moved into place, so an interrupted transfer can't leave a half-written file, and the move never
overwrites — a file that appears mid-transfer wins. The revision and a SHA-256 of each import are
recorded in `.homebase` as provenance: what came from where, and when.

## Your own computers

Each person can keep their files in a folder on their own laptop or desktop as well. Add, change or
delete a file there and the same happens on the host, and the other way round; a computer that was
offline catches up when it reconnects. Open **My computers** in the sidebar.

The sync protocol is [Syncthing](https://syncthing.net)'s, not Uncloud's: device identity,
discovery, NAT traversal, encryption and conflict handling are all its work. The host runs one
Syncthing, supervised by Uncloud with its own home directory under the preference directory and its
own loopback-only API port, started and stopped with the app. Install it on the host
(`brew install syncthing`; 1.x and 2.x both work) and restart Uncloud. Each person installs
Syncthing on their own computer too.

1. On your computer, add the host's ID (shown in **My computers**) as a remote device.
2. In **My computers**, paste your computer's ID and **Add computer**.
3. Either **Sync folder** in Uncloud — a folder such as `Documents`, or leave it empty for all of
   your files — and accept the offer in Syncthing on your computer; or share a folder from your
   computer, and **Keep it here** when it appears under **Offered by your computers**.

Instead of copying device IDs, a computer can pair itself with a code. **Get a pairing code** in
**My computers** shows a ten-character code that works once, for ten minutes. A client on the
computer sends it to `POST /api/sync/pair` as `{ "code", "deviceId", "name" }`, without a session
and with the `X-Homebase-Request: 1` header, and gets back `{ "hostDeviceId", "accountName" }`.
That computer is now paired with the account that asked for the code. Codes are stored only as
hashes, a new one replaces the last, wrong guesses are throttled per address, and a mistake that
isn't the code's — a malformed device ID, a computer another account already has — doesn't use it
up. This is the API the planned Uncloud desktop app, which will carry its own Syncthing, will use.

Syncthing has one configuration for the whole host and no idea of accounts, so Uncloud records
which account every computer and every folder belongs to and answers everything from that record.
Each person sees and manages only their own computers, folders and offers; a computer can be paired
with one account only; a folder can only be made inside your own space and only sent to your own
computers; and a pairing request is refused without saying which other account has that computer.
Disabling an account pauses its computers, and deleting one removes them — refused while Syncthing
can't be reached, so nothing carries on writing into the folder of an account that no longer
exists. Folders synced by the older administrator-only version are given to the account whose
folder they are in the next time Uncloud starts.

Uncloud's index is kept out: before Syncthing first sees a folder, Uncloud writes a `.stignore`
there that excludes `.homebase` (and `.DS_Store`), adding to any rules already in it. One synced
folder can't sit inside or around another. Moving the host's folder moves where every synced folder
points; if the new one doesn't hold the files, Syncthing stops that folder with an error rather than
treating the empty folder as everything having been deleted.

Folders are two-way (`sendreceive`). If a file changes on both sides while they're apart, Syncthing
keeps both and renames the loser with `.sync-conflict-` beside the winner, so you may have a
duplicate to tidy up. **A deletion on your laptop deletes the file here.** Every synced folder on the
host keeps what it deletes or replaces for 30 days under a hidden `.stversions` folder
(Syncthing's staggered versioning), which is how a mistake on a laptop is undone — there is no
restore button yet, so that means copying it back out of `.stversions`.

Syncthing connects through its global discovery and relays by default, so your computers keep
syncing when they're away from home, even though Uncloud's web interface is still only reachable
where the host is.

`Homebase__Syncthing__Path` points at the binary if it isn't on `PATH`,
`Homebase__Syncthing__GuiPort` moves its local API off 8390, and
`Homebase__Syncthing__Enabled=false` switches the whole thing off.

## Keeping a copy

Syncing isn't a backup of the host, and nothing else copies your files anywhere. One host, one
drive: if that drive dies, everything that exists only there is gone. Set up a backup before you
put anything you care about here.

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

Reaching Uncloud's web interface while away from the host is not solved yet; see the [design
notes](docs/multi-user.md). Syncing with your own computers works from anywhere.

## Landing page

The standalone **Uncloud** messaging page for **uncloud.life** is in [`landing/`](landing/README.md). Preview it with `npm --prefix src/homebase-web run dev:landing` at **http://127.0.0.1:5174**. Build it with `npm --prefix src/homebase-web run build:landing`; the static output goes to `artifacts/landing/` and contains no file-browser API. The application and its storage paths still use the internal name Homebase.

## Check

```sh
./scripts/check.sh
```

Builds/type-checks the frontend and runs backend integration tests for accounts (first-run setup, sign-in refusals that say nothing about who exists, throttled guessing, password changes that sign out everywhere else, disabling and deleting accounts, and keeping the last administrator), isolation (separate folders, every path by which one account might name another's files, administrator-only endpoints, nothing readable without signing in, per-account provider connections and import queues, sealed tokens refused under another account, and shared free space with private usage), the upgrade path from a single-user library, host binding rules, persistence, indexing, file integrity/downloads, host folder switching, unavailable folders, symlinks, traversal, request boundaries, Dropbox imports (single files, whole folders, skipping what's already here, refusing to overwrite anything it didn't write, carrying on past a file that fails or times out while still stopping when cancelled, retrying a listing Dropbox rate-limits, refusing a folder that wouldn't fit on the drive, and running an import as a job that reports its progress, refuses a second one and stops when asked), and syncing with your own computers (device-ID checks, one account per computer, each account seeing and using only its own computers, folders and offers, folders confined to the account's space and never nested, Uncloud's index ignored before Syncthing first scans, offered names held to the same rules, pausing a disabled account's computers and removing a deleted one's, adopting folders from the administrator-only version, following a moved host folder, and pairing codes that are single-use, expiring, throttled and bound to the account that asked). Tests use disposable fixtures and isolated settings, never your real library or accounts.

GitHub Actions runs this same script on every pull request and push to `main`, on both macOS and Linux. The tests cover filesystem, indexing, and request-boundary behavior; the native macOS folder chooser isn't automatable and still needs a manual pass.

If macOS blocks a folder, check **System Settings → Privacy & Security → Files and Folders** for the app or terminal launching Uncloud. If a drive disconnects, reconnect it and refresh, or choose another root in Storage settings.
