#!/bin/sh
set -eu

repo=$(CDPATH= cd "$(dirname "$0")/.." && pwd -P)
project="$repo/src/PoECoop/PoECoop.csproj"
version=$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$project" | sed -n '1p')
[ -n "$version" ] || { echo "Could not read the mod version from $project" >&2; exit 1; }

if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
    PATH="$HOME/.dotnet:$PATH"
    export PATH
fi
for command_name in dotnet zip; do
    command -v "$command_name" >/dev/null 2>&1 || {
        echo "Required command not found: $command_name" >&2
        exit 1
    }
done
for platform in win linux; do
    archive="$repo/tools/BepInEx_${platform}_x64_5.4.23.6.zip"
    [ -f "$archive" ] || { echo "Missing official BepInEx archive: $archive" >&2; exit 1; }
done

work=$(mktemp -d "${TMPDIR:-/tmp}/poecoop-release.XXXXXX")
trap 'rm -rf "$work"' EXIT HUP INT TERM

echo "Building PoE Co-op $version (Release)..."
# Redirect the project's post-build deploy step into this temporary directory.
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet build "$project" \
    --nologo -c Release -p:PluginDir="$work/deploy"
plugin="$repo/src/PoECoop/bin/Release/PoECoop.dll"
[ -f "$plugin" ] || { echo "Release build did not produce $plugin" >&2; exit 1; }

package="$work/PoECoop"
mkdir -p "$package/plugin" "$package/bepinex" "$repo/dist"
cp "$plugin" "$package/plugin/PoECoop.dll"
cp "$repo/tools/BepInEx_win_x64_5.4.23.6.zip" "$package/bepinex/"
cp "$repo/tools/BepInEx_linux_x64_5.4.23.6.zip" "$package/bepinex/"
cp "$repo/scripts/install-poecoop.sh" "$package/install-poecoop.sh"
cp "$repo/scripts/install-poecoop.bat" "$package/install-poecoop.bat"
cp "$repo/scripts/launch-poecoop.sh" "$package/launch-poecoop.sh"
cp "$repo/INSTALL.md" "$package/INSTALL.md"
chmod +x "$package/install-poecoop.sh" "$package/launch-poecoop.sh"

artifact="$repo/dist/PoECoop-$version.zip"
rm -f "$artifact"
(cd "$work" && zip -q -r "$artifact" PoECoop)
if command -v sha256sum >/dev/null 2>&1; then
    (cd "$repo/dist" && sha256sum "PoECoop-$version.zip" > "PoECoop-$version.zip.sha256")
fi

echo "Created $artifact"
