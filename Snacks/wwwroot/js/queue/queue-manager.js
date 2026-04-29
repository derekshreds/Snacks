/**
 * Queue manager — rendering, pagination, filtering, and work-item state.
 *
 * Owns the `_workItems` map and all queue-related UI state. Receives new
 * and updated items from SignalR via {@link QueueManager#addItem} and
 * {@link QueueManager#updateItem}. The composition root is responsible for
 * wiring SignalR events to these methods and for reacting to cluster-mode
 * changes via {@link QueueManager#setClusterMode}.
 *
 * The "processing" and "queued" containers are reconciled separately on
 * every load because they represent orthogonal concerns — an item can
 * briefly exist in neither (after it was processing and before the next
 * poll) or in the wrong one after a status transition.
 */

import { queueApi } from '../api.js';
import {
    TRANSFER_STATUSES,
    getStatusString,
    getWorkItemHtml,
    updateWorkItemDom,
} from './work-item-renderer.js';


// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** How many completed/pending items show per page. */
const PAGE_SIZE = 5;

/** Minimum delay between "full refresh" requests triggered by item updates. */
const REFRESH_THROTTLE_MS = 2000;


// ---------------------------------------------------------------------------
// QueueManager
// ---------------------------------------------------------------------------

export class QueueManager {

    /**
     * @param {object} deps
     * @param {{ show(id: string): void }}                            deps.stopCancelDialog
     * @param {{ show(id: string, fileName?: string): Promise<void> }} deps.logViewer
     * @param {{ loadFromServer(): Promise<void> }}                    deps.pauseControl
     */
    constructor({ stopCancelDialog, logViewer, pauseControl }) {

        /** @type {Map<string, object>} work-item id → latest payload */
        this._workItems      = new Map();

        this._page           = 0;
        this._total          = 0;

        /** Active status filter; null means "show all". */
        this._filter         = null;

        this._clusterEnabled = false;

        /** Handle for the throttled refresh timer. */
        this._refreshTimer   = null;

        this._stopCancel   = stopCancelDialog;
        this._logViewer    = logViewer;
        this._pauseControl = pauseControl;
    }


    // ---- Init ----

    /**
     * Binds container-level click delegation and pagination/filter handlers.
     * Safe to call once at startup.
     */
    init() {

        // Delegated action buttons (cancel/log) in both work-item containers.
        for (const containerId of ['processingContainer', 'workItemsContainer']) {
            document.getElementById(containerId)?.addEventListener('click', (e) => {
                const btn    = e.target.closest('[data-action]');
                const itemEl = btn?.closest('.work-item');
                const itemId = itemEl?.id?.replace('work-item-', '');
                if (!btn || !itemId) return;

                if (btn.dataset.action === 'remove') {
                    this._stopCancel.show(itemId);
                }
                if (btn.dataset.action === 'log') {
                    this._logViewer.show(itemId, this._workItems.get(itemId)?.fileName);
                }
            });
        }

        // Pagination buttons (event-delegated).
        document.getElementById('queuePagination')?.addEventListener('click', (e) => {
            const btn = e.target.closest('[data-page-action]');
            if (!btn || btn.disabled) return;

            const totalPages = Math.ceil(this._total / PAGE_SIZE);

            switch (btn.dataset.pageAction) {
                case 'first':
                    if (this._page > 0) { this._page = 0; this.loadItems(); }
                    break;
                case 'prev':
                    if (this._page > 0) { this._page--; this.loadItems(); }
                    break;
                case 'next':
                    if (this._page < totalPages - 1) { this._page++; this.loadItems(); }
                    break;
                case 'last':
                    if (this._page < totalPages - 1) { this._page = totalPages - 1; this.loadItems(); }
                    break;
            }
        });

        // Filter tab buttons (event-delegated).
        document.getElementById('queueFilterTabs')?.addEventListener('click', (e) => {
            const btn = e.target.closest('[data-filter]');
            if (!btn) return;

            this._filter = btn.dataset.filter === '' ? null : btn.dataset.filter;
            this._page   = 0;
            this.loadItems();
        });
    }

