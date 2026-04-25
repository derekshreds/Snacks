#!/usr/bin/env bash
# Bundles libtesseract + libleptonica + transitive deps into a target directory
# so the app is self-contained — end users do NOT need Homebrew.
#
# Resolves all transitive non-system dylibs, copies them into $TARGET, rewrites
# every absolute /opt/homebrew/... reference to @loader_path/<basename> via
# install_name_tool, then re-signs (install_name_tool invalidates ad-hoc sigs).
#
# Layout produced:
#   $TARGET/libtesseract.5.dylib          (with @loader_path/* deps)
#   $TARGET/libleptonica.6.dylib          (with @loader_path/* deps)
#   $TARGET/libpng16.16.dylib, libtiff.6.dylib, ... (transitive deps)
#   $TARGET/x64/libtesseract53.dll.dylib  (symlink — TesseractOCR's LibraryLoader
#   $TARGET/x64/libleptonica-1.83.1.dll.dylib   uses an "x64" subdir on every
#                                                non-Windows platform.)
#
# Usage: bundle-ocr-mac.sh <target-dir>
# Prereq (developer machine only): brew install tesseract leptonica
#
# At runtime the .NET host sets `LibraryLoader.Instance.CustomSearchPath` to
# AppContext.BaseDirectory (see Program.cs), so it picks up x64/lib*.dll.dylib
# from this layout.

set -euo pipefail

TARGET="${1:-}"
if [[ -z "$TARGET" ]]; then
    echo "Usage: $0 <target-dir>" >&2
    exit 1
fi
if [[ ! -d "$TARGET" ]]; then
    echo "Target directory does not exist: $TARGET" >&2
    exit 1
fi

# Source dylibs — fail loudly if brew prereqs are missing.
TESSERACT_SRC=/opt/homebrew/opt/tesseract/lib/libtesseract.5.dylib
LEPTONICA_SRC=/opt/homebrew/opt/leptonica/lib/libleptonica.6.dylib
for f in "$TESSERACT_SRC" "$LEPTONICA_SRC"; do
    if [[ ! -e $f ]]; then
        cat <<EOF >&2

  Missing required dylib: $f

  Install via Homebrew on your build machine:
      brew install tesseract leptonica

  (End users of the app do NOT need Homebrew — the dylibs get copied into
  the bundle by this script, with all transitive paths rewritten.)

EOF
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

bundle_lib() {
    local src="$1"
    # readlink -f isn't available on macOS by default
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

    # Walk the deps recorded inside this dylib, recurse for non-system, rewrite refs to @loader_path/<basename>.
    while IFS= read -r line; do
        local dep
        dep=$(echo "$line" | awk '{print $1}')
        [[ -z "$dep" || "$dep" == *":"* ]] && continue
        is_system_lib "$dep" && continue
        local depbase
        depbase=$(basename "$dep")
        # Skip self-references.
        [[ "$depbase" == "$base" ]] && continue
        # An @rpath or @loader_path reference is already non-absolute — for @rpath we need to
        # try to find the source on the build machine via brew so we can copy it in.
        local depsrc="$dep"
        if [[ "$dep" == "@rpath/"* || "$dep" == "@loader_path/"* ]]; then
            # Probe common Homebrew locations for a matching basename.
            for guess in \
                "/opt/homebrew/opt/webp/lib/$depbase" \
                "/opt/homebrew/lib/$depbase" \
                "/opt/homebrew/opt/${depbase%%.*}/lib/$depbase"; do
                if [[ -e "$guess" ]]; then depsrc="$guess"; break; fi
            done
        fi
        if [[ -e "$depsrc" ]]; then bundle_lib "$depsrc"; fi
        # Rewrite the reference inside this lib to @loader_path/<basename>.
        install_name_tool -change "$dep" "@loader_path/$depbase" "$TARGET/$base"
    done < <(otool -L "$TARGET/$base" | tail -n +2)
}

bundle_lib "$TESSERACT_SRC"
bundle_lib "$LEPTONICA_SRC"

# Re-sign every modified dylib (install_name_tool invalidated ad-hoc sigs).
codesign --force -s - "$TARGET"/lib*.dylib 2>/dev/null || true

# Drop the TesseractOCR-expected names into x64/ as relative symlinks.
# The .NET wrapper hardcodes the dll names "tesseract53.dll" and
# "leptonica-1.83.1.dll" and prefixes "lib" + suffixes ".dylib" before dlopen.
mkdir -p "$TARGET/x64"
ln -sf ../libtesseract.5.dylib  "$TARGET/x64/libtesseract53.dll.dylib"
ln -sf ../libleptonica.6.dylib  "$TARGET/x64/libleptonica-1.83.1.dll.dylib"

# Verify no /opt/homebrew refs leaked through on the libs we bundled.
# (We deliberately don't warn on @rpath here — .NET's own native dylibs like libcoreclr
# and libSkiaSharp use @rpath and resolve via the host binary's LC_RPATH; those are fine.)
fail=0
for f in "$TARGET"/lib*.dylib; do
    if otool -L "$f" 2>/dev/null | grep -q '/opt/homebrew'; then
        echo "  WARNING: $f still references /opt/homebrew:" >&2
        otool -L "$f" | grep '/opt/homebrew' | head >&2
        fail=1
    fi
done

count=$(find "$TARGET" -maxdepth 1 -name 'lib*.dylib' | wc -l | tr -d ' ')
echo "  bundled $count dylibs into $TARGET (+ x64/ shims for the wrapper)"
if (( fail )); then
    echo "  (some libs still reference Homebrew paths — they will fail on machines without Homebrew)" >&2
    exit 1
fi
