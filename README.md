# Homebase v0

[![CI](https://github.com/ethanteng/homebase/actions/workflows/ci.yml/badge.svg)](https://github.com/ethanteng/homebase/actions/workflows/ci.yml)

A local-first personal file library for macOS. Choose a folder on your Mac or an attached drive, then browse its ordinary files. ASP.NET Core + React/TypeScript + SQLite. No cloud account required.

## Run

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [Node.js](https://nodejs.org/) 22.12+ (an LTS release recommended).

```sh
cd /Users/ethanteng/Projects/homebase
./scripts/run.sh
```

Open **http://127.0.0.1:5210**. Choose an existing folder in Finder, or enter its absolute path (`~/Homebase` also works), then select **Use this folder**. Create a new folder in Finder first if needed. Stop the app with Ctrl+C.

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

- Native macOS folder chooser, with a manual path fallback.
- Persistent root selection; change it through **Storage settings**.
- Nested folder navigation, breadcrumbs, browser back/forward, folder-local filtering and sorting, file details, and downloads.
- Live filesystem reads on navigation/refresh; a transactional SQLite metadata cache at `<root>/.homebase/index.db`.
- Empty, loading, missing-folder, permission, and connection error states.

Homebase doesn’t move, modify, or delete your existing files. Hidden entries (including `.homebase`) stay out of the browser. Symbolic links are skipped; traversal and direct hidden-path requests are rejected. Arbitrary file content is downloaded as an attachment, never executed on the app’s origin. The local API restricts host/origin and requires a custom header for mutations.

## Architecture

```text
src/Homebase.Core/       Filesystem boundaries, root preferences, SQLite cache
  Importing/            Contract for future importer adapters
src/Homebase.Server/     ASP.NET Core API, macOS picker, built React assets
src/homebase-web/        React + TypeScript + Vite
tests/Homebase.Tests/    Integration tests against real temporary files and SQLite
```

The filesystem is the source of truth. Browsing scans one directory and atomically replaces that directory’s cached entries. There is no recursive scan, file watcher, content index, or global search yet. Metadata for unvisited or removed nested folders may be stale until visited; the UI always reads disk. Filtering covers the current folder only. Keep v0 to reasonably sized individual directories; pagination is a later step.

Only the selected root path is stored outside the library, in `~/Library/Application Support/Homebase/settings.json`. All file metadata lives in `.homebase`. Stop Homebase and remove `.homebase` to reset the rebuildable cache; choose or browse the folder again to recreate it. This cache is not a backup.

`Homebase__Dropbox__AppKey` supplies the Dropbox app key. `Homebase__Syncthing__*` configures the Syncthing process described above. `Homebase__ConfigDirectory` overrides the preference directory for isolated testing. `Homebase__Port` overrides port 5210 (also update Vite’s proxy for development). The server explicitly binds to `127.0.0.1`, regardless of `ASPNETCORE_URLS`. Run a single Homebase process per preference directory/library. V0 assumes a trusted local user and filesystem; it is not a sandbox against other programs running under your account.

### Future importers and desktop packaging

`IHomebaseImporter` is a small, unimplemented adapter contract. Future Dropbox and Google Drive importers can write to `Files/Dropbox/` and `Files/Google Drive/`; Evernote can write notes and attachments to `Notes/Evernote/`. These folders aren’t created until an importer needs them. Adapters should preserve source IDs and import checkpoints in `.homebase`, avoid overwriting files, and pass destinations through the same filesystem boundary checks. The UI doesn’t need to know the provider.

Core logic has no web or desktop dependency. ASP.NET serves the compiled UI and can be started by a future desktop shell. To create a self-contained macOS build without adding a desktop framework:

```sh
./scripts/publish-macos.sh            # Apple Silicon
./scripts/publish-macos.sh osx-x64    # Intel
./artifacts/osx-arm64/Homebase.Server
```

Open http://127.0.0.1:5210. This produces a local executable and its assets, not a signed `.app` or `.dmg` yet. No remote access, household users, billing, AI, photo management, or synchronization is included.

## Dropbox

Homebase copies files and folders out of Dropbox onto storage you own. An import happens **once**:
after a file is here, this copy is the one that counts, and Homebase never goes back to Dropbox for
it. Nothing already on disk is ever overwritten. Homebase asks only for read-only permissions, so it
cannot change anything in your Dropbox account either.

Create an app at [dropbox.com/developers/apps](https://www.dropbox.com/developers/apps) with the
`account_info.read`, `files.metadata.read` and `files.content.read` permissions and the redirect URI
`http://localhost:5210/api/providers/dropbox/callback`, then start Homebase with its app key:

```sh
Homebase__Dropbox__AppKey=your-app-key ./scripts/run.sh
```

Open **Dropbox** in the sidebar, connect the account, and bring a file or a whole folder home. Files
land in `Files/Dropbox/` as ordinary files, and the normal browser shows them. Importing a folder
again brings only what is new; anything already imported, hidden, or blocked by an existing file is
listed as skipped rather than silently passed over, and one unreadable file doesn't abandon the rest.

Sign-in uses the authorization-code flow with PKCE, so there is no client secret; the refresh token
is written with owner-only permissions to the preference directory, never into the library folder
where it would travel alongside imported files. Each download is written beside its destination and
moved into place, so an interrupted transfer can't leave a half-written file, and the move never
overwrites — a file that appears mid-transfer wins. The revision and a SHA-256 of each import are
recorded in `.homebase` as provenance: what came from where, and when.

## Other computers

Homebase can keep a folder the same across computers you own. Open **Nodes** in the sidebar, give
the other computer this one's ID, paste its ID here, then share a folder. Sharing only *offers* the
folder: the other computer has to take it up before anything moves, which it does under **Offered to
you** if it is also running Homebase, or in Syncthing's own interface if it isn't. After that, a
change made on either side shows up on the other.

The peer protocol is [Syncthing](https://syncthing.net)'s, not Homebase's: device identity, discovery,
NAT traversal, encryption and conflict handling are all its work. Homebase supervises a Syncthing
process with its own home directory under the preference directory and its own loopback-only port,
started and stopped with the app. Install Syncthing (`brew install syncthing`) and restart Homebase;
if it isn't there, the rest of Homebase works and the Nodes panel says what's missing.

Shared folders have to be **inside** your Homebase folder, never the folder itself: `.homebase`
holds a live SQLite database, and copying that between machines corrupts it. Homebase refuses the
root and adds `.homebase` to the folder's ignore patterns as a second line of defence.

Folders are shared as `sendreceive`, so either side may change a file. If both change the same file
while disconnected, Syncthing keeps both — the loser is renamed with a `.sync-conflict-` suffix
beside the winner, so nothing is lost, but you may have a duplicate to tidy up.

`Homebase__Syncthing__Path` points at the binary if it isn't on `PATH`,
`Homebase__Syncthing__GuiPort` moves its local API off 8390, and
`Homebase__Syncthing__Enabled=false` switches the whole thing off.

## Landing page

The standalone messaging page is in [`landing/`](landing/README.md). Preview it with `npm --prefix src/homebase-web run dev:landing` at **http://127.0.0.1:5174**. Build it with `npm --prefix src/homebase-web run build:landing`; the static output goes to `artifacts/landing/` and contains no file-browser API.

## Check

```sh
./scripts/check.sh
```

Builds/type-checks the frontend and runs backend integration tests for persistence, indexing, file integrity/downloads, root switching, unavailable folders, symlinks, traversal, local request boundaries, Dropbox imports (single files, whole folders, skipping what's already here, and refusing to overwrite anything it didn't write), and node pairing, folder sharing and accepting an offered folder (device-ID validation, refusing the library root or any hidden or out-of-bounds path, and keeping Syncthing's state where preferences live rather than somewhere temporary). Tests use disposable fixtures and isolated settings, never your selected library.

GitHub Actions runs this same script on every pull request and push to `main`, on both macOS and Linux. The tests cover filesystem, indexing, and request-boundary behavior; the native macOS folder chooser isn't automatable and still needs a manual pass.

If macOS blocks a folder, check **System Settings → Privacy & Security → Files and Folders** for the app or terminal launching Homebase. If a drive disconnects, reconnect it and refresh, or choose another root in Storage settings.
