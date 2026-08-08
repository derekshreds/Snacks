#!/usr/bin/env bash
# Shared helpers for the Snacks E2E harness. Source this from scenario scripts.
#
# Every instance is fully isolated via environment:
#   SNACKS_WORK_DIR        — per-instance state (config/, snacks.db, uploads/, logs/)
#   ASPNETCORE_URLS        — per-instance port
#   SNACKS_ALLOW_ALL_PATHS — lets tests point at an arbitrary synthetic library
set -euo pipefail

E2E_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$E2E_ROOT/.." && pwd)"
BUILD_DIR="$E2E_ROOT/.build"
RUNS_DIR="$E2E_ROOT/.runs"

# PIDs started via start_instance — killed by the EXIT trap installed below.
E2E_PIDS=()

require() {
    for tool in "$@"; do
        command -v "$tool" >/dev/null || { echo "FATAL: '$tool' is required (try: brew install $tool)"; exit 1; }
    done
}

# Publishes the app once into e2e/.build. Set SNACKS_E2E_REBUILD=1 to force.
build_app() {
    if [[ -f "$BUILD_DIR/Snacks.dll" && "${SNACKS_E2E_REBUILD:-0}" != "1" ]]; then
        echo "[build] reusing $BUILD_DIR (SNACKS_E2E_REBUILD=1 to force)"
        return
    fi
    echo "[build] publishing Snacks → $BUILD_DIR"
    dotnet publish "$REPO_ROOT/Snacks/Snacks.csproj" -c Release -o "$BUILD_DIR" -v q
}

# start_instance NAME PORT WORKDIR — launches one app instance, waits for
# health, appends the PID to E2E_PIDS, and echoes it on stdout.
start_instance() {
    local name="$1" port="$2" workdir="$3"
    mkdir -p "$workdir"
    (
        cd "$BUILD_DIR"
        # 0.0.0.0, not 127.0.0.1: cluster nodes advertise the machine's LAN IP
        # to the master, so loopback-only binding makes every instance
        # unreachable at the address it registers under.
        SNACKS_WORK_DIR="$workdir" \
        ASPNETCORE_URLS="http://0.0.0.0:$port" \
        SNACKS_ALLOW_ALL_PATHS="true" \
        ASPNETCORE_ENVIRONMENT="Production" \
        nohup dotnet "$BUILD_DIR/Snacks.dll" >"$workdir/app.log" 2>&1 &
        echo $! >"$workdir/app.pid"
    )
    local pid
    pid="$(cat "$workdir/app.pid")"
    E2E_PIDS+=("$pid")
    wait_health "$port" 60 "$name"
    echo "$pid"
}

wait_health() {
    local port="$1" timeout="${2:-60}" name="${3:-instance}"
    local waited=0
    until curl -fsS "http://127.0.0.1:$port/api/health" >/dev/null 2>&1; do
        sleep 1
        waited=$((waited + 1))
        if (( waited >= timeout )); then
            echo "FATAL: $name on :$port not healthy after ${timeout}s"
            exit 1
        fi
    done
    echo "[up] $name healthy on :$port (${waited}s)" >&2
}

# api PORT METHOD PATH [JSON_BODY] — curl wrapper that fails the scenario on HTTP errors.
api() {
    local port="$1" method="$2" path="$3" body="${4:-}"
    if [[ -n "$body" ]]; then
        curl -fsS -X "$method" "http://127.0.0.1:$port$path" \
            -H 'Content-Type: application/json' -d "$body"
    else
        curl -fsS -X "$method" "http://127.0.0.1:$port$path"
    fi
}

stats()        { api "$1" GET /api/queue/stats; }
stat_field()   { stats "$1" | jq -r ".$2"; }
metrics()      { curl -fsS "http://127.0.0.1:$1/metrics"; }

# Encoder settings tuned for synthetic clips: software-only (machine-agnostic),
# fast preset, tiny target so every h264 seed qualifies for an encode and the
# 15s bitrate-measurement pre-pass is skipped (bitrate > 2× target).
apply_fast_settings() {
    local port="$1"
    api "$port" POST /api/settings '{
        "Format": "mkv", "Codec": "h265", "Encoder": "libx265",
        "TargetBitrate": 200, "HardwareAcceleration": "none",
        "FfmpegQualityPreset": "ultrafast", "SkipPercentAboveTarget": 20,
        "RetryOnFail": false, "DeleteOriginalFile": false
    }' >/dev/null
}

pause_queue()   { api "$1" POST /api/queue/paused '{"paused": true}'  >/dev/null; }
unpause_queue() { api "$1" POST /api/queue/paused '{"paused": false}' >/dev/null; }

watch_dir()     { api "$1" POST /api/auto-scan/directories "{\"path\": \"$2\"}" >/dev/null; }
enable_scan()   { api "$1" POST /api/auto-scan/enabled '{"enabled": true}' >/dev/null; }
trigger_scan()  { api "$1" POST /api/auto-scan/trigger >/dev/null; }

# poll_until DESCRIPTION TIMEOUT_S INTERVAL_S COMMAND...
# Re-runs COMMAND until it exits 0; fails the scenario on timeout.
poll_until() {
    local desc="$1" timeout="$2" interval="$3"; shift 3
    local waited=0
    until "$@"; do
        sleep "$interval"
        waited=$((waited + interval))
        if (( waited >= timeout )); then
            echo "FATAL: timed out (${timeout}s) waiting for: $desc"
            exit 1
        fi
    done
    echo "[ok] $desc (${waited}s)" >&2
}

rss_kb() { ps -o rss= -p "$1" 2>/dev/null | tr -d ' ' || echo 0; }

assert_eq() {
    local actual="$1" expected="$2" what="$3"
    if [[ "$actual" != "$expected" ]]; then
        echo "ASSERT FAILED: $what — expected '$expected', got '$actual'"
        exit 1
    fi
    echo "[assert] $what == $expected"
}

assert_ge() {
    local actual="$1" min="$2" what="$3"
    if (( actual < min )); then
        echo "ASSERT FAILED: $what — expected >= $min, got $actual"
        exit 1
    fi
    echo "[assert] $what ($actual) >= $min"
}

assert_le() {
    local actual="$1" max="$2" what="$3"
    if (( actual > max )); then
        echo "ASSERT FAILED: $what — expected <= $max, got $actual"
        exit 1
    fi
    echo "[assert] $what ($actual) <= $max"
}

cleanup_instances() {
    for pid in "${E2E_PIDS[@]:-}"; do
        [[ -n "$pid" ]] && kill "$pid" 2>/dev/null || true
    done
    # Give SIGTERM a moment, then force anything still alive.
    sleep 2
    for pid in "${E2E_PIDS[@]:-}"; do
        [[ -n "$pid" ]] && kill -9 "$pid" 2>/dev/null || true
    done
}
trap cleanup_instances EXIT

new_run_dir() {
    local scenario="$1"
    local dir="$RUNS_DIR/$scenario-$(date +%Y%m%d-%H%M%S)"
    mkdir -p "$dir"
    echo "$dir"
}
