/**
 * Top-of-page cluster dashboard.
 *
 * Owns the `_workers` map plus the `clusterEnabled` / `clusterRole` state,
 * and renders the worker-card grid and the node-mode banner. The composition
 * root wires this instance to the relevant SignalR events
 * (`WorkerConnected`, `WorkerUpdated`, ...) and to a callback that lets
 * dependent components (currently the queue manager) react to cluster-mode
 * changes.
 */

import { clusterApi } from '../api.js';
import { escapeHtml } from '../utils/dom.js';


// ---------------------------------------------------------------------------
// Enumerations
// ---------------------------------------------------------------------------

/**
 * Maps both numeric and already-string status values to a canonical
 * status name. The hub ships these as numbers but there are edge cases
 * (e.g. structured log messages) where the string is passed through as-is.
 */
const STATUS_NAMES = Object.freeze({
    0: 'Online',
    1: 'Busy',
    2: 'Uploading',
    3: 'Downloading',
    4: 'Offline',
    5: 'Unreachable',
    6: 'Paused',

    Online:      'Online',
    Busy:        'Busy',
    Uploading:   'Uploading',
    Downloading: 'Downloading',
    Offline:     'Offline',
    Unreachable: 'Unreachable',
    Paused:      'Paused',
});

/** Status-indicator color per canonical status name, with CSS-variable fallbacks. */
const STATUS_COLORS = Object.freeze({
    Online:      'var(--success-color, #28a745)',
    Busy:        'var(--info-color, #17a2b8)',
    Uploading:   'var(--info-color, #17a2b8)',
    Downloading: 'var(--info-color, #17a2b8)',
    Offline:     'var(--danger-color, #dc3545)',
    Unreachable: 'var(--warning-color, #ffc107)',
    Paused:      'var(--warning-color, #ffc107)',
});


// ---------------------------------------------------------------------------
// ClusterDashboard
// ---------------------------------------------------------------------------

export class ClusterDashboard {

    /**
     * @param {object} deps
     * @param {{ openNode(nodeId: string, hostname: string): void }}         deps.nodeOverrideDialog
     * @param {(enabled: boolean, role: string) => void}                      deps.onClusterModeChanged
     */
    constructor({ nodeOverrideDialog, onClusterModeChanged }) {
        /** @type {Map<string, object>} node id → worker payload */
        this._workers       = new Map();

        this._enabled       = false;
        this._role          = 'standalone';
        this._nodeId        = null;
        this._nodeName      = null;
        this._localEncoding = true;
        this._selfCaps      = null;
        this._selfActive    = [];   // ActiveJobInfo[] for the local machine's slots
        this._selfVersion   = null;
        this._localDone     = 0;
        this._localFailed   = 0;

        /**
         * Per-node settings keyed by NodeId, mirroring the server-side
         * NodeSettingsConfig. Used to derive the *effective* per-device
         * concurrency cap when rendering chips so user overrides show up
         * the moment the user saves them, not on the next reload.
         * @type {Object<string, {deviceSettings?: Object<string, {enabled?: boolean, maxConcurrency?: number|null}>}>}
         */
        this._nodeSettings = {};

        this._nodeOverride         = nodeOverrideDialog;
        this._onClusterModeChange  = onClusterModeChanged ?? (() => {});
    }

    get enabled() { return this._enabled; }
    get role()    { return this._role; }


    // ---- SignalR event handlers ----

    /**
     * `WorkerConnected` — a new (or reconnected) node joined the cluster.
     * Toasts only on genuinely new nodes, not reconnects.
     */
    onWorkerConnected(node) {
        if (!this._workers.has(node.nodeId)) {
            showToast(`Node "${node.hostname}" connected`, 'success');
        }
        this._workers.set(node.nodeId, node);
        this.render();
    }

    /** `WorkerDisconnected` — a node left the cluster. */
    onWorkerDisconnected(nodeId) {
        const node = this._workers.get(nodeId);

        this._workers.delete(nodeId);
        this.render();

        if (node) showToast(`Node "${node.hostname}" disconnected`, 'warning');
    }

    /** `WorkerUpdated` — a node's status or stats changed. */
    onWorkerUpdated(node) {
        this._workers.set(node.nodeId, node);
        this.render();
    }

