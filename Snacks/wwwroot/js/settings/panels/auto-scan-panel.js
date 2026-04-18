/**
 * Auto-scan settings panel.
 *
 * Owns the enable toggle, the scan-interval input (debounced), the trigger
 * button, the clear-history button, and the list of watched directories
 * with their per-folder override buttons.
 *
 * The composition root wires this panel together with {@link NodeOverrideDialog}
 * via constructor injection — opening the override dialog for a folder is
 * delegated to the dialog.
 */

import { autoScanApi }        from '../../api.js';
import { escapeHtml }         from '../../utils/dom.js';
import { showConfirmModal }   from '../../utils/modal-controller.js';
import { pickFolder }         from '../folder-picker.js';


// ---------------------------------------------------------------------------
// AutoScanPanel class
// ---------------------------------------------------------------------------

export class AutoScanPanel {

    /**
     * @param {object} deps
     * @param {{ openFolder(path: string): void }} deps.nodeOverrideDialog
     */
    constructor({ nodeOverrideDialog } = {}) {
        this._nodeOverrideDialog = nodeOverrideDialog;

        /**
         * Most recent config returned from the server. Exposed (via the
         * composition root) to the override dialog so it can pre-populate
         * per-folder overrides without its own fetch.
         *
         * @type {object|null}
         */
        this._lastConfig = null;

        /** Debounce handle for the interval input. */
        this._intervalDebounce = null;
    }


    // ---- Init (DOM wiring) ----

    /**
     * Wires the panel's DOM controls. Safe to call once at startup.
     */
    init() {
        document.getElementById('autoScanEnabled')?.addEventListener('change',
            (e) => this._setEnabled(e.target.checked));

        document.getElementById('triggerAutoScan') ?.addEventListener('click',
            () => this._trigger());

        document.getElementById('clearScanHistory')?.addEventListener('click',
            () => this._clearHistory());

        document.getElementById('addWatchedDirectory')?.addEventListener('click',
            () => this._addDirectory());

        // Debounced interval input: defer for a second so a user typing
        // "12" doesn't fire at "1" and save an absurd value.
        const intervalInput = document.getElementById('autoScanInterval');
        intervalInput?.addEventListener('input', (e) => {
            clearTimeout(this._intervalDebounce);
            this._intervalDebounce = setTimeout(() => {
                const val = parseInt(e.target.value);

                if (isNaN(val) || val < 1 || val > 1440) {
                    showToast('Interval must be between 1 and 1440 minutes', 'danger');
                    intervalInput.value = Math.max(1, Math.min(1440, val || 1));
                    this._setInterval(parseInt(intervalInput.value));
                } else {
                    this._setInterval(val);
                }
            }, 1000);
        });
    }


    // ---- Load ----

    /**
     * Fetches the current auto-scan config and re-renders the panel.
     *
     * @param {boolean} [fullLoad=true] When false, leaves the enable toggle
     *                                  and interval input alone — used by
     *                                  incremental refreshes where the user
     *                                  may have the inputs focused.
     */
    async load(fullLoad = true) {
        try {
            const config = await autoScanApi.getConfig();
            this._lastConfig = config;

            if (fullLoad) {
                const enabledEl  = document.getElementById('autoScanEnabled');
                const intervalEl = document.getElementById('autoScanInterval');

                if (enabledEl)  enabledEl.checked = !!config.enabled;
                if (intervalEl) intervalEl.value  = config.intervalMinutes > 0 ? config.intervalMinutes : 60;
            }

            this._renderDirectories(config.directories || []);
            this._renderStatus(config);
        } catch (err) {
            console.error('Error loading auto-scan config:', err);
        }
    }

    /** Shortcut for incremental reloads (used by cross-module callbacks). */
    reload() {
        this.load(false);
    }


    // ---- Rendering ----

