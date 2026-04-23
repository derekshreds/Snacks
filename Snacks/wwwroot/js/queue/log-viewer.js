/**
 * Transcoding-log modal viewer.
 *
 * Maintains a per-work-item in-memory log ring (`Map<string, Array<...>>`)
 * populated from two sources:
 *
 *   1. Live SignalR `TranscodingLog` events — the composition root routes
 *      those to {@link LogViewer#appendLine}.
 *   2. A one-shot server fetch (`GET /api/queue/logs/:id`) performed when
 *      the modal is opened.
 *
 * When the modal is open for a specific item, newly-appended lines are also
 * appended to the DOM and the scroll position is preserved unless the user
 * was already scrolled to the bottom.
 */

import { queueApi }  from '../api.js';
import { escapeHtml } from '../utils/dom.js';
import { openModal } from '../utils/modal-controller.js';


// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const MODAL_ID = 'logModal';


// ---------------------------------------------------------------------------
// LogViewer
// ---------------------------------------------------------------------------

export class LogViewer {

    constructor() {
        /**
         * @type {Map<string, Array<{ message: string, fromServer?: boolean }>>}
         * work-item id → log lines (live + persisted).
         */
        this._logs = new Map();
    }


    // ---- SignalR entry point ----

    /**
     * Appends a new log line received from the hub.
     *
     * If the modal happens to be open for `workItemId`, the line is also
     * appended to the DOM; we preserve the scroll position unless the user
     * was already scrolled to (or near) the bottom.
     *
     * @param {string} workItemId
     * @param {string} message
     */
    appendLine(workItemId, message) {
        if (!this._logs.has(workItemId)) this._logs.set(workItemId, []);
        this._logs.get(workItemId).push({ message, fromServer: false });

        const modal = document.getElementById(MODAL_ID);
        if (modal?.getAttribute('data-work-item-id') !== workItemId) return;

        const content     = document.getElementById('logContent');
        const wasAtBottom = content.scrollHeight - content.scrollTop - content.clientHeight < 50;

        const entry = document.createElement('div');
        entry.className   = 'log-entry';
        entry.textContent = message;
        content.appendChild(entry);

        if (wasAtBottom) content.scrollTop = content.scrollHeight;
    }

    /** Drops every buffered log (invoked on `HistoryCleared`). */
    clear() {
        this._logs.clear();
    }


    // ---- Open / render ----

    /**
     * Opens the log modal for `workItemId`, fetching any persisted lines
     * that aren't in the in-memory buffer first.
     *
     * @param {string}  workItemId
     * @param {string=} fileName    Shown in the modal title; falls back to "Unknown".
     */
    async show(workItemId, fileName) {
        const modal = document.getElementById(MODAL_ID);
        const title = modal.querySelector('.modal-title');

        title.innerHTML = `<i class="fas fa-terminal me-2"></i>Transcoding Log — ${escapeHtml(fileName ?? 'Unknown')}`;
        modal.setAttribute('data-work-item-id', workItemId);

        await this._loadPersisted(workItemId);
        this._render(workItemId);
        openModal(MODAL_ID);
    }


    // ---- Internal ----

    /**
     * Pulls persisted log lines for `workItemId` and seeds the buffer if
     * any were returned. Silent on failure — live lines still work.
     *
     * @param {string} workItemId
     */
    async _loadPersisted(workItemId) {
        try {
            const lines = await queueApi.getLogs(workItemId);

            if (Array.isArray(lines) && lines.length > 0) {
                this._logs.set(
                    workItemId,
                    lines.map(m => ({ message: m, fromServer: true })),
                );
            }
        } catch { /* non-fatal */ }
    }

    /**
     * Repaints the modal body from the current buffer.
     *
     * @param {string} workItemId
     */
    _render(workItemId) {
        const content = document.getElementById('logContent');
        const lines   = this._logs.get(workItemId) ?? [];

        content.innerHTML = lines
            .map(l => `<div class="log-entry">${escapeHtml(l.message)}</div>`)
            .join('');
        content.scrollTop = content.scrollHeight;
    }
}
