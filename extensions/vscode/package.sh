#!/bin/sh
# Packages a platform-specific VSIX with the native terminal app inside.
#   ./package.sh <vsce-target> <path-to-portpilot-binary> [version]
# Targets: linux-x64 linux-arm64 darwin-arm64 darwin-x64 win32-x64
set -eu
cd "$(dirname "$0")"
target=$1; binary=$2; version=${3:-}
rm -rf bin && mkdir bin
case "$target" in win32-*) cp "$binary" bin/portpilot.exe ;; *) cp "$binary" bin/portpilot && chmod 755 bin/portpilot ;; esac
npm ci --silent
npx tsc -p .
if [ -n "$version" ]; then npm version --no-git-tag-version --allow-same-version "$version" >/dev/null; fi
npx vsce package --no-dependencies --target "$target" -o "portpilot-$target.vsix"
