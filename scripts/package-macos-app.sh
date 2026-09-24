#!/usr/bin/env bash
# Builds Uncloud.app: the menu-bar app, with the Uncloud server, its Syncthing and its tunnel
# inside it. The same app is the host on one Mac and the way everybody else's Macs keep their
# files in step, so nobody installs anything else.
#
#   scripts/package-macos-app.sh [osx-arm64|osx-x64]
#
# On a Mac it also signs and zips the app. With UNCLOUD_SIGN_IDENTITY set to a "Developer ID
# Application: …" identity it signs for distribution, and with UNCLOUD_NOTARY_PROFILE set to a
# notarytool keychain profile it notarizes and staples too. Without them it signs ad hoc, which
# runs on the Mac that built it and needs right-click → Open anywhere else.
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
</dict>
</plist>
PLIST

if [[ "$(uname -s)" != Darwin ]]; then
  echo "Built $app. Signing, the icon and the zip need a Mac; run this there to finish." >&2
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
# An ad hoc signature ("-") can't carry a trusted timestamp; a Developer ID one must, to notarize.
timestamp=--timestamp=none
if [[ "$identity" != "-" ]]; then timestamp=--timestamp; fi
sign() { codesign --force "$timestamp" --options runtime --entitlements src/Uncloud.Desktop/Uncloud.entitlements -s "$identity" "$@"; }
# Inside out, or the bundle's seal is broken. codesign counts everything in Contents/MacOS as
# nested code — .NET's managed .dll files and the server's web pages included — so every file
# there is signed, except the app's own executable, which signing the bundle signs.
find "$app/Contents/MacOS" -type f ! -path "$app/Contents/MacOS/Uncloud" -print0 | while IFS= read -r -d '' file; do
  sign "$file"
done
sign "$app"
codesign --verify --deep --strict "$app"

zip="artifacts/Uncloud-$runtime.zip"
rm -f -- "$zip"
ditto -c -k --keepParent "$app" "$zip"

if [[ -n "${UNCLOUD_NOTARY_PROFILE:-}" && "$identity" != "-" ]]; then
  xcrun notarytool submit "$zip" --keychain-profile "$UNCLOUD_NOTARY_PROFILE" --wait
  xcrun stapler staple "$app"
  rm -f -- "$zip"
  ditto -c -k --keepParent "$app" "$zip"
fi
echo "Built $zip"
