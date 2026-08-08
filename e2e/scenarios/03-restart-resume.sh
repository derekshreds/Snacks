#!/usr/bin/env bash
# Scenario 03 — restart resume: the DB-first queue's headline guarantee.
#
# Phase A (clean restart): sweep COUNT files with the queue paused, SIGTERM
# the app, restart against the same work dir, and assert:
#   • the instance is healthy again fast (no per-row restore replay)
#   • the pending count survives exactly — nothing lost, nothing duplicated
#
# Phase B (crash mid-encode): unpause, wait for an encode to be in flight,
# kill -9, restart, and assert the interrupted Processing row returned to the
# queue (RequeueOrphanedLocalProcessing) instead of being stranded.
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/lib.sh"
require jq curl ffmpeg dotnet sqlite3

COUNT="${COUNT:-300}"
MAX_RESTART_S="${MAX_RESTART_S:-15}"
PORT=6811

RUN="$(new_run_dir 03-restart)"
LIB="$RUN/library"
WORK="$RUN/work"
echo "=== Scenario 03: restart resume (COUNT=$COUNT) ==="
echo "    run dir: $RUN"

build_app
"$E2E_ROOT/generate-library.sh" "$LIB" "$COUNT"
source "$LIB/.manifest"

PID="$(start_instance app "$PORT" "$WORK")"
apply_fast_settings "$PORT"
pause_queue "$PORT"   # persisted — survives restarts, keeps Phase A deterministic
watch_dir "$PORT" "$LIB"
enable_scan "$PORT"
trigger_scan "$PORT"

scan_done() { metrics "$PORT" | grep -q '^snacks_scan_last_completed_timestamp_seconds'; }
poll_until "sweep completes" $(( 120 + COUNT / 10 )) 3 scan_done

before="$(stat_field "$PORT" pending)"
assert_ge "$before" 1 "pending before restart"
echo "[phase A] pending=$before — restarting (SIGTERM)…"

kill "$PID"
poll_until "process exited" 30 1 bash -c "! kill -0 $PID 2>/dev/null"

t0=$(date +%s)
PID="$(start_instance app "$PORT" "$WORK")"
t1=$(date +%s)
assert_le "$(( t1 - t0 ))" "$MAX_RESTART_S" "restart-to-healthy seconds"

after="$(stat_field "$PORT" pending)"
assert_eq "$after" "$before" "pending preserved across clean restart"

echo "[phase B] crash mid-encode…"
unpause_queue "$PORT"
encoding() { [[ "$(stat_field "$PORT" processing)" -ge 1 ]]; }
poll_until "an encode is in flight" 120 2 encoding

# Re-pause BEFORE the kill so the post-restart state is static (the pause is
# persisted) — otherwise the queue keeps draining during the settle window
# and the bookkeeping below races a live scheduler. The in-flight encode
# ignores the pause, so kill -9 still lands mid-encode.
pause_queue "$PORT"
kill -9 "$PID"
poll_until "process killed" 30 1 bash -c "! kill -0 $PID 2>/dev/null"

PID="$(start_instance app "$PORT" "$WORK")"

# The recovery guarantee, asserted against the database directly: no row may
# stay stranded in Processing (status 2) — crash recovery flips them back to
# Queued — and every originally-queued row must be in a coherent terminal or
# queued state (Queued=1, Completed=3, Failed=4, NoSavings=7; the synthetic
# music finishes as NoSavings, which is why output-counting was a lie here).
DB="$WORK/config/snacks.db"
no_stranded() { [[ "$(sqlite3 "$DB" 'SELECT COUNT(*) FROM MediaFiles WHERE Status=2;')" -eq 0 ]]; }
poll_until "no rows stranded in Processing after crash restart" 20 2 no_stranded

accounted="$(sqlite3 "$DB" 'SELECT COUNT(*) FROM MediaFiles WHERE Status IN (1,3,4,7);')"
assert_ge "$accounted" "$before" "every originally-queued row accounted for (queued/completed/failed/no-savings)"

echo
echo "=== PASS — clean restart preserved $before items in ${MAX_RESTART_S}s budget; crash recovery left zero stranded rows ==="
