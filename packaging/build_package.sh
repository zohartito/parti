#!/usr/bin/env bash
# Build Parti in Release and package the Grasshopper plugin folder.
#
# Outputs (all under packaging/out/, which is gitignored):
#   parti/                     staged plugin folder + manifest.yml (what yak packs)
#   parti-<ver>-<tag>.yak      Yak package, built with `yak build --platform any`
#   Parti-<ver>.zip            plain zip of a Parti/ folder for manual install
#
# Nothing is published or installed: this script never runs `yak push`,
# `yak install` or `yak login`.
#
# Env: YAK overrides the Yak CLI path (default: Rhino 8 for Mac). If no Yak CLI is
# found the .yak step is skipped and only the zip is produced.
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
out="$repo/packaging/out"
stage="$out/parti"
bin="$repo/src/Parti.Components/bin/Release/net7.0"
yak="${YAK:-/Applications/Rhino 8.app/Contents/Resources/bin/yak}"

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$repo/Directory.Build.props" | head -n 1)"
if [[ -z "$version" ]]; then
  echo "error: <Version> not found in Directory.Build.props" >&2
  exit 1
fi
echo "Parti version: $version"

# net7.0 targets build fine on a newer SDK; roll forward when only a newer runtime exists.
export DOTNET_ROLL_FORWARD="${DOTNET_ROLL_FORWARD:-Major}"
dotnet build "$repo/src/Parti.Components/Parti.Components.csproj" -c Release --nologo -v minimal

rm -rf "$out"
mkdir -p "$stage"

# Parti.gha plus every Parti.*.dll it references. Parti.dll is the same assembly as
# Parti.gha (post-build copy) and must not ship next to it. Newtonsoft.Json is not
# shipped: Rhino 8 provides 13.0.0.0.
cp "$bin/Parti.gha" "$stage/"
cp "$bin"/Parti.*.dll "$stage/"

# Every Parti assembly listed in Parti.deps.json must be in the stage.
missing=0
for dll in $(grep -o '"Parti\.[A-Za-z.]*\.dll"' "$bin/Parti.deps.json" | tr -d '"' | sort -u); do
  if [[ "$dll" == "Parti.dll" ]]; then continue; fi
  if [[ ! -f "$stage/$dll" ]]; then
    echo "error: $dll listed in Parti.deps.json but not staged" >&2
    missing=1
  fi
done
[[ $missing -eq 0 ]] || exit 1

cp "$repo/LICENSE" "$repo/COPYING.LESSER" "$repo/ATTRIBUTION.md" "$repo/docs/NOTICES.md" "$stage/"

# Plain zip: a Parti/ folder without manifest.yml, for the Grasshopper Libraries folder.
zip_root="$out/zip"
mkdir -p "$zip_root/Parti"
cp "$stage"/* "$zip_root/Parti/"
(cd "$zip_root" && zip -q -r -X "$out/Parti-$version.zip" Parti)
rm -rf "$zip_root"
echo "Created $out/Parti-$version.zip"

sed "s/^version: .*/version: $version/" "$repo/packaging/yak/manifest.yml" > "$stage/manifest.yml"

if [[ -x "$yak" ]]; then
  (cd "$stage" && "$yak" build --platform any)
  mv "$stage"/*.yak "$out/"
  echo "Created $(ls "$out"/*.yak)"
else
  echo "warning: Yak CLI not found at '$yak'; skipped .yak (set YAK=/path/to/yak)" >&2
fi
