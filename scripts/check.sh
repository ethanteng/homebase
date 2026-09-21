#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")/.."
if [[ ! -d src/homebase-web/node_modules ]]; then
  npm --prefix src/homebase-web ci
fi
npm --prefix src/homebase-web run build
# The signup function has no package of its own; the glob keeps the runner
# from treating api/ as a module to resolve.
node --test "api/*.test.js"
node --test "landing/*.test.cjs"
./scripts/dotnet.sh test Homebase.slnx --verbosity minimal
