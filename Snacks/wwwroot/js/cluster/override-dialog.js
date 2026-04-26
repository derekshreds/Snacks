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
     * @param {string} nodeId
     * @param {string} hostname Displayed in the title.
     */
    async openNode(nodeId, hostname) {
        this._activeFields = NODE_OVERRIDE_FIELDS;
        this._applyContextVisibility('node');
        this._resetForm();

        document.getElementById('nodeOnly4K').checked    = false;
        document.getElementById('nodeExclude4K').checked = false;

        try {
            const config = await clusterApi.getNodeSettings();
            const ns     = config.nodes?.[nodeId];

            if (ns) {
                document.getElementById('nodeOnly4K').checked    = ns.only4K    || false;
                document.getElementById('nodeExclude4K').checked = ns.exclude4K || false;
                if (ns.encodingOverrides) this._populateForm(ns.encodingOverrides);
            }
        } catch (err) {
            console.error('Failed to load node settings', err);
        }

        this._initToggles();

        // 4K dispatch rules are mutually exclusive.
        const only4K = document.getElementById('nodeOnly4K');
        const excl4K = document.getElementById('nodeExclude4K');
        only4K.onchange = () => { if (only4K.checked) excl4K.checked = false; };
        excl4K.onchange = () => { if (excl4K.checked) only4K.checked = false; };

        this._open(
            `<i class="fas fa-server me-2"></i>Node Settings: ${escapeHtml(hostname)}`,
            true,
            () => this._saveNode(nodeId),
            () => this._deleteNode(nodeId),
        );
    }

    /**
     * Persists the current form state as the per-node override for `nodeId`.
     *
     * Encoding overrides are only saved when at least one field has a
     * non-null value — an all-null payload would just thrash the server
     * without changing behavior.
     *
     * @param {string} nodeId
     */
    async _saveNode(nodeId) {
        const overrides = this._readForm();

        const settings = {
            nodeId,
            only4K:            document.getElementById('nodeOnly4K').checked    || null,
            exclude4K:         document.getElementById('nodeExclude4K').checked || null,
            encodingOverrides: Object.values(overrides).some(v => v !== null) ? overrides : null,
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
    _open(titleHtml, showNodeRules, onSave, onReset) {
        document.getElementById('overrideDialogTitle').innerHTML  = titleHtml;
        document.getElementById('overrideNodeRules').style.display = showNodeRules ? '' : 'none';

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
