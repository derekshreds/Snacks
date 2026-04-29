/**
 * Encoding-override dialog shared between per-folder and per-node flows.
 *
 * The two contexts share a single DOM (ids prefixed with `ovr`) but drive
 * it from two different field lists:
 *
 *   - FOLDER_OVERRIDE_FIELDS: every field that takes effect when the
 *     override is baked into a dispatched job (master-local or worker).
 *   - NODE_OVERRIDE_FIELDS: strict subset — drops fields workers clobber
 *     (OutputDirectory, EncodeDirectory, DeleteOriginalFile) and master-
 *     scanner-only fields (Skip4K, SkipPercentAboveTarget).
 *
 * Fields with `data-context` attributes in Index.cshtml are hidden when
 * the current context doesn't list them, so the user only sees knobs that
 * will actually take effect.
 */

import { clusterApi, autoScanApi }    from '../api.js';
import { escapeHtml }                 from '../utils/dom.js';
import { openModal, closeModal }      from '../utils/modal-controller.js';


// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const MODAL_ID = 'overrideDialog';

/**
 * Folder-context fields. Mirrors what the main settings tabs expose, minus
 * HardwareAcceleration (each node auto-detects its own hardware).
 */
const FOLDER_OVERRIDE_FIELDS = Object.freeze({
    // General
    Format:                     'select',
    Codec:                      'select',
    TargetBitrate:              'number',
    StrictBitrate:              'bool',
    FourKBitrateMultiplier:     'select',
    Skip4K:                     'bool',
    SkipPercentAboveTarget:     'number',
    DeleteOriginalFile:         'bool',
    RetryOnFail:                'bool',

    // Video pipeline
    DownscalePolicy:            'select',
    DownscaleTarget:            'select',
    FfmpegQualityPreset:        'select',
    TonemapHdrToSdr:            'bool',
    RemoveBlackBorders:         'bool',

    // Audio
    AudioCodec:                 'select',
    AudioBitrateKbps:           'number',
    TwoChannelAudio:            'bool',
    KeepOriginalLanguage:       'bool',
    OriginalLanguageProvider:   'select',

    // Subtitles
    ExtractSubtitlesToSidecar:    'bool',
    SidecarSubtitleFormat:        'select',
    ConvertImageSubtitlesToSrt:   'bool',
    PassThroughImageSubtitlesMkv: 'bool',

    // Encoding mode
    EncodingMode:               'select',
    MuxStreams:                 'select',
});

/**
 * Node-context fields. Drops everything a worker overwrites on receipt
 * (OutputDirectory, EncodeDirectory, DeleteOriginalFile — handled at
 * ClusterNodeJobService.cs:347-349) and everything the master-side scanner
 * alone consults (Skip4K, SkipPercentAboveTarget).
 */
const NODE_OVERRIDE_FIELDS = Object.freeze({
    // General
    Format:                     'select',
    Codec:                      'select',
    TargetBitrate:              'number',
    StrictBitrate:              'bool',
    FourKBitrateMultiplier:     'select',
    RetryOnFail:                'bool',

    // Video pipeline
    DownscalePolicy:            'select',
    DownscaleTarget:            'select',
    FfmpegQualityPreset:        'select',
    TonemapHdrToSdr:            'bool',
    RemoveBlackBorders:         'bool',

    // Audio
    AudioCodec:                 'select',
    AudioBitrateKbps:           'number',
    TwoChannelAudio:            'bool',
    KeepOriginalLanguage:       'bool',
    OriginalLanguageProvider:   'select',

    // Subtitles
    ExtractSubtitlesToSidecar:    'bool',
    SidecarSubtitleFormat:        'select',
    ConvertImageSubtitlesToSrt:   'bool',
    PassThroughImageSubtitlesMkv: 'bool',

    // Encoding mode
    EncodingMode:               'select',
    MuxStreams:                 'select',
});

/** Default values restored when a numeric override is toggled off. */
const NUMBER_DEFAULTS = Object.freeze({
    TargetBitrate:          '3500',
    FourKBitrateMultiplier: '4',
    SkipPercentAboveTarget: '20',
    AudioBitrateKbps:       '192',
});


// ---------------------------------------------------------------------------
// OverrideDialog
// ---------------------------------------------------------------------------

