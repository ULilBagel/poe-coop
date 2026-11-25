#!/bin/sh
# Start the Windows build of the game through Proton for co-op testing on one PC.
#
#   scripts/run-instance-proton.sh host  [game arguments]
#   scripts/run-instance-proton.sh guest [game arguments]
#
# Same contract as run-instance.sh, for an install that has only
# PillarsOfEternity.exe. Each role gets its own Wine prefix under
# .dev/<role>/proton, cloned once from the test prefix, so the two copies share
# no settings or saves with each other or with the real Steam prefix. Each
# role's "Saved Games/Pillars of Eternity" is a link to
# .dev/<role>/data/PillarsOfEternity/SavedGames, the folder the native
# launcher uses, so existing save fixtures keep working.
#
# Arguments that are absolute Linux paths are passed to the game as Z:\ paths.
# Set GAME_DIR, PROTON_DIR or COOP_PROTON_TEMPLATE to override the defaults.
set -e

role="$1"; shift || true
repo=$(cd "$(dirname "$0")/.." && pwd)
steam="$HOME/snap/steam/common/.local/share/Steam/steamapps/common"
game="${GAME_DIR:-$steam/Pillars of Eternity}"
proton="${PROTON_DIR:-$steam/Proton - Experimental}"
template="${COOP_PROTON_TEMPLATE:-$repo/.dev/proton-test-prefix/pfx}"
dev="$repo/.dev"

case "$role" in
    host)  set -- --coop-host --coop-name Host "$@" ;;
    guest) set -- --coop-join 127.0.0.1 --coop-name Guest "$@" ;;
    *)
        echo "usage: $0 host|guest [game arguments]" >&2
        exit 2
        ;;
esac

wine="$proton/files/lib/wine/x86_64-unix/wine"
[ -f "$game/PillarsOfEternity.exe" ] || { echo "no PillarsOfEternity.exe in $game" >&2; exit 1; }
[ -x "$wine" ] || { echo "no Proton wine at $wine (set PROTON_DIR)" >&2; exit 1; }

prefix="$dev/$role/proton"
if [ ! -d "$prefix/pfx" ]; then
    [ -d "$template" ] || { echo "no template prefix at $template (set COOP_PROTON_TEMPLATE)" >&2; exit 1; }
    mkdir -p "$prefix"
    cp -a --reflink=auto "$template" "$prefix/pfx.tmp"
    mv "$prefix/pfx.tmp" "$prefix/pfx"
fi

saves="$dev/$role/data/PillarsOfEternity/SavedGames"
link="$prefix/pfx/drive_c/users/steamuser/Saved Games/Pillars of Eternity"
mkdir -p "$saves" "$(dirname "$link")"
if [ ! -L "$link" ]; then
    rm -rf "$link"
    ln -s "$saves" "$link"
fi

# Unity refuses a second copy while boot.config has a single-instance line.
grep -v '^single-instance' "$game/PillarsOfEternity_Data/boot.config" > "$dev/boot.config"

winpath() { printf 'Z:%s' "$1" | tr / '\\'; }

# A copy that fills the screen stalls once the other copy takes focus, so run in
# a window smaller than the display
# unless the caller chose a screen mode.
case " $* " in
    *" -screen-fullscreen "*) ;;
    *) set -- -screen-fullscreen 0 -screen-width 960 -screen-height 540 "$@" ;;
esac

# Keep Player.log where the native build writes it, so test drivers find it.
case " $* " in
    *" -logFile "*) ;;
    *)
        logdir="$dev/$role/config/unity3d/Obsidian Entertainment/Pillars of Eternity"
        mkdir -p "$logdir"
        set -- -logFile "$logdir/Player.log" "$@"
        ;;
esac

set -- --doorstop-boot-config-override "$dev/boot.config" \
    --coop-multi-instance --coop-log "$dev/$role.log" "$@"
for arg in "$@"; do
    case "$arg" in
        /*) [ -d "$(dirname "$arg")" ] && arg=$(winpath "$arg") ;;
    esac
    set -- "$@" "$arg"
    shift
done

export WINEPREFIX="$prefix/pfx"
export WINEDLLOVERRIDES="winhttp=n,b"
export WINEDLLPATH="$proton/files/lib/vkd3d:$proton/files/lib/wine"
export LD_LIBRARY_PATH="$proton/files/lib/x86_64-linux-gnu:$proton/files/lib/i386-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export ORIG_LD_LIBRARY_PATH="$LD_LIBRARY_PATH"
export GST_PLUGIN_SYSTEM_PATH_1_0="$proton/files/lib/x86_64-linux-gnu/gstreamer-1.0:$proton/files/lib/i386-linux-gnu/gstreamer-1.0"
export WINE_GST_REGISTRY_DIR="$prefix/gstreamer-1.0/"
export WINEFSYNC=1
export WINE_LARGE_ADDRESS_AWARE=1
export WINELOADERNOEXEC=1
export __GLVND_DISALLOW_PATCHING=1
export WINEDEBUG="${WINEDEBUG:--all}"
export DISPLAY="${DISPLAY:-:0}"

cd "$game"
exec "$proton/files/lib/wine/x86_64-unix/wine-preloader" "$wine" ./PillarsOfEternity.exe "$@"
