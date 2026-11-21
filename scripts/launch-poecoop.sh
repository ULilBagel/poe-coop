#!/bin/sh
set -eu

GAME_NAME="Pillars of Eternity"
STEAM_APP_ID=291650

usage() {
    cat <<'EOF'
Usage: launch-poecoop.sh [GAME_DIR] [--platform windows|linux] [game arguments]

Steam is the default. Proton uses WINEDLLOVERRIDES="winhttp=n,b"; Direct IP is available as a fallback.
Native Linux: starts the game through BepInEx directly and forwards game arguments.
Windows/Proton: asks Steam to launch the installed game; only -steam is accepted.

Options:
  --platform windows|linux
                          Override automatic game binary detection
  -h, --help              Show this help
EOF
}

fail() {
    echo "PoE Co-op launcher: $*" >&2
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
            break
            ;;
        *)
            if [ -z "$game_dir" ] && [ -d "$1" ]; then
                game_dir=$1
                shift
            else
                break
            fi
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
    else
        platform=linux
    fi
fi
[ "$platform" != windows ] || [ "$has_windows" -eq 1 ] || fail "PillarsOfEternity.exe was not found"
[ "$platform" != linux ] || [ "$has_linux" -eq 1 ] || fail "the native PillarsOfEternity binary was not found"

if [ "$platform" = windows ]; then
    if [ "$#" -gt 0 ] && { [ "$#" -ne 1 ] || [ "$1" != -steam ]; }; then
        fail "Windows/Proton accepts only the optional -steam argument"
    fi
    export WINEDLLOVERRIDES="winhttp=n,b${WINEDLLOVERRIDES:+;$WINEDLLOVERRIDES}"
    echo "Opening $GAME_NAME through Steam..."
    if command -v steam >/dev/null 2>&1; then
        exec steam -applaunch "$STEAM_APP_ID" "$@"
    elif command -v flatpak >/dev/null 2>&1; then
        exec flatpak run com.valvesoftware.Steam -applaunch "$STEAM_APP_ID" "$@"
    else
        fail "Steam was not found on PATH and Flatpak was not found; launch the game from your Steam library instead"
    fi
fi

[ -x "$game_dir/run_bepinex.sh" ] || fail "BepInEx is not installed; run install-poecoop.sh first"
cd "$game_dir"
exec ./run_bepinex.sh ./PillarsOfEternity "$@"
