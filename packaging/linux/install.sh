#!/usr/bin/env bash
# Install Token Perch for the current user (no root needed).
#   ./install.sh                 from a checkout (builds first) or from an unpacked dist/linux-* folder
#   ./install.sh --no-autostart  don't start it at login
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
autostart=1
for a in "$@"; do [ "$a" = --no-autostart ] && autostart=0; done

if [ -x "$here/tokenperch" ]; then
    src="$here"                                   # unpacked release folder
else
    root="$(cd "$here/../.." && pwd)"             # packaging/linux in a checkout
    arch=$(uname -m); rid=linux-x64; [ "$arch" = aarch64 ] && rid=linux-arm64
    "$root/build.sh" "$rid"
    src="$root/dist/$rid"
fi

lib="$HOME/.local/lib/tokenperch"
pkill -x tokenperch 2>/dev/null && sleep 1 || true

# Token Perch was called Claude Tray before 3.0: remove that install (its settings are copied on first start).
pkill -x claude-tray 2>/dev/null && sleep 1 || true
rm -f "$HOME/.local/bin/claude-tray" "$HOME/.local/share/applications/claude-tray.desktop" \
      "$HOME/.config/autostart/claude-tray.desktop" "$HOME/.local/share/icons/hicolor/256x256/apps/claude-tray.png" \
      "$HOME/.local/lib/claude-tray/claude-tray"
rmdir "$HOME/.local/lib/claude-tray" 2>/dev/null || true
mkdir -p "$lib" "$HOME/.local/bin" "$HOME/.local/share/applications" "$HOME/.local/share/icons/hicolor/256x256/apps"
install -m755 "$src/tokenperch" "$lib/tokenperch"
ln -sf "$lib/tokenperch" "$HOME/.local/bin/tokenperch"
install -m644 "$src/tokenperch.png" "$HOME/.local/share/icons/hicolor/256x256/apps/tokenperch.png"

desktop="[Desktop Entry]
Type=Application
Name=Token Perch
Comment=Coding agent sessions, models and usage in the system tray
Exec=$lib/tokenperch
Icon=tokenperch
Terminal=false
Categories=Development;Utility;
StartupNotify=false"
printf '%s\n' "$desktop" > "$HOME/.local/share/applications/tokenperch.desktop"
if [ "$autostart" = 1 ]; then
    mkdir -p "$HOME/.config/autostart"
    printf '%s\nX-GNOME-Autostart-enabled=true\n' "$desktop" > "$HOME/.config/autostart/tokenperch.desktop"
fi
command -v update-desktop-database >/dev/null && update-desktop-database -q "$HOME/.local/share/applications" || true

setsid "$lib/tokenperch" >/dev/null 2>&1 < /dev/null &
echo "Installed to $lib and started. Settings: ~/.config/tokenperch/config.json"
