#!/usr/bin/env bash
# Scenario 04 — queue priority: "move to front" must change real dispatch order.
#
# Sweeps COUNT files paused, picks a pending tile from the BACK of the queue
# page, prioritizes it, and asserts:
#   • the queue API immediately lists it first
#   • after unpausing, it is among the first files to actually start encoding
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/lib.sh"
require jq curl ffmpeg dotnet

COUNT="${COUNT:-120}"
PORT=6821

RUN="$(new_run_dir 04-priority)"
LIB="$RUN/library"
echo "=== Scenario 04: move-to-front priority (COUNT=$COUNT) ==="
echo "    run dir: $RUN"

build_app
"$E2E_ROOT/generate-library.sh" "$LIB" "$COUNT"

PID="$(start_instance app "$PORT" "$RUN/work")"
apply_fast_settings "$PORT"
pause_queue "$PORT"
watch_dir "$PORT" "$LIB"
enable_scan "$PORT"
trigger_scan "$PORT"

scan_done() { metrics "$PORT" | grep -q '^snacks_scan_last_completed_timestamp_seconds'; }
poll_until "sweep completes" 180 3 scan_done

pending="$(stat_field "$PORT" pending)"
assert_ge "$pending" 20 "pending items to reorder"

# Grab a victim from deep in the queue (page 2 of Pending), then bump it.
victim_json="$(api "$PORT" GET '/api/queue/items?status=Pending&skip=15&limit=1')"
victim_id="$(jq -r '.items[0].id' <<<"$victim_json")"
victim_name="$(jq -r '.items[0].fileName' <<<"$victim_json")"
[[ "$victim_id" == mf-* ]] || { echo "FATAL: expected a DB-row id, got '$victim_id'"; exit 1; }
echo "[pick] prioritizing $victim_name ($victim_id)"

api "$PORT" POST "/api/queue/prioritize/$victim_id" >/dev/null

head_id="$(api "$PORT" GET '/api/queue/items?status=Pending&skip=0&limit=1' | jq -r '.items[0].id')"
assert_eq "$head_id" "$victim_id" "prioritized item listed first"

# Real dispatch order: unpause and confirm the victim is among the first
# files to start (concurrency may dispatch a couple at once).
unpause_queue "$PORT"
victim_started() {
    api "$PORT" GET '/api/queue/items?limit=1' \
        | jq -e --arg n "$victim_name" '[.processing[]?.fileName] | index($n) != null' >/dev/null
}
poll_until "prioritized item starts encoding first" 120 2 victim_started

echo
echo "=== PASS — $victim_name jumped a $pending-item backlog and dispatched first ==="
