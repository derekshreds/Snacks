/**
 * Node-only "Integration Sync" panel.
 *
 * Shown when this Snacks instance is running as a worker. Displays the
 * master URL, last successful sync timestamp, and the result of the most
 * recent pull attempt, plus a manual refresh button so the user can verify
 * connectivity without waiting for a queued job.
 */

async function fetchStatus() {
    const resp = await fetch('/api/cluster-admin/integration-sync');
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    return await resp.json();
}

async function refreshNow() {
    const resp = await fetch('/api/cluster-admin/integration-sync/refresh', { method: 'POST' });
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    return await resp.json();
}

function paintStatus({ masterUrl, lastSyncAt, status }) {
    const urlEl    = document.getElementById('nodeSyncMasterUrl');
    const lastEl   = document.getElementById('nodeSyncLastAt');
    const statusEl = document.getElementById('nodeSyncStatus');
    if (!urlEl || !lastEl || !statusEl) return;

    urlEl.textContent = masterUrl || '—';

    if (lastSyncAt) {
        const d = new Date(lastSyncAt);
        lastEl.textContent = isNaN(d.getTime()) ? lastSyncAt : d.toLocaleString();
    } else {
        lastEl.textContent = 'Never';
    }

    statusEl.textContent = status || 'Unknown';
    statusEl.className   = 'badge ' + (
        status === 'OK'                        ? 'bg-success'   :
        (status || '').startsWith('Failed')    ? 'bg-danger'    :
        status === 'Never' || !status          ? 'bg-secondary' :
                                                 'bg-warning'
    );
}

/**
 * Wires the refresh button once at startup. Idempotent — safe to call even
 * in master/standalone modes where the tab isn't visible.
 */
export function initNodeSyncPanel() {
    const btn = document.getElementById('nodeSyncRefreshBtn');
    if (!btn) return;

    btn.addEventListener('click', async () => {
        btn.disabled    = true;
        btn.textContent = ' Pulling…';
        try {
            const data = await refreshNow();
            paintStatus({ ...data, masterUrl: document.getElementById('nodeSyncMasterUrl')?.textContent });
        } catch (err) {
            paintStatus({ status: 'Failed: ' + err.message });
        } finally {
            btn.disabled   = false;
            btn.innerHTML  = '<i class="fas fa-rotate me-1"></i>Pull from master now';
        }
    });
}

/**
 * Loads current sync status from the server and paints it into the panel.
 * Called when the settings modal is first opened.
 */
export async function loadNodeSyncPanel() {
    try {
        paintStatus(await fetchStatus());
    } catch { /* endpoint may be gated by auth on first load */ }
}
