#!/usr/bin/env bash
# Writes THIRD_PARTY_RESTIC.txt, THIRD_PARTY_SYNCTHING.txt and THIRD_PARTY_TUNNEL.txt: the licence
# and notice files of everything compiled into each Go binary Uncloud carries, so they travel with
# every copy. package-macos-app.sh puts every THIRD_PARTY_* file in the app.
#
# A Go binary is one file with its modules linked in, several under licences (Apache-2.0 among
# them) that ask for their licence and NOTICE to go with the binary. Which modules are in it isn't
# guessed from a go.mod: Go records the exact list in every binary it builds, so this reads that
# list from the binaries themselves, built or fetched for every platform Uncloud carries them for,
# and takes each module's files from that exact version's source on proxy.golang.org, the Go module
# proxy. Where a module is replaced, the files come from the replacement, since that's the code
# compiled in. Go's own licence comes from the Go release the binaries were built with.
#
#   scripts/go-notices.sh restic|syncthing|tunnel    regenerate that one's file. Needs the network,
#                                                    and the tunnel needs Go, as build-tunnel.sh does.
#   scripts/go-notices.sh --check NAME BINARY        check BINARY links nothing NAME's file leaves out
#
# Run it after changing restic's or Syncthing's version, or the tunnel's go.mod. publish-macos.sh
# runs --check on everything it puts in the app, and CI on what it fetches and builds, so a binary
# that has moved on without new notices stops a pull request and a release.
set -euo pipefail
cd -- "$(dirname -- "$0")/.."

# Every platform these are fetched or built for, so a module linked into only one can't be missed.
platforms=(darwin/arm64 darwin/amd64 linux/arm64 linux/amd64)

notices_for() {
  case "$1" in
    restic) echo THIRD_PARTY_RESTIC.txt ;;
    syncthing) echo THIRD_PARTY_SYNCTHING.txt ;;
    tunnel) echo THIRD_PARTY_TUNNEL.txt ;;
    *) echo "There are notices for restic, syncthing and tunnel, not $1." >&2; return 1 ;;
  esac
}

# Go's build info, as `go version -m` prints it but without needing Go: a "go" line, then a "dep"
# line per linked module and a "=>" line after any that's replaced.
build_info() {
  python3 -I - "$1" <<'PY'
import sys

def varint(data, i):
    n = shift = 0
    while True:
        byte = data[i]
        i += 1
        n |= (byte & 0x7F) << shift
        shift += 7
        if byte < 0x80:
            return n, i

data = open(sys.argv[1], "rb").read()
at = data.find(b"\xff Go buildinf:")
if at < 0 or not data[at + 15] & 2:
    sys.exit(f"{sys.argv[1]} has no build info this can read")
i = at + 32
length, i = varint(data, i)
print("go\t" + data[i:i + length].decode())
i += length
length, i = varint(data, i)
modinfo = data[i:i + length]
# Go wraps the list in a 16-byte marker at each end; debug/buildinfo strips them the same way.
if len(modinfo) >= 33 and modinfo[-17] == ord("\n"):
    modinfo = modinfo[16:-16]
for line in modinfo.decode().splitlines():
    if line.startswith(("dep\t", "=>\t")):
        print(line)
PY
}

# The binary's list as the files record it: "go <version>", and "module <path> <version>", with
# " => <path> <version>" after a module that's replaced.
linked_in() {
  build_info "$1" | awk -F'\t' '
    $1 == "go" { print "go " $2 }
    $1 == "dep" { if (module) print module; module = "module " $2 " " $3 }
    $1 == "=>" { module = module " => " $2 " " $3 }
    END { if (module) print module }' | sort
}

# The list at the top of a notices file, before the first licence, so no licence text can be
# taken for it.
recorded_in() { awk '/^====/ { exit } /^(go|module) /' "$1" | sort; }

if [[ "${1:-}" == "--check" ]]; then
  name="${2:?Which notices? restic, syncthing or tunnel.}"
  binary="${3:?Which binary? Give its path.}"
  notices="$(notices_for "$name")"
  missing="$(comm -23 <(linked_in "$binary") <(recorded_in "$notices"))"
  if [[ -n "$missing" ]]; then
    echo "$binary links what $notices doesn't cover. Run scripts/go-notices.sh $name to regenerate it:" >&2
    echo "$missing" >&2
    exit 1
  fi
  echo "$notices covers everything $binary links"
  exit 0
fi

name="${1:?Which notices? restic, syncthing or tunnel.}"
notices="$(notices_for "$name")"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

for platform in "${platforms[@]}"; do
  out="$work/${platform/\//-}"
  case "$name" in
    restic)
      GOOS="${platform%/*}" GOARCH="${platform#*/}" ./scripts/fetch-restic.sh "$out" >/dev/null
      binary="$out/restic" ;;
    syncthing)
      GOOS="${platform%/*}" GOARCH="${platform#*/}" ./scripts/fetch-syncthing.sh "$out" >/dev/null
      binary="$out/syncthing" ;;
    tunnel)
      GOOS="${platform%/*}" GOARCH="${platform#*/}" ./scripts/build-tunnel.sh "$out" >/dev/null
      binary="$out/uncloud-tunnel" ;;
  esac
  linked_in "$binary" > "$out.list"
