/**
 * Advanced settings panel.
 *
 * Two heartbeat/timeout inputs (used by cluster mode) plus a
 * "Restart application" button. Restarts go through a confirm dialog
 * because they cancel any in-progress encode.
 */

import { clusterApi, appApi, dashboardApi, queueApi } from '../../api.js';
import { showConfirmModal }                           from '../../utils/modal-controller.js';


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
// Clear dashboard data
// ---------------------------------------------------------------------------

/**
 * Wipes the encode-history ledger after explicit confirmation. The hero/
 * device/codec/recent panels of the dashboard repaint via the SignalR
 * `EncodeHistoryCleared` broadcast that the backend fires on success — no
 * direct page refresh needed here.
 */
async function clearDashboard() {
    const confirmed = await showConfirmModal(
        'Clear dashboard data',
        '<p>This permanently deletes every row in the encode-history ledger.</p>'
        + '<p class="text-muted small mb-0">The dashboard will reset to zero. This does not affect '
        + 'queued, in-progress, or completed work items, only the analytics history.</p>',
        'Clear data',
    );
    if (!confirmed) return;

    try {
        const data = await dashboardApi.clearHistory();
        const n = data?.deleted ?? 0;
        showToast(`Cleared ${n} encode${n === 1 ? '' : 's'} from dashboard history`, 'success');
    } catch (e) {
        showToast('Clear failed: ' + e.message, 'danger');
    }
}


// ---------------------------------------------------------------------------
// Remove failed items
// ---------------------------------------------------------------------------

/**
 * Deletes every Failed row from the database after explicit confirmation.
 * Items whose source file still exists on disk are re-discovered as Unseen
 * by the next library scan and re-evaluated against the current encoder
 * settings. Items whose source has already been replaced (the bogus
 * "Source file was removed during encoding" backlog) simply stay gone —
 * the encoded output continues to live at its destination path. No video
 * files are touched.
 */
async function removeFailed() {
    const confirmed = await showConfirmModal(
        'Remove failed items',
        '<p>Delete every failed item from the database. They will be re-discovered '
        + 'on the next library scan if their source file is still present.</p>'
        + '<p class="text-muted small mb-0">This does not delete any video files. '
        + 'Items that were misreported as failed but already encoded will simply '
        + 'not be re-picked up — their original source no longer exists for the '
        + 'scanner to find, and the encoded output remains in place.</p>',
        'Remove failed',
    );
    if (!confirmed) return;

    try {
        const data = await queueApi.removeFailed();
        const n = data?.deleted ?? 0;
        showToast(`Removed ${n} failed item${n === 1 ? '' : 's'} from database`, 'success');
    } catch (e) {
        showToast('Remove failed: ' + e.message, 'danger');
    }
}


// ---------------------------------------------------------------------------
// Public entry points
// ---------------------------------------------------------------------------

/** Wires the panel's DOM controls. Safe to call once at startup. */
export function initAdvancedPanel() {
    document.getElementById('saveAdvancedCluster')   ?.addEventListener('click', saveTimings);
    document.getElementById('restartAppBtn')         ?.addEventListener('click', restart);
    document.getElementById('clearDashboardHistory') ?.addEventListener('click', clearDashboard);
    document.getElementById('removeFailedFiles')     ?.addEventListener('click', removeFailed);
}

/** Lazy data load, invoked when the settings modal is first opened. */
export const loadAdvancedPanel = load;
