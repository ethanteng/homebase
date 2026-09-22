#!/usr/bin/env bash
# Builds the tunnel Uncloud ships, so nobody has to install Tailscale to be reached from
# outside the house. The binary goes beside the published application, which is where
# RemoteAccessOptions.Executable looks for it.
set -euo pipefail
cd -- "$(dirname -- "$0")/.."
uncloud_out="${1:-src/Homebase.Server/bin/Debug/net10.0}"

if ! command -v go >/dev/null 2>&1; then
  echo "Building the bundled tunnel needs Go. Install it from https://go.dev/dl/," >&2
  echo "or run Uncloud with Homebase__RemoteAccess__Provider=none." >&2
  exit 1
fi

# tailscale.com pins a newer toolchain than most people have; Go fetches it rather than failing.
export GOTOOLCHAIN="${GOTOOLCHAIN:-auto}"
mkdir -p -- "$uncloud_out"
# Resolved before leaving the repository root, so a path given either way lands where it was
# meant to rather than somewhere relative to the module.
uncloud_out="$(cd -- "$uncloud_out" && pwd)"
( cd tools/uncloud-tunnel && go build -trimpath -ldflags "-s -w" -o "$uncloud_out/uncloud-tunnel" . )
echo "Built $uncloud_out/uncloud-tunnel"
