#!/usr/bin/env bash
# Bundles ffmpeg/ffprobe's transitive dylib dependencies into the ffmpeg dir
# so the app is self-contained — end users do NOT need Homebrew.
#
# The Homebrew-built ffmpeg/ffprobe binaries link dynamically against dozens of
# dylibs under /opt/homebrew/... (libav*, libsw*, libx264, libx265, libvpx,
# libssl, ...). Those absolute paths do not exist on an end user's machine, so
# the bundled binary dies with "dyld: Library not loaded: /opt/homebrew/..." and
# ffmpeg exits with code 134. This script resolves every transitive non-system
# dylib, copies it next to the executables, and rewrites all references:
#   - inside the ffmpeg/ffprobe executables  -> @executable_path/<basename>
#   - inside the copied dylibs (deps + id)    -> @loader_path/<basename>
# then re-signs everything (install_name_tool invalidates ad-hoc signatures).
#
# Usage: bundle-ffmpeg-mac.sh <ffmpeg-dir>   (dir containing ffmpeg + ffprobe)
# Prereq (developer machine only): brew install ffmpeg
#
# Mirrors bundle-ocr-mac.sh — see that script for the dylib-relocation pattern.

set -euo pipefail

TARGET="${1:-}"
if [[ -z "$TARGET" ]]; then
    echo "Usage: $0 <ffmpeg-dir>" >&2
    exit 1
fi
if [[ ! -d "$TARGET" ]]; then
    echo "Target directory does not exist: $TARGET" >&2
    exit 1
fi

EXES=(ffmpeg ffprobe)
for e in "${EXES[@]}"; do
    if [[ ! -x "$TARGET/$e" ]]; then
        echo "Missing executable: $TARGET/$e" >&2
        exit 1
    fi
done

BUNDLED=" "  # space-padded basename list — bash 3.2 (macOS default) has no associative arrays

is_system_lib() {
    case "$1" in
        /usr/lib/*|/System/*|/Library/Apple/*) return 0 ;;
        *) return 1 ;;
    esac
}

# Resolve an otool dependency reference to a real source file on the build
# machine. Absolute paths are used as-is; @rpath/@loader_path/@executable_path
# references are probed against common Homebrew locations by basename.
resolve_src() {
    local dep="$1" depbase="$2"
    if [[ "$dep" == /* && -e "$dep" ]]; then
        echo "$dep"; return 0
    fi
    local guess
    for guess in \
        "/opt/homebrew/lib/$depbase" \
        "/opt/homebrew/opt/${depbase%%.*}/lib/$depbase"; do
        if [[ -e "$guess" ]]; then echo "$guess"; return 0; fi
    done
    # Last resort: glob the Cellar / opt trees for a matching basename.
    for guess in \
        /opt/homebrew/opt/*/lib/"$depbase" \
        /opt/homebrew/Cellar/*/*/lib/"$depbase"; do
        if [[ -e "$guess" ]]; then echo "$guess"; return 0; fi
    done
    echo ""; return 0
}

# Copy a dylib into TARGET, rewrite its id + non-system deps to @loader_path/*,
# and recurse into those deps. Idempotent per-basename.
bundle_lib() {
    local src="$1"
    local real
    real=$(python3 -c "import os,sys; print(os.path.realpath(sys.argv[1]))" "$src")
    local base
    base=$(basename "$src")
    if [[ "$BUNDLED" == *" $base "* ]]; then return 0; fi
    BUNDLED="$BUNDLED$base "

    cp -f "$real" "$TARGET/$base"
    chmod u+w "$TARGET/$base"
    codesign --remove-signature "$TARGET/$base" 2>/dev/null || true
    install_name_tool -id "@loader_path/$base" "$TARGET/$base"

    local line dep depbase depsrc
    while IFS= read -r line; do
        dep=$(echo "$line" | awk '{print $1}')
        [[ -z "$dep" || "$dep" == *":"* ]] && continue
        is_system_lib "$dep" && continue
        depbase=$(basename "$dep")
        [[ "$depbase" == "$base" ]] && continue
        depsrc=$(resolve_src "$dep" "$depbase")
        [[ -n "$depsrc" ]] && bundle_lib "$depsrc"
        install_name_tool -change "$dep" "@loader_path/$depbase" "$TARGET/$base"
    done < <(otool -L "$TARGET/$base" | tail -n +2)
}

# Rewrite an executable's non-system deps to @executable_path/* (the dylibs sit
# in the same directory as the executable) and bundle each dep.
process_exe() {
    local exe="$TARGET/$1"
    chmod u+w "$exe"
    codesign --remove-signature "$exe" 2>/dev/null || true

    local line dep depbase depsrc
    while IFS= read -r line; do
        dep=$(echo "$line" | awk '{print $1}')
        [[ -z "$dep" || "$dep" == *":"* ]] && continue
        is_system_lib "$dep" && continue
        depbase=$(basename "$dep")
        depsrc=$(resolve_src "$dep" "$depbase")
        [[ -n "$depsrc" ]] && bundle_lib "$depsrc"
        install_name_tool -change "$dep" "@executable_path/$depbase" "$exe"
    done < <(otool -L "$exe" | tail -n +2)
}

for e in "${EXES[@]}"; do
    process_exe "$e"
done

# Re-sign every modified dylib + executable (install_name_tool invalidated sigs).
codesign --force -s - "$TARGET"/*.dylib 2>/dev/null || true
for e in "${EXES[@]}"; do
    codesign --force -s - "$TARGET/$e" 2>/dev/null || true
done

# Verify no /opt/homebrew refs leaked through anywhere.
fail=0
for f in "$TARGET"/*.dylib "${EXES[@]/#/$TARGET/}"; do
    [[ -e "$f" ]] || continue
    if otool -L "$f" 2>/dev/null | grep -q '/opt/homebrew'; then
        echo "  WARNING: $f still references /opt/homebrew:" >&2
        otool -L "$f" | grep '/opt/homebrew' | head >&2
        fail=1
    fi
done

count=$(find "$TARGET" -maxdepth 1 -name '*.dylib' | wc -l | tr -d ' ')
echo "  bundled $count dylibs into $TARGET (ffmpeg/ffprobe deps relocated)"
if (( fail )); then
    echo "  (some files still reference Homebrew paths — they will fail on machines without Homebrew)" >&2
    exit 1
fi