    /**
     * Renders the watched-directories list and wires its per-row buttons.
     *
     * @param {Array<string | { path: string, encodingOverrides?: object }>} directories
     */
    _renderDirectories(directories) {
        const container = document.getElementById('autoScanDirectories');
        if (!container) return;

        if (!directories || directories.length === 0) {
            container.innerHTML = '<div class="text-muted text-center py-2"><small>No directories added</small></div>';
            return;
        }

        container.innerHTML = directories.map(dir => {
            const path = typeof dir === 'string' ? dir : (dir.path || '');

            const hasOverrides = typeof dir === 'object'
                && dir.encodingOverrides
                && Object.values(dir.encodingOverrides).some(v => v !== null && v !== undefined);

            return `
                <div class="d-flex justify-content-between align-items-center py-1 px-2 border-bottom">
                    <small class="text-truncate me-2" title="${escapeHtml(path)}">
                        <i class="fas fa-folder text-warning me-1"></i>${escapeHtml(path)}
                        ${hasOverrides ? '<i class="fas fa-sliders-h text-info ms-1" title="Has custom encoding settings"></i>' : ''}
                    </small>
                    <div class="d-flex gap-1 flex-shrink-0">
                        <button class="btn btn-sm btn-outline-secondary border-0 p-0 px-1 folder-settings-btn" data-path="${escapeHtml(path)}" title="Folder encoding settings">
                            <i class="fas fa-cog"></i>
                        </button>
                        <button class="btn btn-sm btn-outline-danger border-0 p-0 px-1 folder-remove-btn" data-path="${escapeHtml(path)}" title="Remove">
                            <i class="fas fa-times"></i>
                        </button>
                    </div>
                </div>`;
        }).join('');

        container.querySelectorAll('.folder-remove-btn').forEach(btn =>
            btn.addEventListener('click', () => this._removeDirectory(btn.getAttribute('data-path'))));

        container.querySelectorAll('.folder-settings-btn').forEach(btn =>
            btn.addEventListener('click', () =>
                this._nodeOverrideDialog?.openFolder(btn.getAttribute('data-path'))));
    }

    /**
     * Writes the last-scan summary into the status line.
     *
     * @param {{ lastScanTime?: string, lastScanNewFiles?: number }} config
     */
    _renderStatus(config) {
        const statusEl = document.getElementById('autoScanStatus');
        if (!statusEl) return;

        if (config.lastScanTime) {
            const ago      = formatTimeAgo(new Date(config.lastScanTime));
            const newFiles = config.lastScanNewFiles ?? 0;
            statusEl.textContent = `Last scan: ${ago} — Found ${newFiles} new file(s)`;
        } else {
            statusEl.textContent = 'Last scan: Never';
        }
    }


    // ---- Mutations ----

    async _removeDirectory(path) {
        try {
            await autoScanApi.removeDir(path);
            await this.load(false);
        } catch (err) {
            showToast('Error removing directory: ' + err.message, 'danger');
        }
    }

    /**
     * Opens the folder picker; on confirm, adds the chosen path to the
     * watched-directories list and reloads the panel.
     */
    _addDirectory() {
        pickFolder(async (path) => {
            try {
                await autoScanApi.addDir(path);
                await this.load(false);
                showToast(`Added "${path}" to auto-scan`, 'success');
            } catch (err) {
                showToast('Error adding directory: ' + err.message, 'danger');
            }
        });
    }

    async _setEnabled(enabled) {
        try {
            await autoScanApi.setEnabled(enabled);
        } catch (err) {
            showToast('Error updating auto-scan: ' + err.message, 'danger');
        }
    }

    async _setInterval(minutes) {
        if (isNaN(minutes) || minutes < 1) return;

        try {
            await autoScanApi.setInterval(minutes);
        } catch (err) {
            showToast('Error updating scan interval: ' + err.message, 'danger');
        }
    }

    async _trigger() {
        try {
            showToast('Starting auto-scan...', 'info');
            await autoScanApi.trigger();
            showToast('Auto-scan triggered successfully', 'success');
        } catch (err) {
            showToast('Error triggering scan: ' + err.message, 'danger');
        }
    }

    async _clearHistory() {
        const confirmed = await showConfirmModal(
            'Clear History',
            '<p>Clear all auto-scan history? This cannot be undone.</p>',
            'Clear History',
        );
        if (!confirmed) return;

        try {
            await autoScanApi.clearHistory();
            await this.load(false);
            showToast('Scan history cleared', 'success');
        } catch (err) {
            showToast('Error clearing history: ' + err.message, 'danger');
        }
    }
}


// ---------------------------------------------------------------------------
// Internal helpers
// ---------------------------------------------------------------------------

/**
 * Formats an elapsed duration as a friendly "X minutes ago" string.
 *
 * Falls back to `date.toLocaleString()` for anything older than a week.
 *
 * @param {Date} date
 * @returns {string}
 */
function formatTimeAgo(date) {
    const diffSec  = Math.floor((new Date() - date) / 1000);
    const diffMin  = Math.floor(diffSec / 60);
    const diffHr   = Math.floor(diffMin / 60);
    const diffDays = Math.floor(diffHr  / 24);

    if (diffSec  < 60) return 'just now';
    if (diffMin  < 60) return `${diffMin} minute${diffMin !== 1 ? 's' : ''} ago`;
    if (diffHr   < 24) return `${diffHr} hour${diffHr   !== 1 ? 's' : ''} ago`;
    if (diffDays <  7) return `${diffDays} day${diffDays !== 1 ? 's' : ''} ago`;

    return date.toLocaleString();
}