    /**
     * Called by the composition root when cluster mode changes. Only affects
     * the rendering of node-badge chips on work-item cards.
     */
    setClusterMode(enabled, _role) {
        this._clusterEnabled = enabled;
    }

    /** Called on SignalR `HistoryCleared`. */
    reset() {
        this._workItems.clear();
        this.loadItems();
    }


    // ---- SignalR event handlers ----

    /**
     * Called on SignalR `WorkItemAdded`.
     *
     * @param {object} workItem
     */
    addItem(workItem) {
        this._workItems.set(workItem.id, workItem);
        this._scheduleRefresh();
    }

    /**
     * Called on SignalR `WorkItemUpdated`.
     *
     * For items currently in a transfer state we render immediately so the
     * ephemeral progress % is preserved between refreshes. For everything
     * else we schedule a throttled full refresh.
     *
     * @param {object} workItem
     */
    updateItem(workItem) {
        this._workItems.set(workItem.id, workItem);

        // Off-page (e.g. user is on /dashboard): only keep the in-memory map
        // current. The DOM will repaint from a fresh fetch when the queue
        // page is re-mounted.
        if (!document.getElementById('workItemsContainer')) return;

        const statusString = getStatusString(workItem.status);
        if (!TRANSFER_STATUSES.includes(statusString)) {
            this._scheduleRefresh();
            return;
        }

        // When the master restarts with a new job id for the same file, the
        // old "Processing" item is orphaned — drop it from the UI so the
        // new transfer-phase item takes its place.
        const isTransferPhase = workItem.fileName
            && (workItem.remoteJobPhase === 'Downloading' || workItem.remoteJobPhase === 'Uploading');

        if (isTransferPhase) {
            for (const [existingId, existing] of this._workItems) {
                if (existingId === workItem.id) continue;
                if (existing.fileName !== workItem.fileName) continue;
                if (getStatusString(existing.status) !== 'Processing') continue;

                this._workItems.delete(existingId);
                document.getElementById(`work-item-${existingId}`)?.remove();
            }
        }

        this._renderItem({ ...workItem, status: statusString });

        // The card just moved out of the Queued container into Processing,
        // freeing a slot on the current page. Schedule a throttled refresh
        // so the next Pending item is fetched in to fill the gap. The
        // throttle absorbs bursts when several items dispatch back-to-back.
        this._scheduleRefresh();
    }

    /** Schedules a full refresh, throttled to at most once every {@link REFRESH_THROTTLE_MS}. */
    _scheduleRefresh() {
        if (this._refreshTimer) return;

        this._refreshTimer = setTimeout(() => {
            this._refreshTimer = null;
            this.loadItems();
        }, REFRESH_THROTTLE_MS);
    }


    // ---- Load / render ----

    /**
     * Fetches the current page and the aggregate stats, reconciles both
     * containers, and repaints the pagination + filter strip.
     *
     * SPA shell: the queue containers only exist on the queue page. If the
     * user is on /dashboard when SignalR fires a WorkItem update or a
     * visibility-change tries to resync, bail out — the queue page's
     * `mount` hook calls `loadItems()` again when the user returns, and
     * the in-memory `_workItems` map is repopulated from that fetch.
     */
    async loadItems() {
        if (!document.getElementById('workItemsContainer')) return;

        try {
            const skip = this._page * PAGE_SIZE;

            const [stats, data] = await Promise.all([
                queueApi.getStats(),
                queueApi.getItems(PAGE_SIZE, skip, this._filter ?? undefined),
            ]);

            const queueItems      = data.items;
            const processingItems = data.processing || [];
            this._total = data.total;

            this._workItems.clear();

            this._reconcileProcessingContainer(processingItems);
            this._reconcileQueueContainer(queueItems);

            this._updateStatCounters(stats);

            if (queueItems.length === 0) {
                const queueContainer = document.getElementById('workItemsContainer');
                const msg = this._filter
                    ? `No ${this._filter.toLowerCase()} items`
                    : 'No files in queue';
                queueContainer.innerHTML = `<div class="text-muted text-center py-4"><i class="fas fa-inbox fa-2x mb-2"></i><br>${msg}</div>`;
            }

            this._renderPagination();
            this._renderFilterTabs(stats);
            this._pauseControl.loadFromServer();
        } catch (err) {
            console.error('Error loading work items:', err);
            showToast('Error loading work items: ' + err.message, 'danger');
        }
    }

