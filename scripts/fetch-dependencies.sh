#!/bin/sh
set -eu

repo=$(CDPATH= cd "$(dirname "$0")/.." && pwd -P)
mkdir -p "$repo/tools"
command -v curl >/dev/null 2>&1 || { echo "curl is required" >&2; exit 1; }

base="https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.6"
for platform in win linux; do
    archive="$repo/tools/BepInEx_${platform}_x64_5.4.23.6.zip"
    if [ -f "$archive" ]; then
        echo "Already present: $archive"
        continue
    fi
    url="$base/BepInEx_${platform}_x64_5.4.23.6.zip"
    echo "Downloading $url"
    curl -fL --retry 3 --output "$archive" "$url"
done

if command -v unzip >/dev/null 2>&1; then
    unzip -t "$repo/tools/BepInEx_win_x64_5.4.23.6.zip" >/dev/null
    unzip -t "$repo/tools/BepInEx_linux_x64_5.4.23.6.zip" >/dev/null
fi

echo "Dependencies ready in $repo/tools"
