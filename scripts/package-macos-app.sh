#!/usr/bin/env bash
# Builds Uncloud.app: the menu-bar app, with the Uncloud server, its Syncthing and its tunnel
# inside it. The same app is the host on one Mac and the way everybody else's Macs keep their
# files in step, so nobody installs anything else.
#
#   scripts/package-macos-app.sh [osx-arm64|osx-x64]
#
# On a Mac it also signs the app and puts it in a disk image, artifacts/Uncloud-<runtime>.dmg.
# With UNCLOUD_SIGN_IDENTITY set to a "Developer ID Application: …" identity it signs for
# distribution. It notarizes and staples the disk image too with UNCLOUD_NOTARY_PROFILE set to a
# notarytool keychain profile, or with an App Store Connect API key: UNCLOUD_NOTARY_KEY (the .p8
# file), UNCLOUD_NOTARY_KEY_ID and UNCLOUD_NOTARY_ISSUER, which is how CI does it. Without them it
# signs ad hoc, which runs on the Mac that built it and needs confirming in System Settings
# anywhere else.
set -euo pipefail
cd -- "$(dirname -- "$0")/.."
runtime="${1:-osx-arm64}"
case "$runtime" in
  osx-arm64|osx-x64) ;;
  *) echo "Choose osx-arm64 or osx-x64." >&2; exit 1 ;;
esac

version="$(git describe --tags --always 2>/dev/null || echo 0)"
short_version="0.$(git rev-list --count HEAD 2>/dev/null || echo 0)"
app="artifacts/app-$runtime/Uncloud.app"
rm -rf -- "artifacts/app-$runtime"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

# The server as publish-macos.sh builds it: interface, tunnel and Syncthing beside it.
./scripts/publish-macos.sh "$runtime"
desktop="artifacts/app-$runtime.publish"
rm -rf -- "$desktop"
./scripts/dotnet.sh publish src/Uncloud.Desktop -c Release -r "$runtime" --self-contained true \
  -p:UseAppHost=true -o "$desktop"

# One copy of .NET, not two: the app and the server it carries sit side by side in Contents/MacOS
# and share it. Both reference the same framework, so every file they both ship is the same file.
# One that differs would leave one of them running on the other's copy, so it stops the build.
cp -R "artifacts/$runtime/." "$app/Contents/MacOS/"
different=()
while IFS= read -r -d '' file; do
  file="${file#"$desktop"/}"
  if [[ -e "$app/Contents/MacOS/$file" ]] && ! cmp -s "$desktop/$file" "$app/Contents/MacOS/$file"; then
    different+=("$file")
  fi