    /**
     * Reconciles the "processing" container: remove stale cards, render
     * current items, and hide the section when empty.
     *
     * @param {Array<object>} processingItems
     */
    _reconcileProcessingContainer(processingItems) {
        const processingContainer = document.getElementById('processingContainer');
        const processingSection   = document.getElementById('processingSection');

        const expected = new Set(processingItems.map(i => `work-item-${i.id}`));
        for (const child of [...processingContainer.children]) {
            if (child.id && !expected.has(child.id)) child.remove();
        }

        if (processingItems.length === 0) {
            processingSection.style.display = 'none';
            return;
        }

        processingSection.style.display = '';
        for (const item of processingItems) {
            this._workItems.set(item.id, item);
            this._renderItem({ ...item, status: getStatusString(item.status) });
        }
    }

    /**
     * Reconciles the "queued" container: remove stale cards, render current
     * items, and reorder to match the server's ordering.
     *
     * @param {Array<object>} queueItems
     */
    _reconcileQueueContainer(queueItems) {
        const queueContainer = document.getElementById('workItemsContainer');

        const expected = new Set(queueItems.map(i => `work-item-${i.id}`));
        for (const child of [...queueContainer.children]) {
            if (child.id && !expected.has(child.id)) child.remove();
            else if (!child.id)                      child.remove();
        }

        for (const workItem of queueItems) {
            this._workItems.set(workItem.id, workItem);
            this._renderItem({ ...workItem, status: getStatusString(workItem.status) });
        }

        // Reorder in place so the DOM matches the server's order.
        for (let i = 0; i < queueItems.length; i++) {
            const el = document.getElementById(`work-item-${queueItems[i].id}`);
            if (el && el !== queueContainer.children[i]) {
                queueContainer.insertBefore(el, queueContainer.children[i]);
            }
        }
    }

    /**
     * Renders or updates a single work-item card in the appropriate container.
     *
     * @param {object} workItem  Must have a canonical `status` string.
     */
    _renderItem(workItem) {
        const queueContainer      = document.getElementById('workItemsContainer');
        const processingContainer = document.getElementById('processingContainer');
        const processingSection   = document.getElementById('processingSection');
        const statusString        = workItem.status;

        // Ensure the element exists.
        let element = document.getElementById(`work-item-${workItem.id}`);
        if (!element) {
            element           = document.createElement('div');
            element.id        = `work-item-${workItem.id}`;
            element.className = 'work-item new';
        }

        // Route into the correct container for its status.
        if (TRANSFER_STATUSES.includes(statusString)) {
            if (element.parentNode !== processingContainer) processingContainer.appendChild(element);
            processingSection.style.display = '';
        } else {
            if (element.parentNode === processingContainer) {
                element.remove();
                if (processingContainer.children.length === 0) processingSection.style.display = 'none';
            }
            queueContainer.querySelector('.text-muted.text-center')?.remove();
            if (!element.parentNode || element.parentNode !== queueContainer) {
                queueContainer.appendChild(element);
            }
        }

        // Paint or repaint the card body.
        element.className = `work-item ${statusString.toLowerCase()}`;
        if (element.dataset.status) {
            updateWorkItemDom(element, workItem, this._clusterEnabled);
        } else {
            element.innerHTML = getWorkItemHtml(workItem, this._clusterEnabled);
        }
        element.dataset.status = statusString;
    }