export class OverrideDialog {

    /**
     * @param {object} deps
     * @param {() => { directories?: Array }|null} deps.getLastAutoScanConfig
     *        Returns a snapshot of the current auto-scan config for folder
     *        lookups. Called lazily to break the cycle with AutoScanPanel.
     * @param {() => void} deps.onFolderSaved
     *        Invoked after folder overrides are saved/reset so the auto-scan
     *        panel can refresh.
     */
    constructor({ getLastAutoScanConfig, onFolderSaved }) {
        this._getLastAutoScan = getLastAutoScanConfig;
        this._onFolderSaved   = onFolderSaved ?? (() => {});
        this._activeFields    = FOLDER_OVERRIDE_FIELDS;   // set per-open
    }

    /** Closes the dialog if it is currently open. */
    close() {
        closeModal(MODAL_ID);
    }


    // ---- Node settings ----

    /**
     * Opens the dialog in per-node mode.
     *
     * In master mode this edits the master's NodeSettings entry for the
     * remote node; in standalone mode the same dialog edits the local
     * machine's NodeSettings entry — only the Hardware Concurrency
     * section is shown there since 4K dispatch rules and encoding
     * overrides only fire during cluster dispatch (which standalone
     * doesn't run).
     *
     * @param {string} nodeId
     * @param {string} hostname Displayed in the title.
     * @param {{standalone?: boolean}} [opts]
     */
    async openNode(nodeId, hostname, opts = {}) {
        const standalone = opts.standalone === true;
        this._activeFields = NODE_OVERRIDE_FIELDS;
        this._applyContextVisibility('node');
        this._resetForm();

        document.getElementById('nodeOnly4K').checked    = false;
        document.getElementById('nodeExclude4K').checked = false;

        let savedDeviceSettings = {};

        try {
            const config = await clusterApi.getNodeSettings();
            const ns     = config.nodes?.[nodeId];

            if (ns) {
                document.getElementById('nodeOnly4K').checked    = ns.only4K    || false;
                document.getElementById('nodeExclude4K').checked = ns.exclude4K || false;
                if (ns.encodingOverrides) this._populateForm(ns.encodingOverrides);
                if (ns.deviceSettings) savedDeviceSettings = ns.deviceSettings;
            }
        } catch (err) {
            console.error('Failed to load node settings', err);
        }

        // Resolve the node's detected hardware devices and render the per-device
        // concurrency editor. The device list lives on the live cluster status,
        // not in node-settings (which only stores user overrides).
        try {
            const devices = await this._loadNodeDevices(nodeId);
            this._renderDeviceList(devices, savedDeviceSettings);
        } catch (err) {
            console.error('Failed to load node devices', err);
            this._renderDeviceList([], savedDeviceSettings);
        }

        this._initToggles();

        // 4K dispatch rules are mutually exclusive.
        const only4K = document.getElementById('nodeOnly4K');
        const excl4K = document.getElementById('nodeExclude4K');
        only4K.onchange = () => { if (only4K.checked) excl4K.checked = false; };
        excl4K.onchange = () => { if (excl4K.checked) only4K.checked = false; };

        const titleHtml = standalone
            ? `<i class="fas fa-microchip me-2"></i>Hardware Settings: ${escapeHtml(hostname)}`
            : `<i class="fas fa-server me-2"></i>Node Settings: ${escapeHtml(hostname)}`;

        this._open(
            titleHtml,
            { showNodeRules: !standalone, showHardware: true, showEncoding: !standalone },
            () => this._saveNode(nodeId, { standalone }),
            () => this._deleteNode(nodeId),
        );
    }

