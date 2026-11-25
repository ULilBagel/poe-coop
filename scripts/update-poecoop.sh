#!/bin/sh
# Download the latest PoE Co-op release and install it over the current one.
set -eu

REPO="ULilBagel/poe-coop"
SCRIPT_DIR=$(CDPATH= cd "$(dirname "$0")" && pwd -P)

# The installer may replace this file while it runs, so the whole script is read
# into a function before anything executes.
main() {

usage() {
    cat <<'EOF'
Usage: update-poecoop.sh [GAME_DIR] [options]

Download the latest PoE Co-op release, check it against its published SHA-256
when the release has one, and run its installer. Saves, configuration and
unrelated plugins are kept; replaced files are backed up by the installer.

Options:
  --version TAG           Install this release (for example v0.9.0) instead of the latest
  --game-dir DIR          Pillars of Eternity installation directory
  --platform windows|linux
                          Passed to the installer
  -h, --help              Show this help

Run from the game folder, the game directory does not need to be given.
EOF
}

fail() {
    echo "PoE Co-op updater: $*" >&2
    exit 1
}

tag=
count=$#
while [ "$count" -gt 0 ]; do
    count=$((count - 1))
    arg=$1
    shift
    case "$arg" in
        -h|--help) usage; exit 0 ;;
        --version)
            [ "$count" -gt 0 ] || fail "--version needs a release tag"
            tag=$1
            shift
            count=$((count - 1))
            ;;
        *) set -- "$@" "$arg" ;;       # everything else goes to the installer
    esac
done

fetch() { # fetch URL FILE
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL --proto '=https' --tlsv1.2 -o "$2" "$1"
    elif command -v wget >/dev/null 2>&1; then
        wget -q --https-only -O "$2" "$1"
    else
        fail "curl or wget is required"
    fi
}
command -v unzip >/dev/null 2>&1 || fail "required command not found: unzip"

temp_dir=$(mktemp -d "${TMPDIR:-/tmp}/poecoop-update.XXXXXX")
trap 'rm -rf "$temp_dir"' EXIT HUP INT TERM

if [ -n "$tag" ]; then
    api="https://api.github.com/repos/$REPO/releases/tags/$tag"
else
    api="https://api.github.com/repos/$REPO/releases/latest"
fi
fetch "$api" "$temp_dir/release.json" || fail "could not read the release list from GitHub"

asset_url() { # asset_url PATTERN: first download URL whose file name matches
    tr ',' '\n' < "$temp_dir/release.json" |
        sed -n 's/.*"browser_download_url"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' |
        grep -E "/$1\$" | sed -n '1p'
}
zip_url=$(asset_url 'PoECoop-[0-9][0-9A-Za-z.+-]*\.zip')
sum_url=$(asset_url 'PoECoop-[0-9][0-9A-Za-z.+-]*\.zip\.sha256')
[ -n "$zip_url" ] || fail "the release has no PoECoop zip"
case "$zip_url" in
    "https://github.com/$REPO/releases/download/"*) ;;
    *) fail "unexpected download address: $zip_url" ;;
esac
name=${zip_url##*/}

echo "Downloading $name..."
fetch "$zip_url" "$temp_dir/$name" || fail "download failed: $zip_url"
if [ -n "$sum_url" ] && command -v sha256sum >/dev/null 2>&1; then
    fetch "$sum_url" "$temp_dir/$name.sha256" || fail "download failed: $sum_url"
    expected=$(sed -n '1s/[[:space:]].*//p' "$temp_dir/$name.sha256")
    actual=$(sha256sum "$temp_dir/$name" | sed 's/[[:space:]].*//')
    [ "$expected" = "$actual" ] || fail "$name does not match its published SHA-256; nothing was installed"
    echo "Checksum matches."
else
    echo "No published checksum to compare for this release; continuing."
fi

unzip -q "$temp_dir/$name" -d "$temp_dir/package"
installer="$temp_dir/package/PoECoop/install-poecoop.sh"
[ -f "$installer" ] || fail "the release has no installer"

# From inside the game folder, install there unless a directory was given.
here=$SCRIPT_DIR
given=0
for arg in "$@"; do
    case "$arg" in
        --platform|windows|linux) ;;
        *) given=1 ;;
    esac
done
if [ "$given" -eq 0 ] && [ -z "${GAME_DIR:-}" ] && [ -f "$here/PillarsOfEternity_Data/Managed/Assembly-CSharp.dll" ]; then
    set -- --game-dir "$here" "$@"
fi

sh "$installer" "$@"
echo ""
echo "Updated to $name. Every player in a session needs this same release."
}

main "$@"