    /**
     * `HardwareDetected` — reload status so the self-card's GPU info reflects
     * the newly-detected hardware.
     */
    onHardwareDetected() {
        this.loadStatus().then(() => this.render());
    }

    /**
     * `WorkItemUpdated` — when a local (non-remote) item flips into or out of
     * Processing, the master's self-active-job list has changed. Coalesce
     * refreshes through a debounce so a flurry of progress updates produces
     * a single status fetch.
     */
    onWorkItemUpdated(workItem) {
        if (!workItem || workItem.assignedNodeId) return;   // remote item — nodes update via WorkerUpdated
        if (!this._enabled || this._role === 'standalone' && !this._nodeId) return;

        // Local progress updates fire every couple of seconds; cheap render.
        const local = this._selfActive.find(j => j.jobId === workItem.id);
        if (local) {
            local.progress = workItem.progress ?? local.progress;
            local.fileName = workItem.fileName ?? local.fileName;
            this.render();
        }

        // Status transitions (started, completed, failed) need a fresh
        // server-side list. Debounce to avoid storming /status on a busy queue.
        if (this._refreshHandle) clearTimeout(this._refreshHandle);
        this._refreshHandle = setTimeout(() => this.loadStatus().then(() => this.render()), 500);
    }

    /** `ClusterConfigChanged` — role / enabled flipped; repaint and notify dependents. */
    onClusterConfigChanged(config) {
        this._enabled = config.enabled;
        this._role    = config.role;

        this.render();
        this._updateBanner();
        this._onClusterModeChange(this._enabled, this._role);
    }

    /**
     * `NodeSettingsChanged` — server pushed an updated NodeSettings config
     * (the user saved/deleted a node's overrides). Cache the new settings
     * and re-render so per-device chip caps reflect the change immediately.
     */
    onNodeSettingsChanged(config) {
        this._nodeSettings = (config && config.nodes) ? config.nodes : {};
        this.render();
    }


    // ---- Initial load / refresh ----

    /**
     * Fetches cluster config (and live status when in a clustered role),
     * repaints, and notifies dependents.
     *
     * @returns {Promise<object|null>} The config object, or null on failure.
     */
    async load() {
        try {
            const config = await clusterApi.getConfig();
            this._enabled  = config.enabled;
            this._role     = config.role;
            this._nodeId   = config.nodeId;
            this._nodeName = config.nodeName;

            // Load status in every mode so standalone users see their own
            // self-card (per-device chips, active-jobs bars, hardware cog).
            await this.loadStatus();

            this.render();
            this._updateBanner();
            this._onClusterModeChange(this._enabled, this._role);
            return config;
        } catch (err) {
            console.error('Failed to load cluster config:', err);
            return null;
        }
    }

    /**
     * Fetches the live cluster status (worker list, self-capabilities,
     * aggregated local-job counters) and updates internal state.
     */
    async loadStatus() {
        try {
            // Pull node settings alongside status so the very first render
            // already has per-device caps applied (overrides take precedence
            // over the worker-reported defaults).
            const settingsPromise = clusterApi.getNodeSettings()
                .then(cfg => { this._nodeSettings = cfg?.nodes || {}; })
                .catch(() => { /* falls back to defaults */ });

            const status = await clusterApi.getStatus();
            await settingsPromise;

            this._localEncoding = status.localEncodingEnabled !== false;
            this._selfCaps      = status.selfCapabilities  || null;
            this._selfActive    = status.localActiveJobs    || [];
            this._selfVersion   = status.selfVersion        || null;
            this._localDone     = status.localCompletedJobs || 0;
            this._localFailed   = status.localFailedJobs    || 0;

            if (status.nodeId)   this._nodeId   = status.nodeId;
            if (status.nodeName) this._nodeName = status.nodeName;

            this._workers.clear();
            for (const node of (status.nodes || [])) {
                this._workers.set(node.nodeId, node);
            }
        } catch (err) {
            console.error('Failed to load cluster status:', err);
        }
    }

