#!/bin/bash
# Runs Uncloud on a Mac host in a macOS account of its own, so that nobody signed in at the Mac —
# its owner included — can look through everybody's files in Finder or Terminal. Only an
# administrator who uses sudo and their password can.
#
# The Uncloud app runs this as root, through macOS's own password prompt. It ships inside the app,
# in Contents/Resources, and is safe to run again: every step checks what is already there.
#
#   uncloud-host.sh install --app /Applications/Uncloud.app --owner <user>
#                           [--root <folder>] [--legacy-config <folder>]
#       Makes the hidden _uncloud account, the folders it keeps everything in, and the background
#       service that runs Uncloud from startup. --root is where everyone's files go (a folder on
#       this Mac by default). --legacy-config is the settings folder of an Uncloud that ran as
#       <user> until now: it is moved in, with its files, and nothing is lost.
#   uncloud-host.sh prepare --folder <folder>
#       Opens a folder or drive to Uncloud's account, so it can be chosen in Settings. Prints the
#       folder to choose, which is <folder>/Uncloud unless <folder> is empty or already Uncloud's.
#   uncloud-host.sh uninstall
#       Stops the service and takes it out. Everybody's files and the accounts stay where they
#       are, still locked to Uncloud's account; installing again picks them up.
# What it says reaches people in the Uncloud app, written the way Uncloud writes everywhere else:
# “quoted” names and apostrophes are typography here, not shell quoting.
# shellcheck disable=SC1111
set -euo pipefail
PATH=/usr/bin:/bin:/usr/sbin:/sbin
export PATH
umask 022

label=life.uncloud.host
account=_uncloud
base="/Library/Application Support/Uncloud"
host="$base/Host"
files="$base/Files"
bridge="$base/Bridge"
payload="$base/Server"
logs=/Library/Logs/Uncloud
plist="/Library/LaunchDaemons/$label.plist"
rotation=/etc/newsyslog.d/life.uncloud.host.conf
port=5210

fail() {
  printf '%s\n' "$*" >&2
  exit 1
}

[[ "$(id -u)" -eq 0 ]] || fail "This needs an administrator: run it with sudo."

verb="${1:-}"
if (($#)); then shift; fi
app="" owner="" root="" legacy="" folder="" had_service=false
while (($#)); do
  case "$1" in
    --app) app="${2:?}"; shift 2 ;;
    --owner) owner="${2:?}"; shift 2 ;;
    --root) root="${2:?}"; shift 2 ;;
    --legacy-config) legacy="${2:?}"; shift 2 ;;
    --folder) folder="${2:?}"; shift 2 ;;
    *) fail "Unknown option: $1" ;;
  esac
done

# --- The account -------------------------------------------------------------------------------

# Whether a user or group already has this number.
taken() {
  dscl . -list /Users UniqueID | awk -v id="$1" '$2 == id { found = 1 } END { exit !found }' ||
    dscl . -list /Groups PrimaryGroupID | awk -v id="$1" '$2 == id { found = 1 } END { exit !found }'
}

# A role account: named with an underscore, numbered 450–499, which macOS keeps off the login
# window and out of System Settings, with no password and no shell, so nobody can sign in as it.
ensure_account() {
  local id="" gid candidate
  if dscl . -read "/Users/$account" UniqueID >/dev/null 2>&1; then
    id="$(dscl . -read "/Users/$account" UniqueID | awk '{ print $2 }')"
  else
    for candidate in $(seq 499 -1 450); do
      if ! taken "$candidate"; then
        id="$candidate"
        break
      fi
    done
    [[ -n "$id" ]] || fail "This Mac has no room left for another service account (450–499 are all taken)."
  fi
  if ! dscl . -read "/Groups/$account" PrimaryGroupID >/dev/null 2>&1; then
    dscl . -create "/Groups/$account"
    dscl . -create "/Groups/$account" PrimaryGroupID "$id"
    dscl . -create "/Groups/$account" RealName "Uncloud"
    dscl . -create "/Groups/$account" Password '*'
  fi
  gid="$(attribute "/Groups/$account" PrimaryGroupID)"
  if ! dscl . -read "/Users/$account" UniqueID >/dev/null 2>&1; then
    dscl . -create "/Users/$account"
    dscl . -create "/Users/$account" UniqueID "$id"
    dscl . -create "/Users/$account" PrimaryGroupID "$gid"
    dscl . -create "/Users/$account" UserShell /usr/bin/false
    dscl . -create "/Users/$account" NFSHomeDirectory "$host"
    dscl . -create "/Users/$account" RealName "Uncloud"
    dscl . -create "/Users/$account" Password '*'
    dscl . -create "/Users/$account" IsHidden 1
  fi
  verify_account
}

