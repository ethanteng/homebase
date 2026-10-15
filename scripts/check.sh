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
# The backup service's own tests: neither a network nor a database.
node --test "services/backup/test/*.test.mjs"
# The bundled tunnel, when there is a Go to build it with. Without one, Uncloud still builds
# and runs; only Homebase__RemoteAccess__Provider=builtin is unavailable.
if command -v go >/dev/null 2>&1; then
  ( cd tools/uncloud-tunnel && GOTOOLCHAIN="${GOTOOLCHAIN:-auto}" go vet ./... && go test ./... )
  ./scripts/build-tunnel.sh >/dev/null
  # Everything the tunnel links has its licence and notices in THIRD_PARTY_TUNNEL.txt, which ships in
  # the app; a change to its go.mod fails here until that's regenerated.
  ./scripts/go-notices.sh --check tunnel src/Homebase.Server/bin/Debug/net10.0/uncloud-tunnel
else
  echo "No Go on PATH; skipping the bundled tunnel." >&2
fi
./scripts/dotnet.sh test Homebase.slnx --verbosity minimal
# The iPhone app's library, when there is a Swift 6 to test it with: Xcode's on a Mac, or a toolchain
# from swift.org on Linux. The app itself needs Xcode, and CI's iOS job builds it.
if swift --version 2>/dev/null | grep -E 'Swift version ([6-9]|[1-9][0-9])\.' >/dev/null; then
  swift test --package-path src/Uncloud.iOS/UncloudKit
else
  echo "No Swift 6 on PATH; skipping the iPhone app's library." >&2
fi
