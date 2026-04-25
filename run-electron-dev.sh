#!/usr/bin/env bash
# Snacks Desktop - Dev Mode (macOS)
# Publishes backend (osx-arm64) and launches Electron in development mode.
# Mirrors run-electron-dev.bat.

set -euo pipefail

cd "$(dirname "$0")"

# Some dev environments export ELECTRON_RUN_AS_NODE=1 (e.g., when other tools
# use Electron's bundled Node). When set, Electron skips Chromium entirely and
# main.js fails with "Cannot read properties of undefined (reading 'on')". Unset
# it for our own launch so the container actually opens as a GUI.
unset ELECTRON_RUN_AS_NODE

echo "Snacks Desktop - Dev Mode (macOS arm64)"
echo "========================================"
echo

# ---------------------------------------------------------------------------
# Step 1: Publish ASP.NET Core backend
# ---------------------------------------------------------------------------
echo "[1/4] Publishing ASP.NET Core backend..."
rm -rf electron-app/backend Snacks/bin/Release Snacks/obj/Release
dotnet publish Snacks/Snacks.csproj -c Release -r osx-arm64 --self-contained
mkdir -p electron-app/backend
cp -R Snacks/bin/Release/net10.0/osx-arm64/publish/. electron-app/backend/
chmod +x electron-app/backend/Snacks
echo "  bundling OCR native libs..."
bash electron-app/scripts/bundle-ocr-mac.sh electron-app/backend
echo "Backend published."
echo

# ---------------------------------------------------------------------------
# Step 2: Check for FFmpeg
# ---------------------------------------------------------------------------
echo "[2/4] Checking for FFmpeg..."
if [[ ! -x electron-app/ffmpeg/ffmpeg || ! -x electron-app/ffmpeg/ffprobe ]]; then
    echo "FFmpeg not found in electron-app/ffmpeg/"
    echo "  brew install ffmpeg && \\"
    echo "    cp \"\$(brew --prefix ffmpeg)/bin/ffmpeg\"  electron-app/ffmpeg/ && \\"
    echo "    cp \"\$(brew --prefix ffmpeg)/bin/ffprobe\" electron-app/ffmpeg/"
    exit 1
fi
echo "FFmpeg found."
echo

# ---------------------------------------------------------------------------
# Step 3: Install npm dependencies (only when missing)
# ---------------------------------------------------------------------------
echo "[3/4] Checking npm dependencies..."
if [[ ! -d electron-app/node_modules ]]; then
    ( cd electron-app && npm install )
else
    echo "node_modules present, skipping."
fi
echo

# ---------------------------------------------------------------------------
# Step 4: Launch Electron
# ---------------------------------------------------------------------------
# The dock title and menu-bar app name come from Electron.app's Info.plist —
# unpackaged dev mode would otherwise show "Electron". Patch the bundle's
# CFBundleName + CFBundleDisplayName so dev runs read "Snacks". This is idempotent;
# npm install recreates Electron.app, so the patch is reapplied each launch.
ELECTRON_PLIST=electron-app/node_modules/electron/dist/Electron.app/Contents/Info.plist
if [[ -f "$ELECTRON_PLIST" ]]; then
    /usr/libexec/PlistBuddy -c "Set :CFBundleName Snacks"        "$ELECTRON_PLIST" 2>/dev/null \
        || /usr/libexec/PlistBuddy -c "Add :CFBundleName string Snacks"        "$ELECTRON_PLIST"
    /usr/libexec/PlistBuddy -c "Set :CFBundleDisplayName Snacks" "$ELECTRON_PLIST" 2>/dev/null \
        || /usr/libexec/PlistBuddy -c "Add :CFBundleDisplayName string Snacks" "$ELECTRON_PLIST"
    # macOS caches LaunchServices info per bundle path — bump the modified time
    # so the dock picks up the renamed bundle on next launch.
    touch electron-app/node_modules/electron/dist/Electron.app
fi

echo "[4/4] Launching Electron..."
( cd electron-app && npx electron . )
