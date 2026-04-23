/**
 * Cluster-tab settings form.
 *
 * Loads/saves the cluster configuration and manages role UI, the manual-node
 * list, shared-secret generation, and the "Switch to Standalone" banner
 * action. After a save it notifies the dashboard instance so the top-of-page
 * panel repaints without a full reload.
 */

import { clusterApi, appApi } from '../api.js';
import { escapeHtml }         from '../utils/dom.js';
import { showConfirmModal }   from '../utils/modal-controller.js';


// ---------------------------------------------------------------------------
// ClusterSettingsForm
// ---------------------------------------------------------------------------

export class ClusterSettingsForm {

    /**
     * @param {object} deps
     * @param {import('./cluster-dashboard.js').ClusterDashboard} deps.dashboard
     */
    constructor({ dashboard }) {
        this._dashboard         = dashboard;
        this._manualNodes       = [];
        this._hasExistingSecret = false;
    }


    // ---- Init ----

    /**
     * Wires the form's DOM controls. Safe to call once at startup.
     */
    init() {
        document.getElementById('clusterRole')?.addEventListener('change',
            (e) => this._updateRoleUI(e.target.value));

        document.getElementById('saveClusterConfig')    ?.addEventListener('click', () => this._save());
        document.getElementById('generateSecret')       ?.addEventListener('click', () => this._generateSecret());
        document.getElementById('toggleSecretVisibility')?.addEventListener('click', () => this._toggleSecretVisibility());
        document.getElementById('addManualNode')        ?.addEventListener('click', () => this._addManualNode());
        document.getElementById('switchToStandalone')   ?.addEventListener('click', () => this._switchToStandalone());
    }


    // ---- Load ----

    /**
     * Populates every form field from the persisted cluster config.
     * Silent on failure because the advanced panel is secondary — users
     * shouldn't see an error toast just for opening the dialog.
     */
    async load() {
        try {
            const config = await clusterApi.getConfig();
            const el = (id) => document.getElementById(id);

            if (el('clusterEnabled'))       el('clusterEnabled').checked = config.enabled;
            if (el('clusterRole'))          el('clusterRole').value      = config.role;
            if (el('clusterNodeName'))      el('clusterNodeName').value  = config.nodeName || '';

            this._hasExistingSecret = config.hasSecret;
            if (el('clusterSecret')) {
                el('clusterSecret').value       = '';
                el('clusterSecret').placeholder = config.hasSecret ? '(secret configured)' : 'Enter a shared secret';
            }

            if (el('clusterAutoDiscovery')) el('clusterAutoDiscovery').checked = config.autoDiscovery        !== false;
            if (el('clusterLocalEncoding')) el('clusterLocalEncoding').checked = config.localEncodingEnabled !== false;
            if (el('clusterMasterUrl'))     el('clusterMasterUrl').value       = config.masterUrl            || '';
            if (el('clusterNodeTempDir'))   el('clusterNodeTempDir').value     = config.nodeTempDirectory    || '';

            this._updateRoleUI(config.role);
            this._renderManualNodes(config.manualNodes || []);
        } catch (err) {
            console.error('Failed to load cluster config:', err);
        }
    }


    // ---- Save flow ----

    /**
     * Persists the form state, handling the two confirm dialogs (switching
     * to node mode, restarting the backend on network-binding changes) and
     * notifying the dashboard of the new effective mode.
     */
    async _save() {

        // Snapshot the server's current "is this a cluster?" state so we
        // can decide later whether a restart is required.
        let config;
        let serverWasCluster = false;
        try {
            config           = await clusterApi.getConfig();
            serverWasCluster = config.enabled && config.role !== 'standalone';
        } catch {
            config = {};
        }

        const el = (id) => document.getElementById(id);
        config.enabled              = el('clusterEnabled')?.checked || false;
        config.role                 = el('clusterRole')?.value      || 'standalone';
        config.nodeName             = el('clusterNodeName')?.value  || '';

        const newSecret = el('clusterSecret')?.value;
        if (newSecret) config.sharedSecret = newSecret;

        config.autoDiscovery        = el('clusterAutoDiscovery')?.checked !== false;
        config.localEncodingEnabled = el('clusterLocalEncoding')?.checked !== false;
        config.masterUrl            = el('clusterMasterUrl')?.value       || '';
        config.nodeTempDirectory    = el('clusterNodeTempDir')?.value     || '';
        config.manualNodes          = this._manualNodes;

        // Enabling cluster mode requires a shared secret; reject up-front.
        if (config.enabled
            && config.role !== 'standalone'
            && !config.sharedSecret
            && !this._hasExistingSecret) {
            showToast('A shared secret is required to enable cluster mode. Enter one or click Generate.', 'danger');
            return;
        }

        // Switching to node mode is destructive to the local queue — confirm.
        if (config.role === 'node' && this._dashboard.role !== 'node') {
            const confirmed = await showConfirmModal(
                'Switch to Node Mode',
                '<p>Switching to Node mode will:</p><ul><li>Stop any active encoding</li><li>Clear the local queue</li><li>Disable auto-scanning</li></ul><p>This instance will only process jobs delegated by a master.</p>',
                'Switch to Node Mode',
            );
            if (!confirmed) return;
        }

        try {
            const result = await clusterApi.saveConfig(config);
            if (!result.success) {
                showToast('Error saving cluster settings: ' + (result.error || 'Unknown error'), 'danger');
                return;
            }

            // Notify the dashboard of the new mode so its panel repaints.
            const isCluster = config.enabled && config.role !== 'standalone';
            this._dashboard.setMode(config.enabled, config.role, config.localEncodingEnabled);

            // Brief "Saved" status pip next to the save button.
            const status = document.getElementById('clusterSaveStatus');
            if (status) {
                status.style.display = 'inline';
                setTimeout(() => status.style.display = 'none', 3000);
            }

            // Toggling cluster on/off changes the backend's network binding,
            // which requires a restart to take effect.
            if (serverWasCluster !== isCluster) {
                const confirmed = await showConfirmModal(
                    'Restart Required',
                    '<p>Snacks needs to restart to apply network binding changes.</p><p>Any active encoding will be stopped and re-queued after restart.</p>',
                    'Restart Now',
                );
                if (confirmed) await appApi.restart();
            } else {
                showToast('Cluster settings saved', 'success');
            }

            if (config.enabled) await this._dashboard.loadStatus();
            this._dashboard.render();
        } catch (err) {
            showToast('Error saving cluster settings: ' + err.message, 'danger');
        }
    }


