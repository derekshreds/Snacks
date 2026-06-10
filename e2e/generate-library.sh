#!/usr/bin/env bash
# Synthetic library generator.
#
#   ./generate-library.sh TARGET_DIR COUNT
#
# Builds a handful of real, probe-able, encode-able seed files with ffmpeg's
# lavfi test sources (a few seconds each), then hardlinks them out to COUNT
# uniquely-named paths in a Show/Season tree. Real files at library scale with
# near-zero disk cost — 100k entries is a few hundred MB of actual data.
#
# Seed mix (recorded in TARGET_DIR/.manifest for scenario assertions):
#   ~70%  h264 @ ~2.5 Mbps  → ENCODES under the harness settings (h265 @ 200k)
#   ~20%  hevc @ ~100 kbps  → SKIPS  (already target codec, under the ceiling)
#   ~10%  music (mp3/flac)  → music pipeline
#
# Seed mtimes are pushed into the past — the auto-scanner ignores files
# modified <30 minutes ago ("may still be transferring"), and hardlinks share
# the seed's inode mtime.
set -euo pipefail

TARGET="${1:?usage: generate-library.sh TARGET_DIR COUNT}"
COUNT="${2:?usage: generate-library.sh TARGET_DIR COUNT}"
FFMPEG="${FFMPEG_PATH:-ffmpeg}"

# Seeds live BESIDE the library, not inside it — the scanner walks every
# subdirectory of the watched tree (including dot-dirs), and seed files
# leaking into the scan throws off every manifest-based assertion. Same
# volume, so the hardlink fan-out still works.
SEED_DIR="${TARGET%/}.seeds"
mkdir -p "$SEED_DIR" "$TARGET"

gen_video() { # name vcodec bitrate
    local out="$SEED_DIR/$1" vcodec="$2" rate="$3"
    [[ -f "$out" ]] && return
    "$FFMPEG" -v error -f lavfi -i "testsrc2=duration=8:size=1280x720:rate=24" \
        -f lavfi -i "sine=frequency=440:duration=8" \
        -c:v "$vcodec" -b:v "$rate" -preset ultrafast \
        -c:a aac -b:a 96k -shortest -y "$out"
    touch -t 202001010000 "$out"
}

gen_music() { # name acodec
    local out="$SEED_DIR/$1" acodec="$2"
    [[ -f "$out" ]] && return
    "$FFMPEG" -v error -f lavfi -i "sine=frequency=330:duration=8" \
        -c:a "$acodec" -y "$out"
    touch -t 202001010000 "$out"
}

echo "[seeds] generating base files…"
gen_video "h264_a.mkv" libx264 2500k
gen_video "h264_b.mkv" libx264 2200k
gen_video "hevc_lo_a.mkv" libx265 100k
gen_video "hevc_lo_b.mkv" libx265 120k
gen_music "music_a.mp3" libmp3lame
gen_music "music_b.flac" flac

# link SEED REL_PATH — hardlink with copy fallback (cross-device, exotic FS).
link() {
    local seed="$1" rel="$2"
    local dst="$TARGET/$rel"
    mkdir -p "$(dirname "$dst")"
    ln "$SEED_DIR/$seed" "$dst" 2>/dev/null || cp "$SEED_DIR/$seed" "$dst"
}

encodable=0 skippable=0 music=0
echo "[library] fanning out $COUNT entries…"
for ((i = 0; i < COUNT; i++)); do
    # ~200 files per Season directory exercises the scan's chunked walk.
    show=$(( i / 2000 ))
    season=$(( (i / 200) % 10 ))
    bucket=$(( i % 10 ))

    if (( bucket < 7 )); then
        seed=$([[ $((i % 2)) == 0 ]] && echo h264_a.mkv || echo h264_b.mkv)
        link "$seed" "Show $show/Season $season/Show$show.S${season}E$(printf '%04d' "$i").mkv"
        encodable=$((encodable + 1))
    elif (( bucket < 9 )); then
        seed=$([[ $((i % 2)) == 0 ]] && echo hevc_lo_a.mkv || echo hevc_lo_b.mkv)
        link "$seed" "Show $show/Season $season/Show$show.S${season}E$(printf '%04d' "$i").mkv"
        skippable=$((skippable + 1))
    else
        if (( i % 2 == 0 )); then
            link "music_a.mp3" "Music/Artist $show/track-$(printf '%05d' "$i").mp3"
        else
            link "music_b.flac" "Music/Artist $show/track-$(printf '%05d' "$i").flac"
        fi
        music=$((music + 1))
    fi

    if (( i > 0 && i % 10000 == 0 )); then echo "  …$i"; fi
done

cat >"$TARGET/.manifest" <<EOF
total=$COUNT
encodable=$encodable
skippable=$skippable
music=$music
EOF

echo "[done] $COUNT entries ($encodable encodable video, $skippable skip-eligible video, $music music)"
echo "       manifest: $TARGET/.manifest"
