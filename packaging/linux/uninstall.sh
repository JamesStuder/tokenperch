#!/usr/bin/env bash
# Remove Token Perch (keeps ~/.config/tokenperch and the cache).
set -euo pipefail
pkill -x tokenperch 2>/dev/null || true
rm -rf "$HOME/.local/lib/tokenperch"
rm -f "$HOME/.local/bin/tokenperch" "$HOME/.local/share/applications/tokenperch.desktop" \
      "$HOME/.config/autostart/tokenperch.desktop" "$HOME/.local/share/icons/hicolor/256x256/apps/tokenperch.png"
echo "Removed. Settings are still in ~/.config/tokenperch."
