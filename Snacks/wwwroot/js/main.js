/**
 * Frontend composition root.
 *
 * This file is the single place that knows how every other frontend module
 * fits together. It:
 *
 *   1. Constructs the shared controllers (modal controller, SignalR client,
 *      connection-status indicator).
 *   2. Builds domain components bottom-up so each can receive its dependencies
 *      via constructor injection (no hidden globals).
 *   3. Binds DOM events (no data fetches — those happen in step 4).
 *   4. Kicks off initial data loads.
 *   5. Wires the settings-modal shell: opening, auto-save, sidecar visibility.
 *   6. Wires SignalR hub events to the components that care about them.
 *   7. Handles tab-visibility changes (iOS Safari suspends WebSockets when
 *      backgrounded — retry + resync when the tab comes back).
 *   8. Exposes a small legacy-compat surface on `window.transcodingManager`
 *      so any remaining inline `onclick="..."` handlers in razor partials
 *      keep working during the migration.
 */


// ---------------------------------------------------------------------------
// Imports
// ---------------------------------------------------------------------------

import { SignalRClient }        from './core/signalr-client.js';
import { ConnectionStatus }     from './core/connection-status.js';

import { initModalController, showConfirmModal } from './utils/modal-controller.js';
import { escapeHtml }           from './utils/dom.js';

import { PauseControl }         from './queue/pause-control.js';
import { LogViewer }            from './queue/log-viewer.js';
import { StopCancelDialog }     from './queue/stop-cancel-dialog.js';
import { QueueManager }         from './queue/queue-manager.js';

import { LibraryBrowser }       from './library/library-browser.js';
import { AnalyzeModal }         from './library/analyze-modal.js';

import { ClusterDashboard }     from './cluster/cluster-dashboard.js';
import { ClusterSettingsForm }  from './cluster/cluster-settings-form.js';
import { OverrideDialog }       from './cluster/override-dialog.js';

import { initAllChipInputs }    from './settings/chip-input.js';
import { initFolderPicker }     from './settings/folder-picker.js';
import { initSettingsTabs, applySettingsRoleVisibility } from './settings/settings-tabs.js';
import { restoreEncoderOptions, getEncoderOptions } from './settings/encoder-form.js';

import { AutoScanPanel }                                  from './settings/panels/auto-scan-panel.js';
import { initIntegrationsPanel,  loadIntegrationsPanel }  from './settings/panels/integrations-panel.js';
import { initNotificationsPanel, loadNotificationsPanel } from './settings/panels/notifications-panel.js';
import { initAuthPanel,          loadAuthPanel }          from './settings/panels/auth-panel.js';
import { initExclusionPanel,     loadExclusionPanel }     from './settings/panels/exclusion-panel.js';
import { initAdvancedPanel,      loadAdvancedPanel }      from './settings/panels/advanced-panel.js';
import { initNodeSyncPanel,      loadNodeSyncPanel }      from './settings/panels/node-sync-panel.js';


// ---------------------------------------------------------------------------
// Legacy globals
// ---------------------------------------------------------------------------

// Some razor partials still have inline `onclick="..."` handlers that expect
// these helpers on `window`. Expose them so we can finish the migration in
// smaller PRs without breaking production pages.
if (typeof window !== 'undefined') {
    window.escapeHtml       = escapeHtml;
    window.showConfirmModal = showConfirmModal;
}


// ---------------------------------------------------------------------------
// Bootstrap
// ---------------------------------------------------------------------------

