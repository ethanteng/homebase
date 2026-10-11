#!/usr/bin/env bash
# Sets a Mac up as an Uncloud host for real, the way the Mac app does through macOS's password
# prompt — here with sudo, which CI's Macs have without a password — and checks what it promises:
# everybody's files belong to Uncloud's own account, and the person signed in at the Mac can't list
# them, in their home folder or anywhere else.
#
# It starts from a host set up the old way, as the person signed in, with its files in their home
# folder, and moves it across. Then the Mac's folders reach Uncloud through the app, a drive is
# opened to it, and turning it off leaves everything locked.
#
#   scripts/smoke-macos-host.sh artifacts/app-osx-arm64/Uncloud.app
#
# It changes the Mac it runs on — a hidden account, a background service, an app in Applications —
# so it only runs in CI.
set -euo pipefail
if [[ "${CI:-}" != true ]]; then
  echo "This sets up a real Uncloud service on this Mac, so it only runs in CI." >&2
  exit 1
fi
built="$(cd -- "${1:?Give the path to Uncloud.app.}" && pwd)"
app=/Applications/Uncloud.app
script="$app/Contents/Resources/uncloud-host.sh"
base="/Library/Application Support/Uncloud"
logs=/Library/Logs/Uncloud
address=http://127.0.0.1:5210
work="$(mktemp -d)"
jar="$work/cookies"
diary="Ada's diary $(openssl rand -hex 3).txt"
letter="Letter to Gran $(openssl rand -hex 3).txt"
holiday="Holiday plans $(openssl rand -hex 3).txt"
app_pid=""

cleanup() {
  if [[ -n "$app_pid" ]]; then kill "$app_pid" 2>/dev/null || true; fi
  pkill -f "$app/Contents/MacOS/" 2>/dev/null || true
}
trap cleanup EXIT

fail() {
  echo "$1" >&2
  echo "--- server log" >&2
  sudo tail -n 100 "$logs/server.log" >&2 2>/dev/null || true
  sudo cat "$logs/server-errors.log" >&2 2>/dev/null || true
  echo "--- app log" >&2
  cat "$work/home/Library/Application Support/Uncloud/uncloud.log" >&2 2>/dev/null || true
  exit 1
}

