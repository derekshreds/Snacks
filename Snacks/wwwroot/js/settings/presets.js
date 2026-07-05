/**
 * Quality presets.
 *
 * Two layers on top of the encoder form:
 *
 *  - Built-in presets — opinionated one-click profiles (Space Saver /
 *    Balanced / Quality First / Max Compatibility) that set the handful of
 *    fields that matter most. The matching card highlights when the current
 *    form values equal a preset; anything else reads as "Custom".
 *
 *  - User presets — named snapshots of the FULL form, stored server-side
 *    (config/presets.json) and shareable as .snacks-preset.json files via
 *    export/import. This is the groundwork for the community preset site.
 *
 * Applying any preset writes the values into the form via
 * `applyEncoderOptionsToForm` and persists through the normal auto-save path
 * (`getEncoderOptions` reads + saves), so presets and hand-edits are
 * indistinguishable to the backend.
 */

import { presetsApi } from '../api.js';
import { escapeHtml } from '../utils/dom.js';
import { streamDownload } from '../utils/download.js';
import { showConfirmModal } from '../utils/modal-controller.js';
import {
    applyEncoderOptionsToForm,
    getEncoderOptions,
    retryRestoreEncoderOptionsIfNeeded,
} from './encoder-form.js';

const PREFIX = 'settings';

/**
 * Built-in profiles. `options` lists only the fields the preset takes a
 * position on — everything else (audio, subtitles, paths…) is left alone.
 * `match` is the subset used for active-card detection.
 */
export const BUILTIN_PRESETS = [
    {
        key:  'space-saver',
        name: 'Space Saver',
        icon: 'fa-piggy-bank',
        desc: 'Smallest files that still look good on a TV. Slower encodes.',
        spec: 'H.265 · MKV · 2500 kbps · slow',
        options: { Format: 'mkv', Codec: 'h265', TargetBitrate: 2500, FourKBitrateMultiplier: 3, FfmpegQualityPreset: 'slow' },
    },
    {
        key:  'balanced',
        name: 'Balanced',
        icon: 'fa-scale-balanced',
        badge: 'Recommended',
        desc: 'The defaults — great quality-per-gigabyte for a Plex library.',
        spec: 'H.265 · MKV · 3500 kbps · medium',
        options: { Format: 'mkv', Codec: 'h265', TargetBitrate: 3500, FourKBitrateMultiplier: 4, FfmpegQualityPreset: 'medium' },
    },
    {
        key:  'quality-first',
        name: 'Quality First',
        icon: 'fa-gem',
        desc: 'Near-transparent quality for home-theater setups. Bigger files.',
        spec: 'H.265 · MKV · 6000 kbps · slow',
        options: { Format: 'mkv', Codec: 'h265', TargetBitrate: 6000, FourKBitrateMultiplier: 4, FfmpegQualityPreset: 'slow' },
    },
    {
        key:  'max-compat',
        name: 'Max Compatibility',
        icon: 'fa-plug',
        desc: 'H.264 in MP4 — direct-plays on almost anything, including old TVs.',
        spec: 'H.264 · MP4 · 4500 kbps · medium',
        options: { Format: 'mp4', Codec: 'h264', TargetBitrate: 4500, FourKBitrateMultiplier: 4, FfmpegQualityPreset: 'medium' },
    },
    {
        key:  'ipod-classic',
        name: 'iPod Classic',
        icon: 'fa-music',
        desc: '640×480 H.264 Baseline for older iPods (Classic, 5th Gen, Nano). AAC stereo at 48 kHz.',
        spec: 'H.264 Baseline L3.0 · MP4 · 640×480 · 1500 kbps · AAC 160k',
        options: {
            Format: 'mp4',
            Codec: 'h264',
            // Force software (libx264): the Baseline profile + Level 3.0 flags below are
            // only emitted for lib* encoders, and HW encoders (VideoToolbox/VAAPI/NVENC)
            // produce a High-profile stream the older iPods can't decode. See
            // TranscodingService profile/level gate.
            HardwareAcceleration: 'none',
            TargetBitrate: 1500,
            StrictBitrate: true,
            FourKBitrateMultiplier: 1,
            FfmpegQualityPreset: 'medium',
            FixedFrameSize: '640x480',
            VideoProfile: 'baseline',
            VideoLevel: '3.0',
            // Level 3.0 at 640×480 tops out at ~33 fps; cap so 50/60 fps sources stay
            // conformant (24/25/30 fps sources are left untouched).
            MaxFrameRate: 30,
            TonemapHdrToSdr: true,
            PreserveOriginalAudio: false,
            AudioOutputs: [
                { Codec: 'aac', Layout: 'Stereo', BitrateKbps: 160, SampleRateHz: 48000 },
            ],
        },
    },
];

