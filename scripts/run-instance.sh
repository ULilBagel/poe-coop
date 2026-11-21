#!/bin/sh
# Start a copy of the game for co-op testing on one PC.
#
#   scripts/run-instance.sh host  [game arguments]
#   scripts/run-instance.sh guest [game arguments]
#
# Both may run at the same time. The guest gets its own settings and save
# folders under .dev/guest, so it cannot touch the real saves.
# Set GAME_DIR if the game is not in the Steam snap location.
# COOP_STEAM=1 adds -steam for Steam API/lobby smoke tests. In that mode,
# COOP_STEAM_NATIVE_DIR optionally prepends a matching Steam native library
# directory to LD_LIBRARY_PATH for this process only.
set -e

role="$1"; shift || true
repo=$(cd "$(dirname "$0")/.." && pwd)
game="${GAME_DIR:-$HOME/snap/steam/common/.local/share/Steam/steamapps/common/Pillars of Eternity}"
dev="$repo/.dev"
mkdir -p "$dev"

# Unity refuses a second copy while boot.config has a single-instance line.
grep -v '^single-instance' "$game/PillarsOfEternity_Data/boot.config" > "$dev/boot.config"

case "$role" in
    host)
        # COOP_ISOLATE_HOST=1 gives the host its own folders too (no access to real saves).
        if [ -n "$COOP_ISOLATE_HOST" ]; then
            mkdir -p "$dev/host/config" "$dev/host/data"
            export XDG_CONFIG_HOME="$dev/host/config" XDG_DATA_HOME="$dev/host/data"
        fi
        set -- --coop-host --coop-name Host "$@"
        ;;
    guest)
        mkdir -p "$dev/guest/config" "$dev/guest/data"
        export XDG_CONFIG_HOME="$dev/guest/config" XDG_DATA_HOME="$dev/guest/data"
        set -- --coop-join 127.0.0.1 --coop-name Guest "$@"
        ;;
    *)
        echo "usage: $0 host|guest [game arguments]" >&2
        exit 2
        ;;
esac

if [ "$COOP_STEAM" = "1" ]; then
    set -- -steam "$@"
    if [ -n "$COOP_STEAM_NATIVE_DIR" ]; then
        export LD_LIBRARY_PATH="$COOP_STEAM_NATIVE_DIR${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
    fi
fi

cd "$game"
exec ./run_bepinex.sh ./PillarsOfEternity \
    --doorstop-boot-config-override "$dev/boot.config" \
    --coop-multi-instance --coop-log "$dev/$role.log" "$@"
