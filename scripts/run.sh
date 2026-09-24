#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")/.."
if [[ ! -d src/homebase-web/node_modules ]]; then
  npm --prefix src/homebase-web ci
fi
npm --prefix src/homebase-web run build
# Syncthing comes with Uncloud rather than from the host's package manager. Without it everything
# but syncing people's computers still works, so a failed download (offline, say) is only a warning.
./scripts/fetch-syncthing.sh || echo "Continuing without a bundled Syncthing; My computers will say what's missing." >&2
exec ./scripts/dotnet.sh run --project src/Homebase.Server --no-launch-profile
