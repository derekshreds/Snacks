/**
 * Pure rendering functions for work-item cards.
 *
 * No module state and no DOM lookups beyond the element passed into
 * {@link updateWorkItemDom}. Keeping this layer stateless means rendering
 * logic can be updated in isolation from {@link QueueManager}, which owns
 * the work-item map and the container-reconcile loop.
 *
 * `formatFileSize`, `formatBitrate`, and `formatDuration` live in `site.js`
 * as classical globals that are accessible from any module.
 */

import { escapeHtml } from '../utils/dom.js';


// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Statuses that render an animated transfer progress bar. Used both for
 * layout decisions (which container to place the card in) and for
 * rendering (progress bar vs. not).
 */
export const TRANSFER_STATUSES = Object.freeze(['Processing', 'Uploading', 'Downloading']);

/** Numeric → canonical status name lookup (the hub emits numbers). */
const STATUS_NAMES_BY_CODE = Object.freeze({
    0: 'Pending',
    1: 'Processing',
    2: 'Completed',
    3: 'Failed',
    4: 'Cancelled',
    5: 'Uploading',
    6: 'Downloading',
});


// ---------------------------------------------------------------------------
// Public: status + action buttons
// ---------------------------------------------------------------------------

/**
 * Converts a numeric or string status into a canonical status string.
 *
 * @param {number|string} status
 * @returns {string}
 */
export function getStatusString(status) {
    if (typeof status === 'string') return status;
    return STATUS_NAMES_BY_CODE[status] ?? 'Unknown';
}

/**
 * Returns the HTML markup for the action buttons appropriate to the item's
 * current status (empty string when none are relevant).
 *
 * @param {{ status: string }} workItem
 * @returns {string}
 */
export function getActionButtons(workItem) {
    switch (workItem.status) {

        case 'Pending':
            return '<button class="btn btn-sm btn-outline-danger remove-btn" data-action="remove" title="Remove from queue"><i class="fas fa-times"></i></button>';

        case 'Processing':
        case 'Uploading':
        case 'Downloading':
            return `
                <div class="btn-group" role="group">
                    <button class="btn btn-sm btn-outline-danger remove-btn" data-action="remove" title="Stop/Cancel"><i class="fas fa-times"></i></button>
                    <button class="btn btn-sm btn-outline-info log-btn" data-action="log" title="View Log"><i class="fas fa-terminal"></i></button>
                </div>`;

        case 'Completed':
        case 'Failed':
        case 'Cancelled':
        case 'Stopped':
            return '<button class="btn btn-sm btn-outline-info log-btn" data-action="log" title="View Log"><i class="fas fa-terminal"></i></button>';

        default:
            return '';
    }
}


// ---------------------------------------------------------------------------
// Public: full card render
// ---------------------------------------------------------------------------

/**
 * Returns the full HTML for a work-item card. Used for the first render of
 * an item; subsequent updates go through {@link updateWorkItemDom} to avoid
 * blowing away focus/selection.
 *
 * @param {object}  workItem
 * @param {boolean} clusterEnabled   Whether to render the assigned-node badge.
 * @returns {string}
 */
export function getWorkItemHtml(workItem, clusterEnabled) {
    const statusClass = `status-${workItem.status.toLowerCase()}`;

    const progressPct = workItem.progress         || 0;
    const xferPct     = workItem.transferProgress || 0;
    const isTransfer  = TRANSFER_STATUSES.includes(workItem.status);
    const pct         = workItem.status === 'Uploading' || workItem.status === 'Downloading'
        ? xferPct
        : progressPct;

    const nodeBadge = clusterEnabled && workItem.assignedNodeName
        ? `<span class="badge bg-secondary flex-shrink-0" title="Processing on remote node"><i class="fas fa-server me-1"></i>${escapeHtml(workItem.assignedNodeName)}</span>`
        : '';

    const progressBar = isTransfer ? `
        <div class="progress mb-2" style="position: relative;">
            <div class="progress-bar progress-bar-striped progress-bar-animated" role="progressbar"
                 style="width: ${pct}%" aria-valuenow="${progressPct}" aria-valuemin="0" aria-valuemax="100"></div>
            <span class="progress-label">${pct}%</span>
        </div>` : '';

    const errorBlock = workItem.errorMessage ? `
        <div class="alert alert-danger alert-sm mb-0 mt-2">
            <i class="fas fa-exclamation-triangle me-2"></i>${escapeHtml(workItem.errorMessage)}
        </div>` : '';

    return `
        <div class="d-flex justify-content-between align-items-start mb-2">
            <div class="flex-grow-1" style="min-width:0;">
                <div class="d-flex align-items-center flex-wrap gap-1 mb-1" style="min-width:0;">
                    <div class="d-flex align-items-center" style="min-width:0; max-width:100%;">
                        <i class="fas fa-file-video me-2 text-primary flex-shrink-0"></i>
                        <strong style="overflow:hidden; text-overflow:ellipsis; white-space:nowrap;">${escapeHtml(workItem.fileName)}</strong>
                    </div>
                    <span class="status-badge ${statusClass} flex-shrink-0">${workItem.status}</span>
                    ${nodeBadge}
                </div>
                <small class="text-muted">
                    ${formatFileSize(workItem.size)} &bull; ${formatBitrate(workItem.bitrate)} &bull; ${formatDuration(workItem.length)}
                </small>
            </div>
            <div class="ms-2 flex-shrink-0">${getActionButtons(workItem)}</div>
        </div>
        ${progressBar}
        ${errorBlock}
        <div class="text-muted small mt-1">
            ${new Date(workItem.createdAt).toLocaleString()}${workItem.completedAt ? ` &rarr; ${new Date(workItem.completedAt).toLocaleString()}` : ''}
        </div>`;
}