    /**
     * Called by {@link ClusterSettingsForm} after saving a config that
     * changed role/enabled/local-encoding. Skips the full fetch because the
     * caller already has the new values.
     */
    setMode(enabled, role, localEncodingEnabled) {
        this._enabled       = enabled;
        this._role          = role;
        this._localEncoding = localEncodingEnabled;

        this.render();
        this._updateBanner();
        this._onClusterModeChange(this._enabled, this._role);
    }


    // ---- Rendering ----

    /**
     * Repaints the dashboard panel. No-op when the panel is not currently
     * visible (standalone mode or missing DOM).
     */
    render() {
        const panel      = document.getElementById('clusterPanel');
        const container  = document.getElementById('clusterNodesContainer');
        const countBadge = document.getElementById('clusterNodeCount');
        if (!panel || !container) return;

        // Show the panel any time we have a self-card to display. In
        // cluster mode that includes remote nodes; in standalone mode it's
        // just this machine — but the user still benefits from seeing
        // per-device slot usage and the cog for tuning concurrency.
        const showPanel = this._nodeId != null;
        panel.style.display = showPanel ? '' : 'none';
        if (!showPanel) return;

        // Master sorts first; the self-card is rendered separately below.
        const nodes = Array.from(this._workers.values())
            .filter(n => n.nodeId !== this._nodeId)
            .sort((a, b) => (a.role === 'master' ? -1 : 1) - (b.role === 'master' ? -1 : 1));

        const totalNodes = nodes.length + (this._nodeId ? 1 : 0);
        if (countBadge) countBadge.textContent = `${totalNodes} node${totalNodes !== 1 ? 's' : ''}`;

        if (nodes.length === 0 && !this._nodeId) {
            container.innerHTML = '<div class="text-muted"><i class="fas fa-search me-1"></i>Discovering nodes...</div>';
            return;
        }

        const localPaused = this._role === 'master' && !this._localEncoding;
        container.innerHTML =
            this._selfCard(localPaused)
            + nodes.map(node => this._remoteCard(node)).join('');

        this._wireCardEvents(localPaused);
    }

    /**
     * Returns the HTML for this-machine's own card. Master-only controls
     * (pause local encoding, per-node settings) only render when the local
     * role is master.
     *
     * @param {boolean} localPaused
     * @returns {string}
     */
    _selfCard(localPaused) {
        if (!this._nodeId) return '';

        const selfGpu = this._selfCaps?.gpuVendor && this._selfCaps.gpuVendor !== 'none'
            ? this._selfCaps.gpuVendor.charAt(0).toUpperCase() + this._selfCaps.gpuVendor.slice(1)
            : 'CPU only';
        const selfOs     = this._selfCaps?.osPlatform || '';
        const selfStatus = localPaused ? 'Paused' : (this._selfActive.length > 0 ? 'Busy' : 'Online');
        const selfColor  = STATUS_COLORS[selfStatus] || 'gray';
        const devicesHtml = this._renderDeviceSummary(this._nodeId, this._selfCaps, this._selfActive);

        return `
            <div class="card hover-lift cluster-card" style="min-width: 240px; max-width: 300px; flex: 1 1 260px;">
                <div class="card-body p-2 cluster-card-body">
                    ${devicesHtml}
                    <div class="d-flex align-items-center mb-1 cluster-card-title" style="min-width:0;">
                        <span class="flex-shrink-0" style="display:inline-block;width:8px;height:8px;border-radius:50%;background:${selfColor};margin-right:6px;"></span>
                        <strong style="overflow:hidden; text-overflow:ellipsis; white-space:nowrap;">${escapeHtml(this._nodeName || 'This Machine')}</strong>
                    </div>
                    <div class="text-muted small">
                        <div>${escapeHtml(this._role)} &bull; ${escapeHtml(selfOs)}${selfGpu ? ' / ' + escapeHtml(selfGpu) : ''}</div>
                        <div style="overflow:hidden; text-overflow:ellipsis; white-space:nowrap;">${escapeHtml(selfStatus)}${this._selfVersion ? ` &bull; v${escapeHtml(this._selfVersion)}` : ''}</div>
                        <div class="mt-1">Jobs: ${this._localDone} done, ${this._localFailed} failed</div>
                        ${this._role === 'master' ? `
                        <div class="d-flex gap-1 mt-1">
                            <button class="btn btn-sm ${localPaused ? 'btn-outline-success' : 'btn-outline-warning'} flex-grow-1" id="masterLocalPause">
                                <i class="fas fa-${localPaused ? 'play' : 'pause'} me-1"></i>${localPaused ? 'Resume' : 'Pause'}
                            </button>
                            <button class="btn btn-sm btn-outline-secondary cluster-node-settings" data-node-id="${this._nodeId}" data-hostname="${escapeHtml(this._nodeName || 'This Machine')}">
                                <i class="fas fa-cog"></i>
                            </button>
                        </div>` : ''}
                        ${this._role === 'standalone' ? `
                        <div class="d-flex gap-1 mt-1">
                            <button class="btn btn-sm btn-outline-secondary cluster-node-settings flex-grow-1" data-node-id="${this._nodeId}" data-hostname="${escapeHtml(this._nodeName || 'This Machine')}" title="Hardware concurrency">
                                <i class="fas fa-microchip me-1"></i>Hardware
                            </button>
                        </div>` : ''}
                    </div>
                </div>
            </div>`;
    }

