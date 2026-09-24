#!/usr/bin/env bash
# Puts Syncthing beside the application, so neither the host nor anybody on it has to install it
# to keep their computers in step. SyncthingHost looks there before it looks on the PATH.
#
# The version and every archive's SHA-256 are pinned here, taken from the release's
# sha256sum.txt.asc after checking its signature against Syncthing's release key
# (37C8 4554 E7E0 A261 E4F7  6E1E D26E 6ED0 0065 4A3E). A download that doesn't match is refused,
# so what ships is exactly what was checked, and moving to a new version is a reviewed change to
# this file rather than whatever the internet served that day.
#
#   scripts/fetch-syncthing.sh [output directory]
#
# GOOS and GOARCH choose the platform, as for build-tunnel.sh; they default to this machine's.
set -euo pipefail
cd -- "$(dirname -- "$0")/.."

syncthing_version="v2.1.5"
syncthing_sha256() {
  case "$1" in
    macos-arm64) echo 4a3153cce6f3bdc6290824e409b1622c3b78ad8b8a8b2fc846c0061b61e44d4a ;;
    macos-amd64) echo f4535e479472a1ae43d3e6ffc4bcfc7651179e948387c08f3696847d142b62c7 ;;
    linux-arm64) echo 3666f3069feeee3651e185f867759206059755101797bdd69ea5317610130855 ;;
    linux-amd64) echo 3d222b609f7ab2944e02748cb10488b4160d446b49e0eafc107ef2a525ab3486 ;;
    *) return 1 ;;
  esac
}

uncloud_out="${1:-src/Homebase.Server/bin/Debug/net10.0}"

goos="${GOOS:-$(uname -s | tr '[:upper:]' '[:lower:]')}"
goarch="${GOARCH:-$(uname -m)}"
case "$goos" in
  darwin) syncthing_os=macos; syncthing_ext=zip ;;
  linux) syncthing_os=linux; syncthing_ext=tar.gz ;;
  *) echo "Uncloud doesn't bundle Syncthing for $goos; install it and put it on the PATH." >&2; exit 1 ;;
esac
case "$goarch" in
  arm64|aarch64) syncthing_arch=arm64 ;;
  amd64|x86_64) syncthing_arch=amd64 ;;
  *) echo "Uncloud doesn't bundle Syncthing for $goarch; install it and put it on the PATH." >&2; exit 1 ;;
esac
platform="$syncthing_os-$syncthing_arch"
expected="$(syncthing_sha256 "$platform")"

mkdir -p -- "$uncloud_out"
uncloud_out="$(cd -- "$uncloud_out" && pwd)"
stamp="$uncloud_out/syncthing.version"
if [[ -x "$uncloud_out/syncthing" && -f "$stamp" && "$(cat "$stamp")" == "$syncthing_version $platform" ]]; then
  echo "Syncthing $syncthing_version ($platform) is already in $uncloud_out"
  exit 0
fi

archive_name="syncthing-$platform-$syncthing_version"
url="https://github.com/syncthing/syncthing/releases/download/$syncthing_version/$archive_name.$syncthing_ext"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

echo "Fetching Syncthing $syncthing_version for $platform"
curl -fsSL --retry 3 -o "$work/archive" "$url"
if command -v sha256sum >/dev/null 2>&1; then
  actual="$(sha256sum "$work/archive" | cut -d' ' -f1)"
else
  actual="$(shasum -a 256 "$work/archive" | cut -d' ' -f1)"
fi
if [[ "$actual" != "$expected" ]]; then
  echo "Refusing Syncthing from $url: its SHA-256 is $actual, not the pinned $expected." >&2
  exit 1
fi

if [[ "$syncthing_ext" == zip ]]; then
  unzip -q "$work/archive" -d "$work"
else
  tar -xzf "$work/archive" -C "$work"
fi
# Replaced rather than written over, so a running Syncthing keeps the file it started from.
install -m 0755 "$work/$archive_name/syncthing" "$uncloud_out/syncthing.new"
mv -f -- "$uncloud_out/syncthing.new" "$uncloud_out/syncthing"
# Syncthing is MPL-2.0; its licence travels with the copy of it Uncloud hands out.
install -m 0644 "$work/$archive_name/LICENSE.txt" "$uncloud_out/syncthing-LICENSE.txt"
echo "$syncthing_version $platform" > "$stamp"
echo "Put Syncthing $syncthing_version in $uncloud_out"