    // ---- Counters + tabs + pagination ----

    /**
     * Writes aggregate counters into their targets for both the desktop and
     * mobile sidebars.
     *
     * @param {{ pending?: number, processing?: number, completed?: number, failed?: number }} stats
     */
    _updateStatCounters(stats) {
        const set = (id, val) => {
            const el = document.getElementById(id);
            if (el) el.textContent = val || 0;
        };

        for (const prefix of ['', 'Mobile']) {
            set(`pendingCount${prefix}`,    stats.pending);
            set(`processingCount${prefix}`, stats.processing);
            set(`completedCount${prefix}`,  stats.completed);
            set(`failedCount${prefix}`,     stats.failed);
        }
    }

    /**
     * Repaints the filter tab strip with current counts.
     *
     * @param {{ pending?: number, processing?: number, completed?: number, failed?: number }} stats
     */
    _renderFilterTabs(stats) {
        const container = document.getElementById('queueFilterTabs');
        if (!container) return;

        const filters = [
            { label: 'All',       value: null,        count: (stats.pending || 0) + (stats.completed || 0) + (stats.failed || 0) },
            { label: 'Pending',   value: 'Pending',   count: stats.pending   || 0 },
            { label: 'Completed', value: 'Completed', count: stats.completed || 0 },
            { label: 'Failed',    value: 'Failed',    count: stats.failed    || 0 },
        ];

        container.innerHTML = filters.map(f => {
            const active = this._filter === f.value ? 'active' : '';
            return `<button class="btn btn-sm btn-outline-secondary ${active} queue-filter-btn" data-filter="${f.value ?? ''}">${f.label} <span class="badge bg-secondary ms-1">${f.count}</span></button>`;
        }).join('');
    }

    /**
     * Repaints the pagination bar. Hidden entirely when there is only one page.
     */
    _renderPagination() {
        const el = document.getElementById('queuePagination');
        if (!el) return;

        const totalPages = Math.ceil(this._total / PAGE_SIZE);
        if (totalPages <= 1) {
            el.innerHTML = '';
            return;
        }

        const page = this._page;
        el.innerHTML = `
            <nav class="d-flex justify-content-between align-items-center mt-3">
                <small class="text-muted">${this._total} items</small>
                <div class="btn-group btn-group-sm">
                    <button class="btn btn-outline-secondary" ${page === 0 ? 'disabled' : ''} data-page-action="first" title="First page"><i class="fas fa-angle-double-left"></i></button>
                    <button class="btn btn-outline-secondary" ${page === 0 ? 'disabled' : ''} data-page-action="prev"><i class="fas fa-chevron-left"></i></button>
                    <button class="btn btn-outline-secondary disabled">${page + 1} / ${totalPages}</button>
                    <button class="btn btn-outline-secondary" ${page >= totalPages - 1 ? 'disabled' : ''} data-page-action="next"><i class="fas fa-chevron-right"></i></button>
                    <button class="btn btn-outline-secondary" ${page >= totalPages - 1 ? 'disabled' : ''} data-page-action="last" title="Last page"><i class="fas fa-angle-double-right"></i></button>
                </div>
            </nav>`;
    }


    // ---- Action dispatchers ----
    //
    // These are invoked by the StopCancelDialog buttons via the composition
    // root (main.js), which wires the dialog's two callbacks to
    // queueManager.stop / queueManager.cancel respectively.

    /** Stops an in-progress work item. */
    async stop(id) {
        try {
            await queueApi.stop(id);
            showToast('Work item stopped — will be re-queued on next scan', 'info');
        } catch (err) {
            showToast('Error stopping work item: ' + err.message, 'danger');
        }
    }

    /** Cancels a queued or in-progress work item. */
    async cancel(id) {
        try {
            await queueApi.cancel(id);
            showToast("Work item cancelled — will not be reprocessed", 'info');
        } catch (err) {
            showToast('Error cancelling work item: ' + err.message, 'danger');
        }
    }
}
