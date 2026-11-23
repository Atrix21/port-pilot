#!/bin/sh
# Builds PortPilot.app from published output and zips it for release.
#   packaging/macos/make-app.sh <publish-dir> <version> <rid> <out-dir>
# The bundle holds the desktop app, the terminal app (portpilot) and the native libraries side by side.
set -eu

publish=$1; version=$2; rid=$3; out=$4
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
app="$out/PortPilot.app"

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$publish"/portpilot-desktop "$publish"/portpilot "$app/Contents/MacOS/"
find "$publish" -maxdepth 1 -name '*.dylib' -exec cp {} "$app/Contents/MacOS/" \;
sed "s/__VERSION__/$version/g" "$here/Info.plist" > "$app/Contents/Info.plist"

# App icon from the 1024 px master.
iconset=$(mktemp -d)/PortPilot.iconset
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
    sips -z $size $size "$root/docs/PortPilot-1024.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z $double $double "$root/docs/PortPilot-1024.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/PortPilot.icns"

# Ad-hoc signature: Apple silicon refuses to run unsigned code. (Not notarised; see README.)
codesign --force --deep --sign - "$app"

ditto -c -k --keepParent "$app" "$out/PortPilot-$rid.zip"
echo "Built $out/PortPilot-$rid.zip"