# One attribute of a user or group, read as a property list, so a value with spaces in it — a
# home folder in Application Support — comes back whole.
attribute() {
  dscl -plist . -read "$1" "$2" 2>/dev/null | plutil -extract "dsAttrTypeStandard:$2.0" raw -o - - 2>/dev/null || true
}

# Everybody's files are about to belong to this account, so it has to be the one Uncloud made, or
# would have: a role account nobody can sign in to, with no password, no shell, nobody else in its
# group, and Uncloud's folder for a home. One that is anything else — made by somebody, for
# something else — is refused rather than trusted.
verify_account() {
  local id gid group shell home
  id="$(attribute "/Users/$account" UniqueID)"
  gid="$(attribute "/Users/$account" PrimaryGroupID)"
  group="$(attribute "/Groups/$account" PrimaryGroupID)"
  shell="$(attribute "/Users/$account" UserShell)"
  home="$(attribute "/Users/$account" NFSHomeDirectory)"
  if ! [[ "$id" =~ ^[0-9]+$ ]] || ((id < 450 || id > 499)) ||
    [[ "$gid" != "$group" || "$shell" != /usr/bin/false || "$home" != "$host" ]] ||
    [[ -n "$(attribute "/Users/$account" AuthenticationAuthority)" ]] ||
    [[ "$(attribute "/Users/$account" Password)" != "*" ]] ||
    [[ -n "$(attribute "/Groups/$account" GroupMembership)" ]]; then
    fail "This Mac already has an account called $account that Uncloud didn’t set up, so Uncloud won’t give it everyone’s files. An administrator can remove it in System Settings → Users & Groups, or with: sudo dscl . -delete /Users/$account"
  fi
}

# --- Folders -------------------------------------------------------------------------------------

# One of Uncloud's own folders, made if it isn't there, and given to whoever it belongs to. Never
# through a link: one planted here would hand the next step somebody else's folder.
own_dir() {
  local path="$1" owner_group="$2" mode="$3"
  [[ ! -L "$path" ]] || fail "$path is a link, which Uncloud won't follow. Remove it and try again."
  mkdir -p "$path"
  chown "$owner_group" "$path"
  chmod "$mode" "$path"
  chmod -N "$path"
}

# Everything under a folder belonging to Uncloud's account alone. The owner and mode aren't enough:
# an access list survives both and can still let anybody in, and one inherited from the folder
# above is how a folder made inside a shared one arrives with it. Links keep theirs, which open
# nothing.
take() {
  find "$1" -xdev -exec chown -h "$account:$account" {} +
  find "$1" -xdev ! -type l -exec chmod -N {} +
  chmod -h 700 "$1"
}

is_empty() {
  local entries
  entries="$(ls -A "$1")"
  [[ -z "$(grep -v '^\.DS_Store$' <<<"$entries" || true)" ]]
}

# A folder Uncloud has kept everybody's files in, and nothing else: a "users" folder, spelled
# exactly so, holding nothing but accounts' folders, which are named by 32-character ids. Hidden
# entries count too — a folder holding anything of anybody else's isn't Uncloud's to take whole.
is_uncloud_folder() {
  local entries
  entries="$(ls -A "$1")"
  grep -qx users <<<"$entries" || return 1
  ! grep -qvxE 'users|\.homebase|\.DS_Store' <<<"$entries" || return 1
  [[ -d "$1/users" && ! -L "$1/users" ]] || return 1
  entries="$(ls -A "$1/users")"
  [[ -z "$entries" ]] || ! grep -qvE '^[0-9a-f]{32}$' <<<"$entries"
}

# The top of a drive, as opposed to a folder on it.
is_mount_point() {
  [[ "$(stat -f %d "$1")" != "$(stat -f %d "$1/..")" ]]
}

# Whether Uncloud's account can pass through this folder to what's inside it.
reachable() {
  sudo -u "$account" /bin/test -x "$1"
}