    /**
     * Persists the current form state as the per-node override for `nodeId`.
     *
     * Encoding overrides are only saved when at least one field has a
     * non-null value — an all-null payload would just thrash the server
     * without changing behavior. Hardware concurrency entries persist
     * even at their defaults, since the user may have explicitly chosen
     * "use the detected default" by leaving the slot count alone.
     *
     * @param {string} nodeId
     */
    async _saveNode(nodeId, opts = {}) {
        const standalone     = opts.standalone === true;
        const overrides      = standalone ? {} : this._readForm();
        const deviceSettings = this._readDeviceSettings();

        const settings = {
            nodeId,
            only4K:            !standalone && document.getElementById('nodeOnly4K').checked    ? true : null,
            exclude4K:         !standalone && document.getElementById('nodeExclude4K').checked ? true : null,
            encodingOverrides: !standalone && Object.values(overrides).some(v => v !== null) ? overrides : null,
            deviceSettings:    Object.keys(deviceSettings).length > 0 ? deviceSettings : null,
        };

        try {
            await clusterApi.saveNodeSettings(settings);
            showToast('Node settings saved', 'success');
            this.close();
        } catch (err) {
            showToast('Error saving node settings: ' + err.message, 'danger');
        }
    }

    /**
     * Looks up the detected hardware devices for a node.
     *
     * Master-side workers report devices on `selfCapabilities`; remote nodes
     * carry them under `node.capabilities.devices`. We check both so a user
     * can edit settings for any node from any other instance.
     *
     * @param {string} nodeId
     * @returns {Promise<Array>}
     */
    async _loadNodeDevices(nodeId) {
        const status = await clusterApi.getStatus();
        if (status?.nodeId === nodeId)
            return status.selfCapabilities?.devices || [];
        const node = (status?.nodes || []).find(n => n.nodeId === nodeId);
        return node?.capabilities?.devices || [];
    }

    /**
     * Paints one row per detected device with an enable toggle and a
     * slot-count input. Existing user overrides take precedence over the
     * worker's defaults; a missing override falls back to the device's
     * `defaultConcurrency`.
     *
     * @param {Array<{deviceId: string, displayName: string, defaultConcurrency: number, isHardware: boolean, supportedCodecs: string[]}>} devices
     * @param {Object<string, {enabled?: boolean, maxConcurrency?: number}>} saved
     */
    _renderDeviceList(devices, saved) {
        const container = document.getElementById('nodeDeviceList');
        if (!container) return;

        if (!devices || devices.length === 0) {
            container.innerHTML = '<div class="text-muted small"><em>No devices reported. Run an encode or refresh to populate.</em></div>';
            return;
        }

        container.innerHTML = devices.map(d => {
            const cur     = saved?.[d.deviceId] || {};
            const enabled = cur.enabled !== false;            // default: enabled
            const max     = cur.maxConcurrency ?? d.defaultConcurrency ?? 1;
            const codecs  = (d.supportedCodecs || []).join(', ').toUpperCase() || '—';
            const badge   = d.isHardware ? 'success' : 'secondary';

            return `
                <div class="d-flex align-items-center mb-2 p-2 border rounded" data-device-id="${escapeHtml(d.deviceId)}">
                    <div class="form-check form-switch me-3">
                        <input class="form-check-input device-enabled" type="checkbox" id="dev_en_${escapeHtml(d.deviceId)}" ${enabled ? 'checked' : ''}>
                    </div>
                    <div class="flex-grow-1" style="min-width:0;">
                        <div class="fw-bold" style="overflow:hidden; text-overflow:ellipsis; white-space:nowrap;">
                            ${escapeHtml(d.displayName || d.deviceId)}
                            <span class="badge bg-${badge} ms-1">${d.isHardware ? 'HW' : 'SW'}</span>
                        </div>
                        <small class="text-muted">Codecs: ${escapeHtml(codecs)} &middot; default ${d.defaultConcurrency || 1}</small>
                    </div>
                    <div class="ms-3" style="width: 110px;">
                        <label class="small text-muted mb-1" for="dev_max_${escapeHtml(d.deviceId)}">Max slots</label>
                        <input class="form-control form-control-sm device-max" type="number" min="0" max="16"
                               id="dev_max_${escapeHtml(d.deviceId)}" value="${max}">
                    </div>
                </div>`;
        }).join('');
    }

