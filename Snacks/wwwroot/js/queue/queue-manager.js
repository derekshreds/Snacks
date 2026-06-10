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

import { queueApi, autoScanApi } from '../api.js';
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

        /** Which refresh the pending timer should run: 'full' | 'queue' | null. */
        this._pendingRefreshKind = null;

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
                if (btn.dataset.action === 'retry') {
                    const path = this._workItems.get(itemId)?.path;
                    if (path) this.retry(itemId, path);
                }
                if (btn.dataset.action === 'prioritize') {
                    this.prioritize(itemId);
                }
            });
        }

        // First-run onboarding hero buttons (rendered into the empty queue
        // container — delegated here because the hero comes and goes).
        document.getElementById('workItemsContainer')?.addEventListener('click', (e) => {
            const btn = e.target.closest('[data-onboard]');
            if (!btn) return;
            if (btn.dataset.onboard === 'library') {
                document.getElementById('openLibraryBtn')?.click();
            } else if (btn.dataset.onboard === 'preset') {
                document.getElementById('openSettingsBtn')?.click();
                document.querySelector('[data-bs-target="#generalTab"]')?.click();
            } else if (btn.dataset.onboard === 'watch') {
                document.getElementById('openSettingsBtn')?.click();
                document.querySelector('[data-bs-target="#libraryTab"]')?.click();
            }
        });

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
        // Capture the previous status before overwriting so we can detect
        // transitions (e.g. Pending → Processing) — used below to backfill
        // the queued container only on the moment of dispatch, not on every
        // progress tick within a transfer phase.
        const prev          = this._workItems.get(workItem.id);
        const prevStatus    = prev ? getStatusString(prev.status) : null;
        const prevWasQueued = prev == null || !TRANSFER_STATUSES.includes(prevStatus);

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

        // Backfill the queued container on the *transition* into a
        // transfer state — the item just left the Queued list and freed
        // a slot. Use the queue-only refresh so the in-flight transfer
        // cards in "Now Processing" aren't reconciled (which would
        // recreate their progress bars and flicker mid-upload).
        if (prevWasQueued) this._scheduleQueueRefresh();
    }

    /** Schedules a full refresh, throttled to at most once every {@link REFRESH_THROTTLE_MS}. */
    _scheduleRefresh() {
        // A full refresh supersedes a pending queue-only refresh — upgrade in
        // place rather than first-wins on the shared handle, otherwise the full
        // reconcile is silently dropped and stale "Now Processing" cards linger.
        this._pendingRefreshKind = 'full';
        this._armRefreshTimer();
    }

    /**
     * Schedules a queued-container-only refresh. Used when an item is
     * dispatched out of Pending: we want to backfill the freed slot with
     * the next pending item, but reconciling the processing container
     * would tear down and rebuild in-flight transfer cards (cancelling
     * their progress bars). Throttled on the same handle so a burst of
     * dispatches collapses into one fetch; a pending FULL refresh keeps
     * its priority (full ⊃ queue-only).
     */
    _scheduleQueueRefresh() {
        if (this._pendingRefreshKind !== 'full') this._pendingRefreshKind = 'queue';
        this._armRefreshTimer();
    }

    /** Arms the shared throttle timer; on fire, dispatches by pending kind. */
    _armRefreshTimer() {
        if (this._refreshTimer) return;

        this._refreshTimer = setTimeout(() => {
            this._refreshTimer = null;
            const kind = this._pendingRefreshKind;
            this._pendingRefreshKind = null;
            if (kind === 'full') this.loadItems();
            else this._refreshQueueOnly();
        }, REFRESH_THROTTLE_MS);
    }

    /**
     * Re-fetches stats + the current queued page and reconciles ONLY the
     * queued container. Leaves the "Now Processing" container untouched —
     * SignalR `WorkItemUpdated` ticks keep those cards current via
     * `updateWorkItemDom`.
     */
    async _refreshQueueOnly() {
        if (!document.getElementById('workItemsContainer')) return;

        try {
            const skip = this._page * PAGE_SIZE;
            const [stats, data] = await Promise.all([
                queueApi.getStats(),
                queueApi.getItems(PAGE_SIZE, skip, this._filter ?? undefined),
            ]);

            const queueItems = data.items;
            this._total = data.total;

            this._reconcileQueueContainer(queueItems);

            // Prune DB-sourced pending entries ("mf-…") that left the current
            // page. Only the full loadItems() reconciles deletions, and during a
            // multi-hour sweep this queue-only path runs thousands of times —
            // without pruning, every tile that ever crossed page 1 stays in the
            // map for the session.
            const fetchedIds = new Set(queueItems.map((i) => i.id));
            for (const id of [...this._workItems.keys()]) {
                if (id.startsWith('mf-') && !fetchedIds.has(id)) this._workItems.delete(id);
            }

            if (queueItems.length === 0) {
                this._renderEmptyQueue(stats);
            }

            this._updateStatCounters(stats);
            this._renderPagination();
            this._renderFilterTabs(stats);
        } catch (err) {
            console.error('Error refreshing queue:', err);
        }
    }

    /**
     * Renders the empty-queue state. Three flavors:
     *  - a status-filter with no matches → plain "no X items" line,
     *  - an established library with nothing queued → plain "no files" line,
     *  - a true first run (nothing known to Snacks at all, no watched folders)
     *    → a "get started" hero walking through preset → library → auto-scan.
     *
     * @param {{ total: number }} stats Aggregate counters from /api/queue/stats.
     */
    async _renderEmptyQueue(stats) {
        const queueContainer = document.getElementById('workItemsContainer');
        if (!queueContainer) return;

        if (this._filter) {
            queueContainer.innerHTML = `<div class="text-muted text-center py-4"><i class="fas fa-inbox fa-2x mb-2"></i><br>No ${this._filter.toLowerCase()} items</div>`;
            return;
        }

        // Generation guard: this method awaits a config fetch below, and a
        // newer load may render real cards (or a newer empty state) in the
        // meantime — a stale resolve must not overwrite them.
        const gen = (this._emptyQueueGen = (this._emptyQueueGen || 0) + 1);

        let watchedDirs = 0;
        if (stats.total === 0) {
            try {
                const cfg = await autoScanApi.getConfig();
                watchedDirs = (cfg.directories || []).length;
            } catch { /* treat as configured — never block the simple message on an error */ watchedDirs = 1; }
            if (gen !== this._emptyQueueGen) return;
            // Items may have arrived while we awaited — never paint over them.
            if (queueContainer.querySelector('.work-item')) return;
        }

        if (stats.total > 0 || watchedDirs > 0) {
            queueContainer.innerHTML = '<div class="text-muted text-center py-4"><i class="fas fa-inbox fa-2x mb-2"></i><br>No files in queue</div>';
            return;
        }

        queueContainer.innerHTML = `
            <div class="text-center py-4 onboarding-hero">
                <h4 class="mb-1">Welcome to Snacks <span aria-hidden="true">🍿</span></h4>
                <p class="text-muted mb-4">Three steps to a smaller, cleaner media library.</p>
                <div class="row g-3 justify-content-center text-start">
                    <div class="col-12 col-md-4">
                        <div class="card h-100">
                            <div class="card-body">
                                <div class="fw-bold mb-1"><span class="badge bg-primary me-2">1</span>Pick a preset</div>
                                <div class="small text-muted mb-3">Choose a quality profile — "Balanced" is great for Plex libraries.</div>
                                <button type="button" class="btn btn-sm btn-outline-primary" data-onboard="preset"><i class="fas fa-wand-magic-sparkles me-1"></i>Choose preset</button>
                            </div>
                        </div>
                    </div>
                    <div class="col-12 col-md-4">
                        <div class="card h-100">
                            <div class="card-body">
                                <div class="fw-bold mb-1"><span class="badge bg-primary me-2">2</span>Add your media</div>
                                <div class="small text-muted mb-3">Browse to a folder and queue it — or dry-run "Analyze" first to preview what would happen.</div>
                                <button type="button" class="btn btn-sm btn-primary" data-onboard="library"><i class="fas fa-folder-plus me-1"></i>Browse library</button>
                            </div>
                        </div>
                    </div>
                    <div class="col-12 col-md-4">
                        <div class="card h-100">
                            <div class="card-body">
                                <div class="fw-bold mb-1"><span class="badge bg-primary me-2">3</span>Automate it</div>
                                <div class="small text-muted mb-3">Watch folders so new downloads are scanned and converted automatically.</div>
                                <button type="button" class="btn btn-sm btn-outline-primary" data-onboard="watch"><i class="fas fa-folder-tree me-1"></i>Watch folders</button>
                            </div>
                        </div>
                    </div>
                </div>
            </div>`;
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

            // Reconcile the map additively instead of clear()-then-refill: a
            // SignalR update that landed during the awaits above would be wiped
            // by clear(), leaving stale data behind the retry/stop/log buttons.
            const fetchedIds = new Set(
                [...processingItems, ...queueItems].map((i) => i.id));
            for (const id of [...this._workItems.keys()]) {
                if (!fetchedIds.has(id)) this._workItems.delete(id);
            }
            for (const item of [...processingItems, ...queueItems]) {
                this._workItems.set(item.id, item);
            }

            this._reconcileProcessingContainer(processingItems);
            this._reconcileQueueContainer(queueItems);

            this._updateStatCounters(stats);

            if (queueItems.length === 0) {
                this._renderEmptyQueue(stats);
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

    /**
     * Re-queues a Failed or NoSavings item by clearing its DB failure state
     * and re-adding the file under the current encoder options. The card is
     * removed via SignalR `WorkItemRemoved`, and a fresh `WorkItemAdded`
     * arrives moments later.
     */
    async retry(id, filePath) {
        try {
            await queueApi.retry(filePath);
            // Remove the card optimistically — the SignalR removal arrives moments
            // later, but the user sees instant feedback from the click.
            this._workItems.delete(id);
            document.getElementById(`work-item-${id}`)?.remove();
            showToast('Item re-queued', 'info');
            this._scheduleRefresh();
        } catch (err) {
            showToast('Error retrying item: ' + err.message, 'danger');
        }
    }

    /**
     * Moves a pending item to the front of the queue, then refreshes so the
     * card jumps to its new position. A 404 means the item started processing
     * (or finished) between render and click — the refresh resolves that too.
     */
    async prioritize(id) {
        try {
            await queueApi.prioritize(id);
            showToast('Moved to front of queue', 'info');
        } catch {
            showToast('Item is no longer pending', 'warning');
        }
        this.loadItems();
    }

    /**
     * Called on SignalR `QueueChanged` — the server-side pending queue changed
     * (add, cancel, prioritize, bulk reset). Pending tiles are fetch-driven, so
     * a throttled queued-container refresh is all that's needed.
     */
    queueChanged() {
        this._scheduleQueueRefresh();
    }

    /** Called on SignalR `WorkItemRemoved`. Drops the card from the in-memory map and DOM. */
    removeItem(id) {
        this._workItems.delete(id);
        document.getElementById(`work-item-${id}`)?.remove();
        this._scheduleRefresh();
    }
}