    // ---- Role / UI helpers ----

    /**
     * Shows the master-only or node-only settings block and updates the
     * role-description label.
     *
     * @param {'master'|'node'|'standalone'} role
     */
    _updateRoleUI(role) {
        const master = document.getElementById('masterSettings');
        const node   = document.getElementById('nodeSettings');
        const desc   = document.getElementById('roleDescription');

        if (master) master.style.display = role === 'master' ? '' : 'none';
        if (node)   node.style.display   = role === 'node'   ? '' : 'none';

        if (!desc) return;
        desc.textContent =
            role === 'master' ? 'Has the media library, delegates encoding to nodes, and encodes locally' :
            role === 'node'   ? 'Accepts encoding jobs from a master instance' :
                                'Standard single-instance mode';
    }


    // ---- Manual-node list ----

    /**
     * Renders the manual-node list and wires its per-row remove buttons.
     *
     * @param {Array<{ name: string, url: string }>} nodes
     */
    _renderManualNodes(nodes) {
        this._manualNodes = nodes || [];

        const container = document.getElementById('manualNodesList');
        if (!container) return;

        if (this._manualNodes.length === 0) {
            container.innerHTML = '<div class="text-muted text-center py-2"><small>No manual nodes configured</small></div>';
            return;
        }

        container.innerHTML = this._manualNodes.map((node, idx) => `
            <div class="d-flex justify-content-between align-items-center mb-1 p-1 border rounded">
                <div>
                    <strong class="me-2">${escapeHtml(node.name)}</strong>
                    <small class="text-muted">${escapeHtml(node.url)}</small>
                </div>
                <button class="btn btn-sm btn-outline-danger remove-manual-node" data-idx="${idx}">
                    <i class="fas fa-times"></i>
                </button>
            </div>`).join('');

        container.querySelectorAll('.remove-manual-node').forEach(btn => {
            btn.addEventListener('click', () => {
                this._manualNodes.splice(parseInt(btn.dataset.idx), 1);
                this._renderManualNodes(this._manualNodes);
            });
        });
    }

    /**
     * Appends a manual node from the inline "add new" form to the in-memory list.
     */
    _addManualNode() {
        const name = document.getElementById('manualNodeName')?.value?.trim();
        const url  = document.getElementById('manualNodeUrl')?.value?.trim();
        if (!name || !url) return;

        this._manualNodes.push({ name, url });
        this._renderManualNodes(this._manualNodes);

        document.getElementById('manualNodeName').value = '';
        document.getElementById('manualNodeUrl').value  = '';
    }


    // ---- Shared secret ----

    /**
     * Generates a cryptographically random 32-character shared secret and
     * writes it into the secret input. Characters are alphanumeric only
     * (no symbols) so the secret can be pasted into any shell/config
     * without escaping.
     */
    _generateSecret() {
        const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
        const array = new Uint8Array(32);
        crypto.getRandomValues(array);

        let secret = '';
        for (let i = 0; i < 32; i++) {
            secret += chars.charAt(array[i] % chars.length);
        }

        document.getElementById('clusterSecret').value = secret;
    }

    /**
     * Toggles the secret input between `type="password"` and `type="text"`
     * and swaps the eye/eye-slash icon on the toggle button.
     */
    _toggleSecretVisibility() {
        const input      = document.getElementById('clusterSecret');
        const button     = document.getElementById('toggleSecretVisibility');
        const isPassword = input.type === 'password';

        input.type       = isPassword ? 'text' : 'password';
        button.innerHTML = `<i class="fas fa-eye${isPassword ? '-slash' : ''}"></i>`;
    }


    // ---- Switch to standalone ----

    /**
     * Confirms with the user and saves a "standalone" config, notifying the
     * dashboard so its panel collapses.
     */
    async _switchToStandalone() {
        const confirmed = await showConfirmModal(
            'Switch to Standalone',
            '<p>Switch back to standalone mode? This will disconnect from the cluster.</p>',
            'Switch to Standalone',
        );
        if (!confirmed) return;

        try {
            const config = await clusterApi.getConfig();
            config.role    = 'standalone';
            config.enabled = false;

            const result = await clusterApi.saveConfig(config);
            if (!result.success) throw new Error(result.error || 'save failed');

            this._dashboard.setMode(false, 'standalone', true);
            showToast('Switched to standalone mode', 'success');
        } catch (err) {
            showToast('Error: ' + err.message, 'danger');
        }
    }
}
