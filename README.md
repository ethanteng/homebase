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

`Homebase__Dropbox__AppKey` supplies the Dropbox app key. `Homebase__ConfigDirectory` overrides the preference directory for isolated testing. `Homebase__Port` overrides port 5210 (also update Vite’s proxy for development). The server explicitly binds to `127.0.0.1`, regardless of `ASPNETCORE_URLS`. Run a single Homebase process per preference directory/library. V0 assumes a trusted local user and filesystem; it is not a sandbox against other programs running under your account.

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

Homebase can copy a file out of Dropbox onto storage you own, then keep that copy current as the
original changes. It is one-way — Dropbox to your folder — and Homebase asks only for read-only
permissions, so it cannot change anything in your Dropbox account.

Create an app at [dropbox.com/developers/apps](https://www.dropbox.com/developers/apps) with the
`account_info.read`, `files.metadata.read` and `files.content.read` permissions and the redirect URI
`http://localhost:5210/api/providers/dropbox/callback`, then start Homebase with its app key:

```sh
Homebase__Dropbox__AppKey=your-app-key ./scripts/run.sh
```

Open **Dropbox** in the sidebar, connect the account, and choose a file to bring home. Sign-in uses
the authorization-code flow with PKCE, so there is no client secret; the refresh token is written
with owner-only permissions to the preference directory, never into the library folder where it
would travel alongside synced files.

Synced files land in `Files/Dropbox/` as ordinary files, and the normal browser shows them. **Check
for changes** compares each tracked file against Dropbox and downloads any newer revision, writing
beside the destination and moving into place so an interrupted download can't leave a half-written
file. Homebase overwrites only files it wrote itself and still recognises: if you edit your copy, or
something already occupies the destination, it says so and leaves the file alone. Hidden files and
folders aren't synced yet.

## Landing page

The standalone messaging page is in [`landing/`](landing/README.md). Preview it with `npm --prefix src/homebase-web run dev:landing` at **http://127.0.0.1:5174**. Build it with `npm --prefix src/homebase-web run build:landing`; the static output goes to `artifacts/landing/` and contains no file-browser API.

## Check

```sh
./scripts/check.sh
```

Builds/type-checks the frontend and runs backend integration tests for persistence, indexing, file integrity/downloads, root switching, unavailable folders, symlinks, traversal, local request boundaries, and Dropbox sync (bringing a file home, following a later revision, and refusing to overwrite a copy you changed). Tests use disposable fixtures and isolated settings, never your selected library.

GitHub Actions runs this same script on every pull request and push to `main`, on both macOS and Linux. The tests cover filesystem, indexing, and request-boundary behavior; the native macOS folder chooser isn't automatable and still needs a manual pass.

If macOS blocks a folder, check **System Settings → Privacy & Security → Files and Folders** for the app or terminal launching Homebase. If a drive disconnects, reconnect it and refresh, or choose another root in Storage settings.