/**
 * Reset floor layered UNDER a preset's own options on every apply (built-in AND user).
 *
 * The writer in applyEncoderOptionsToForm only touches fields the incoming object
 * actually carries; a field that's absent is left at whatever the form currently shows.
 * That's the leak: applying a preset that doesn't mention a "sticky" field lets a value
 * from a previously applied profile linger. It bites two ways —
 *   1. Built-in presets are partial by design (they list only a handful of fields), so
 *      switching from the iPod Classic device profile to Balanced would otherwise keep
 *      640×480 / Baseline / fps-cap / forced-software stuck on.
 *   2. A USER preset saved before a field existed won't carry that key, so flipping to
 *      iPod Classic and back to that old custom preset hits the exact same bug.
 * Layering these defaults underneath fixes both: the preset's own values always win
 * (spread last), so a modern full snapshot is unaffected — only genuinely-absent keys
 * fall back to their default.
 *
 * Listed here = every field that (a) changes encode behavior/mode and (b) isn't set by
 * ALL built-in presets. Fields every preset sets (Format/Codec/bitrate/…) never leak;
 * fields no preset touches (subtitles, languages, paths) are intentionally left alone.
 * When adding a new "sticky" encoder field in the future, add its default here too.
 */
const PRESET_BASELINE = Object.freeze({
    HardwareAcceleration:  'auto',
    StrictBitrate:         false,
    FixedFrameSize:        null,
    VideoProfile:          null,
    VideoLevel:            null,
    MaxFrameRate:          0,
    TonemapHdrToSdr:       false,
    PreserveOriginalAudio: true,
    AudioOutputs:          [],
});

let userPresets = [];


// ---------------------------------------------------------------------------
// Active-preset detection
// ---------------------------------------------------------------------------

/** Reads the form field that corresponds to a preset option key. */
function formValue(key) {
    const node = document.getElementById(`${PREFIX}${key}`);
    if (!node) return undefined;
    return node.type === 'checkbox' ? node.checked : node.value;
}

/** Reads the audio-outputs editor rows as an array of {Codec,Layout,BitrateKbps,SampleRateHz}. */
function formAudioOutputs() {
    const root = document.getElementById(`${PREFIX}AudioOutputs`);
    if (!root) return [];
    return Array.from(root.querySelectorAll('[data-audio-output-row]')).map(row => ({
        Codec:       row.querySelector('[data-field="Codec"]')?.value       ?? 'aac',
        Layout:      row.querySelector('[data-field="Layout"]')?.value      ?? 'Source',
        BitrateKbps: parseInt(row.querySelector('[data-field="BitrateKbps"]')?.value, 10) || 0,
        SampleRateHz: parseInt(row.querySelector('[data-field="SampleRateHz"]')?.value, 10) || 0,
    }));
}

/** Deep-equals two AudioOutputs arrays (order-sensitive, like the preset definition). */
function audioOutputsMatch(a, b) {
    if (!Array.isArray(a) || !Array.isArray(b) || a.length !== b.length) return false;
    return a.every((row, i) =>
        String(row.Codec ?? 'aac')        === String(b[i].Codec ?? 'aac') &&
        String(row.Layout ?? 'Source')    === String(b[i].Layout ?? 'Source') &&
        Number(row.BitrateKbps ?? 0)      === Number(b[i].BitrateKbps ?? 0) &&
        Number(row.SampleRateHz ?? 0)     === Number(b[i].SampleRateHz ?? 0));
}

/** True when every field the preset takes a position on matches the form. */
function presetMatchesForm(preset) {
    return Object.entries(preset.options).every(([key, val]) => {
        if (key === 'AudioOutputs') return audioOutputsMatch(formAudioOutputs(), val);
        const fv = formValue(key);
        // Normalize null/empty-string so a preset specifying null matches a blank text input.
        if (val === null) return fv === '' || fv === null || fv === undefined;
        return String(fv) === String(val);
    });
}

