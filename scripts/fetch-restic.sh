#!/usr/bin/env bash
# Puts restic beside the application, so a host can back up without anybody installing anything.
# Nothing in Uncloud runs it yet: it is carried for the backup feature that is on its way.
#
# The version and every binary's SHA-256 are pinned here, taken from the release's SHA256SUMS
# after checking SHA256SUMS.asc against restic's release key
# (CF8F 18F2 8445 7597 3F79  D4E1 91A6 868B D3F7 A907). The release ships no licence beside the
# binaries, so restic's LICENSE comes from the same tag, and its SHA-256 is pinned too: it is the
# LICENSE in the release's source tarball, which SHA256SUMS covers. A download that doesn't match
# is refused, so what ships is exactly what was checked, and moving to a new version is a reviewed
# change to this file rather than whatever the internet served that day. After moving it, run
# scripts/restic-notices.sh, which rewrites THIRD_PARTY_RESTIC.txt for what the new version links;
# CI fails until it has.
#
#   scripts/fetch-restic.sh [output directory]
#
# GOOS and GOARCH choose the platform, as for build-tunnel.sh; they default to this machine's.
set -euo pipefail
cd -- "$(dirname -- "$0")/.."

restic_version="0.19.1"
restic_sha256() {
  case "$1" in
    darwin-arm64) echo 7be0a144ccc377880f294204aa271d76e4b79554b42a751151d425ce6ebac143 ;;
    darwin-amd64) echo c38d579622cf602f665234c5a8c315030b6cf70656028fe6dc29a786b60e5f35 ;;
    linux-arm64) echo a5f64aaab53d51e311fa3829124c5b703f2d14cf187d8640b6be3b2b49376465 ;;
    linux-amd64) echo f415415624dcc452f2a02b8c33641791a8c6d6d3b65bbb3543fcf9a25151585c ;;
    *) return 1 ;;
  esac
}
restic_license_sha256=6f08a01a9fab5b24e139a09f15cc24a73087c7bc09e3bacf099fdf2d767bf897

uncloud_out="${1:-src/Homebase.Server/bin/Debug/net10.0}"

goos="${GOOS:-$(uname -s | tr '[:upper:]' '[:lower:]')}"
goarch="${GOARCH:-$(uname -m)}"
case "$goos" in
  darwin|linux) restic_os="$goos" ;;
  *) echo "Uncloud doesn't bundle restic for $goos." >&2; exit 1 ;;
esac
case "$goarch" in
  arm64|aarch64) restic_arch=arm64 ;;
  amd64|x86_64) restic_arch=amd64 ;;
  *) echo "Uncloud doesn't bundle restic for $goarch." >&2; exit 1 ;;
esac
platform="$restic_os-$restic_arch"
expected="$(restic_sha256 "$platform")"

mkdir -p -- "$uncloud_out"
uncloud_out="$(cd -- "$uncloud_out" && pwd)"
stamp="$uncloud_out/restic.version"
if [[ -x "$uncloud_out/restic" && -f "$stamp" && "$(cat "$stamp")" == "$restic_version $platform" ]]; then
  echo "restic $restic_version ($platform) is already in $uncloud_out"
  exit 0
fi

url="https://github.com/restic/restic/releases/download/v$restic_version/restic_${restic_version}_${restic_os}_${restic_arch}.bz2"
license_url="https://raw.githubusercontent.com/restic/restic/v$restic_version/LICENSE"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d' ' -f1
  else
    shasum -a 256 "$1" | cut -d' ' -f1
  fi
}

echo "Fetching restic $restic_version for $platform"
curl -fsSL --retry 3 -o "$work/restic.bz2" "$url"
actual="$(sha256 "$work/restic.bz2")"
if [[ "$actual" != "$expected" ]]; then
  echo "Refusing restic from $url: its SHA-256 is $actual, not the pinned $expected." >&2
  exit 1
fi
curl -fsSL --retry 3 -o "$work/LICENSE" "$license_url"
actual="$(sha256 "$work/LICENSE")"
if [[ "$actual" != "$restic_license_sha256" ]]; then
  echo "Refusing restic's licence from $license_url: its SHA-256 is $actual, not the pinned $restic_license_sha256." >&2
  exit 1
fi

# The release is the bare binary, compressed on its own.
bunzip2 -c "$work/restic.bz2" > "$work/restic"
# Replaced rather than written over, so a running restic keeps the file it started from.
install -m 0755 "$work/restic" "$uncloud_out/restic.new"
mv -f -- "$uncloud_out/restic.new" "$uncloud_out/restic"
# restic is BSD-2-Clause, which asks for its licence to go with every copy of the binary.
install -m 0644 "$work/LICENSE" "$uncloud_out/restic-LICENSE.txt"
echo "$restic_version $platform" > "$stamp"
echo "Put restic $restic_version in $uncloud_out"
