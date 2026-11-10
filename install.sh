#!/bin/sh
# NetWatch for Linux and macOS (experimental edition) - installer.
#
#   Install:    sh install.sh
#   Remove:     sh install.sh --uninstall
#
# It copies ONE file (netwatch.py) into your home folder and adds a "netwatch" command.
# Nothing is installed system-wide and nothing needs to be compiled.
#   program : ~/.local/share/netwatch/netwatch.py
#   command : ~/.local/bin/netwatch

set -eu

RAW="https://raw.githubusercontent.com/Mohenni/NetWatch/main/unix/netwatch.py"
APP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/netwatch"
BIN_DIR="$HOME/.local/bin"
APP="$APP_DIR/netwatch.py"
CMD="$BIN_DIR/netwatch"

if [ "${1:-}" = "--uninstall" ]; then
    rm -f "$APP" "$CMD"
    rmdir "$APP_DIR" 2>/dev/null || true
    echo "NetWatch was removed."
    exit 0
fi

OS="$(uname -s)"
if [ "$OS" != "Linux" ] && [ "$OS" != "Darwin" ] && [ -z "${NETWATCH_SKIP_OS_CHECK:-}" ]; then
    echo "This installer is for Linux and macOS. On Windows, download NetWatchSetup.exe from"
    echo "https://github.com/Mohenni/NetWatch/releases"
    exit 1
fi

PYTHON=""
for candidate in python3 python; do
    if command -v "$candidate" >/dev/null 2>&1 &&
       "$candidate" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 7) else 1)' 2>/dev/null; then
        PYTHON="$(command -v "$candidate")"
        break
    fi
done
if [ -z "$PYTHON" ]; then
    echo "NetWatch needs Python 3.7 or newer, which was not found."
    echo "  Debian/Ubuntu: sudo apt install python3      Fedora: sudo dnf install python3"
    echo "  macOS: install the Xcode command line tools with:  xcode-select --install"
    exit 1
fi
if [ "$OS" = "Linux" ] && ! command -v ss >/dev/null 2>&1; then
    echo "Note: the 'ss' tool is missing. Install the package 'iproute2' before starting NetWatch."
fi

mkdir -p "$APP_DIR" "$BIN_DIR"

# Use the copy next to this script when there is one (a downloaded or cloned project folder);
# otherwise fetch the single file from the project page.
HERE="$(cd "$(dirname "$0")" 2>/dev/null && pwd || echo .)"
if [ -f "$HERE/unix/netwatch.py" ]; then
    cp "$HERE/unix/netwatch.py" "$APP"
elif command -v curl >/dev/null 2>&1; then
    curl -fsSL "$RAW" -o "$APP"
elif command -v wget >/dev/null 2>&1; then
    wget -q "$RAW" -O "$APP"
else
    echo "Neither curl nor wget was found, so the program file could not be downloaded."
    exit 1
fi

# The command. On Linux, seeing the programs of ALL users needs administrator rights, so it asks
# for them with sudo (start it as "netwatch --no-sudo" to watch only your own programs).
cat > "$CMD" <<EOF
#!/bin/sh
if [ "\${1:-}" = "--no-sudo" ]; then
    shift
    exec "$PYTHON" "$APP" "\$@"
fi
if [ "\$(uname -s)" = "Linux" ] && [ "\$(id -u)" -ne 0 ] && command -v sudo >/dev/null 2>&1; then
    exec sudo "$PYTHON" "$APP" "\$@"
fi
exec "$PYTHON" "$APP" "\$@"
EOF
chmod +x "$CMD"

"$PYTHON" "$APP" --selftest >/dev/null && echo "NetWatch is installed." || echo "Installed, but its self-test failed. Please report this."
echo
echo "Start it with:   netwatch"
case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *) echo "(Your PATH does not contain $BIN_DIR yet. Start it with:  $CMD"
       echo " or add this line to your shell profile:  export PATH=\"\$HOME/.local/bin:\$PATH\")" ;;
esac
echo "Remove it with:  sh install.sh --uninstall"