/** Re-highlights whichever built-in card (if any) matches the current form. */
export function syncActivePresetCard() {
    document.querySelectorAll('[data-preset-key]').forEach(card => {
        const preset = BUILTIN_PRESETS.find(p => p.key === card.dataset.presetKey);
        card.classList.toggle('active', !!preset && presetMatchesForm(preset));
    });
}


// ---------------------------------------------------------------------------
// Rendering
// ---------------------------------------------------------------------------

function renderBuiltins() {
    const row = document.getElementById('presetBuiltinRow');
    if (!row) return;
    row.innerHTML = BUILTIN_PRESETS.map(p => `
        <div class="col-12 col-md-6 col-xl-3">
            <button type="button" class="card w-100 h-100 text-start preset-card" data-preset-key="${p.key}">
                <div class="card-body p-2">
                    <div class="d-flex align-items-center justify-content-between">
                        <div class="fw-bold"><i class="fas ${p.icon} me-2 text-primary"></i>${p.name}</div>
                        ${p.badge ? `<span class="badge bg-primary" style="font-size: 0.65rem;">${p.badge}</span>` : ''}
                    </div>
                    <div class="small text-muted mt-1" style="min-height: 2.5em;">${p.desc}</div>
                    <div class="small text-info mt-1">${p.spec}</div>
                </div>
            </button>
        </div>`).join('');
    syncActivePresetCard();
}

function renderUserPresets() {
    const section = document.getElementById('presetUserSection');
    const list    = document.getElementById('presetUserList');
    if (!section || !list) return;

    section.style.display = userPresets.length ? '' : 'none';
    list.innerHTML = userPresets.map(p => `
        <div class="btn-group btn-group-sm" role="group" data-user-preset="${escapeHtml(p.name)}">
            <button type="button" class="btn btn-outline-primary" data-preset-action="apply" title="Apply this preset">
                <i class="fas fa-bookmark me-1"></i>${escapeHtml(p.name)}
            </button>
            <button type="button" class="btn btn-outline-secondary" data-preset-action="export" title="Download as a shareable file">
                <i class="fas fa-file-export"></i>
            </button>
            <button type="button" class="btn btn-outline-danger" data-preset-action="delete" title="Delete this preset">
                <i class="fas fa-trash"></i>
            </button>
        </div>`).join('');
}


// ---------------------------------------------------------------------------
// Actions
// ---------------------------------------------------------------------------

/**
 * Confirms before applying — a preset overwrites the user's current encoder
 * settings, so a stray click on a card shouldn't silently wipe a hand-tuned
 * config. Proceeds to {@link applyPreset} only when the user accepts.
 */
async function confirmAndApplyPreset(options, label) {
    const ok = await showConfirmModal(
        'Apply Preset',
        `Apply the "${label}" preset? This overwrites your current encoder settings.`,
        'Apply');
    if (ok) applyPreset(options, label);
}

/** Applies an options object to the form, persists, and re-runs the UI syncs. */
function applyPreset(options, label) {
    // Layer over the reset floor so any "sticky" field the preset doesn't carry —
    // a built-in's unlisted fields, or a field newer than a saved user preset — falls
    // back to its default instead of lingering from a previously applied profile.
    applyEncoderOptionsToForm(PREFIX, { ...PRESET_BASELINE, ...options });

    // Persist via the normal auto-save path, then let main.js's sync handlers
    // (WebM codec lockout, MuxOnly banner) react to the new values.
    getEncoderOptions(PREFIX);
    document.getElementById(`${PREFIX}Format`)
        ?.dispatchEvent(new Event('change', { bubbles: true }));

    syncActivePresetCard();
    showToast(`Applied preset "${label}"`, 'success');
}

async function refreshUserPresets() {
    try {
        const data = await presetsApi.list();
        userPresets = data.presets || [];
        renderUserPresets();
    } catch (err) {
        console.error('Failed to load presets', err);
    }
}

async function onSaveConfirm() {
    const input = document.getElementById('presetNameInput');
    const name  = (input?.value || '').trim();
    if (!name) { showToast('Give the preset a name first', 'warning'); return; }

    try {
        await presetsApi.save(name, getEncoderOptions(PREFIX));
        document.getElementById('presetSaveRow').style.display = 'none';
        input.value = '';
        showToast(`Saved preset "${name}"`, 'success');
        await refreshUserPresets();
    } catch (err) {
        showToast('Failed to save preset: ' + err.message, 'danger');
    }
}

