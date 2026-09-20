#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")/.."
if [[ ! -d src/homebase-web/node_modules ]]; then
  npm --prefix src/homebase-web ci
fi
npm --prefix src/homebase-web run build
./scripts/dotnet.sh test Homebase.slnx --verbosity minimal
