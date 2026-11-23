#!/bin/sh
# Installs PortPilot for the current user: no root needed. Re-run to update; pass --uninstall to remove.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
prefix=${PREFIX:-$HOME/.local}
lib="$prefix/lib/portpilot"
bin="$prefix/bin"
share="${XDG_DATA_HOME:-$HOME/.local/share}"

if [ "${1:-}" = "--uninstall" ]; then
    rm -rf "$lib"
    rm -f "$bin/portpilot" "$bin/portpilot-desktop" "$share/applications/portpilot.desktop" "$share/icons/hicolor/512x512/apps/portpilot.png"
    echo "PortPilot removed."
    exit 0
fi

mkdir -p "$lib" "$bin" "$share/applications" "$share/icons/hicolor/512x512/apps"
cp "$here/portpilot" "$here/portpilot-desktop" "$here"/*.so "$lib/"
chmod 755 "$lib/portpilot" "$lib/portpilot-desktop"
ln -sf "$lib/portpilot" "$bin/portpilot"
ln -sf "$lib/portpilot-desktop" "$bin/portpilot-desktop"
cp "$here/portpilot.png" "$share/icons/hicolor/512x512/apps/portpilot.png"
sed "s|^Exec=.*|Exec=$lib/portpilot-desktop|" "$here/portpilot.desktop" > "$share/applications/portpilot.desktop"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$share/applications" >/dev/null 2>&1 || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q "$share/icons/hicolor" >/dev/null 2>&1 || true

echo "PortPilot installed. Open it from your app launcher, or run portpilot-desktop (GUI) or portpilot (terminal)."
case ":$PATH:" in *":$bin:"*) ;; *) echo "Note: $bin is not on your PATH." ;; esac
