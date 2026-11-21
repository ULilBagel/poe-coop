#!/bin/sh
set -eu

GAME_NAME="Pillars of Eternity"
STEAM_APP_ID=291650

usage() {
    cat <<'EOF'
Usage: install-poecoop.sh [GAME_DIR] [options]

Install PoE Co-op and the matching BepInEx build. Existing files are backed up
only when they need replacing; existing configuration and saves are preserved.

Options:
  --game-dir DIR          Pillars of Eternity installation directory
  --platform windows|linux
                          Override automatic game binary detection
  -h, --help              Show this help

Usually no options are needed when Steam is installed in a standard location.
EOF
}

fail() {
    echo "PoE Co-op installer: $*" >&2
    exit 1
}

is_game_dir() {
    [ -f "$1/PillarsOfEternity_Data/Managed/Assembly-CSharp.dll" ] || return 1
    [ -f "$1/PillarsOfEternity.exe" ] || [ -f "$1/PillarsOfEternity" ]
}

find_game_dir() {
    for candidate in \
        "${GAME_DIR:-}" \
        "$HOME/.local/share/Steam/steamapps/common/$GAME_NAME" \
        "$HOME/.steam/steam/steamapps/common/$GAME_NAME" \
        "$HOME/.steam/root/steamapps/common/$GAME_NAME" \
        "$HOME/snap/steam/common/.local/share/Steam/steamapps/common/$GAME_NAME" \
        "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/$GAME_NAME"
    do
        if [ -n "$candidate" ] && is_game_dir "$candidate"; then
            (cd "$candidate" && pwd -P)
            return 0
        fi
    done
    return 1
}

platform=auto
game_dir=
while [ "$#" -gt 0 ]; do
    case "$1" in
        --game-dir)
            [ "$#" -ge 2 ] || fail "--game-dir needs a directory"
            game_dir=$2
            shift 2
            ;;
        --platform)
            [ "$#" -ge 2 ] || fail "--platform needs windows or linux"
            platform=$2
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        --)
            shift
            [ "$#" -eq 0 ] || fail "unexpected arguments: $*"
            ;;
        -*)
            fail "unknown option: $1 (use --help)"
            ;;
        *)
            [ -z "$game_dir" ] || fail "game directory was supplied more than once"
            game_dir=$1
            shift
            ;;
    esac
done

case "$platform" in
    auto|windows|linux) ;;
    *) fail "--platform must be windows or linux" ;;
esac

if [ -z "$game_dir" ]; then
    game_dir=$(find_game_dir) || fail "could not find $GAME_NAME; pass its directory as the first argument"
fi
is_game_dir "$game_dir" || fail "not a $GAME_NAME directory: $game_dir"
game_dir=$(cd "$game_dir" && pwd -P)

has_windows=0
has_linux=0
[ -f "$game_dir/PillarsOfEternity.exe" ] && has_windows=1
[ -f "$game_dir/PillarsOfEternity" ] && has_linux=1
if [ "$platform" = auto ]; then
    if [ "$has_windows" -eq 1 ] && [ "$has_linux" -eq 1 ]; then
        fail "both Windows and Linux binaries found; use --platform windows or --platform linux"
    elif [ "$has_windows" -eq 1 ]; then
        platform=windows
    elif [ "$has_linux" -eq 1 ]; then
        platform=linux
    else
        fail "could not find a Pillars of Eternity game binary"
    fi
fi
[ "$platform" != windows ] || [ "$has_windows" -eq 1 ] || fail "PillarsOfEternity.exe was not found"
[ "$platform" != linux ] || [ "$has_linux" -eq 1 ] || fail "the native PillarsOfEternity binary was not found"

script_dir=$(CDPATH= cd "$(dirname "$0")" && pwd -P)
if [ -d "$script_dir/bepinex" ]; then
    package_root=$script_dir
else
    package_root=$(cd "$script_dir/.." && pwd -P)