done < <(find "$desktop" -type f -print0)
if ((${#different[@]})); then
  echo "The app and the server ship different copies of: ${different[*]}" >&2
  exit 1
fi
cp -R "$desktop/." "$app/Contents/MacOS/"

# Preserve repository terms, upstream attributions and source-access notices in the download.
mkdir -p "$app/Contents/Resources/Licenses"
cp LICENSE THIRD_PARTY_* "$app/Contents/Resources/Licenses/"
rm -rf -- "$desktop"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Uncloud</string>
  <key>CFBundleDisplayName</key><string>Uncloud</string>
  <key>CFBundleIdentifier</key><string>life.uncloud.app</string>
  <key>CFBundleExecutable</key><string>Uncloud</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$short_version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>CFBundleIconFile</key><string>Uncloud</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <!-- In the menu bar, not the Dock. -->
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <!-- uncloud://pair?… links from Uncloud's web page open here. -->
  <key>CFBundleURLTypes</key>
  <array><dict>
    <key>CFBundleURLName</key><string>life.uncloud.pair</string>
    <key>CFBundleURLSchemes</key><array><string>uncloud</string></array>
  </dict></array>
  <!-- macOS asks before an app looks around the local network. The host announces itself over
       Bonjour, a computer setting up looks for it, and Syncthing finds the other side nearby. -->
  <key>NSLocalNetworkUsageDescription</key>
  <string>Uncloud finds your household’s Uncloud on this network, so you don’t have to type its address, and syncs with it directly when it’s nearby.</string>
  <key>NSBonjourServices</key><array><string>_uncloud._tcp</string></array>
</dict>
</plist>
PLIST

if [[ "$(uname -s)" != Darwin ]]; then
  echo "Built $app. Signing, the icon and the disk image need a Mac; run this there to finish." >&2
  exit 0
fi

icons="$(mktemp -d)/Uncloud.iconset"
mkdir -p "$icons"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" src/Uncloud.Desktop/Assets/icon-1024.png --out "$icons/icon_${size}x${size}.png" >/dev/null
  sips -z $((size * 2)) $((size * 2)) src/Uncloud.Desktop/Assets/icon-1024.png --out "$icons/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$icons" -o "$app/Contents/Resources/Uncloud.icns"

identity="${UNCLOUD_SIGN_IDENTITY:--}"
# Who notarizes: a notarytool keychain profile on a Mac somebody set up, or an API key in CI.
notary=()
if [[ -n "${UNCLOUD_NOTARY_PROFILE:-}" ]]; then
  notary=(--keychain-profile "$UNCLOUD_NOTARY_PROFILE")
elif [[ -n "${UNCLOUD_NOTARY_KEY:-}" ]]; then
  notary=(--key "$UNCLOUD_NOTARY_KEY" --key-id "${UNCLOUD_NOTARY_KEY_ID:?Set UNCLOUD_NOTARY_KEY_ID too.}"
    --issuer "${UNCLOUD_NOTARY_ISSUER:?Set UNCLOUD_NOTARY_ISSUER too.}")
fi
if ((${#notary[@]})) && [[ "$identity" == "-" ]]; then
  echo "Apple won't notarize an ad hoc signature. Set UNCLOUD_SIGN_IDENTITY too." >&2
  exit 1
fi
# An ad hoc signature ("-") can't carry a trusted timestamp; a Developer ID one must, to notarize.
timestamp=--timestamp=none
if [[ "$identity" != "-" ]]; then timestamp=--timestamp; fi
# Apple's timestamp server now and then answers that it isn't available; a moment later it is.
sign() {
  local attempt
  for attempt in 1 2 3; do
    codesign --force "$timestamp" --options runtime --entitlements src/Uncloud.Desktop/Uncloud.entitlements -s "$identity" "$@" && return
    if ((attempt == 3)); then return 1; fi
    sleep 5
  done
}
# Inside out, or the bundle's seal is broken. codesign counts everything in Contents/MacOS as
# nested code — .NET's managed .dll files and the server's web pages included — so every file
# there is signed, except the app's own executable, which signing the bundle signs.
find "$app/Contents/MacOS" -type f ! -path "$app/Contents/MacOS/Uncloud" -print0 | while IFS= read -r -d '' file; do
  sign "$file"
done
sign "$app"
codesign --verify --deep --strict "$app"

# What people download: the app beside a shortcut to Applications, to drag it onto. Compressed
# with LZMA (ULMO), the smallest format macOS opens with nothing extra: 59 MB for Apple silicon,
# against 76 MB zipped.
dmg="artifacts/Uncloud-$runtime.dmg"
contents="$(mktemp -d)/Uncloud"
mkdir -p "$contents"
ditto "$app" "$contents/Uncloud.app"
ln -s /Applications "$contents/Applications"
rm -f -- "$dmg"
# hdiutil sometimes answers "Resource busy" on a busy Mac, CI's included; a moment later it works.
for attempt in 1 2 3; do
  hdiutil create -volname Uncloud -srcfolder "$contents" -format ULMO -ov "$dmg" >/dev/null && break
  if ((attempt == 3)); then exit 1; fi
  sleep 5
done
if [[ "$identity" != "-" ]]; then codesign --force --timestamp -s "$identity" "$dmg"; fi

# The copy inside is the one people run. .NET's .dll files carry their signatures in extended
# attributes, which a copy that dropped them would break, so the app is checked as it is there.
check_inside() {
  local mounted ok=true
  mounted="$(mktemp -d)"
  hdiutil attach "$dmg" -readonly -nobrowse -mountpoint "$mounted" >/dev/null
  "$@" "$mounted/Uncloud.app" || ok=false
  hdiutil detach "$mounted" >/dev/null
  [[ "$ok" == true ]]
}
if ! check_inside codesign --verify --deep --strict; then
  echo "The app in $dmg doesn't pass codesign --verify." >&2
  exit 1
fi

# Notarized, macOS opens it without anybody allowing it in System Settings first. Apple checks the
# disk image and everything in it, and the ticket it hands back is stapled to the disk image, so a
# Mac opening it has the answer with it.
if ((${#notary[@]})); then
  submission="$(mktemp)"
  # What Apple decided is read from its answer rather than notarytool's exit status, so that a
  # rejection can fetch the log that says why.
  xcrun notarytool submit "$dmg" "${notary[@]}" --wait --output-format json >"$submission" || true
  status="$(plutil -extract status raw -o - "$submission" 2>/dev/null || true)"
  if [[ "$status" != Accepted ]]; then
    echo "Apple didn't notarize $dmg (${status:-no answer}): $(cat "$submission")" >&2
    # Apple's log names each file it objected to, and why.
    id="$(plutil -extract id raw -o - "$submission" 2>/dev/null || true)"
    if [[ -n "$id" ]]; then xcrun notarytool log "$id" "${notary[@]}" >&2 || true; fi
    exit 1
  fi
  # The ticket can take a moment to be there to fetch after Apple accepts the submission.
  for attempt in 1 2 3; do
    xcrun stapler staple "$dmg" && break
    if ((attempt == 3)); then exit 1; fi
    sleep 15
  done
  # Gatekeeper's own verdict, which is what a Mac that downloaded it will go by.
  if ! check_inside spctl --assess --type execute --verbose=2; then
    echo "Gatekeeper rejects the app in $dmg, though Apple notarized it." >&2
    exit 1
  fi
fi
echo "Built $dmg ($(du -h "$dmg" | cut -f1))"