document.addEventListener('DOMContentLoaded', () => {

    // 1. Global shared controllers.
    initModalController();

    const connectionStatus = new ConnectionStatus();
    const signalR          = new SignalRClient();


    // 2. Build components bottom-up so each can receive its dependencies.

    // Leaf components first.
    const logViewer    = new LogViewer();
    const pauseControl = new PauseControl();

    // AutoScanPanel and OverrideDialog form a small cycle — the override
    // dialog reads the panel's last-loaded config and calls back on save,
    // while the panel opens the dialog for per-folder overrides. We break
    // the cycle with an indirection box that the dialog reads lazily.
    const autoScanPanelRef = {};

    const nodeOverride = new OverrideDialog({
        getLastAutoScanConfig: () => autoScanPanelRef.current?._lastConfig ?? null,
        onFolderSaved:         () => autoScanPanelRef.current?.reload(),
    });

    const autoScan = new AutoScanPanel({ nodeOverrideDialog: nodeOverride });
    autoScanPanelRef.current = autoScan;

    const clusterDashboard = new ClusterDashboard({
        nodeOverrideDialog:   nodeOverride,
        onClusterModeChanged: (enabled, role) => {
            queueManager.setClusterMode(enabled, role);
            applySettingsRoleVisibility(role);
        },
    });
    // Expose the dashboard so other scripts (e.g. Node Sync tab) can read the
    // current role without round-tripping through the config API.
    if (typeof window !== 'undefined') window.clusterDashboard = clusterDashboard;
    const clusterSettings = new ClusterSettingsForm({ dashboard: clusterDashboard });

    const stopCancelDialog = new StopCancelDialog(
        (id) => queueManager.stop(id),
        (id) => queueManager.cancel(id),
    );

    const queueManager  = new QueueManager({ stopCancelDialog, logViewer, pauseControl });
    const analyzeModal  = new AnalyzeModal();
    const library       = new LibraryBrowser({
        onWatchAdded:       () => autoScan.reload(),
        onAnalyzeRequested: (dirPath, recursive) => analyzeModal.open(dirPath, recursive),
    });


    // 3. Initialize (bind DOM events). No data fetches here — those happen in step 4.
    pauseControl.init();
    queueManager.init();
    library.init();
    analyzeModal.init();
    autoScan.init();
    clusterSettings.init();

    initAllChipInputs();
    initFolderPicker();
    initSettingsTabs();
    initIntegrationsPanel();
    initNotificationsPanel();
    initAuthPanel();
    initExclusionPanel();
    initAdvancedPanel();
    initNodeSyncPanel();


    // 4. Initial data loads.
    restoreEncoderOptions('settings');
    autoScan.load();
    clusterDashboard.load();


    // 5. Settings modal shell.

    const settingsModal = document.getElementById('settingsModal');
    let settingsPanelsLoaded = false;

    document.getElementById('openSettingsBtn')?.addEventListener('click', () => {
        settingsModal.classList.add('open');
        autoScan.load();
        clusterSettings.load();

        // Load the secondary panels lazily on first open — they each hit an
        // auth-gated endpoint and we don't want to 401 on initial page load.
        if (!settingsPanelsLoaded) {
            settingsPanelsLoaded = true;
            loadIntegrationsPanel();
            loadNotificationsPanel();
            loadAuthPanel();
            loadExclusionPanel();
            loadAdvancedPanel();
            loadNodeSyncPanel();
        }
    });

    // Closing the settings modal also closes the per-node override dialog,
    // since the override dialog is logically nested inside the settings UX.
    new MutationObserver(() => {
        if (!settingsModal.classList.contains('open')) nodeOverride.close();
    }).observe(settingsModal, { attributes: true, attributeFilter: ['class'] });

    // Auto-save encoder settings on any input change in the main settings
    // (exclude auto-scan fields + the override dialog, which have their own
    // save paths).
    const isMainSettingsField = (e) =>
        !e.target.id.startsWith('autoScan') && !e.target.closest('#overrideDialog');

    settingsModal.addEventListener('change', (e) => {
        if (isMainSettingsField(e)) getEncoderOptions('settings');
    });
    settingsModal.addEventListener('input', (e) => {
        if (isMainSettingsField(e)) getEncoderOptions('settings');
    });

    // The sidecar format dropdown is always visible so users can see the
    // option exists; it's disabled/dimmed until its enable checkbox is on.
    const sidecarToggle = document.getElementById('settingsExtractSubtitlesToSidecar');
    const sidecarWrap   = document.getElementById('settingsSidecarFormatWrapper');
    const sidecarSelect = document.getElementById('settingsSidecarSubtitleFormat');
    const syncSidecar = () => {
        const enabled = !!sidecarToggle?.checked;
        if (sidecarSelect) sidecarSelect.disabled = !enabled;
        if (sidecarWrap)   sidecarWrap.style.opacity = enabled ? '' : '0.55';
    };
    sidecarToggle?.addEventListener('change', syncSidecar);
    setTimeout(syncSidecar, 200);


    // 6. Wire SignalR hub events to component methods.

    signalR.onOpen(() => {
        connectionStatus.setConnected();
        queueManager.loadItems();
        clusterDashboard.load();
    });
    signalR.onClose(() => connectionStatus.setDisconnected());

    signalR.on('WorkItemAdded',     (wi)      => queueManager.addItem(wi));
    signalR.on('WorkItemUpdated',   (wi)      => {
        queueManager.updateItem(wi);
        // The dashboard self-card mirrors the master's own slot occupancy;
        // refresh it on local-job transitions so chips and per-job bars stay
        // in sync as encodes start, progress, and finish.
        clusterDashboard.onWorkItemUpdated?.(wi);
    });
    signalR.on('TranscodingLog',    (id, msg) => logViewer.appendLine(id, msg));

    signalR.on('AutoScanCompleted', (newFiles) => {
        showToast(
            `Auto-scan complete: ${newFiles} new file(s) found`,
            newFiles > 0 ? 'success' : 'info',
        );
        autoScan.reload();
    });

    signalR.on('HistoryCleared', () => {
        logViewer.clear();
        queueManager.reset();
    });

    signalR.on('WorkerConnected',      (node)   => clusterDashboard.onWorkerConnected(node));
    signalR.on('WorkerDisconnected',   (id)     => clusterDashboard.onWorkerDisconnected(id));
    signalR.on('WorkerUpdated',        (node)   => clusterDashboard.onWorkerUpdated(node));
    signalR.on('HardwareDetected',     ()       => clusterDashboard.onHardwareDetected());
    signalR.on('ClusterConfigChanged', (config) => clusterDashboard.onClusterConfigChanged(config));
    signalR.on('ClusterNodePaused',    (paused) => pauseControl.setFromRemote(paused));

    signalR.start();


    // 7. Tab-visibility handling.
    //    iOS Safari suspends WebSockets when the tab is backgrounded; when
    //    it comes back we may need to reconnect and always need to resync
    //    the queue in case we missed updates.
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState !== 'visible') return;

        if (signalR.state() !== 'Connected') signalR.start();
        else                                 connectionStatus.setConnected();

        queueManager.loadItems();
    });


    // 8. Legacy inline-handler compatibility.
    //    Exposes the queue manager on `window` for any remaining
    //    `onclick="transcodingManager.foo()"` references in razor partials.
    window.transcodingManager = {
        getEncoderOptions,
        restoreSettings:         (p = 'settings') => restoreEncoderOptions(p),
        processCurrentDirectory: (recursive)      => library.processCurrentDirectory(recursive),
        loadDirectoryFiles:      (path)           => library.loadSubdirectories(path),
    };
});