fi
archive_platform=$platform
[ "$platform" != windows ] || archive_platform=win
if [ -f "$package_root/bepinex/BepInEx_${archive_platform}_x64_5.4.23.6.zip" ]; then
    bepinex_zip="$package_root/bepinex/BepInEx_${archive_platform}_x64_5.4.23.6.zip"
else
    bepinex_zip="$package_root/tools/BepInEx_${archive_platform}_x64_5.4.23.6.zip"
fi
[ -f "$bepinex_zip" ] || fail "missing official BepInEx archive: $bepinex_zip"

if [ -f "$package_root/plugin/PoECoop.dll" ]; then
    plugin_dll="$package_root/plugin/PoECoop.dll"
else
    plugin_dll="$package_root/src/PoECoop/bin/Release/PoECoop.dll"
fi
[ -f "$plugin_dll" ] || fail "missing plugin/PoECoop.dll; build a release package first"

for command_name in unzip cmp mktemp; do
    command -v "$command_name" >/dev/null 2>&1 || fail "required command not found: $command_name"
done

temp_dir=$(mktemp -d "${TMPDIR:-/tmp}/poecoop-install.XXXXXX")
trap 'rm -rf "$temp_dir"' EXIT HUP INT TERM
extract_dir="$temp_dir/bepinex"
mkdir -p "$extract_dir"
unzip -q "$bepinex_zip" -d "$extract_dir"

backup_dir=
make_backup_dir() {
    if [ -z "$backup_dir" ]; then
        backup_dir="$game_dir/.poecoop-backups/$(date +%Y%m%d-%H%M%S)-$$"
        mkdir -p "$backup_dir"
    fi
}

backup_file() {
    backup_relative=$1
    backup_source=$2
    make_backup_dir
    mkdir -p "$(dirname "$backup_dir/$backup_relative")"
    cp -p "$backup_source" "$backup_dir/$backup_relative"
}

install_file() {
    relative=$1
    source_file=$2
    destination="$game_dir/$relative"
    if [ -f "$destination" ] && cmp -s "$source_file" "$destination"; then
        return 0
    fi
    [ ! -e "$destination" ] || [ -f "$destination" ] || fail "refusing to replace non-file: $destination"
    mkdir -p "$(dirname "$destination")"
    if [ -f "$destination" ]; then
        backup_file "$relative" "$destination"
    fi
    cp "$source_file" "$destination"
    echo "Installed $relative"
}

# doorstop_config.ini is user configuration: retain it on upgrades.
find "$extract_dir" -type f > "$temp_dir/bepinex-files"
while IFS= read -r source_file; do
    relative=${source_file#"$extract_dir"/}
    case "$relative" in
        changelog.txt) continue ;;
        doorstop_config.ini)
            [ ! -e "$game_dir/$relative" ] || continue
            ;;
    esac
    install_file "$relative" "$source_file"
done < "$temp_dir/bepinex-files"

if [ "$platform" = linux ]; then
    chmod +x "$game_dir/run_bepinex.sh"
fi

install_file "BepInEx/plugins/PoECoop/PoECoop.dll" "$plugin_dll"

default_config="$game_dir/BepInEx/config/poecoop.cfg"
if [ ! -e "$default_config" ]; then
    mkdir -p "$(dirname "$default_config")"
    cat > "$default_config" <<'EOF'
## PoE Co-op defaults. Existing settings are never replaced by the installer.
[Connection]
PreferredTransport = Steam
EOF
    echo "Installed Steam default configuration"
fi

echo ""
echo "PoE Co-op installed for $platform in: $game_dir"
if [ -n "$backup_dir" ]; then
    echo "Replaced files were backed up in: $backup_dir"
fi
if [ "$platform" = windows ]; then
    echo "For Proton, add this Steam launch option before playing:"
    echo 'WINEDLLOVERRIDES="winhttp=n,b" %command%'
    echo "For Steam mode, append -steam to that launch option."
else
    if [ -f "$package_root/launch-poecoop.sh" ]; then
        launcher="$package_root/launch-poecoop.sh"
    else
        launcher="$package_root/scripts/launch-poecoop.sh"
    fi
    echo "Launch with: $launcher \"$game_dir\""
fi