    /**
     * Renders the per-device slot summary as a vertical stack of chips
     * pinned to the top-right of a node card. Each chip shows
     * "DEVICE used/cap" — capacity is the user's per-device override (from
     * NodeSettings) when present, otherwise the worker-reported default.
     * Returns an empty string when no devices have been reported yet.
     *
     * @param {string}        nodeId  The NodeId for resolving overrides.
     * @param {object|null}   caps    The node's capabilities payload.
     * @param {Array}         active  The node's reported active jobs.
     * @returns {string}
     */
    _renderDeviceSummary(nodeId, caps, active) {
        const devices = caps?.devices || [];
        if (devices.length === 0) return '';

        const used = {};
        for (const j of (active || [])) {
            if (!j.deviceId) continue;
            used[j.deviceId] = (used[j.deviceId] || 0) + 1;
        }

        const overrides = this._nodeSettings?.[nodeId]?.deviceSettings || {};

        // The chips advertise hardware encoder utilization. CPU is the
        // implicit fallback and doesn't earn its own chip — it would just
        // appear on every card and dilute the signal. CPU usage is still
        // visible in the active-jobs list (prefixed with "[cpu]") and CPU
        // concurrency is still tunable in the override dialog.
        const hwDevices = devices.filter(d => d.deviceId !== 'cpu');
        if (hwDevices.length === 0) return '';

        const chips = hwDevices.map(d => {
            const cur = overrides[d.deviceId] || {};
            const enabled = cur.enabled !== false;
            const cap = enabled
                ? (Number.isInteger(cur.maxConcurrency) ? cur.maxConcurrency : (d.defaultConcurrency || 1))
                : 0;
            const u = used[d.deviceId] || 0;

            const state = !enabled ? 'off'
                        : (u === 0 ? 'idle' : (u >= cap ? 'full' : 'busy'));
            const label = (d.deviceId || '').toUpperCase();

            return `<span class="device-chip-mini state-${state}" title="${escapeHtml(d.displayName || d.deviceId)}${enabled ? '' : ' (disabled)'}">${escapeHtml(label)} ${u}/${cap}</span>`;
        }).join('');

        return `<div class="cluster-card-chips">${chips}</div>`;
    }

