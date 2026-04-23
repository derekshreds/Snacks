/**
 * Advanced settings panel.
 *
 * Two heartbeat/timeout inputs (used by cluster mode) plus a
 * "Restart application" button. Restarts go through a confirm dialog
 * because they cancel any in-progress encode.
 */

import { clusterApi, appApi } from '../../api.js';
import { showConfirmModal }   from '../../utils/modal-controller.js';


// ---------------------------------------------------------------------------
// Read / write timings
// ---------------------------------------------------------------------------

/**
 * Populates the heartbeat/timeout fields from the cluster config.
 * Silent on failure — these defaults are fine for a standalone instance.
 */
async function load() {
    try {
        const cfg = await clusterApi.getConfig();
        document.getElementById('advHeartbeatInterval').value = cfg.heartbeatIntervalSeconds ?? 15;
        document.getElementById('advNodeTimeout').value       = cfg.nodeTimeoutSeconds       ?? 60;
    } catch { /* non-fatal */ }
}

/**
 * Saves the two timing values back into the cluster config.
 *
 * We read the existing config first and write back a modified copy so we
 * don't clobber fields the advanced panel doesn't own.
 */
async function saveTimings() {
    try {
        const cur = await clusterApi.getConfig();
        cur.heartbeatIntervalSeconds = parseInt(document.getElementById('advHeartbeatInterval').value) || 15;
        cur.nodeTimeoutSeconds       = parseInt(document.getElementById('advNodeTimeout').value)       || 60;

        const data = await clusterApi.saveConfig(cur);
        if (!data.success) throw new Error(data.error || 'Save failed');

        showToast('Timings saved', 'success');
    } catch (e) {
        showToast('Save failed: ' + e.message, 'danger');
    }
}


// ---------------------------------------------------------------------------
// Restart
// ---------------------------------------------------------------------------

/**
 * Confirms with the user and triggers a backend restart. The electron main
 * process is responsible for relaunching the backend; see `electron-app/main.js`.
 */
async function restart() {
    const confirmed = await showConfirmModal(
        'Restart',
        '<p>Restart the application? Any in-progress encode will be cancelled and resumed on next start.</p>',
        'Restart',
    );
    if (!confirmed) return;

    try {
        await appApi.restart();
        showToast('Restarting…', 'info');
    } catch (e) {
        showToast('Restart failed: ' + e.message, 'danger');
    }
}


// ---------------------------------------------------------------------------
// Public entry points
// ---------------------------------------------------------------------------

/** Wires the panel's DOM controls. Safe to call once at startup. */
export function initAdvancedPanel() {
    document.getElementById('saveAdvancedCluster')?.addEventListener('click', saveTimings);
    document.getElementById('restartAppBtn')      ?.addEventListener('click', restart);
}

/** Lazy data load, invoked when the settings modal is first opened. */
export const loadAdvancedPanel = load;
