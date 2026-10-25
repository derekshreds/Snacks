#!/usr/bin/env bash
# Builds the FFmpeg that ships inside the macOS app and stages it in electron-app/ffmpeg.
#
# Homebrew's ffmpeg@8 bottle is built without libzimg and libvorbis, so the Mac app could
# not tone-map HDR to SDR (the zscale filter) or encode Ogg Vorbis music — both of which the
# Docker (jellyfin-ffmpeg) and Windows (gyan.dev "full") builds support. This builds the same
# FFmpeg release with Homebrew's exact ffmpeg@8 configure flags plus those two libraries,
# against the same Homebrew dependency libraries, then hands the result to
# bundle-ffmpeg-mac.sh so the app stays self-contained.
#
# Run it once per FFmpeg update (a few minutes on Apple silicon), then ./build-mac.sh.
#
# Usage: electron-app/scripts/build-ffmpeg-mac.sh

set -euo pipefail

# Keep in step with Homebrew's ffmpeg@8 formula (`brew cat ffmpeg@8`).
FFMPEG_VERSION="8.1.3"
FFMPEG_SHA256="7138d28c96d9d3e3af4ee3d8cad72741f8ffb40da90c1112235dea3ecd3178a3"

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
STAGE="$REPO_ROOT/electron-app/ffmpeg"
# Outside the repo on purpose: FFmpeg's makefiles break on a --prefix containing spaces.
WORK="${SNACKS_FFMPEG_BUILD_DIR:-$HOME/Library/Caches/snacks-ffmpeg-build}"
if [[ "$WORK" == *" "* ]]; then
    echo "Build directory '$WORK' contains a space, which FFmpeg's build can't handle." >&2
    echo "Set SNACKS_FFMPEG_BUILD_DIR to a path without spaces." >&2
    exit 1
fi

if ! command -v brew >/dev/null 2>&1; then
    echo "Homebrew is required: https://brew.sh" >&2
    exit 1
fi
if [[ "$(uname -m)" != "arm64" ]]; then
    echo "The Snacks Mac app targets Apple silicon; run this on an arm64 Mac." >&2
    exit 1
fi

# ffmpeg@8's dependencies (minus sdl2-compat, which only ffplay needs) plus zimg and libvorbis.
echo "[1/4] Installing build dependencies..."
brew install pkgconf dav1d lame libvmaf libvpx openssl@4 opus svt-av1 x264 x265 zimg libvorbis

echo "[2/4] Fetching FFmpeg $FFMPEG_VERSION..."
mkdir -p "$WORK"
TARBALL="$WORK/ffmpeg-$FFMPEG_VERSION.tar.xz"
if [[ ! -f "$TARBALL" ]]; then
    curl -fL --retry 3 -o "$TARBALL.part" "https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz"
    mv "$TARBALL.part" "$TARBALL"
fi
echo "$FFMPEG_SHA256  $TARBALL" | shasum -a 256 -c -

SRC="$WORK/ffmpeg-$FFMPEG_VERSION"
PREFIX="$WORK/install"
rm -rf "$SRC" "$PREFIX"
tar -xf "$TARBALL" -C "$WORK"

echo "[3/4] Building FFmpeg $FFMPEG_VERSION..."
# Homebrew's bottle also carries yadif_videotoolbox, whose shader needs Xcode's separately
# downloaded Metal Toolchain. Snacks never uses that filter, so its absence is only a warning.
if ! xcrun --sdk macosx metal -v >/dev/null 2>&1; then
    echo "WARNING: Xcode's Metal Toolchain is not installed, so the yadif_videotoolbox filter will be"
    echo "         left out (Snacks doesn't use it). To include it: xcodebuild -downloadComponent MetalToolchain"
fi
# openssl@4 is keg-only, so pkg-config has to be pointed at it explicitly.
export PKG_CONFIG_PATH="$(brew --prefix openssl@4)/lib/pkgconfig${PKG_CONFIG_PATH:+:$PKG_CONFIG_PATH}"
BREW_PREFIX="$(brew --prefix)"
(
    cd "$SRC"
    # Homebrew's build environment adds its include/lib paths itself (LAME ships no
    # pkg-config file) and hides undeclared packages. Outside it, pass the paths explicitly
    # and switch off the autodetected extras the bottle never sees (X11/XCB capture, SDL2
    # output, LZMA), so the result matches ffmpeg@8 apart from zimg and libvorbis.
    ./configure \
        --prefix="$PREFIX" \
        --extra-cflags="-I$BREW_PREFIX/include" \
        --extra-ldflags="-L$BREW_PREFIX/lib" \
        --disable-libxcb \
        --disable-xlib \
        --disable-sdl2 \
        --disable-lzma \
        --enable-shared \
        --enable-pthreads \
        --enable-version3 \
        --cc=clang \
        --disable-ffplay \
        --enable-gpl \
        --enable-libsvtav1 \
        --enable-libopus \
        --enable-libx264 \
        --enable-libmp3lame \
        --enable-libdav1d \
        --enable-libvmaf \
        --enable-libvpx \
        --enable-libx265 \
        --enable-openssl \
        --enable-videotoolbox \
        --enable-audiotoolbox \
        --enable-neon \
        --enable-libzimg \
        --enable-libvorbis
    make -j"$(sysctl -n hw.ncpu)"
    make install
)

echo "[4/4] Staging into electron-app/ffmpeg..."
mkdir -p "$STAGE"
find "$STAGE" -maxdepth 1 -type f \( -name ffmpeg -o -name ffprobe -o -name 'lib*.dylib' \) -delete
cp "$PREFIX/bin/ffmpeg" "$PREFIX/bin/ffprobe" "$STAGE/"
chmod +x "$STAGE/ffmpeg" "$STAGE/ffprobe"
bash "$REPO_ROOT/electron-app/scripts/bundle-ffmpeg-mac.sh" "$STAGE"

# Capture before reading: under pipefail, piping ffmpeg into `head` or `grep -q` fails when
# the reader exits early and ffmpeg dies of SIGPIPE.
VERSION_INFO="$("$STAGE/ffmpeg" -hide_banner -version 2>/dev/null || true)"
echo "${VERSION_INFO%%$'\n'*}"
FILTERS="$("$STAGE/ffmpeg" -hide_banner -filters 2>/dev/null || true)"
ENCODERS="$("$STAGE/ffmpeg" -hide_banner -encoders 2>/dev/null || true)"
grep -q ' zscale ' <<<"$FILTERS" || { echo "zscale missing from the build" >&2; exit 1; }
grep -q ' libvorbis ' <<<"$ENCODERS" || { echo "libvorbis missing from the build" >&2; exit 1; }
echo "Staged FFmpeg $FFMPEG_VERSION with zscale and libvorbis in electron-app/ffmpeg."
