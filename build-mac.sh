#!/usr/bin/env bash
# Snacks - Build macOS DMG (Apple Silicon / arm64)
# Mirrors build-installer.bat for Mac. Publishes the backend, stages ffmpeg,
# and runs electron-builder. Signing + notarization fire automatically when
# CSC_NAME / APPLE_ID / APPLE_APP_SPECIFIC_PASSWORD / APPLE_TEAM_ID are set
# (see electron-app/scripts/notarize.js); otherwise produces an unsigned DMG.

set -euo pipefail

cd "$(dirname "$0")"

# Some dev environments export ELECTRON_RUN_AS_NODE=1 (e.g., when other tools
# use Electron's bundled Node). It breaks both `electron .` dev launches and
# electron-builder's packaging steps. Always unset for our own builds.
unset ELECTRON_RUN_AS_NODE

echo
echo "  ==================================="
echo "   Snacks - Build macOS DMG (arm64)"
echo "  ==================================="
echo

# Optional local env file with signing/notarization secrets. Gitignored.
if [[ -f electron-app/.env.mac.local ]]; then
    echo "[env] Sourcing electron-app/.env.mac.local"
    # shellcheck disable=SC1091
    source electron-app/.env.mac.local
fi

if [[ -n "${CSC_NAME:-}" ]]; then
    echo "[sign] Identity: $CSC_NAME"
else
    echo "[sign] No CSC_NAME set — DMG will be unsigned (dev build)."
fi
if [[ -n "${APPLE_ID:-}" && -n "${APPLE_APP_SPECIFIC_PASSWORD:-}" && -n "${APPLE_TEAM_ID:-}" ]]; then
    echo "[notarize] Will submit to Apple notary service after signing."
else
    echo "[notarize] Skipping (APPLE_ID / APPLE_APP_SPECIFIC_PASSWORD / APPLE_TEAM_ID not all set)."
fi
echo

# ---------------------------------------------------------------------------
# Step 1: Clean
# ---------------------------------------------------------------------------
echo "[1/5] Cleaning previous build..."
rm -rf electron-app/backend electron-app/dist
echo "Done."
echo

# ---------------------------------------------------------------------------
# Step 2: Verify ffmpeg / ffprobe
# ---------------------------------------------------------------------------
echo "[2/5] Checking for FFmpeg..."
if [[ ! -x electron-app/ffmpeg/ffmpeg || ! -x electron-app/ffmpeg/ffprobe ]]; then
    cat <<'EOF'

  ffmpeg or ffprobe not found (or not executable) in electron-app/ffmpeg/

  Get arm64 macOS builds from one of:
    - https://evermeet.cx/ffmpeg/   (download the arm64 zips)
    - brew install ffmpeg          (then copy the binaries:
                                    cp "$(brew --prefix ffmpeg)/bin/ffmpeg"  electron-app/ffmpeg/
                                    cp "$(brew --prefix ffmpeg)/bin/ffprobe" electron-app/ffmpeg/)

  Then make them executable and clear quarantine:
    chmod +x electron-app/ffmpeg/ffmpeg electron-app/ffmpeg/ffprobe
    xattr -d com.apple.quarantine electron-app/ffmpeg/* 2>/dev/null || true

EOF
    exit 1
fi
# Best-effort unquarantine (no-op if already clean)
xattr -d com.apple.quarantine electron-app/ffmpeg/ffmpeg  2>/dev/null || true
xattr -d com.apple.quarantine electron-app/ffmpeg/ffprobe 2>/dev/null || true
chmod +x electron-app/ffmpeg/ffmpeg electron-app/ffmpeg/ffprobe
echo "FFmpeg found."
echo

# ---------------------------------------------------------------------------
# Step 3: Publish backend (osx-arm64, self-contained)
# ---------------------------------------------------------------------------
echo "[3/5] Publishing backend (self-contained, osx-arm64)..."
dotnet publish Snacks/Snacks.csproj -c Release -r osx-arm64 --self-contained
mkdir -p electron-app/backend
cp -R Snacks/bin/Release/net10.0/osx-arm64/publish/. electron-app/backend/
chmod +x electron-app/backend/Snacks
echo "  bundling OCR native libs (libtesseract + libleptonica + transitive deps)..."
bash electron-app/scripts/bundle-ocr-mac.sh electron-app/backend
echo "Backend published."
echo

# SkiaSharp 2.88.8 ships macOS arm64 natives in the base package, so nothing to do
# here for image rendering. If a future SDK bump drops them, libSkiaSharp.dylib will
# be missing from the publish output and the app will fail at runtime.
if ! ls electron-app/backend/libSkiaSharp*.dylib >/dev/null 2>&1; then
    echo "  WARNING: libSkiaSharp.dylib not present in publish output —"
    echo "           image rendering may fail. Add SkiaSharp.NativeAssets.macOS to Snacks.csproj."
    echo
fi

# ---------------------------------------------------------------------------
# Step 4: npm install
# ---------------------------------------------------------------------------
echo "[4/5] Installing npm dependencies..."
( cd electron-app && npm install )
echo "Dependencies installed."
echo

# ---------------------------------------------------------------------------
# Step 5: electron-builder
# ---------------------------------------------------------------------------
echo "[5/5] Building DMG..."
( cd electron-app && npx electron-builder --mac --arm64 )
echo

echo "  ==================================="
echo "   Build complete!"
echo "  ==================================="
echo
echo "  DMG location:"
ls -1 electron-app/dist/*.dmg 2>/dev/null | sed 's/^/    /'
echo