# Folders that are never Uncloud's to take, by where they are: the system's, and anything in
# somebody's home folder, which nobody else may pass through — Uncloud's account included — and
# where a folder only Uncloud could open would be a puzzle to whoever lives there. Judged before
# anything is made, so nothing is left behind by a refusal.
refuse_path() {
  local path="$1" parts part
  [[ "$path" == /* ]] || fail "Uncloud needs the folder’s full path, starting with /."
  case "$path" in
    "$base" | "$base"/*) return 0 ;;
  esac
  IFS=/ read -r -a parts <<<"${path#/}"
  for part in "${parts[@]}"; do
    if [[ -z "$part" || "$part" == . || "$part" == .. ]]; then fail "Choose “$path” by its own path, without . or .. in it."; fi
  done
  if ((${#parts[@]} < 2)); then fail "“$path” is one of this Mac’s own folders. Choose a folder inside it instead."; fi
  case "/${parts[0]}" in
    /System | /Library | /Applications | /private | /usr | /bin | /sbin | /etc | /var | /tmp | /cores | /opt | /dev)
      fail "“$path” is one of this Mac’s own folders. Choose one of yours, or a drive." ;;
    /Users)
      if [[ "${parts[1]}" != Shared || ${#parts[@]} -lt 3 ]]; then
        fail "“$path” is in a home folder, which only its owner can open. Choose a drive, or a folder in /Users/Shared."
      fi ;;
  esac
}

# The folder a path really is, with every link along the way followed.
physical() { (cd -P -- "$1" 2>/dev/null && pwd -P); }

# Refuses a path that runs through a link anywhere along it, not only at its end. Every check here
# is about where a folder is, and a link lets a harmless-looking name stand for /etc.
refuse_links() {
  local path="$1" real
  while [[ "$path" == */ && "$path" != / ]]; do path="${path%/}"; done
  real="$(physical "$path")" || fail "“$1” isn’t a folder on this Mac."
  [[ "$real" == "$path" ]] ||
    fail "“$1” is reached through a linked folder, and is really “$real”. Choose the folder by its real path."
}

# The same, for a folder that is there: reached through no link, and not the whole of a drive.
refuse_unsafe() {
  [[ -d "$1" && ! -L "$1" ]] || fail "“$1” isn’t a folder on this Mac."
  refuse_links "$1"
  refuse_path "$1"
  if is_mount_point "$1"; then fail "“$1” is a whole drive. Choose a folder on it instead."; fi
}

value() { diskutil info -plist "$1" | plutil -extract "$2" raw -o - - 2>/dev/null || true; }

# A drive formatted so that a folder on it can belong to one account alone, with macOS honouring
# that. External drives usually arrive with "Ignore ownership on this volume" ticked, which would
# leave everything on them open to anyone signed in.
private_volume() {
  local device type name mount
  device="$(stat -f %Sd "$1")"
  type="$(value "$device" FilesystemType)"
  name="$(value "$device" FilesystemName)"
  mount="$(value "$device" MountPoint)"
  case "$type" in
    apfs | hfs) ;;
    *) fail "“${mount:-$1}” is formatted as ${name:-${type:-something macOS can’t describe}}, which can’t keep files private to one account. Choose a drive formatted as APFS or Mac OS Extended, or reformat this one in Disk Utility." ;;
  esac
  if [[ "$(value "$device" GlobalPermissionsEnabled)" != true ]]; then
    diskutil enableOwnership "$mount" >/dev/null ||
      fail "macOS wouldn’t stop ignoring ownership on “$mount”, so files there can’t be kept private."
  fi
}

# Everything protect checks, without changing anything: run first wherever something else is about
# to move, so a folder that won't do is refused while everything is still where it was.
check_protectable() {
  refuse_unsafe "$1"
  private_volume "$1"
  reachable "$(dirname "$1")" ||
    fail "Uncloud’s account can’t reach “$1”, because a folder it’s in is private. Choose a folder outside anybody’s home folder, or on a drive."
}

# Gives a folder, and everything in it on the same drive, to Uncloud's account alone.
protect() {
  check_protectable "$1"
  take "$1"
}

