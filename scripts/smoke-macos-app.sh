#!/usr/bin/env bash
# Opens a built Uncloud.app the way a person would, and fails if it doesn't stay open. First as a
# Mac nobody has set up: the menu bar item, the setup window and a few status checks run, which is
# where a crash on opening shows up. Then as the host: the app starts the server it carries, which
# has to answer. Everything either writes goes in a throwaway home folder.
#
#   scripts/smoke-macos-app.sh artifacts/app-osx-arm64/Uncloud.app
set -euo pipefail
app="$(cd -- "${1:?Give the path to Uncloud.app.}" && pwd)"
home="$(mktemp -d)"
support="$home/Library/Application Support/Uncloud"
log="$support/uncloud.log"

stop() {
  # The app, and the server and Syncthing it started, all run from inside the bundle.
  pkill -f "$app/Contents/MacOS/" 2>/dev/null || true
  sleep 2
}
trap stop EXIT

fail() {
  echo "$1" >&2
  echo "--- what it printed" >&2
  cat "$home/output.txt" >&2 || true
  echo "--- uncloud.log" >&2
  cat "$log" >&2 2>/dev/null || true
  exit 1
}

open_app() {
  HOME="$home" "$app/Contents/MacOS/Uncloud" >>"$home/output.txt" 2>&1 &
  pid=$!
}

check_log() {
  if [[ -f "$log" ]] && grep -Eq '^[0-9-]+ [0-9:]+ (Critical|Error) ' "$log"; then fail "Uncloud logged an error $1."; fi
}

open_app
sleep 20
kill -0 "$pid" 2>/dev/null || fail "Uncloud quit on opening."
check_log "on opening"
stop
echo "Opened on a Mac that isn't set up, and stayed open."

mkdir -p "$support"
echo '{ "mode": "Host" }' >"$support/desktop.json"
open_app
for _ in $(seq 1 60); do
  if curl -fsS http://127.0.0.1:5210/api/health >/dev/null 2>&1; then
    kill -0 "$pid" 2>/dev/null || fail "Uncloud quit after starting the server."
    check_log "as the host"
    echo "Opened as the host, and the server it carries answered."
    exit 0
  fi
  kill -0 "$pid" 2>/dev/null || fail "Uncloud quit on opening as the host."
  sleep 1
done
fail "The server Uncloud carries didn't answer within a minute."
