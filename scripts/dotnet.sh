#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "$0")/.." && pwd)"

# Major SDK version required by global.json; an older dotnet on PATH can't satisfy it.
required_major="$(sed -n 's/^[[:space:]]*"version"[[:space:]]*:[[:space:]]*"\([0-9][0-9]*\)\..*/\1/p' "$repo_root/global.json" | head -n 1)"
required_major="${required_major:-10}"

homebase_candidates=()
if command -v dotnet >/dev/null 2>&1; then
  homebase_candidates+=("$(command -v dotnet)")
fi
homebase_candidates+=("$HOME/.dotnet/dotnet" "$HOME/.cache/homebase/dotnet/dotnet")

for homebase_sdk in "${homebase_candidates[@]}"; do
  if [[ -x "$homebase_sdk" ]] && "$homebase_sdk" --list-sdks 2>/dev/null | grep -q "^${required_major}\."; then
    # Keep nested `dotnet` invocations on the same SDK.
    PATH="$(dirname -- "$homebase_sdk"):$PATH"
    export PATH
    exec "$homebase_sdk" "$@"
  fi
done

echo "No .NET ${required_major} SDK found on PATH, in ~/.dotnet, or in ~/.cache/homebase/dotnet." >&2
echo "Install it from https://dotnet.microsoft.com/download/dotnet/${required_major}.0" >&2
exit 1
