#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")/.."
if [[ ! -d src/homebase-web/node_modules ]]; then
  npm --prefix src/homebase-web ci
fi
npm --prefix src/homebase-web run build
# The app's own return path: what it does when a browser comes back from Dropbox carrying how the
# sign-in went. Everything that has gone wrong there was in React's lifecycle rather than in any
# function worth calling directly, so these render the app.
npm --prefix src/homebase-web run test
# The Dropbox callback has no package of its own; the glob keeps the runner
# from treating api/ as a module to resolve.
node --test "api/*.test.js"
node --test "landing/*.test.cjs"
# The bundled tunnel, when there is a Go to build it with. Without one, Uncloud still builds
# and runs; only Homebase__RemoteAccess__Provider=builtin is unavailable.
if command -v go >/dev/null 2>&1; then
  ( cd tools/uncloud-tunnel && GOTOOLCHAIN="${GOTOOLCHAIN:-auto}" go vet ./... && go test ./... )
  ./scripts/build-tunnel.sh >/dev/null
else
  echo "No Go on PATH; skipping the bundled tunnel." >&2
fi
./scripts/dotnet.sh test Homebase.slnx --verbosity minimal