    /**
     * Reads the device-list form state back into a server-shaped
     * `deviceSettings` object. Only emits an entry when the user has made
     * a non-default choice (disabled the device or set a non-default slot
     * count) — keeps the persisted file from ballooning with no-ops.
     *
     * @returns {Object<string, {enabled: boolean, maxConcurrency: number|null}>}
     */
    _readDeviceSettings() {
        const result    = {};
        const container = document.getElementById('nodeDeviceList');
        if (!container) return result;

        container.querySelectorAll('[data-device-id]').forEach(row => {
            const deviceId = row.dataset.deviceId;
            const enabled  = row.querySelector('.device-enabled')?.checked ?? true;
            const maxRaw   = row.querySelector('.device-max')?.value;
            const max      = maxRaw === '' || maxRaw == null ? null : parseInt(maxRaw, 10);

            // Only persist when user diverged from defaults. Anything else
            // inherits worker-reported settings on every dispatch.
            const diverged = !enabled || (Number.isInteger(max) && max >= 0);
            if (diverged)
                result[deviceId] = { enabled, maxConcurrency: Number.isInteger(max) ? max : null };
        });
        return result;
    }

    /**
     * Resets a node's overrides back to defaults on the server.
     *
     * @param {string} nodeId
     */
    async _deleteNode(nodeId) {
        try {
            await clusterApi.deleteNodeSettings(nodeId);
            showToast('Node settings reset to defaults', 'success');
            this.close();
        } catch (err) {
            showToast('Error resetting node settings: ' + err.message, 'danger');
        }
    }


    // ---- Folder settings ----

    /**
     * Opens the dialog in per-folder mode, seeding existing overrides from
     * the auto-scan panel's cached config when available.
     *
     * @param {string} path
     */
    openFolder(path) {
        this._activeFields = FOLDER_OVERRIDE_FIELDS;
        this._applyContextVisibility('folder');
        this._resetForm();

        const config = this._getLastAutoScan?.();
        if (config?.directories) {
            const folder = config.directories.find(d =>
                (typeof d === 'string' ? d : d.path) === path);

            if (folder && typeof folder === 'object' && folder.encodingOverrides) {
                this._populateForm(folder.encodingOverrides);
            }
        }

        this._initToggles();

        const displayName = path.split(/[/\\]/).filter(Boolean).pop() || path;
        this._open(
            `<i class="fas fa-folder-open me-2"></i>Folder Settings: ${escapeHtml(displayName)}`,
            false,
            () => this._saveFolder(path),
            () => this._resetFolder(path),
        );
    }

    /**
     * Persists the current form state as per-folder overrides for `path`.
     *
     * An all-null payload is translated to a clear (`null`) so the server
     * drops the override entirely rather than storing a no-op record.
     *
     * @param {string} path
     */
    async _saveFolder(path) {
        const overrides    = this._readForm();
        const hasOverrides = Object.values(overrides).some(v => v !== null);

        try {
            await clusterApi.saveFolderSettings(path, hasOverrides ? overrides : null);
            showToast('Folder settings saved', 'success');
            this.close();
            this._onFolderSaved();
        } catch (err) {
            showToast('Error saving folder settings: ' + err.message, 'danger');
        }
    }

    /**
     * Clears all overrides for `path`.
     *
     * @param {string} path
     */
    async _resetFolder(path) {
        try {
            await clusterApi.saveFolderSettings(path, null);
            showToast('Folder settings reset to defaults', 'success');
            this.close();
            this._onFolderSaved();
        } catch (err) {
            showToast('Error resetting folder settings: ' + err.message, 'danger');
        }
    }


    // ---- Shared form plumbing ----

    /**
     * Toggles visibility of each field wrapper based on its `data-context`
     * attribute. Wrappers marked for a context different from the active
     * one are hidden so the user only sees knobs that will actually take
     * effect.
     *
     * @param {'folder'|'node'} context
     */
    _applyContextVisibility(context) {
        document.querySelectorAll('#overrideFields [data-context]').forEach(el => {
            const contexts = (el.dataset.context || '').split(',').map(s => s.trim());
            el.style.display = contexts.includes(context) ? '' : 'none';
        });
    }

