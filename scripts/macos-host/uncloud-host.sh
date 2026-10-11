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
app="" owner="" root="" legacy="" folder=""
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
  gid="$(dscl . -read "/Groups/$account" PrimaryGroupID | awk '{ print $2 }')"
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
}

is_empty() {
  local entries
  entries="$(ls -A "$1")"
  [[ -z "$(grep -v '^\.DS_Store$' <<<"$entries" || true)" ]]
}

# A folder Uncloud has kept everybody's files in: a "users" folder, spelled exactly so, holding
# nothing but accounts' folders, which are named by 32-character ids.
is_uncloud_folder() {
  local entries
  entries="$(ls "$1")"
  grep -qx users <<<"$entries" || return 1
  [[ -d "$1/users" && ! -L "$1/users" ]] || return 1
  entries="$(ls -A "$1/users")"
  ! grep -qvE '^[0-9a-f]{32}$' <<<"$entries"
}

# The top of a drive, as opposed to a folder on it.
is_mount_point() {
  [[ "$(stat -f %d "$1")" != "$(stat -f %d "$1/..")" ]]
}

# Whether Uncloud's account can pass through this folder to what's inside it.
reachable() {
  sudo -u "$account" /bin/test -x "$1"
}

# Folders that are never Uncloud's to take, by where they are: the system's, and everybody's
# homes as a whole. Judged before anything is made, so nothing is left behind by a refusal.
refuse_path() {
  local path="${1%/}" parts
  [[ "$path" == /* ]] || fail "Uncloud needs the folder’s full path, starting with /."
  case "$path" in
    "$base" | "$base"/*) return 0 ;;
  esac
  IFS=/ read -r -a parts <<<"${path#/}"
  if ((${#parts[@]} < 2)); then fail "“$path” is one of this Mac’s own folders. Choose a folder inside it instead."; fi
  case "/${parts[0]}" in
    /System | /Library | /Applications | /private | /usr | /bin | /sbin | /etc | /var | /tmp | /cores | /opt | /dev)
      fail "“$path” is one of this Mac’s own folders. Choose one of yours, or a drive." ;;
    /Users)
      if ((${#parts[@]} < 3)); then fail "“$path” is a home folder. Choose a folder outside it, or a drive."; fi ;;
  esac
}

# The same, for a folder that is there: not a link, and not the whole of a drive.
refuse_unsafe() {
  refuse_path "$1"
  [[ -d "$1" && ! -L "$1" ]] || fail "“$1” isn’t a folder on this Mac."
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
  find "$1" -xdev -exec chown -h "$account:$account" {} +
  chmod 700 "$1"
}

# Where in the chosen folder everyone's files go: the folder itself when it is empty or already
# Uncloud's, and a new folder called Uncloud inside it otherwise, so nothing already there changes
# hands. Every refusal comes before the new folder is made.
target_in() {
  local chosen="${1%/}" target
  [[ "$chosen" == /* ]] || fail "Uncloud needs the folder’s full path, starting with /."
  [[ -d "$chosen" && ! -L "$chosen" ]] || fail "“$chosen” isn’t a folder on this Mac."
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

migrate() {
  local from="$1" home old moving=false
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
    [[ -d "$old" ]] ||
      fail "Everyone’s files are in “$old”, which isn’t there right now. If it’s on a drive, plug it in, then try again."
    home="$(dscl . -read "/Users/$owner" NFSHomeDirectory | awk '{ $1 = ""; sub(/^ /, ""); print }')"
    case "$old" in
      "$home"/*)
        # Nobody else may pass through a home folder, Uncloud's account included, so the files
        # move out of it: a rename on the same disk, which copies nothing.
        moving=true
        if [[ -e "$files" || -L "$files" ]]; then
          { [[ -d "$files" && ! -L "$files" ]] && is_empty "$files"; } ||
            fail "$files already has something in it, so Uncloud’s files can’t be moved there."
        fi
        ;;
      *) check_protectable "$old" ;;
    esac
  fi

  rmdir "$host" 2>/dev/null || true
  [[ ! -e "$host" ]] || fail "$host already has something in it, so Uncloud’s settings weren’t moved there."
  mv "$from" "$host"

  if $moving; then
    rmdir "$files" 2>/dev/null || true
    if ! mv "$old" "$files"; then
      # Put back as it was, so the Uncloud that ran as this person still finds everything.
      mv "$host" "$from" || true
      fail "Uncloud couldn’t move its files out of “$old”. Move that folder somewhere outside your home folder, choose it in Uncloud’s Settings, and try again."
    fi
    sqlite3 "$host/homebase.db" \
      "INSERT OR REPLACE INTO host_settings(key, value) VALUES ('root_path', '${files//\'/\'\'}')"
  elif [[ -n "$old" ]]; then
    protect "$old"
  fi
}

# --- The service ---------------------------------------------------------------------------------

xml() {
  local text="$1"
  text="${text//&/&amp;}"
  text="${text//</&lt;}"
  text="${text//>/&gt;}"
  printf '%s' "$text"
}

write_service() {
  local server="$1" start_root="$2"
  cat >"$plist.new" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$label</string>
  <key>AssociatedBundleIdentifiers</key><array><string>life.uncloud.app</string></array>
  <key>ProgramArguments</key><array><string>$(xml "$server")</string></array>
  <key>WorkingDirectory</key><string>$(xml "$(dirname "$server")")</string>
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
    <key>Homebase__StopWhenReplaced</key><string>true</string>
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
  launchctl bootstrap system "$plist" || fail "macOS wouldn’t start Uncloud’s background service."
  for _ in $(seq 1 120); do
    if curl -fsS -m 2 "http://127.0.0.1:$port/api/health" >/dev/null 2>&1; then return 0; fi
    sleep 1
  done
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

  ensure_account
  stop_service
  own_dir "$base" root:wheel 755
  own_dir "$host" "$account:$account" 700
  if [[ -n "$legacy" ]]; then migrate "$legacy"; fi
  own_dir "$host" "$account:$account" 700
  find "$host" -xdev -exec chown -h "$account:$account" {} +
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

  write_service "$server" "$start_root"
  start_service
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
