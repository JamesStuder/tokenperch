#!/usr/bin/env bash
# Self-contained single-file builds into dist/<rid>/ (default: linux-x64 and win-x64).
#   ./build.sh                 both
#   ./build.sh linux-x64       one (also: linux-arm64, win-arm64)
set -euo pipefail
cd "$(dirname "$0")"
rids=("$@")
[ ${#rids[@]} -gt 0 ] || rids=(linux-x64 win-x64)
for rid in "${rids[@]}"; do
    rm -rf "dist/$rid"
    dotnet publish src/TokenPerch -c Release -r "$rid" --self-contained \
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "dist/$rid" --nologo -v quiet
    case "$rid" in
        win-*) cp packaging/windows/*.ps1 "dist/$rid/" ;;
        *)     cp packaging/linux/install.sh packaging/linux/uninstall.sh src/TokenPerch/Assets/tokenperch.png "dist/$rid/" ;;
    esac
    rm -f "dist/$rid"/*.pdb
    echo "dist/$rid: $(du -sh "dist/$rid" | cut -f1)"
done
