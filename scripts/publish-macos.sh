#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")/.."
homebase_runtime="${1:-osx-arm64}"
case "$homebase_runtime" in
  osx-arm64|osx-x64) ;;
  *) echo "Choose osx-arm64 or osx-x64." >&2; exit 1 ;;
esac
if [[ ! -d src/homebase-web/node_modules ]]; then
  npm --prefix src/homebase-web ci
fi
npm --prefix src/homebase-web run build
./scripts/dotnet.sh publish src/Homebase.Server -c Release -r "$homebase_runtime" --self-contained true -o "artifacts/$homebase_runtime"
