#!/usr/bin/env bash
#
# Downloads the MediaMTX binary into tools/bin.
#
# MediaMTX is the RTSP/WebRTC edge server ThePlayer supervises as a child process. It is not
# committed to the repository, so this script fetches the right build for this machine.
#
# The binary is deliberately downloaded by a script rather than by the application at startup:
# a network fetch during boot is fragile, hard to diagnose, and surprising in an air-gapped or
# container environment. MediaMtxSupervisor only ever looks for a binary that is already there.
#
# Usage:
#   ./tools/fetch-mediamtx.sh            # latest release
#   ./tools/fetch-mediamtx.sh v1.9.3     # a specific release

set -euo pipefail

REPOSITORY="bluenviron/mediamtx"
TOOLS_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_DIRECTORY="$TOOLS_ROOT/bin"
VERSION="${1:-latest}"

# MediaMTX names its assets by GOOS/GOARCH, which is not what uname reports.
case "$(uname -s)" in
    Linux)  OS="linux" ;;
    Darwin) OS="darwin" ;;
    *)      echo "Unsupported operating system: $(uname -s)" >&2; exit 1 ;;
esac

case "$(uname -m)" in
    x86_64|amd64)  ARCH="amd64" ;;
    aarch64|arm64) ARCH="arm64" ;;
    armv7l)        ARCH="armv7" ;;
    *)             echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

if [ "$VERSION" = "latest" ]; then
    echo "Looking up the latest MediaMTX release..."
    VERSION="$(curl -fsSL "https://api.github.com/repos/$REPOSITORY/releases/latest" \
        | grep -o '"tag_name": *"[^"]*"' \
        | head -1 \
        | sed 's/.*"tag_name": *"\([^"]*\)".*/\1/')"

    if [ -z "$VERSION" ]; then
        echo "Could not determine the latest release. Pass a version explicitly." >&2
        exit 1
    fi
fi

ASSET_NAME="mediamtx_${VERSION}_${OS}_${ARCH}.tar.gz"
DOWNLOAD_URL="https://github.com/$REPOSITORY/releases/download/$VERSION/$ASSET_NAME"

echo "Downloading $ASSET_NAME..."

mkdir -p "$BIN_DIRECTORY"
ARCHIVE_PATH="$(mktemp -t mediamtx.XXXXXX.tar.gz)"
trap 'rm -f "$ARCHIVE_PATH"' EXIT

curl -fsSL "$DOWNLOAD_URL" -o "$ARCHIVE_PATH"
tar -xzf "$ARCHIVE_PATH" -C "$BIN_DIRECTORY" mediamtx

chmod +x "$BIN_DIRECTORY/mediamtx"

# The bundled config is unused - ThePlayer generates its own at startup so that stream paths,
# and the camera credentials in them, are never written to a file on disk.
rm -f "$BIN_DIRECTORY/mediamtx.yml"

echo "MediaMTX $VERSION installed at $BIN_DIRECTORY/mediamtx"
