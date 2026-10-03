# Third-party notices — Uncloud

Uncloud's original code is proprietary (see LICENSE). Third-party components retain
their original copyrights and licenses. This notice grants no rights in trademarks.

## Bundled Syncthing

The packaging script downloads unmodified Syncthing **v2.1.5**, licensed under
**MPL-2.0**, as a separate executable. Source code for that exact version is
available under MPL-2.0 at https://github.com/syncthing/syncthing/tree/v2.1.5
and https://github.com/syncthing/syncthing/archive/refs/tags/v2.1.5.tar.gz.
Recipients may obtain and modify that source under the MPL. The upstream license
is reproduced in THIRD_PARTY_LICENSES.txt, and fetch-syncthing.sh also retains
syncthing-LICENSE.txt alongside the binary. Any future modifications to covered
files must be made available under MPL-2.0. Preserve upstream third-party notices
and verify corresponding source availability for each release.

## Tunnel and Go dependencies

The tunnel uses **tailscale.com v1.102.4**, Copyright (c) 2020 Tailscale Inc & AUTHORS,
under BSD-3-Clause. Its license and upstream dependency manifest are included in
THIRD_PARTY_LICENSES.txt. The manifest describes upstream CLI/daemon builds; it is
not an exact list of packages linked into Uncloud's custom tsnet tunnel. go.mod
and go.sum pin the module graph. Review the licenses of packages actually linked
for every target, including nested forked packages, Go's runtime, and native code.
The full target-specific Go notice set remains a release review item. A module's
root license does not establish the license of all of its subpackages.

## .NET and desktop dependencies

The restored package inventory is THIRD_PARTY_NUGET_INVENTORY.json (32 distinct
packages across the four application projects). Microsoft.Data.Sqlite, Avalonia and
QRCoder are MIT; SQLitePCLRaw is Apache-2.0; SkiaSharp and HarfBuzzSharp include native
third-party code. Upstream license/notice files present in the exact cached NuGet
archives are reproduced in THIRD_PARTY_LICENSES.txt. Some archives supply only a
license expression; retain their copyright statements and obtain the full
applicable upstream notices before distribution. The self-contained .NET runtime,
Skia, HarfBuzz, platform native libraries and their embedded dependencies require
a release-specific notice review, beyond package-level metadata.

## Web interface and landing assets

THIRD_PARTY_INVENTORY.json records all 152 npm lockfile package entries, including
optional platforms and development dependencies. THIRD_PARTY_LICENSES.txt preserves
available exact-version license and notice texts. MIT/ISC/BSD attribution and
Apache license/NOTICE requirements continue to apply to distributed code.
**lightningcss 1.33.0** and its optional binaries use MPL-2.0, but are development
build tools in this lockfile; review if the tools themselves are distributed.

Platform SVGs: **Font Awesome Free 6.7.2**, Fonticons, Inc., **CC BY 4.0**.
Source: https://github.com/FortAwesome/Font-Awesome/tree/6.7.2/svgs/brands
License: https://creativecommons.org/licenses/by/4.0/
The original SVG attribution comments are retained. Preserve attribution and
identify changes when adapting these icons.

Comparison SVGs: **Simple Icons 13.12.0**, and OneDrive from **11.15.0**, **CC0 1.0**.
Sources: https://github.com/simple-icons/simple-icons/tree/13.12.0 and
https://github.com/simple-icons/simple-icons/tree/11.15.0
License: https://creativecommons.org/publicdomain/zero/1.0/
Colors are supplied by the stylesheet. These brand icons do not imply endorsement;
trademark rights remain separate. See landing/icons/README.md.

## Distribution and audit limits

package-macos-app.sh copies LICENSE and all root THIRD_PARTY_* files into the app's
Contents/Resources/Licenses before signing. Keep them with redistributed bundles.
This collection is an inventory and notice baseline, not a complete compliance
certification. Missing separate notice files are visible in the inventories;
check upstream README/file headers where a tarball has no standalone license.
Regenerate/review notices whenever dependencies change. No blanket GPL/AGPL grant
is being applied to Uncloud. Exact Go/native/runtime release coverage is pending.
