/**
 * Advanced settings panel.
 *
 * Two heartbeat/timeout inputs (used by cluster mode) plus a
 * "Restart application" button. Restarts go through a confirm dialog
 * because they cancel any in-progress encode.
 */

import { clusterApi, appApi, dashboardApi, queueApi, settingsApi } from '../../api.js';
import { showConfirmModal }                                        from '../../utils/modal-controller.js';


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
// Re-evaluate queue against current settings
// ---------------------------------------------------------------------------

/**
 * Calls `POST /api/settings/reevaluate` and surfaces the result via toast.
 *
 * The server holds a process-wide lock so only one walk runs at a time.
 * Mirroring that here, the button disables itself for the duration of the
 * round-trip and re-enables in the `finally` block — even if the network
 * call throws or the server returns 409 ("a walk is already in progress").
 *
 * The dataset.busy guard handles the rapid-double-click case before the
 * first request even hits the wire, so we don't burn an extra HTTP round
 * trip just to be told "already running".
 */
async function reevaluateQueue() {
    const btn = document.getElementById('reevaluateQueueBtn');
    if (!btn) return;
    if (btn.dataset.busy === '1') return;

    btn.dataset.busy = '1';
    btn.disabled     = true;
    const originalHtml = btn.innerHTML;
    btn.innerHTML = '<i class="fas fa-spinner fa-spin me-1"></i> Re-evaluating…';

    try {
        // Default off — only retry NoSavings rows when the user explicitly opts in.
        // The whole point of the NoSavings status is that "we already ran ffmpeg and it
        // didn't shrink" should not auto-retry on every Re-evaluate click.
        const forceRetryNoSavings = document.getElementById('forceRetryNoSavingsCheck')?.checked ?? false;
        const data = await settingsApi.reevaluate({ forceRetryNoSavings });
        if (data?.success === false) {
            // Server-side rejection (e.g. no settings file). Body is JSON, not an error.
            showToast(data.error || 'Re-evaluation failed', 'warning');
            return;
        }

        const requeued         = data?.requeued         ?? 0;
        const reskipped        = data?.reskipped        ?? 0;
        const dequeued         = data?.dequeued         ?? 0;
        const retriedNoSavings = data?.retriedNoSavings ?? 0;

        if (requeued + reskipped + dequeued + retriedNoSavings === 0) {
            showToast('Re-evaluation complete — no changes needed.', 'info');
        } else {
            // ReevaluateSkippedAsync flips Skipped → Unseen — those rows wait for the next
            // library scan to actually enter the queue. ReevaluateUnseenAsync flips Unseen →
            // Skipped immediately. RemoveSettingsObsoletedQueueItemsAsync drops queue items.
            // ReevaluateNoSavingsAsync flips NoSavings → Unseen — only when forceRetryNoSavings
            // was on; same wait-for-next-scan semantics as the Skipped flip.
            const parts = [];
            if (requeued)         parts.push(`${requeued} flagged for re-scan`);
            if (retriedNoSavings) parts.push(`${retriedNoSavings} no-savings retried`);
            if (reskipped)        parts.push(`${reskipped} re-skipped`);
            if (dequeued)         parts.push(`${dequeued} dropped from queue`);
            showToast(`Re-evaluation complete — ${parts.join(', ')}.`, 'success');
        }
    } catch (e) {
        // postJson throws "POST <url> → <status>" on any non-2xx. Detect the
        // 409 the server emits when a walk is already in progress and message
        // the user accordingly; everything else is an unexpected failure.
        const msg = String(e?.message ?? '');
        if (msg.includes('→ 409')) {
            showToast('A re-evaluation is already in progress. Try again in a moment.', 'warning');
        } else {
            showToast('Re-evaluation failed: ' + msg, 'danger');
        }
    } finally {
        btn.dataset.busy = '';
        btn.disabled     = false;
        btn.innerHTML    = originalHtml;
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
    document.getElementById('reevaluateQueueBtn')    ?.addEventListener('click', reevaluateQueue);
}

/** Lazy data load, invoked when the settings modal is first opened. */
export const loadAdvancedPanel = load;
