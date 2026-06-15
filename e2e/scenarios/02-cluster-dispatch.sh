#!/usr/bin/env bash
# Scenario 02 — three-instance cluster on one machine.
#
# Master (local encoding DISABLED) + two worker nodes, manual node list (no
# UDP discovery — deterministic on a single host). Queues a small library and
# asserts:
#   • both workers register and stay online
#   • every encodable file completes and a [snacks] output lands on disk
#   • BOTH nodes did work (per-node throughput from the encode ledger)
#
# This exercises the full remote path: dispatch, upload, node-side encode,
# download, placement, ledger.
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/lib.sh"
require jq curl ffmpeg dotnet

COUNT="${COUNT:-20}"
MPORT=6801; N1PORT=6802; N2PORT=6803
SECRET="e2e-secret-$(date +%s)"

RUN="$(new_run_dir 02-cluster)"
LIB="$RUN/library"
echo "=== Scenario 02: cluster dispatch (COUNT=$COUNT) ==="
echo "    run dir: $RUN"

build_app
"$E2E_ROOT/generate-library.sh" "$LIB" "$COUNT"
source "$LIB/.manifest"

MPID="$(start_instance master "$MPORT" "$RUN/master")"
N1PID="$(start_instance node1  "$N1PORT" "$RUN/node1")"
N2PID="$(start_instance node2  "$N2PORT" "$RUN/node2")"

apply_fast_settings "$MPORT"
apply_fast_settings "$N1PORT"
apply_fast_settings "$N2PORT"

# Workers first, so they're listening when the master starts polling them.
node_config() { # port name
    api "$1" POST /api/cluster-admin/config "{
        \"enabled\": true, \"role\": \"node\", \"nodeName\": \"$2\",
        \"sharedSecret\": \"$SECRET\", \"autoDiscovery\": false,
        \"masterUrl\": \"http://127.0.0.1:$MPORT\"
    }" >/dev/null
}
node_config "$N1PORT" e2e-node-1
node_config "$N2PORT" e2e-node-2

api "$MPORT" POST /api/cluster-admin/config "{
    \"enabled\": true, \"role\": \"master\", \"nodeName\": \"e2e-master\",
    \"sharedSecret\": \"$SECRET\", \"autoDiscovery\": false,
    \"localEncodingEnabled\": false,
    \"manualNodes\": [
        { \"name\": \"e2e-node-1\", \"url\": \"http://127.0.0.1:$N1PORT\" },
        { \"name\": \"e2e-node-2\", \"url\": \"http://127.0.0.1:$N2PORT\" }
    ]
}" >/dev/null

# node.status is the NodeStatus enum serialized numerically: 0 = Online.
workers_online() {
    [[ "$(api "$MPORT" GET /api/cluster-admin/status | jq '[.nodes[]? | select(.status == 0)] | length')" -ge 2 ]]
}
poll_until "both workers online" 120 3 workers_online

api "$MPORT" POST /api/library/process-directory "{
    \"directoryPath\": \"$LIB\", \"recursive\": true,
    \"options\": $(api "$MPORT" GET /api/settings)
}" >/dev/null

# Completion target: the encodable VIDEOS. The synthetic music seeds (8s sine
# in flac/mp3) legitimately finish as NoSavings — they encode on a node but
# the output doesn't shrink, so they count in neither completed nor failed.
# Draining the queue (pending=0, processing=0) is what proves they ran.
expected=$encodable
all_done() {
    local c p r
    c="$(stat_field "$MPORT" completed)"; p="$(stat_field "$MPORT" pending)"; r="$(stat_field "$MPORT" processing)"
    echo "    completed=$c pending=$p processing=$r (target $expected, queue drained)" >&2
    [[ "$c" -ge "$expected" && "$p" -eq 0 && "$r" -eq 0 ]]
}
poll_until "all $expected video items complete and the queue drains" $(( 300 + expected * 30 )) 10 all_done

outputs="$(find "$LIB" -name '* \[snacks\].*' | wc -l | tr -d ' ')"
assert_ge "$outputs" "$expected" "[snacks] outputs on disk"

nodes_used="$(api "$MPORT" GET '/api/dashboard/node-throughput?days=1' | jq '[.[] | select(.encodes > 0)] | length')"
assert_ge "$nodes_used" 2 "distinct nodes that completed encodes"

echo
echo "=== PASS — $outputs outputs across $nodes_used nodes, master encoded nothing locally ==="