json() { python3 -c 'import json, sys; value = json.load(sys.stdin)
for key in sys.argv[1:]: value = value[int(key) if key.isdigit() else key]
print(value)' "$@"; }

api() {
  local method="$1" path="$2"
  shift 2
  curl -fsS -b "$jar" -c "$jar" -X "$method" -H 'X-Homebase-Request: 1' -H 'Content-Type: application/json' \
    "$@" "$address$path"
}

# Whether My files lists this name. Read as JSON, which escapes an apostrophe.
listed() { api GET "/api/files?path=" | python3 -c 'import json, sys
sys.exit(0 if sys.argv[1] in [entry["name"] for entry in json.load(sys.stdin)["entries"]] else 1)' "$1"; }

body() { python3 -c 'import json, sys; print(json.dumps(dict(zip(sys.argv[1::2], sys.argv[2::2]))))' "$@"; }

answering() {
  for _ in $(seq 1 120); do
    if curl -fsS -m 2 "$address/api/health" >/dev/null 2>&1; then return 0; fi
    sleep 1
  done
  fail "Uncloud didn't answer within two minutes."
}

upload() {
  local name="$1" contents="$2" id
  id="$(api POST /api/uploads -d "{\"destination\":\"\",\"bytes\":${#contents}}" | json id)"
  printf '%s' "$contents" | api PUT "/api/uploads/$id?path=$(python3 -c 'import sys, urllib.parse; print(urllib.parse.quote(sys.argv[1]))' "$name")" \
    -H 'Content-Type: application/octet-stream' --data-binary @- >/dev/null
  api POST "/api/uploads/$id/finish" -d '{"destination":""}' >/dev/null
}

# Nobody signed in here can list it, or find a name inside it.
private() {
  local folder="$1" name="$2"
  if ls "$folder" >/dev/null 2>&1; then fail "$USER can list $folder."; fi
  if find "$folder" 2>/dev/null | grep -qF "$name"; then fail "$USER can find $name in $folder."; fi
  sudo find "$folder" -name "$name" | grep -q . || fail "$name isn't in $folder at all."
}

# --- An Uncloud that ran as the person signed in here, with its files in their home folder -------
sudo rm -rf "$app"
sudo ditto "$built" "$app"
legacy="$HOME/Library/Application Support/Homebase"
old_files="$HOME/Uncloud Files"
mkdir -p "$old_files"
# A mode and an access list of its own, which a move that can't finish has to put back.
chmod 751 "$old_files"
chmod +a "everyone deny delete" "$old_files"
run_legacy() {
  Homebase__Announce=false "$app/Contents/MacOS/Homebase.Server" >>"$work/legacy.log" 2>&1 &
  legacy_pid=$!
  answering
}
stop_legacy() {
  kill "$legacy_pid"
  wait "$legacy_pid" 2>/dev/null || true
}
run_legacy
api POST /api/setup -d "$(body username ada displayName Ada password 'correct horse battery')" >/dev/null
api PUT /api/host -d "$(body path "$old_files")" >/dev/null
upload "$diary" "Dear diary"
find "$old_files/users" -name "$diary" | grep -q . || fail "The diary didn't arrive where it should have."
echo "Ran as $USER, which could list everybody's files."
stop_legacy

# --- A move that can't finish puts everything back ------------------------------------------------
# A copy of the app whose server never starts, so the service can't come up after moving in.
broken=/Applications/Broken.app
sudo rm -rf "$broken"
sudo ditto "$built" "$broken"
printf '#!/bin/sh\nexit 1\n' | sudo tee "$broken/Contents/MacOS/Homebase.Server" >/dev/null
sudo chmod 755 "$broken/Contents/MacOS/Homebase.Server"
if said="$(sudo /bin/bash "$broken/Contents/Resources/uncloud-host.sh" install --app "$broken" --owner "$USER" --legacy-config "$legacy" 2>&1)"; then
  fail "Setting up a server that can't start reported success."
fi
grep -q "put back as it was" <<<"$said" || fail "A failed move didn't say what happened: $said"
[[ -f "$legacy/homebase.db" ]] || fail "A failed move didn't put the settings back in $legacy."
[[ "$(stat -f %Su "$legacy/homebase.db")" == "$USER" ]] || fail "A failed move left the settings with Uncloud's account."
find "$old_files/users" -name "$diary" | grep -q . || fail "A failed move didn't put everybody's files back."
[[ "$(stat -f %Su "$old_files")" == "$USER" && "$(stat -f %Lp "$old_files")" == 751 ]] ||
  fail "A failed move didn't put $old_files back as it was: $(stat -f '%Su %Lp' "$old_files")"
listing="$(ls -led "$old_files")"
grep -q "everyone deny delete" <<<"$listing" || fail "A failed move lost $old_files's access list: $listing"
[[ ! -e "$base/Host/homebase.db" && ! -e /Library/LaunchDaemons/life.uncloud.host.plist ]] ||
  fail "A failed move left a service or settings behind."
sudo rm -rf "$broken"
run_legacy
[[ "$(api GET /api/session | json user username)" == ada ]] || fail "After a failed move, Uncloud didn't carry on as it was."
listed "$diary" || fail "After a failed move, Ada's diary is missing."
stop_legacy
echo "A move that couldn't finish put everything back, and Uncloud ran as before."

# The folder chosen back then also had somebody's own things in it, which Uncloud promised to leave.
echo "Mine alone" >"$old_files/My own notes.txt"

# --- Moved into an account of its own --------------------------------------------------------------
sudo /bin/bash "$script" install --app "$app" --owner "$USER" --legacy-config "$legacy"
answering
[[ ! -e "$legacy" ]] || fail "The old settings are still in $legacy."
[[ ! -e "$old_files/users" ]] || fail "Everybody's files are still in $old_files."
# What was somebody's own stays where it was, and still theirs.
[[ "$(cat "$old_files/My own notes.txt")" == "Mine alone" ]] || fail "The notes that were already there moved or changed hands."
# The same account, and the same session, carry on as they were.
[[ "$(api GET /api/session | json user username)" == ada ]] || fail "Ada's session didn't survive the move."
[[ "$(api GET /api/host | json rootPath)" == "$base/Files" ]] || fail "Everybody's files aren't in $base/Files."
listed "$diary" || fail "Ada's diary didn't come across."
[[ "$(sudo stat -f %Su "$base/Files")" == _uncloud ]] || fail "$base/Files doesn't belong to Uncloud's account."
[[ "$(sudo stat -f %Lp "$base/Files")" == 700 ]] || fail "$base/Files is open to others."
private "$base/Files" "$diary"
# The service runs a copy of Uncloud only an administrator can change, not the app this user owns.
program="$(plutil -extract ProgramArguments.0 raw -o - /Library/LaunchDaemons/life.uncloud.host.plist)"
[[ "$program" == "$base/Server/Homebase.Server" ]] || fail "The service runs $program, not its own copy."
[[ "$(stat -f %Su "$program")" == root && ! -w "$program" && ! -w "$base/Server" ]] ||
  fail "$USER can change the copy of Uncloud the service runs."
private "$base/Host" homebase.db
private "$logs" server.log
upload "$holiday" "Somewhere warm"
private "$base/Files" "$holiday"
echo "Moved into its own account, which nobody signed in here can look into."

# --- An update that can't start leaves the one before running -------------------------------------
sudo ditto "$built" "$broken"
printf '#!/bin/sh\nexit 1\n' | sudo tee "$broken/Contents/MacOS/Homebase.Server" >/dev/null
sudo chmod 755 "$broken/Contents/MacOS/Homebase.Server"
if said="$(sudo /bin/bash "$broken/Contents/Resources/uncloud-host.sh" install --app "$broken" --owner "$USER" 2>&1)"; then
  fail "Updating to a server that can't start reported success."
fi
grep -q "carries on as it was before" <<<"$said" || fail "A failed update didn't say what happened: $said"
sudo rm -rf "$broken"
answering
[[ "$(api GET /api/session | json user username)" == ada ]] || fail "After a failed update, Uncloud didn't carry on as it was."
cmp -s "$app/Contents/MacOS/Homebase.Server" "$base/Server/Homebase.Server" ||
  fail "After a failed update, the service isn't running the copy it ran before."
echo "An update that couldn't start left the version before it running."

# --- This Mac's folders, read by the app as the person signed in here -------------------------------
home="$work/home"
mkdir -p "$home/Library/Application Support/Uncloud" "$home/Documents/Letters"
printf 'Dear Gran' >"$home/Documents/Letters/$letter"
echo '{ "mode": "Host" }' >"$home/Library/Application Support/Uncloud/desktop.json"
HOME="$home" "$app/Contents/MacOS/Uncloud" >"$work/app.log" 2>&1 &
app_pid=$!
socket="$base/Bridge/host-folders.sock"
for _ in $(seq 1 60); do
  if [[ -S "$socket" ]]; then break; fi
  kill -0 "$app_pid" 2>/dev/null || fail "The app quit on opening as the host."
  sleep 1
done
[[ -S "$socket" ]] || fail "The app didn't hand over this Mac's folders."
sudo -u _uncloud curl -fsS --unix-socket "$socket" "http://app/candidates" | grep -q Documents ||
  fail "Uncloud's account can't reach the app's folders."
if sudo -u nobody curl -fsS --unix-socket "$socket" "http://app/candidates" >/dev/null 2>&1; then
  fail "Somebody other than Uncloud's account can reach the app's folders."
fi
place="$(api POST /api/host/places -d "$(body path "$home/Documents")" | json id)"
api GET "/api/imports/sources/$place/files?path=" | grep -q Letters || fail "Uncloud can't see into Documents."
api POST /api/imports -d "$(body remotePath /Letters source "$place")" >/dev/null
for _ in $(seq 1 60); do
  if [[ "$(api GET /api/imports/job | json job running)" == False ]]; then break; fi
  sleep 1
done
[[ "$(api GET /api/imports/job | json job stage)" == Done ]] || fail "Bringing Letters in didn't finish: $(api GET /api/imports/job)"
private "$base/Files" "$letter"
kill "$app_pid"
wait "$app_pid" 2>/dev/null || true
app_pid=""
[[ "$(api GET /api/imports/sources | json computerUnavailable)" == *"isn’t open on the host Mac"* ]] ||
  fail "With the app closed, nobody is told why."
echo "This Mac's folders reached Uncloud through the app, and only Uncloud's account could ask."

# --- A drive, opened to Uncloud's account ----------------------------------------------------------
hdiutil create -size 64m -fs APFS -volname UncloudDrive "$work/drive.dmg" >/dev/null
hdiutil attach -nobrowse "$work/drive.dmg" >/dev/null
drive=/Volumes/UncloudDrive
echo "Something of mine" >"$drive/mine.txt"
target="$(sudo /bin/bash "$script" prepare --folder "$drive")"
[[ "$target" == "$drive/Uncloud" ]] || fail "A drive with things on it should get a folder of Uncloud's own, not $target."
[[ "$(stat -f %Su "$drive/mine.txt")" == "$USER" ]] || fail "What was already on the drive changed hands."
api PUT /api/host -d "$(body path "$target")" >/dev/null
upload "$diary" "Dear diary, on the drive"
private "$target" "$diary"
echo "A drive was opened to Uncloud, which keeps its files there, and nobody else can look."

# A folder whose access list lets everyone in, and passes that on to whatever is made inside it.
shared="/Users/Shared/uncloud-smoke-shared"
mkdir -p "$shared"
echo "Ours" >"$shared/ours.txt"
chmod +a "everyone allow list,search,readattr,read,file_inherit,directory_inherit" "$shared"
inside="$(sudo /bin/bash "$script" prepare --folder "$shared")"
[[ "$inside" == "$shared/Uncloud" ]] || fail "A shared folder with things in it should get a folder of Uncloud's own, not $inside."
listing="$(ls -led "$inside")"
if grep -q '^ *[0-9]*: ' <<<"$listing"; then fail "$inside kept an access list: $listing"; fi
if ls "$inside" >/dev/null 2>&1; then fail "$USER can list $inside through an access list."; fi
[[ "$(cat "$shared/ours.txt")" == Ours ]] || fail "What was already in the shared folder changed."
echo "A folder made inside a shared one didn't keep the access list that would have let everyone in."

hdiutil create -size 64m -fs ExFAT -volname LooseDrive "$work/loose.dmg" >/dev/null
hdiutil attach -nobrowse "$work/loose.dmg" >/dev/null
if refused="$(sudo /bin/bash "$script" prepare --folder /Volumes/LooseDrive 2>&1)"; then
  fail "A drive that can't keep files private was taken anyway."
fi
grep -q "formatted as" <<<"$refused" || fail "Refusing an ExFAT drive didn't say why: $refused"
[[ ! -e /Volumes/LooseDrive/Uncloud ]] || fail "Refusing a drive left a folder behind on it."
echo "A drive that can't keep files private was refused, and left as it was."

# Nothing that isn't Uncloud's to take is taken — however it's named. A link anywhere along the way
# can make a harmless-looking path stand for the system's own folders.
link=/Users/Shared/uncloud-smoke-link
ln -sfn / "$link"
for folder in / /Users "$HOME" "$HOME/Documents" /Applications /Library "$drive/../.." \
  "$link/etc" "$link/Users/Shared" "$link/private/var"; do
  if sudo /bin/bash "$script" prepare --folder "$folder" >/dev/null 2>&1; then
    fail "$folder was opened to Uncloud."
  fi
done
rm -f "$link"
[[ "$(stat -f %Su /private/etc)" == root && "$(stat -f %Su /private/var)" == root ]] ||
  fail "A folder of the system's changed hands."
[[ "$(stat -f %Su "$HOME")" == "$USER" ]] || fail "$HOME changed hands."
[[ ! -e "$HOME/Uncloud" ]] || fail "Refusing $HOME left a folder behind in it."

# --- Off, and on again ----------------------------------------------------------------------------
sudo /bin/bash "$script" uninstall
if curl -fsS -m 2 "$address/api/health" >/dev/null 2>&1; then fail "Uncloud still answers after turning it off."; fi
private "$base/Files" "$holiday"
private "$target" "$diary"
# Turned on again, it picks up where everybody's files are; a new folder would never be used.
mkdir -p /Users/Shared/uncloud-smoke-unused
if sudo /bin/bash "$script" install --app "$app" --owner "$USER" --root /Users/Shared/uncloud-smoke-unused >/dev/null 2>&1; then
  fail "Turning Uncloud on again took a new folder it would never use."
fi
[[ "$(stat -f %Su /Users/Shared/uncloud-smoke-unused)" == "$USER" && ! -e /Users/Shared/uncloud-smoke-unused/Uncloud ]] ||
  fail "Refusing a new folder on turning Uncloud on again changed it."
sudo /bin/bash "$script" install --app "$app" --owner "$USER"
answering
[[ "$(api GET /api/session | json user username)" == ada ]] || fail "Turning it on again lost Ada."
[[ "$(api GET /api/host | json rootPath)" == "$target" ]] || fail "Turning it on again lost where the files are."
echo "Turned off, everything stayed locked; turned on again, everything was where it was."