async function onImportFile(file) {
    try {
        const parsed = JSON.parse(await file.text());
        await presetsApi.importFile(parsed);
        showToast(`Imported preset "${parsed.name}"`, 'success');
        await refreshUserPresets();
    } catch (err) {
        showToast('Import failed: ' + (err.message || 'not a valid preset file'), 'danger');
    }
}

async function onUserPresetAction(group, action) {
    const name   = group.dataset.userPreset;
    const preset = userPresets.find(p => p.name === name);
    if (!preset) return;

    if (action === 'apply') {
        confirmAndApplyPreset(preset.options, preset.name);
    } else if (action === 'export') {
        const btn = group.querySelector('[data-preset-action="export"]');
        try { await streamDownload(presetsApi.exportUrl(name), btn, `${name}.snacks-preset.json`); }
        catch { /* streamDownload already toasts */ }
    } else if (action === 'delete') {
        const ok = await showConfirmModal('Delete Preset', `Delete the preset "${name}"? This cannot be undone.`, 'Delete');
        if (!ok) return;
        try {
            await presetsApi.remove(name);
            showToast(`Deleted preset "${name}"`, 'success');
            await refreshUserPresets();
        } catch (err) {
            showToast('Failed to delete preset: ' + err.message, 'danger');
        }
    }
}


// ---------------------------------------------------------------------------
// Init
// ---------------------------------------------------------------------------

/**
 * Wires the preset panel. Call once at startup — the panel lives in the
 * settings modal markup, which exists for the lifetime of the page.
 */
export function initPresets() {
    const row = document.getElementById('presetBuiltinRow');
    if (!row) return;

    renderBuiltins();
    refreshUserPresets();

    row.addEventListener('click', (e) => {
        const card = e.target.closest('[data-preset-key]');
        if (!card) return;
        // Auto-save must be armed before a preset write, or the save would be
        // blocked (and the user's click silently lost) after a failed restore.
        retryRestoreEncoderOptionsIfNeeded(PREFIX);
        const preset = BUILTIN_PRESETS.find(p => p.key === card.dataset.presetKey);
        if (preset) confirmAndApplyPreset(preset.options, preset.name);
    });

    document.getElementById('presetUserList')?.addEventListener('click', (e) => {
        const btn = e.target.closest('[data-preset-action]');
        const group = e.target.closest('[data-user-preset]');
        if (btn && group) onUserPresetAction(group, btn.dataset.presetAction);
    });

    document.getElementById('presetSaveBtn')?.addEventListener('click', () => {
        const saveRow = document.getElementById('presetSaveRow');
        saveRow.style.display = saveRow.style.display === 'none' ? 'flex' : 'none';
        if (saveRow.style.display !== 'none') document.getElementById('presetNameInput')?.focus();
    });
    document.getElementById('presetSaveConfirmBtn')?.addEventListener('click', onSaveConfirm);
    document.getElementById('presetSaveCancelBtn') ?.addEventListener('click', () => {
        document.getElementById('presetSaveRow').style.display = 'none';
    });
    document.getElementById('presetNameInput')?.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') { e.preventDefault(); onSaveConfirm(); }
    });

    document.getElementById('presetImportBtn')?.addEventListener('click', () =>
        document.getElementById('presetImportFile')?.click());
    document.getElementById('presetImportFile')?.addEventListener('change', (e) => {
        const file = e.target.files?.[0];
        e.target.value = ''; // allow re-importing the same file
        if (file) onImportFile(file);
    });

    // Keep the active-card highlight honest as the user hand-edits fields.
    document.getElementById('settingsModal')?.addEventListener('change', () => {
        // Defer one tick so the change that triggered us has landed in the DOM.
        setTimeout(syncActivePresetCard, 0);
    });

    // Re-evaluate once the form is populated from saved settings. The first
    // sync in renderBuiltins() runs against the HTML defaults (which equal the
    // "Balanced" preset), so without this a pre-existing library whose settings
    // don't match any preset would keep Balanced highlighted after the restore
    // quietly set non-matching values (programmatic value changes fire no event).
    document.addEventListener('snacks:settings-restored', () => syncActivePresetCard());
}