# Where in the chosen folder everyone's files go: the folder itself when it is empty or already
# Uncloud's, and a new folder called Uncloud inside it otherwise, so nothing already there changes
# hands. Every refusal comes before the new folder is made.
target_in() {
  local chosen="$1" target
  while [[ "$chosen" == */ && "$chosen" != / ]]; do chosen="${chosen%/}"; done
  [[ "$chosen" == /* ]] || fail "Uncloud needs the folder’s full path, starting with /."
  [[ -d "$chosen" && ! -L "$chosen" ]] || fail "“$chosen” isn’t a folder on this Mac."
  # The top of the startup disk is the one place a folder can't be made in: it is the system's.
  if [[ "$chosen" == / ]]; then fail "“/” is this Mac’s own disk. Choose a drive, or a folder in /Users/Shared."; fi
  refuse_links "$chosen"
  private_volume "$chosen"
  if is_uncloud_folder "$chosen" || { is_empty "$chosen" && ! is_mount_point "$chosen"; }; then
    target="$chosen"
    check_protectable "$target"
  else
    target="$chosen/Uncloud"
    refuse_path "$target"
    reachable "$chosen" ||
      fail "Uncloud’s account can’t reach “$chosen”, because it’s private. Choose a folder outside anybody’s home folder, or on a drive."
    if [[ -e "$target" || -L "$target" ]]; then
      { [[ -d "$target" && ! -L "$target" ]] && { is_uncloud_folder "$target" || is_empty "$target"; }; } ||
        fail "There’s already something called Uncloud in “$chosen”. Choose another folder."
    else
      mkdir "$target"
    fi
  fi
  printf '%s\n' "$target"
}

# --- Moving in an Uncloud that ran as a person ---------------------------------------------------

setting() { sqlite3 "$1/homebase.db" "SELECT value FROM host_settings WHERE key = 'root_path'" 2>/dev/null || true; }

set_root() {
  sqlite3 "$host/homebase.db" \
    "INSERT OR REPLACE INTO host_settings(key, value) VALUES ('root_path', '${1//\'/\'\'}')"
}

# Uncloud's own entries at the top of a host folder. Anything else there is somebody's, and stays.
only_uncloud() {
  local entries
  entries="$(ls -A "$1")"
  [[ -z "$entries" ]] || ! grep -qvxE 'users|\.homebase|\.DS_Store' <<<"$entries"
}

# What moving in has done so far, so that if Uncloud then doesn't start, it can all be put back and
# the Uncloud that ran as this person carries on exactly as it was.
migrated_from=""
moved=()
given=()
root_was=""
made_root=""

# Gives a folder to Uncloud's account, remembering it was the owner's.
give() {
  take "$1"
  given+=("$1")
}

move() {
  mv "$1" "$2"
  moved+=("$1|$2")
}

undo_migration() {
  local group i pair
  [[ -n "$migrated_from" ]] || return 0
  group="$(id -gn "$owner")"
  stop_service
  if ((${#given[@]})); then
    for i in "${given[@]}"; do find "$i" -xdev -exec chown -h "$owner:$group" {} +; done
  fi
  if [[ -n "$root_was" ]]; then set_root "$root_was"; fi
  for ((i = ${#moved[@]} - 1; i >= 0; i--)); do
    pair="${moved[i]}"
    mv "${pair#*|}" "${pair%%|*}"
  done
  # A folder made to hold what moved, empty again now.
  if [[ -n "$made_root" ]]; then rmdir "$made_root" 2>/dev/null || true; fi
  find "$host" -xdev -exec chown -h "$owner:$group" {} +
  mv "$host" "$migrated_from"
  migrated_from=""
  echo "Everything was put back as it was, and Uncloud runs as $owner again." >&2
}

migrate() {
  local from="$1" home old new_root="" whole=false entry
  [[ -d "$from" && ! -L "$from" ]] || return 0
  [[ -f "$from/homebase.db" ]] || return 0
  # Already moved in once: whatever is in Host now is the real one.
  if [[ -f "$host/homebase.db" ]]; then return 0; fi
  [[ "$(stat -f %Su "$from")" == "$owner" ]] || fail "“$from” doesn’t belong to $owner, so it isn’t moved."

  old="$(setting "$from")"
  # A library from before accounts kept its folder in settings.json instead.
  if [[ -z "$old" && -f "$from/settings.json" ]]; then
    old="$(plutil -extract RootPath raw -o - "$from/settings.json" 2>/dev/null || true)"
  fi

  # Everything that could refuse is asked first, while nothing has moved.
  if [[ -n "$old" ]]; then
    [[ -d "$old" && ! -L "$old" ]] ||
      fail "Everyone’s files are in “$old”, which isn’t there right now. If it’s on a drive, plug it in, then try again."
    refuse_links "$old"
    home="$(attribute "/Users/$owner" NFSHomeDirectory)"
    case "$old" in
      "$home"/*)
        # Nobody else may pass through a home folder, Uncloud's account included, so everyone's
        # files move out of it: a rename on the same disk, which copies nothing.
        new_root="$files"
        if [[ -e "$files" || -L "$files" ]]; then
          { [[ -d "$files" && ! -L "$files" ]] && is_empty "$files"; } ||
            fail "$files already has something in it, so Uncloud’s files can’t be moved there."
        fi
        ;;
      *)
        check_protectable "$old"
        new_root="$old"
        ;;
    esac
    if only_uncloud "$old"; then
      whole=true
    else
      # Other things share the folder, and they stay where they are, still their owner's. Only
      # Uncloud's own move, into a folder of its own.
      if [[ "$new_root" == "$old" ]]; then
        new_root="$old/Uncloud"
        [[ ! -e "$new_root" && ! -L "$new_root" ]] ||
          fail "“$old” already has something called Uncloud in it, so Uncloud’s files can’t be moved there. Rename it, then try again."
      fi
    fi
  fi

  rmdir "$host" 2>/dev/null || true
  [[ ! -e "$host" ]] || fail "$host already has something in it, so Uncloud’s settings weren’t moved there."
  mv "$from" "$host"
  migrated_from="$from"

  [[ -n "$old" ]] || return 0
  if [[ "$new_root" == "$old" ]]; then
    give "$old"
  elif $whole; then
    rmdir "$files" 2>/dev/null || true
    move "$old" "$files"
    give "$files"
    root_was="$old"
    set_root "$files"
  else
    mkdir -p "$new_root"
    made_root="$new_root"
    for entry in users .homebase; do
      if [[ -e "$old/$entry" ]]; then move "$old/$entry" "$new_root/$entry"; fi
    done
    give "$new_root"
    root_was="$old"
    set_root "$new_root"
  fi
}

# Anything that stops the script part way through moving in puts it all back.
finished=false
on_exit() {
  local status=$?
  if ((status != 0)) && ! $finished && [[ -n "$migrated_from" ]]; then undo_migration || true; fi
}
trap on_exit EXIT

# --- The service ---------------------------------------------------------------------------------

# The Uncloud the service runs: a copy of the app's, which only an administrator can change. Run
# straight from the app, anybody who can change the app — its owner can, without a password — could
# have the service run something else as Uncloud's account, which can read everybody's files. An
# update to the app reaches the service when this runs again.
install_payload() {
  local version
  [[ ! -L "$payload" && ! -L "$payload.new" && ! -L "$payload.old" ]] ||
    fail "$payload is a link, which Uncloud won't follow. Remove it and try again."
  rm -rf "$payload.new"
  # Without the app's access lists too, any of which could let somebody change the copy.
  ditto --noqtn --noacl "$app/Contents/MacOS" "$payload.new"
  version="$(plutil -extract CFBundleVersion raw -o - "$app/Contents/Info.plist" 2>/dev/null || true)"
  printf '%s\n' "${version:-unknown}" >"$payload.new/.uncloud-version"
  chown -R root:wheel "$payload.new"
  chmod -R go-w "$payload.new"
  rm -rf "$payload.old"
  if [[ -e "$payload" ]]; then mv "$payload" "$payload.old"; fi
  mv "$payload.new" "$payload"
}

# The copy that ran before, back in its place.
restore_payload() {
  if [[ -d "$payload.old" ]]; then
    rm -rf "$payload"
    mv "$payload.old" "$payload"
  fi
}

xml() {
  local text="$1"
  text="${text//&/&amp;}"
  text="${text//</&lt;}"
  text="${text//>/&gt;}"
  printf '%s' "$text"
}

write_service() {
  local start_root="$1"
  cat >"$plist.new" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$label</string>
  <key>AssociatedBundleIdentifiers</key><array><string>life.uncloud.app</string></array>
  <key>ProgramArguments</key><array><string>$(xml "$payload/Homebase.Server")</string></array>
  <key>WorkingDirectory</key><string>$(xml "$payload")</string>
  <key>UserName</key><string>$account</string>
  <key>GroupName</key><string>$account</string>
  <key>Umask</key><integer>63</integer>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>ThrottleInterval</key><integer>10</integer>
  <key>StandardOutPath</key><string>/dev/null</string>
  <key>StandardErrorPath</key><string>$logs/server-errors.log</string>
  <key>EnvironmentVariables</key>
  <dict>
    <key>HOME</key><string>$(xml "$host")</string>
    <key>Homebase__ConfigDirectory</key><string>$(xml "$host")</string>
    <key>Homebase__Root</key><string>$(xml "$start_root")</string>
    <key>Homebase__OwnAccount</key><string>true</string>
    <key>Homebase__HostFolders__Socket</key><string>$(xml "$bridge/host-folders.sock")</string>
    <key>Homebase__LogFile</key><string>$logs/server.log</string>
  </dict>
</dict>
</plist>
PLIST
  plutil -lint "$plist.new" >/dev/null || fail "Uncloud wrote a service description macOS can’t read."
  chown root:wheel "$plist.new"
  chmod 644 "$plist.new"
  mv "$plist.new" "$plist"

  # What crashes leave behind before the log file is open. Kept small and taken away in time.
  printf '%s\n' "$logs/server-errors.log $account:$account 600 3 1024 * N" >"$rotation"
  chmod 644 "$rotation"
}

stop_service() {
  launchctl bootout "system/$label" 2>/dev/null || true
  # bootout returns before the server has let go of its port.
  for _ in $(seq 1 20); do
    curl -fsS -m 1 "http://127.0.0.1:$port/api/health" >/dev/null 2>&1 || return 0
    sleep 0.5
  done
}

start_service() {
  launchctl enable "system/$label"
  if launchctl bootstrap system "$plist"; then
    for _ in $(seq 1 120); do
      if curl -fsS -m 2 "http://127.0.0.1:$port/api/health" >/dev/null 2>&1; then return 0; fi
      sleep 1
    done
  fi
  # A service set up just now that can't start is taken away again, rather than left failing in
  # the background. One that was already there goes back to the copy it ran before.
  stop_service
  if $had_service; then
    restore_payload
    launchctl bootstrap system "$plist" 2>/dev/null || true
  else
    rm -f "$plist" "$rotation"
  fi
  fail "Uncloud didn’t start. What went wrong is in $logs, which only an administrator can open: sudo tail \"$logs/server.log\"."
}

install() {
  local server start_root
  [[ -n "$app" && -n "$owner" ]] || fail "Say which app and whose Mac: --app and --owner."
  server="$app/Contents/MacOS/Homebase.Server"
  [[ -x "$server" && ! -L "$server" ]] || fail "“$app” isn’t a copy of Uncloud this can run."
  dscl . -read "/Users/$owner" UniqueID >/dev/null 2>&1 || fail "There’s no account called $owner on this Mac."
  if curl -fsS -m 2 "http://127.0.0.1:$port/api/health" >/dev/null 2>&1 && [[ ! -f "$plist" ]]; then
    fail "Something is already answering where Uncloud runs. Quit it, then try again."
  fi

  if [[ -f "$plist" ]]; then had_service=true; fi
  ensure_account
  stop_service
  own_dir "$base" root:wheel 755
  install_payload
  own_dir "$host" "$account:$account" 700
  if [[ -n "$legacy" ]]; then migrate "$legacy"; fi
  take "$host"
  own_dir "$files" "$account:$account" 700
  # The app, as the person signed in here, answers on a socket in this folder. They make it;
  # Uncloud's account, through the folder's group, can open it; nobody else can reach it.
  own_dir "$bridge" "$owner:$account" 750
  own_dir "$logs" "$account:$account" 700

  start_root="$files"
  if [[ -n "$root" ]]; then
    start_root="$(target_in "$root")"
    protect "$start_root"
  fi

  write_service "$start_root"
  start_service
  rm -rf "$payload.old"
  finished=true
}

prepare() {
  local target
  [[ -n "$folder" ]] || fail "Say which folder: --folder."
  dscl . -read "/Users/$account" UniqueID >/dev/null 2>&1 || fail "Set Uncloud up on this Mac first."
  target="$(target_in "$folder")"
  protect "$target"
  printf '%s\n' "$target"
}

uninstall() {
  stop_service
  rm -f "$plist" "$rotation"
}

case "$verb" in
  install) install ;;
  prepare) prepare ;;
  uninstall) uninstall ;;
  *) fail "Say what to do: install, prepare or uninstall." ;;
esac
