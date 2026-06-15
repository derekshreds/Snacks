#!/usr/bin/env bash
# Samples a process's RSS (plus queue counters when a port is given) to CSV.
#
#   ./watch-memory.sh PID OUT_CSV [PORT] [INTERVAL_S]
#
# Runs until the PID exits or it is killed. Scenario scripts launch it in the
# background and analyze the CSV afterwards.
set -euo pipefail

PID="${1:?usage: watch-memory.sh PID OUT_CSV [PORT] [INTERVAL_S]}"
OUT="${2:?usage: watch-memory.sh PID OUT_CSV [PORT] [INTERVAL_S]}"
PORT="${3:-}"
INTERVAL="${4:-2}"

echo "epoch,rss_kb,pending,processing,completed" >"$OUT"

while kill -0 "$PID" 2>/dev/null; do
    rss="$(ps -o rss= -p "$PID" 2>/dev/null | tr -d ' ' || echo 0)"
    pending="" processing="" completed=""
    if [[ -n "$PORT" ]]; then
        if json="$(curl -fsS --max-time 2 "http://127.0.0.1:$PORT/api/queue/stats" 2>/dev/null)"; then
            pending="$(jq -r '.pending' <<<"$json")"
            processing="$(jq -r '.processing' <<<"$json")"
            completed="$(jq -r '.completed' <<<"$json")"
        fi
    fi
    echo "$(date +%s),${rss:-0},$pending,$processing,$completed" >>"$OUT"
    sleep "$INTERVAL"
done