    /**
     * Mutates the modal chrome and opens it. Called by both `openNode` and
     * `openFolder` after they populate the form.
     *
     * @param {string}   titleHtml      Already-escaped title HTML.
     * @param {boolean}  showNodeRules  Whether to show the 4K dispatch block.
     * @param {Function} onSave         Save-button handler.
     * @param {Function} onReset        Reset-button handler.
     */
    _open(titleHtml, sections, onSave, onReset) {
        // Backward-compat: a boolean second arg means "show 4K node rules"
        // (folder vs node distinction). The new shape is an object naming
        // each toggleable section.
        const cfg = typeof sections === 'boolean'
            ? { showNodeRules: sections, showHardware: false, showEncoding: true }
            : { showNodeRules: false, showHardware: false, showEncoding: true, ...sections };

        document.getElementById('overrideDialogTitle').innerHTML  = titleHtml;
        document.getElementById('overrideNodeRules').style.display = cfg.showNodeRules ? '' : 'none';
        const hwSection = document.getElementById('overrideHardwareConcurrency');
        if (hwSection) hwSection.style.display = cfg.showHardware ? '' : 'none';
        const encHeader = document.getElementById('overrideEncodingHeader');
        const encFields = document.getElementById('overrideFields');
        if (encHeader) encHeader.style.display = cfg.showEncoding ? '' : 'none';
        if (encFields) encFields.style.display = cfg.showEncoding ? '' : 'none';

        // Replace save/reset buttons so each invocation gets clean handlers.
        const rewire = [
            ['overrideDialogSave',  onSave],
            ['overrideDialogReset', onReset],
        ];
        for (const [id, handler] of rewire) {
            const btn   = document.getElementById(id);
            const clone = btn.cloneNode(true);
            btn.replaceWith(clone);
            clone.addEventListener('click', handler);
        }

        openModal(MODAL_ID);
    }

    /**
     * Wires each field's toggle so unchecking it disables (and blanks) the
     * corresponding value input. Only wires fields in the active context.
     */
    _initToggles() {
        for (const [field, type] of Object.entries(this._activeFields)) {
            const toggle = document.getElementById(`ovr_${field}`);
            if (!toggle) continue;
            toggle.onchange = () => {
                const el = document.getElementById(`ovr${field}`);
                if (!el) return;

                el.disabled = !toggle.checked;

                if (!toggle.checked) {
                    if      (type === 'bool')   el.checked = false;
                    else if (type === 'number') el.value   = NUMBER_DEFAULTS[field] || '0';
                    else                        el.selectedIndex = 0;
                }
            };
        }
    }

    /**
     * Clears every field in the override form back to its default, disabled state.
     */
    _resetForm() {
        for (const [field, type] of Object.entries(this._activeFields)) {
            const toggle = document.getElementById(`ovr_${field}`);
            if (toggle) toggle.checked = false;

            const el = document.getElementById(`ovr${field}`);
            if (!el) continue;

            el.disabled = true;
            if      (type === 'bool')   el.checked = false;
            else if (type === 'number') el.value   = NUMBER_DEFAULTS[field] || '0';
            else                        el.selectedIndex = 0;
        }
    }

    /**
     * Populates the form from a server-side overrides object. Server keys
     * are camelCased versions of our PascalCase field names.
     *
     * @param {object} overrides
     */
    _populateForm(overrides) {
        for (const [field, type] of Object.entries(this._activeFields)) {
            const key = field.charAt(0).toLowerCase() + field.slice(1);
            const val = overrides[key];
            if (val === null || val === undefined) continue;

            const toggle = document.getElementById(`ovr_${field}`);
            if (toggle) toggle.checked = true;

            const el = document.getElementById(`ovr${field}`);
            if (!el) continue;

            el.disabled = false;
            if (type === 'bool') el.checked = val;
            else                 el.value   = val;
        }
    }

    /**
     * Reads the form back into a server-shaped overrides object. Unchecked
     * toggles map to `null` so the server can distinguish "override off"
     * from "override set to the default value."
     *
     * @returns {Object<string, any>}
     */
    _readForm() {
        const result = {};

        for (const [field, type] of Object.entries(this._activeFields)) {
            const key    = field.charAt(0).toLowerCase() + field.slice(1);
            const toggle = document.getElementById(`ovr_${field}`);

            if (!toggle?.checked) {
                result[key] = null;
                continue;
            }

            const el = document.getElementById(`ovr${field}`);
            if (!el) {
                result[key] = null;
                continue;
            }

            if      (type === 'bool')   result[key] = el.checked;
            else if (type === 'number') result[key] = parseInt(el.value) || 0;
            else                        result[key] = el.value;
        }

        return result;
    }
}