    /**
     * Returns the HTML for a remote-node card. Master-only controls only
     * render when our local role is master.
     *
     * @param {object} node
     * @returns {string}
     */
    _remoteCard(node) {
        const statusName  = STATUS_NAMES[node.status]   || 'Unknown';
        const statusColor = STATUS_COLORS[statusName]   || 'gray';
        const gpuInfo     = node.capabilities?.gpuVendor && node.capabilities.gpuVendor !== 'none'
            ? node.capabilities.gpuVendor.charAt(0).toUpperCase() + node.capabilities.gpuVendor.slice(1)
            : 'CPU only';
        const osInfo     = node.capabilities?.osPlatform || '';
        const canControl = this._role === 'master' && node.role === 'node';
        const active     = node.activeJobs || [];
        const devicesHtml = this._renderDeviceSummary(node.nodeId, node.capabilities, active);

        return `
            <div class="card hover-lift cluster-card" style="min-width: 240px; max-width: 300px; flex: 1 1 260px;">
                <div class="card-body p-2 cluster-card-body">
                    ${devicesHtml}
                    <div class="d-flex align-items-center mb-1 cluster-card-title" style="min-width:0;">
                        <span class="flex-shrink-0" style="display:inline-block;width:8px;height:8px;border-radius:50%;background:${statusColor};margin-right:6px;"></span>
                        <strong style="overflow:hidden; text-overflow:ellipsis; white-space:nowrap;">${escapeHtml(node.hostname)}</strong>
                    </div>
                    <div class="text-muted small">
                        <div>${escapeHtml(node.role)} &bull; ${escapeHtml(osInfo)}${gpuInfo ? ' / ' + escapeHtml(gpuInfo) : ''}</div>
                        <div style="overflow:hidden; text-overflow:ellipsis; white-space:nowrap;">${escapeHtml(statusName)}${node.version ? ` &bull; v${escapeHtml(node.version)}` : ''}</div>
                        <div class="mt-1">Jobs: ${node.completedJobs || 0} done, ${node.failedJobs || 0} failed</div>
                        ${canControl ? `
                            <div class="d-flex gap-1 mt-1">
                                <button class="btn btn-sm ${node.isPaused ? 'btn-outline-success' : 'btn-outline-warning'} flex-grow-1 cluster-node-pause" data-node-id="${node.nodeId}" data-paused="${node.isPaused}">
                                    <i class="fas fa-${node.isPaused ? 'play' : 'pause'} me-1"></i>${node.isPaused ? 'Resume' : 'Pause'}
                                </button>
                                <button class="btn btn-sm btn-outline-secondary cluster-node-settings" data-node-id="${node.nodeId}" data-hostname="${escapeHtml(node.hostname)}" title="Node settings">
                                    <i class="fas fa-cog"></i>
                                </button>
                            </div>` : ''}
                    </div>
                </div>
            </div>`;
    }

    /**
     * Wires per-card button handlers after a render. Must be called after
     * any innerHTML rewrite of the container.
     *
     * @param {boolean} localPaused
     */
    _wireCardEvents(localPaused) {
        const container = document.getElementById('clusterNodesContainer');

        container.querySelectorAll('.cluster-node-pause').forEach(btn => {
            btn.addEventListener('click', async () => {
                try {
                    await clusterApi.setNodePaused(btn.dataset.nodeId, btn.dataset.paused !== 'true');
                } catch (err) {
                    showToast('Error: ' + err.message, 'danger');
                }
            });
        });

        container.querySelectorAll('.cluster-node-settings').forEach(btn => {
            btn.addEventListener('click', () => {
                const isSelf = btn.dataset.nodeId === this._nodeId;
                const standalone = isSelf && this._role === 'standalone';
                this._nodeOverride?.openNode(btn.dataset.nodeId, btn.dataset.hostname, { standalone });
            });
        });

        document.getElementById('masterLocalPause')?.addEventListener('click', async () => {
            try {
                await clusterApi.setLocalEncodingPaused(!localPaused);
                this._localEncoding = localPaused;     // flip state to match the server
                this.render();
            } catch (err) {
                showToast('Error: ' + err.message, 'danger');
            }
        });
    }

    /**
     * Repaints both the cluster panel and the node-mode banner from
     * cached in-memory state, without re-fetching from the server. Used by
     * the SPA shell when the queue page is re-mounted: the old DOM was
     * thrown out during navigation, but the dashboard's worker/role state
     * is still here, so we just need to draw it into the fresh DOM.
     */
    redraw() {
        this.render();
        this._updateBanner();
    }

    /**
     * Shows/hides the "running as node" banner at the top of the page and
     * updates the master's name/IP inside it.
     */
    _updateBanner() {
        const banner = document.getElementById('nodeBanner');
        if (!banner) return;

        if (!this._enabled || this._role !== 'node') {
            banner.style.display = 'none';
            return;
        }

        banner.style.display = '';

        const master     = Array.from(this._workers.values()).find(n => n.role === 'master');
        const masterName = document.getElementById('nodeBannerMaster');
        if (masterName) {
            masterName.textContent = master
                ? `${master.hostname} (${master.ipAddress})`
                : 'a master';
        }
    }
}
