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
# The tunnel travels with the application: reaching this host from outside the house should not
# start with installing somebody else's daemon.
case "$homebase_runtime" in
  osx-arm64) homebase_arch=arm64 ;;
  osx-x64) homebase_arch=amd64 ;;   # .NET calls it x64; Go calls it amd64.
esac
GOOS=darwin GOARCH="$homebase_arch" ./scripts/build-tunnel.sh "artifacts/$homebase_runtime"
