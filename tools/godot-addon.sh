#!/usr/bin/env bash
# Zips the Godot addon (tools/ManagedCodecs.Godot/addons/managedcodecs/) for the Godot Asset Library, which installs a
# zip's files at their paths in it: addons/managedcodecs/ at the zip's root installs as res://addons/managedcodecs/.
# The README's package reference is pinned to the version, so the addon asks for the packages it was released with.
# release.yml attaches the zip to each version's GitHub release; the Asset Library listing downloads it from there
# (tools/ManagedCodecs.Godot/listing/README.md).
#
# Usage: tools/godot-addon.sh <version> <output directory>   (writes managedcodecs-godot-<version>.zip there)
set -euo pipefail
cd "$(dirname "$0")/.."

version=${1:?version}
out=$(mkdir -p "${2:?output directory}" && cd "$2" && pwd)

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/addons"
cp -r tools/ManagedCodecs.Godot/addons/managedcodecs "$work/addons/"

readme="$work/addons/managedcodecs/README.md"
grep -q 'Version="\*"' "$readme" || { echo "The addon's README has no Version=\"*\" to pin." >&2; exit 1; }
sed -i "s/Version=\"\\*\"/Version=\"$version\"/" "$readme"

zip="$out/managedcodecs-godot-$version.zip"
rm -f "$zip"
# Fixed times and order, so the same sources give the same zip.
find "$work/addons" -exec touch -d '2000-01-01 00:00:00 UTC' {} +
(cd "$work" && find addons -type f | LC_ALL=C sort | zip -q -X -D "$zip" -@)
unzip -l "$zip"