done

# One Go release builds them all, or the licence below would be the wrong one for some.
if [[ "$(grep -h '^go ' "$work"/*.list | sort -u | wc -l)" -ne 1 ]]; then
  echo "$name's builds weren't all made with the same Go: $(grep -h '^go ' "$work"/*.list | sort -u | tr '\n' ' ')" >&2
  exit 1
fi
go_version="$(grep -h -m1 '^go ' "$work/darwin-arm64.list" | cut -d' ' -f2)"
go_release="${go_version#go}"
sort -u "$work"/*.list > "$work/all"
if grep -E '^module .* => [^ ]+ ?$' "$work/all" >/dev/null; then
  echo "$name replaces a module with a local directory; take its licence by hand." >&2
  exit 1
fi

case "$name" in
  restic)
    what="restic $(cut -d' ' -f1 "$work/darwin-arm64/restic.version")"
    own="restic itself is BSD-2-Clause; its licence is in THIRD_PARTY_LICENSES.txt and beside the
binary as restic-LICENSE.txt." ;;
  syncthing)
    what="Syncthing $(cut -d' ' -f1 "$work/darwin-arm64/syncthing.version")"
    own="Syncthing itself is MPL-2.0; its licence is in THIRD_PARTY_LICENSES.txt and beside the
binary as syncthing-LICENSE.txt, and THIRD_PARTY_NOTICES.md says where its source is." ;;
  tunnel)
    what="the tunnel (tools/uncloud-tunnel)"
    own="The tunnel's own code is Uncloud's. It's built on tailscale.com, which is among the
modules below with the rest of what it links." ;;
esac

# The proxy spells capitals in module paths as "!" and the lower-case letter.
escape() { sed -E 's/([A-Z])/!\1/g' <<<"$1" | tr '[:upper:]' '[:lower:]'; }
rule() { printf '%s\n' "================================================================================"; }

{
  echo "Third-party licences and notices for $what, as carried by Uncloud"
  echo
  echo "$own"
  echo
  echo "Compiled in are Go $go_release's standard library and runtime and the modules below, as"
  echo "Go records them in the builds for darwin and linux on arm64 and amd64. Not every build"
  echo "links every module; this lists them all. Each one's licence, notice and patents files"
  echo "follow, from that exact version's source, or its replacement's where it's replaced."
  echo
  echo "Generated by scripts/go-notices.sh. Don't edit it by hand."
  echo
  cat "$work/all"
  echo
  rule
  echo "Go $go_release (standard library and runtime)"
  for file in LICENSE PATENTS; do
    curl -fsSL --retry 3 -o "$work/go-$file" "https://raw.githubusercontent.com/golang/go/$go_version/$file"
    rule
    echo "Go $go_release: $file"
    echo
    cat "$work/go-$file"
    echo
  done
} > "$work/notices"

missing=()
while read -r kind path version arrow replacement replacement_version; do
  [[ "$kind" == module ]] || continue
  source_path="$path" source_version="$version" as=""
  if [[ "${arrow:-}" == "=>" ]]; then
    source_path="$replacement" source_version="$replacement_version"
    as=" (as $replacement $replacement_version)"
  fi
  zip="$work/module.zip"
  curl -fsSL --retry 3 -o "$zip" "https://proxy.golang.org/$(escape "$source_path")/@v/$source_version.zip"
  # Licence and notice files anywhere in the module, since a subpackage can carry code under a
  # licence of its own; but not in test data, which isn't compiled in, and not source files that
  # merely have such a name (license.go, say).
  files=()
  while IFS= read -r entry; do files+=("$entry"); done < <(
    unzip -Z1 "$zip" \
      | grep -iE '/(licen[cs]e|copying|notice|patents)([._-][^/]*)?$' \
      | grep -viE '\.(go|s|c|h|cc|proto|json|ya?ml|html?|tmpl|sh|py)$' \
      | grep -vE '/testdata/' | sort)
  if [[ ${#files[@]} -eq 0 ]]; then missing+=("$source_path@$source_version"); continue; fi
  for entry in "${files[@]}"; do
    {
      rule
      echo "$path $version$as: ${entry#"$source_path@$source_version/"}"
      echo
      unzip -p "$zip" "$entry"
      echo
    } >> "$work/notices"
  done
done < "$work/all"

if [[ ${#missing[@]} -gt 0 ]]; then
  printf 'No licence file found in %s; look at it by hand.\n' "${missing[@]}" >&2
  exit 1
fi
mv -f -- "$work/notices" "$notices"
echo "Wrote $notices: Go $go_release and $(grep -c '^module ' "$notices") modules"