// ---------------------------------------------------------------------------
// Public: surgical DOM update
// ---------------------------------------------------------------------------

/**
 * Surgically updates an existing card in place, touching only the badge,
 * progress bar, action buttons, error block, node badge, and timestamp.
 *
 * Preserves focus and selection that a full innerHTML rebuild would blow
 * away — important when the user is mid-click on an action button.
 *
 * @param {HTMLElement} element       The card element to update.
 * @param {object}      workItem      The latest work-item payload.
 * @param {boolean}     clusterEnabled Whether to render the assigned-node badge.
 */
export function updateWorkItemDom(element, workItem, clusterEnabled) {
    const prevStatus   = element.dataset.status;
    const statusString = workItem.status;


    // Status badge.
    const badge = element.querySelector('.status-badge');
    if (badge) {
        const newClass = `status-badge status-${statusString.toLowerCase()} flex-shrink-0`;
        if (badge.className   !== newClass)      badge.className   = newClass;
        if (badge.textContent !== statusString)  badge.textContent = statusString;
    }


    // Progress bar (transfer-style or simple encode).
    const isTransfer = workItem.remoteJobPhase === 'Uploading' || workItem.remoteJobPhase === 'Downloading';
    const pct        = isTransfer ? (workItem.transferProgress || 0) : (workItem.progress || 0);
    const progressContainer = element.querySelector('.progress');

    if (TRANSFER_STATUSES.includes(statusString)) {
        if (progressContainer) {
            const bar = progressContainer.querySelector('.progress-bar');
            if (bar) bar.style.width = pct + '%';

            const label = progressContainer.querySelector('.progress-label');
            if (label && label.textContent !== `${pct}%`) label.textContent = `${pct}%`;
        } else {
            // Status just transitioned into a transfer — fall back to full rebuild.
            element.innerHTML = getWorkItemHtml(workItem, clusterEnabled);
            return;
        }
    } else if (progressContainer) {
        // Leaving a transfer status — drop the progress bar.
        progressContainer.remove();
    }


    // Action buttons — only repaint when the status actually changed.
    if (prevStatus !== statusString) {
        const actions = element.querySelector('.ms-2.flex-shrink-0');
        if (actions) actions.innerHTML = getActionButtons(workItem);
    }


    // Error block.
    const existingError = element.querySelector('.alert-danger');
    if (workItem.errorMessage) {
        const msg = `<i class="fas fa-exclamation-triangle me-2"></i>${escapeHtml(workItem.errorMessage)}`;
        if (existingError) {
            if (existingError.innerHTML !== msg) existingError.innerHTML = msg;
        } else {
            const err = document.createElement('div');
            err.className = 'alert alert-danger alert-sm mb-0 mt-2';
            err.innerHTML = msg;
            element.querySelector('.text-muted.small.mt-1')?.before(err);
        }
    } else if (existingError) {
        existingError.remove();
    }


    // Assigned-node badge.
    const nodeBadge = element.querySelector('.badge.bg-secondary');
    if (clusterEnabled && workItem.assignedNodeName) {
        if (nodeBadge && !nodeBadge.textContent.includes(workItem.assignedNodeName)) {
            nodeBadge.innerHTML = `<i class="fas fa-server me-1"></i>${escapeHtml(workItem.assignedNodeName)}`;
        }
    } else if (nodeBadge && !workItem.assignedNodeName) {
        nodeBadge.remove();
    }


    // Timestamp / completion line.
    const timeEl = element.querySelector('.text-muted.small.mt-1');
    if (timeEl) {
        const newTime = `${new Date(workItem.createdAt).toLocaleString()}${workItem.completedAt ? ` &rarr; ${new Date(workItem.completedAt).toLocaleString()}` : ''}`;
        if (timeEl.innerHTML !== newTime) timeEl.innerHTML = newTime;
    }
}
