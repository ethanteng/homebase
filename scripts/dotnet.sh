#!/usr/bin/env bash
set -euo pipefail
if command -v dotnet >/dev/null 2>&1; then
  exec dotnet "$@"
fi
for homebase_sdk in "$HOME/.dotnet/dotnet" "$HOME/.cache/homebase/dotnet/dotnet"; do
  if [[ -x "$homebase_sdk" ]]; then
    exec "$homebase_sdk" "$@"
  fi
done
echo "Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0" >&2
exit 1
