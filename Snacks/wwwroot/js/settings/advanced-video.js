/**
 * Transactional editor for the opt-in advanced-video policy block.
 *
 * The main encoder form reads only `committed`; every control below edits a deep
 * staged clone. Validate & Apply atomically swaps the clone and persists the full
 * encoder snapshot. Cancel restores the last committed block without an API call.
 */

import { settingsApi, autoScanApi } from '../api.js';
import { escapeHtml } from '../utils/dom.js';
import { showConfirmModal } from '../utils/modal-controller.js';

const ACTION_PROFILE = 'TranscodeWithProfile';
const FIELD_OPTIONS = [
    ['Codec', 'Codec'], ['Width', 'Width'], ['Height', 'Height'],
    ['ResolutionClass', 'Resolution class'], ['BitrateKbps', 'Video bitrate (kb/s)'],
    ['FileSizeBytes', 'File size (bytes)'], ['DurationSeconds', 'Duration (seconds)'],
    ['PixelFormat', 'Pixel format'], ['BitDepth', 'Bit depth'], ['IsHdr', 'HDR'], ['Is4K', '4K'],
];
const OPERATOR_OPTIONS = [
    ['Is', 'is'], ['IsNot', 'is not'], ['In', 'is one of'], ['NotIn', 'is not one of'],
    ['GreaterThan', '>'], ['GreaterThanOrEqual', '≥'], ['LessThan', '<'], ['LessThanOrEqual', '≤'],
    ['Between', 'inclusive range'], ['IsKnown', 'is known'], ['IsUnknown', 'is unknown'],
];
const TEXT_OPERATORS = new Set(['Is', 'IsNot', 'In', 'NotIn', 'IsKnown', 'IsUnknown']);
const BOOLEAN_OPERATORS = new Set(['Is', 'IsNot', 'IsKnown', 'IsUnknown']);
const BOOLEAN_FIELDS = new Set(['IsHdr', 'Is4K']);
const TEXT_FIELDS = new Set(['Codec', 'ResolutionClass', 'PixelFormat']);

// ---------------------------------------------------------------------------
// Plain-language rendering: the decision flow reads as sentences, so the
// first-match-wins model is visible without learning the field/operator grid.
// ---------------------------------------------------------------------------

const FIELD_PHRASES = {
    Codec: 'the codec', Width: 'the width', Height: 'the height',
    ResolutionClass: 'the resolution class', BitrateKbps: 'the video bitrate',
    FileSizeBytes: 'the file size', DurationSeconds: 'the duration',
    PixelFormat: 'the pixel format', BitDepth: 'the bit depth',
};
const OPERATOR_PHRASES = {
    Is: 'is', IsNot: 'is not', In: 'is one of', NotIn: 'is not one of',
    GreaterThan: 'is more than', GreaterThanOrEqual: 'is at least',
    LessThan: 'is less than', LessThanOrEqual: 'is at most',
    IsKnown: 'is known', IsUnknown: 'is unknown',
};

export function formatConditionValue(field, value) {
    const number = Number(value);
    if (!Number.isFinite(number)) return value;
    if (field === 'FileSizeBytes') {
        if (number >= 1e9) return `${(number / 1e9).toFixed(number % 1e9 ? 1 : 0)} GB`;
        if (number >= 1e6) return `${(number / 1e6).toFixed(number % 1e6 ? 1 : 0)} MB`;
        return `${number} bytes`;
    }
    if (field === 'DurationSeconds') {
        if (number >= 3600) return `${(number / 3600).toFixed(number % 3600 ? 1 : 0)} h`;
        if (number >= 60) return `${(number / 60).toFixed(number % 60 ? 1 : 0)} min`;
        return `${number} s`;
    }
    if (field === 'BitrateKbps') return `${number} kb/s`;
    return value;
}

export function describeCondition(condition) {
    const values = (condition.values ?? []).map(v => formatConditionValue(condition.field, v));
    if (condition.field === 'IsHdr' || condition.field === 'Is4K') {
        const label = condition.field === 'IsHdr' ? 'HDR' : '4K';
        if (condition.operator === 'IsKnown') return `${label} is known`;
        if (condition.operator === 'IsUnknown') return `${label} is unknown`;
        const positive = (condition.operator === 'Is') === (String(values[0]).toLowerCase() !== 'false');
        return positive ? `it is ${label}` : `it is not ${label}`;
    }
    const subject = FIELD_PHRASES[condition.field] ?? `the ${String(condition.field).toLowerCase()}`;
    if (condition.operator === 'Between') return `${subject} is between ${values[0] ?? '…'} and ${values[1] ?? '…'}`;
    const verb = OPERATOR_PHRASES[condition.operator] ?? condition.operator;
    if (condition.operator === 'IsKnown' || condition.operator === 'IsUnknown') return `${subject} ${verb}`;
    const list = values.length > 1
        ? `${values.slice(0, -1).join(', ')} or ${values[values.length - 1]}`
        : values[0] ?? '…';
    return `${subject} ${verb} ${list}`;
}

export function actionPhrase(action, profileName) {
    switch (action) {
        case 'TranscodeWithProfile': return profileName ? `encode with “${profileName}”` : 'encode with a recipe (none selected)';
        case 'UseSimpleSettings': return 'use the Simple settings above';
        case 'MuxOnly': return 'remux only — video is copied';
        case 'Skip': return 'skip the file entirely';
        default: return String(action);
    }
}

export function describeRule(rule, profiles = staged.profiles) {
    const profileName = profiles.find(p => p.id === rule.profileId)?.name ?? null;
    const outcome = actionPhrase(rule.action, profileName);
    if (!rule.conditions.length) return `Never matches — add a condition. Would ${outcome}.`;
    const sentences = rule.conditions.map(describeCondition);
    const joined = rule.match === 'Any' && sentences.length > 1
        ? sentences.join(', or ')
        : sentences.join(' and ');
    return `If ${joined} → ${outcome}`;
}

const emptyAdvanced = () => ({
    version: 1,
    enabled: false,
    profiles: [],
    rules: [],
    defaultAction: 'UseSimpleSettings',
    defaultProfileId: null,
});

let committed = emptyAdvanced();
let staged = emptyAdvanced();
let selectedProfileId = null;
let selectedRuleId = null;
let encoders = [];
let folderReferences = new Map();
let initialized = false;
let readOptions = null;
let customParseError = null;
let draggedRuleId = null;
let quickStartAdjusted = false;
let previousApplied = null;   // one-deep undo: the policy replaced by this session's last Apply
let lastSampleMatch = null;   // rule id (or 'default') the hypothetical tester matched
let impactFileQuery = '';
let showReevalPrompt = false; // set after Apply: existing files need Re-evaluate to change
let measured = null;
let userTemplates = [];

/** One-line guidance per adapter family, shown in the exact-encoder picker. */
const FAMILY_NOTES = {
    'x264': 'software · best compatibility',
    'x265': 'software · strong compression',
    'svt-av1': 'software · best size, practical speed',
    'libaom': 'software · smallest files, slowest',
    'rav1e': 'software · rust AV1, moderate speed',
    'nvenc': 'NVIDIA GPU · fast, larger files',
    'qsv': 'Intel GPU · fast, larger files',
    'vaapi': 'Linux GPU · fast, larger files',
    'amf': 'AMD GPU · fast, larger files',
    'videotoolbox': 'Apple GPU · fast, larger files',
};

/** Annotated marks for the quality slider, keyed by adapter family. */
const QUALITY_HINTS = {
    'x264': '18 near-lossless · 23 balanced · 28 compact',
    'x265': '20 near-lossless · 24 balanced · 28 compact',
    'svt-av1': '28 archival · 32 balanced · 38 compact',
    'libaom': '28 archival · 32 balanced · 38 compact',
    'rav1e': '60 archival · 100 balanced · 140 compact',
    'nvenc': '24 high quality · 30 balanced · 36 compact',
    'qsv': '22 high quality · 27 balanced · 33 compact',
    'vaapi': '22 high quality · 27 balanced · 33 compact',
    'amf': '22 high quality · 27 balanced · 33 compact',
    'videotoolbox': '65 high quality · 50 balanced · 40 compact (higher = better)',
};

const DEFAULT_SOFTWARE_ENCODER = { av1: 'libsvtav1', h264: 'libx264', h265: 'libx265' };

const clone = value => JSON.parse(JSON.stringify(value));
const byId = id => document.getElementById(id);
const value = (id, fallback = '') => byId(id)?.value ?? fallback;
const checked = id => !!byId(id)?.checked;
const integer = (id, fallback = 0) => {
    const parsed = parseInt(value(id), 10);
    return Number.isFinite(parsed) ? parsed : fallback;
};
const nullableInteger = id => {
    const raw = value(id).trim();
    if (!raw) return null;
    const parsed = parseInt(raw, 10);
    return Number.isFinite(parsed) ? parsed : null;
};
const nullableText = id => value(id).trim() || null;
const guid = () => globalThis.crypto?.randomUUID?.()
    ?? `${Date.now().toString(16)}-${Math.random().toString(16).slice(2)}-4000-8000-${Math.random().toString(16).slice(2)}`;
const readKey = (object, name, fallback) => {
    if (!object) return fallback;
    if (object[name] !== undefined) return object[name];
    const upper = name.charAt(0).toUpperCase() + name.slice(1);
    return object[upper] !== undefined ? object[upper] : fallback;
};

function normalizeProfile(raw = {}) {
    const rc = readKey(raw, 'rateControl', {});
    return {
        id: readKey(raw, 'id', guid()),
        name: readKey(raw, 'name', 'New video profile'),
        codec: String(readKey(raw, 'codec', 'h265')).toLowerCase().replace('hevc', 'h265'),
        encoderSelection: readKey(raw, 'encoderSelection', 'Automatic'),
        encoder: readKey(raw, 'encoder', null),
        hardwareAcceleration: readKey(raw, 'hardwareAcceleration', 'auto'),
        rateControl: {
            mode: readKey(rc, 'mode', 'Bitrate'),
            targetKbps: Number(readKey(rc, 'targetKbps', 3500)),
            minKbps: readKey(rc, 'minKbps', null),
            maxKbps: readKey(rc, 'maxKbps', null),
            bufferKbits: readKey(rc, 'bufferKbits', null),
            strictBitrate: !!readKey(rc, 'strictBitrate', false),
            quality: Number(readKey(rc, 'quality', 35)),
        },
        preset: readKey(raw, 'preset', 'medium'),
        threads: Number(readKey(raw, 'threads', 0)),
        pixelFormat: readKey(raw, 'pixelFormat', null),
        gopSize: Number(readKey(raw, 'gopSize', 0)),
        videoProfile: readKey(raw, 'videoProfile', null),
        videoLevel: readKey(raw, 'videoLevel', null),
        downscalePolicy: readKey(raw, 'downscalePolicy', 'Never'),
        downscaleTarget: readKey(raw, 'downscaleTarget', '1080p'),
        fixedFrameSize: readKey(raw, 'fixedFrameSize', null),
        maxFrameRate: Number(readKey(raw, 'maxFrameRate', 0)),
        tonemapHdrToSdr: !!readKey(raw, 'tonemapHdrToSdr', false),
        removeBlackBorders: !!readKey(raw, 'removeBlackBorders', false),
        additionalVideoFilters: clone(readKey(raw, 'additionalVideoFilters', []) ?? []),
        customOptions: (readKey(raw, 'customOptions', []) ?? []).map(option => ({
            option: readKey(option, 'option', ''),
            values: clone(readKey(option, 'values', []) ?? []),
        })),
        outputRetention: readKey(raw, 'outputRetention', 'SmallerOnly'),
    };
}

function normalizeRule(raw = {}) {
    return {
        id: readKey(raw, 'id', guid()),
        name: readKey(raw, 'name', 'New rule'),
        enabled: !!readKey(raw, 'enabled', true),
        match: readKey(raw, 'match', 'All'),
        conditions: (readKey(raw, 'conditions', []) ?? []).map(condition => ({
            field: readKey(condition, 'field', 'Codec'),
            operator: readKey(condition, 'operator', 'Is'),
            values: clone(readKey(condition, 'values', []) ?? []).map(String),
        })),
        action: readKey(raw, 'action', ACTION_PROFILE),
        profileId: readKey(raw, 'profileId', null),
    };
}

function normalizeAdvanced(raw) {
    if (!raw) return emptyAdvanced();
    return {
        version: Number(readKey(raw, 'version', 1)),
        enabled: !!readKey(raw, 'enabled', false),
        profiles: (readKey(raw, 'profiles', []) ?? []).map(normalizeProfile),
        rules: (readKey(raw, 'rules', []) ?? []).map(normalizeRule),
        defaultAction: readKey(raw, 'defaultAction', 'UseSimpleSettings'),
        defaultProfileId: readKey(raw, 'defaultProfileId', null),
    };
}

export function getAdvancedVideoOptions() {
    return clone(committed);
}

export function getAdvancedVideoProfileChoices() {
    return committed.profiles.map(profile => ({ id: profile.id, name: profile.name }));
}

export function restoreAdvancedVideoOptions(raw) {
    committed = normalizeAdvanced(raw);
    staged = clone(committed);
    // Keep the quick-start gallery prominent on first use, out of the way for
    // anyone who already built a policy. Only the first restore decides — later
    // saves must not collapse a panel the user opened.
    if (!quickStartAdjusted) {
        quickStartAdjusted = true;
        const quickStart = byId('advancedVideoQuickStart');
        if (quickStart) quickStart.open = committed.profiles.length === 0 && committed.rules.length === 0;
    }
    selectedProfileId = staged.profiles.some(p => p.id === selectedProfileId)
        ? selectedProfileId : staged.profiles[0]?.id ?? null;
    selectedRuleId = staged.rules.some(r => r.id === selectedRuleId)
        ? selectedRuleId : staged.rules[0]?.id ?? null;
    if (initialized) renderAll();
    document.dispatchEvent(new CustomEvent('snacks:advanced-video-profiles-changed'));
}

/** Built-in Simple presets turn policy evaluation off but preserve its catalogs. */
export function disableAdvancedVideoForSimplePreset() {
    committed.enabled = false;
    staged = clone(committed);
    if (initialized) renderAll(false);
}

function selectedProfile() { return staged.profiles.find(p => p.id === selectedProfileId) ?? null; }
function selectedRule() { return staged.rules.find(r => r.id === selectedRuleId) ?? null; }

function markDirty() {
    const dirty = JSON.stringify(staged) !== JSON.stringify(committed);
    byId('advancedVideoDirty')?.classList.toggle('d-none', !dirty);
    if (byId('advancedVideoApply')) byId('advancedVideoApply').disabled = !dirty;
    if (byId('advancedVideoCancel')) byId('advancedVideoCancel').disabled = !dirty;
    if (dirty) lastSampleMatch = null; // a changed draft invalidates the sample-match highlight
    if (initialized) scheduleImpact();
}

function setInput(id, next, checkbox = false) {
    const node = byId(id);
    if (!node) return;
    if (checkbox) node.checked = !!next;
    else node.value = next ?? '';
}

function profileReferences(profileId) {
    const normalizedId = String(profileId).toLowerCase();
    const rules = staged.rules
        .filter(rule => String(rule.profileId).toLowerCase() === normalizedId)
        .map(rule => `rule “${rule.name}”`);
    const folders = folderReferences.get(normalizedId) ?? [];
    const defaults = staged.defaultAction === ACTION_PROFILE && String(staged.defaultProfileId).toLowerCase() === normalizedId
        ? ['the default action'] : [];
    return [...rules, ...defaults, ...folders.map(path => `folder “${path}”`)];
}

function renderProfileList() {
    const root = byId('advancedVideoProfileList');
    if (!root) return;
    root.innerHTML = staged.profiles.length ? staged.profiles.map(profile => {
        const descriptor = descriptorFor(profile);
        const exact = profile.encoderSelection === 'Explicit';
        const available = !exact || descriptor?.localAvailable || compatibleWorkers(descriptor).length > 0;
        const rate = profile.rateControl.mode === 'Quality'
            ? `${descriptor?.qualityLabel || 'quality'} ${profile.rateControl.quality}`
            : profile.rateControl.mode === 'Bitrate' ? `${profile.rateControl.targetKbps} kb/s` : 'custom rate control';
        return `<button type="button" class="list-group-item list-group-item-action ${profile.id === selectedProfileId ? 'active' : ''}" data-profile-id="${escapeHtml(profile.id)}">
            <div class="d-flex justify-content-between gap-2"><span class="fw-semibold text-truncate">${escapeHtml(profile.name)}</span><span class="badge ${available ? 'bg-success' : 'bg-warning text-dark'}">${available ? 'available' : 'waiting'}</span></div>
            <div class="small ${profile.id === selectedProfileId ? 'text-white-50' : 'text-muted'}">${escapeHtml(profile.codec.toUpperCase())} · ${escapeHtml(exact ? profile.encoder || 'missing encoder' : 'automatic')} · ${escapeHtml(rate)} · ${profile.outputRetention === 'AlwaysKeep' ? 'always keep' : 'keep if smaller'}</div>
        </button>`;
    }).join('') : '<div class="small text-muted border rounded p-3">No profiles yet.</div>';

    const profile = selectedProfile();
    byId('advancedVideoDuplicateProfile').disabled = !profile;
    const refs = profile ? profileReferences(profile.id) : [];
    byId('advancedVideoDeleteProfile').disabled = !profile || refs.length > 0;
    byId('advancedVideoDeleteProfile').title = refs.length ? 'Reassign or remove profile references before deleting.' : '';
    byId('advancedVideoProfileReferences').textContent = !profile ? ''
        : refs.length ? `Referenced by ${refs.join(', ')}. Reassign those references before deletion.` : 'Not currently referenced.';
}

function descriptorFor(profile) {
    if (!profile || profile.encoderSelection !== 'Explicit' || !profile.encoder) return null;
    return encoders.find(item => String(item.encoder).toLowerCase() === String(profile.encoder).toLowerCase()) ?? null;
}

function compatibleWorkers(descriptor) {
    return (descriptor?.workers ?? []).filter(worker => worker.protocolSupported !== false);
}

function renderEncoderChoices(profile) {
    const select = byId('advancedVideoEncoder');
    if (!select) return;
    const matching = encoders.filter(item => item.codec === profile.codec)
        .sort((a, b) => (b.detected === true) - (a.detected === true) || String(a.encoder).localeCompare(String(b.encoder)));
    const present = matching.some(item => item.encoder === profile.encoder);
    const choices = [...matching];
    if (profile.encoder && !present) choices.unshift({ encoder: profile.encoder, family: 'Unavailable', localAvailable: false, workers: [] });
    select.innerHTML = '<option value="">Choose an exact encoder…</option>' + choices.map(item => {
        const hosts = [item.localAvailable ? 'local' : null, ...compatibleWorkers(item).map(worker => worker.hostname || worker.nodeId)].filter(Boolean);
        const where = hosts.length ? hosts.join(', ') : item.detected === false ? 'not detected yet' : 'unavailable';
        const note = FAMILY_NOTES[item.family] ?? item.family ?? 'unknown';
        return `<option value="${escapeHtml(item.encoder)}">${escapeHtml(item.encoder)} — ${escapeHtml(note)} (${escapeHtml(where)})</option>`;
    }).join('');
    select.value = profile.encoder ?? '';
    select.disabled = profile.encoderSelection !== 'Explicit';
    if (byId('advancedVideoHardwarePreference'))
        byId('advancedVideoHardwarePreference').disabled = profile.encoderSelection !== 'Automatic';

    const descriptor = descriptorFor(profile);
    const hosts = descriptor
        ? [descriptor.localAvailable ? 'this host' : null, ...compatibleWorkers(descriptor).map(worker => worker.hostname || worker.nodeId)].filter(Boolean)
        : [];
    byId('advancedVideoEncoderAvailability').textContent = profile.encoderSelection !== 'Explicit'
        ? 'Automatic retains existing hardware selection and software fallback.'
        : descriptor ? (hosts.length
            ? `Available on ${hosts.join(', ')}.`
            : 'Not detected on this host or any connected worker. The recipe stays portable — matching jobs wait until a node that has it connects; nothing is silently substituted.')
        : 'This exact encoder is currently unavailable. The recipe remains portable but jobs wait.';
    // Automatic selection still gets a meaningful quality scale: annotate with
    // the default software encoder for the chosen codec, which is what the
    // resolver falls back to when no hardware slot claims the job.
    const effective = descriptor ?? encoders.find(item =>
        String(item.encoder).toLowerCase() === DEFAULT_SOFTWARE_ENCODER[profile.codec]) ?? null;
    byId('advancedVideoQualityLabel').textContent = `${effective?.qualityLabel || 'Quality value'}${effective?.qualityMin != null ? ` (${effective.qualityMin}–${effective.qualityMax})` : ''}`;
    const slider = byId('advancedVideoQualitySlider');
    if (slider) {
        const hasRange = effective?.qualityMin != null && effective.qualityMax > effective.qualityMin;
        slider.classList.toggle('d-none', !hasRange);
        if (hasRange) {
            slider.min = effective.qualityMin;
            slider.max = effective.qualityMax;
            slider.value = profile.rateControl.quality;
        }
    }
    const hints = byId('advancedVideoQualityHints');
    if (hints) hints.textContent = QUALITY_HINTS[effective?.family] ?? 'Lower = better quality, bigger file.';
    const suggestionSources = profile.encoderSelection === 'Explicit'
        ? (descriptor ? [descriptor] : [])
        : matching;
    const renderSuggestions = (id, property) => {
        const list = byId(id);
        if (!list) return;
        const values = [...new Set(suggestionSources.flatMap(item => item[property] ?? []))]
            .sort((a, b) => String(a).localeCompare(String(b), undefined, { numeric: true }));
        list.innerHTML = values.map(item => `<option value="${escapeHtml(item)}"></option>`).join('');
    };
    renderSuggestions('advancedVideoPresetChoices', 'presets');
    renderSuggestions('advancedVideoPixelFormatChoices', 'pixelFormats');
    renderSuggestions('advancedVideoCustomOptionChoices', 'supportedOptions');
    renderRateChoices(profile, descriptor);
}

function renderRateChoices(profile, descriptor) {
    const select = byId('advancedVideoRateMode');
    if (!select) return;
    const supported = profile.encoderSelection === 'Explicit' && descriptor
        ? descriptor.rateControlModes ?? ['Custom']
        : ['Bitrate', 'Quality', 'Custom'];
    const labels = {
        Bitrate: 'Bitrate',
        Quality: 'Quality (CRF / CQ / QP)',
        Custom: 'Custom options only',
    };
    select.innerHTML = Object.entries(labels).map(([mode, label]) => {
        const available = supported.includes(mode);
        const selectedUnavailable = mode === profile.rateControl.mode && !available;
        return `<option value="${mode}" ${!available && !selectedUnavailable ? 'disabled' : ''}>${label}${selectedUnavailable ? ' — unsupported here' : ''}</option>`;
    }).join('');
    select.value = profile.rateControl.mode;
}

function renderCustomOptions(profile) {
    const root = byId('advancedVideoCustomOptions');
    if (!root) return;
    root.innerHTML = profile.customOptions.map((entry, index) => `
        <div class="row g-1 mb-1" data-custom-option="${index}">
            <div class="col-md-4"><input class="form-control form-control-sm font-monospace" data-custom-field="option" list="advancedVideoCustomOptionChoices" value="${escapeHtml(entry.option)}" placeholder="-aom-params"></div>
            <div class="col-md-7"><input class="form-control form-control-sm font-monospace" data-custom-field="values" value="${escapeHtml(JSON.stringify(entry.values))}" placeholder='["cq-level=35:cpu-used=4"]'></div>
            <div class="col-md-1"><button type="button" class="btn btn-sm btn-outline-danger w-100" data-remove-custom="${index}" title="Remove"><i class="fas fa-xmark"></i></button></div>
        </div>`).join('');
}

function renderProfileEditor() {
    const profile = selectedProfile();
    byId('advancedVideoProfileEmpty')?.classList.toggle('d-none', !!profile);
    byId('advancedVideoProfileEditor')?.classList.toggle('d-none', !profile);
    if (!profile) return;

    setInput('advancedVideoProfileName', profile.name);
    setInput('advancedVideoProfileCodec', profile.codec);
    setInput('advancedVideoEncoderSelection', profile.encoderSelection);
    setInput('advancedVideoHardwarePreference', profile.hardwareAcceleration);
    renderEncoderChoices(profile);
    setInput('advancedVideoRateMode', profile.rateControl.mode);
    setInput('advancedVideoTargetKbps', profile.rateControl.targetKbps);
    setInput('advancedVideoQuality', profile.rateControl.quality);
    setInput('advancedVideoMinKbps', profile.rateControl.minKbps);
    setInput('advancedVideoMaxKbps', profile.rateControl.maxKbps);
    setInput('advancedVideoBufferKbits', profile.rateControl.bufferKbits);
    setInput('advancedVideoStrictBitrate', profile.rateControl.strictBitrate, true);
    setInput('advancedVideoPreset', profile.preset);
    setInput('advancedVideoThreads', profile.threads);
    setInput('advancedVideoPixelFormat', profile.pixelFormat);
    setInput('advancedVideoGop', profile.gopSize);
    setInput('advancedVideoCodecProfile', profile.videoProfile);
    setInput('advancedVideoCodecLevel', profile.videoLevel);
    setInput('advancedVideoRetention', profile.outputRetention);
    setInput('advancedVideoDownscalePolicy', profile.downscalePolicy);
    setInput('advancedVideoDownscaleTarget', profile.downscaleTarget);
    setInput('advancedVideoFixedFrame', profile.fixedFrameSize);
    setInput('advancedVideoMaxFps', profile.maxFrameRate);
    setInput('advancedVideoTonemap', profile.tonemapHdrToSdr, true);
    setInput('advancedVideoCrop', profile.removeBlackBorders, true);
    setInput('advancedVideoFilters', profile.additionalVideoFilters.join('\n'));
    renderCustomOptions(profile);
    updateRateVisibility(profile);
    scheduleArgsPreview(50);
}

function updateRateVisibility(profile) {
    document.querySelectorAll('[data-advanced-rate]').forEach(node => {
        const modes = node.dataset.advancedRate.split(',');
        node.style.display = modes.includes(profile.rateControl.mode) ? '' : 'none';
    });
    byId('advancedVideoQualityWarning')?.classList.toggle('d-none',
        profile.rateControl.mode !== 'Quality' || profile.outputRetention === 'AlwaysKeep');
}

function profileSelectOptions(selected, allowBlank = true) {
    return `${allowBlank ? '<option value="">Select profile…</option>' : ''}${staged.profiles.map(profile => `<option value="${escapeHtml(profile.id)}" ${profile.id === selected ? 'selected' : ''}>${escapeHtml(profile.name)}</option>`).join('')}`;
}

function renderRuleList() {
    const root = byId('advancedVideoRuleList');
    if (!root) return;
    const impactFresh = lastImpact && staged.enabled && lastImpactKey === impactStateKey();
    const shadowedById = new Map((impactFresh ? lastImpact.shadowed ?? [] : [])
        .map(entry => [String(entry.ruleId).toLowerCase(), entry.byRuleName]));
    root.innerHTML = staged.rules.length ? staged.rules.map((rule, index) => {
        const shadowedBy = shadowedById.get(String(rule.id).toLowerCase());
        const matched = lastSampleMatch != null && String(lastSampleMatch).toLowerCase() === String(rule.id).toLowerCase();
        return `
        <div class="av-rule-card ${rule.id === selectedRuleId ? 'selected' : ''} ${rule.enabled ? '' : 'paused'} ${shadowedBy ? 'shadowed' : ''} ${matched ? 'matched' : ''}" data-rule-row="${escapeHtml(rule.id)}" draggable="true">
            <span class="av-flow-num">${index + 1}</span>
            <button type="button" class="av-rule-main" data-rule-id="${escapeHtml(rule.id)}">
                <span class="av-rule-name">${rule.enabled ? '' : '<i class="fas fa-pause me-1"></i>'}${escapeHtml(rule.name)}${rule.enabled ? '' : ' <span class="text-muted">(paused — skipped)</span>'}${matched ? ' <span class="badge bg-success av-matched-badge">sample lands here</span>' : ''}</span>
                <span class="av-rule-sentence">${escapeHtml(describeRule(rule))}</span>
                ${shadowedBy ? `<span class="av-rule-shadowed"><i class="fas fa-triangle-exclamation me-1"></i>Never reached — “${escapeHtml(shadowedBy)}” always claims these files first.</span>` : ''}
            </button>
            <span class="badge av-count-badge d-none" data-rule-count="${escapeHtml(rule.id)}"></span>
            <span class="av-rule-actions">
                <button type="button" class="btn btn-sm btn-outline-secondary" data-rule-move="up" data-rule-index="${index}" ${index === 0 ? 'disabled' : ''} title="Move up" aria-label="Move rule ${index + 1} up"><i class="fas fa-arrow-up"></i></button>
                <button type="button" class="btn btn-sm btn-outline-secondary" data-rule-move="down" data-rule-index="${index}" ${index === staged.rules.length - 1 ? 'disabled' : ''} title="Move down" aria-label="Move rule ${index + 1} down"><i class="fas fa-arrow-down"></i></button>
            </span>
        </div>`;
    }).join('')
        : '<div class="av-flow-node av-flow-empty small text-muted">No rules yet — every video falls straight through to “Everything else” below. Add a rule or load a template.</div>';
    document.querySelector('.av-flow-default')?.classList.toggle('matched', lastSampleMatch === 'default');
    renderImpactBadges();
}

function conditionRow(condition, index) {
    const fields = FIELD_OPTIONS.map(([key, label]) => `<option value="${key}" ${condition.field === key ? 'selected' : ''}>${label}</option>`).join('');
    const allowed = BOOLEAN_FIELDS.has(condition.field) ? BOOLEAN_OPERATORS
        : TEXT_FIELDS.has(condition.field) ? TEXT_OPERATORS : null;
    if (allowed && !allowed.has(condition.operator)) condition.operator = 'Is';
    const operators = OPERATOR_OPTIONS
        .filter(([key]) => !allowed || allowed.has(key))
        .map(([key, label]) => `<option value="${key}" ${condition.operator === key ? 'selected' : ''}>${label}</option>`).join('');
    const noValues = condition.operator === 'IsKnown' || condition.operator === 'IsUnknown';
    const values = BOOLEAN_FIELDS.has(condition.field) && !noValues
        ? `<select class="form-select form-select-sm" data-condition-field="values"><option value="true" ${condition.values[0] === 'true' ? 'selected' : ''}>true</option><option value="false" ${condition.values[0] === 'false' ? 'selected' : ''}>false</option></select>`
        : `<input class="form-control form-control-sm" data-condition-field="values" value="${escapeHtml(condition.values.join(', '))}" placeholder="value, value" ${noValues ? 'disabled' : ''}>`;
    return `<div class="row g-1 mb-1" data-condition="${index}">
        <div class="col-md-4"><select class="form-select form-select-sm" data-condition-field="field">${fields}</select></div>
        <div class="col-md-3"><select class="form-select form-select-sm" data-condition-field="operator">${operators}</select></div>
        <div class="col-md-4">${values}</div>
        <div class="col-md-1"><button type="button" class="btn btn-sm btn-outline-danger w-100" data-remove-condition="${index}"><i class="fas fa-xmark"></i></button></div>
    </div>`;
}

function renderRuleEditor() {
    const rule = selectedRule();
    byId('advancedVideoRuleEmpty')?.classList.toggle('d-none', !!rule);
    byId('advancedVideoRuleEditor')?.classList.toggle('d-none', !rule);
    if (!rule) return;
    setInput('advancedVideoRuleName', rule.name);
    setInput('advancedVideoRuleMatch', rule.match);
    setInput('advancedVideoRuleEnabled', rule.enabled, true);
    setInput('advancedVideoRuleAction', rule.action);
    byId('advancedVideoRuleProfile').innerHTML = profileSelectOptions(rule.profileId);
    byId('advancedVideoRuleProfile').disabled = rule.action !== ACTION_PROFILE;
    byId('advancedVideoRuleProfile').closest('.col-md-6')?.classList.toggle('d-none', rule.action !== ACTION_PROFILE);
    byId('advancedVideoConditions').innerHTML = rule.conditions.length
        ? rule.conditions.map(conditionRow).join('')
        : '<div class="small text-muted border rounded p-2">No conditions: this rule never matches. Add one below.</div>';
}

function renderDefaults() {
    setInput('advancedVideoDefaultAction', staged.defaultAction);
    const select = byId('advancedVideoDefaultProfile');
    if (!select) return;
    select.innerHTML = profileSelectOptions(staged.defaultProfileId);
    select.disabled = staged.defaultAction !== ACTION_PROFILE;
    select.classList.toggle('d-none', staged.defaultAction !== ACTION_PROFILE);
}

function renderAll(rebuildEditors = true) {
    setInput('advancedVideoEnabled', staged.enabled, true);
    byId('advancedVideoBody')?.classList.toggle('opacity-50', !staged.enabled);
    renderProfileList();
    if (rebuildEditors) renderProfileEditor();
    renderRuleList();
    if (rebuildEditors) renderRuleEditor();
    renderDefaults();
    markDirty();
}

function readCustomOptions() {
    customParseError = null;
    return Array.from(document.querySelectorAll('#advancedVideoCustomOptions [data-custom-option]')).map((row, index) => {
        const option = row.querySelector('[data-custom-field="option"]')?.value.trim() ?? '';
        const raw = row.querySelector('[data-custom-field="values"]')?.value.trim() || '[]';
        try {
            const values = JSON.parse(raw);
            if (!Array.isArray(values) || values.some(item => typeof item !== 'string')) throw new Error();
            return { option, values };
        } catch {
            customParseError = `Custom option row ${index + 1}: values must be a JSON array of strings.`;
            return { option, values: [] };
        }
    });
}

function syncProfileFromForm({ rateChanged = false } = {}) {
    const profile = selectedProfile();
    if (!profile) return;
    const oldMode = profile.rateControl.mode;
    const oldName = profile.name;
    profile.name = value('advancedVideoProfileName').trim();
    profile.codec = value('advancedVideoProfileCodec', 'h265');
    profile.encoderSelection = value('advancedVideoEncoderSelection', 'Automatic');
    profile.encoder = profile.encoderSelection === 'Explicit' ? nullableText('advancedVideoEncoder') : null;
    profile.hardwareAcceleration = value('advancedVideoHardwarePreference', 'auto');
    profile.rateControl = {
        mode: value('advancedVideoRateMode', 'Bitrate'),
        targetKbps: integer('advancedVideoTargetKbps', 3500),
        minKbps: nullableInteger('advancedVideoMinKbps'),
        maxKbps: nullableInteger('advancedVideoMaxKbps'),
        bufferKbits: nullableInteger('advancedVideoBufferKbits'),
        strictBitrate: checked('advancedVideoStrictBitrate'),
        quality: Number(value('advancedVideoQuality', '35')) || 0,
    };
    if (rateChanged && oldMode !== 'Quality' && profile.rateControl.mode === 'Quality') {
        profile.outputRetention = 'AlwaysKeep';
        setInput('advancedVideoRetention', 'AlwaysKeep');
    }
    profile.preset = nullableText('advancedVideoPreset');
    profile.threads = integer('advancedVideoThreads', 0);
    profile.pixelFormat = nullableText('advancedVideoPixelFormat');
    profile.gopSize = integer('advancedVideoGop', 0);
    profile.videoProfile = nullableText('advancedVideoCodecProfile');
    profile.videoLevel = nullableText('advancedVideoCodecLevel');
    profile.outputRetention = value('advancedVideoRetention', 'SmallerOnly');
    profile.downscalePolicy = value('advancedVideoDownscalePolicy', 'Never');
    profile.downscaleTarget = value('advancedVideoDownscaleTarget', '1080p');
    profile.fixedFrameSize = nullableText('advancedVideoFixedFrame');
    profile.maxFrameRate = integer('advancedVideoMaxFps', 0);
    profile.tonemapHdrToSdr = checked('advancedVideoTonemap');
    profile.removeBlackBorders = checked('advancedVideoCrop');
    profile.additionalVideoFilters = value('advancedVideoFilters').split(/\r?\n/).map(line => line.trim()).filter(Boolean);
    profile.customOptions = readCustomOptions();
    renderEncoderChoices(profile);
    updateRateVisibility(profile);
    renderProfileList();
    renderDefaults();
    renderRuleEditor();
    // Flow sentences embed recipe names; every other profile field is invisible
    // to the cards, so skip the re-render (it matters at large rule counts).
    if (profile.name !== oldName) renderRuleList();
    scheduleArgsPreview();
    markDirty();
}

function syncRuleFromForm() {
    const rule = selectedRule();
    if (!rule) return;
    rule.name = value('advancedVideoRuleName').trim();
    rule.match = value('advancedVideoRuleMatch', 'All');
    rule.enabled = checked('advancedVideoRuleEnabled');
    rule.action = value('advancedVideoRuleAction', ACTION_PROFILE);
    rule.profileId = rule.action === ACTION_PROFILE ? nullableText('advancedVideoRuleProfile') : null;
    rule.conditions = Array.from(document.querySelectorAll('#advancedVideoConditions [data-condition]')).map(row => {
        const operator = row.querySelector('[data-condition-field="operator"]')?.value ?? 'Is';
        const values = operator === 'IsKnown' || operator === 'IsUnknown' ? []
            : (row.querySelector('[data-condition-field="values"]')?.value ?? '').split(',').map(part => part.trim()).filter(Boolean);
        return {
            field: row.querySelector('[data-condition-field="field"]')?.value ?? 'Codec',
            operator,
            values,
        };
    });
    byId('advancedVideoRuleProfile').disabled = rule.action !== ACTION_PROFILE;
    byId('advancedVideoRuleProfile').closest('.col-md-6')?.classList.toggle('d-none', rule.action !== ACTION_PROFILE);
    renderRuleList();
    renderProfileList();
    markDirty();
}

function profileFromSimple(simple) {
    const codec = String(simple.Codec ?? simple.codec ?? 'h265').toLowerCase();
    return normalizeProfile({
        id: guid(),
        name: `New ${codec.toUpperCase()} profile`,
        codec,
        encoderSelection: 'Automatic',
        hardwareAcceleration: simple.HardwareAcceleration ?? simple.hardwareAcceleration ?? 'auto',
        rateControl: {
            mode: 'Bitrate', targetKbps: simple.TargetBitrate ?? simple.targetBitrate ?? 3500,
            strictBitrate: simple.StrictBitrate ?? simple.strictBitrate ?? false, quality: 35,
        },
        preset: simple.FfmpegQualityPreset ?? simple.ffmpegQualityPreset ?? 'medium',
        downscalePolicy: simple.DownscalePolicy ?? simple.downscalePolicy ?? 'Never',
        downscaleTarget: simple.DownscaleTarget ?? simple.downscaleTarget ?? '1080p',
        fixedFrameSize: simple.FixedFrameSize ?? simple.fixedFrameSize ?? null,
        maxFrameRate: simple.MaxFrameRate ?? simple.maxFrameRate ?? 0,
        tonemapHdrToSdr: simple.TonemapHdrToSdr ?? simple.tonemapHdrToSdr ?? false,
        removeBlackBorders: simple.RemoveBlackBorders ?? simple.removeBlackBorders ?? false,
        videoProfile: simple.VideoProfile ?? simple.videoProfile ?? null,
        videoLevel: simple.VideoLevel ?? simple.videoLevel ?? null,
        outputRetention: 'SmallerOnly',
    });
}

/**
 * Quick-start templates: complete working policies (profiles + rules + default
 * action) a user can load, inspect, and apply without building anything by hand.
 *
 * Every value must stay on AdvancedVideoValidator's diagnostic-free path — a
 * starter template that loads with warnings reads as broken. That is why
 * quality-mode profiles ship with AlwaysKeep and automatic-selection profiles
 * leave preset/pixel format null (each encoder family maps them differently).
 * The expert template mirrors examples/advanced-video-policy.json verbatim.
 */
export const ADVANCED_VIDEO_TEMPLATES = [
    {
        key: 'av1-everything',
        name: 'Convert everything to AV1',
        icon: 'fa-compress',
        spec: 'AV1 · CRF 32 · software · always keep',
        description: 'One quality-based AV1 recipe. Anything not AV1 yet gets transcoded; AV1 sources are skipped.',
        build() {
            const av1 = normalizeProfile({
                id: guid(), name: 'AV1 quality (CRF 32)', codec: 'av1', preset: null, hardwareAcceleration: 'none',
                rateControl: { mode: 'Quality', quality: 32 }, outputRetention: 'AlwaysKeep',
            });
            return {
                profiles: [av1],
                rules: [
                    normalizeRule({ id: guid(), name: 'Not AV1 yet', profileId: av1.id, conditions: [{ field: 'Codec', operator: 'IsNot', values: ['av1'] }] }),
                    normalizeRule({ id: guid(), name: 'Already AV1 — leave alone', action: 'Skip', conditions: [{ field: 'Codec', operator: 'Is', values: ['av1'] }] }),
                ],
                defaultAction: 'UseSimpleSettings',
            };
        },
    },
    {
        key: 'av1-tiered',
        name: 'Tiered AV1 (1080p + 4K)',
        icon: 'fa-layer-group',
        spec: 'AV1 · CRF 32 / 35 by resolution · software',
        description: 'Two AV1 quality recipes; ordered rules send 4K sources to one and everything else to the other.',
        build() {
            const fourK = normalizeProfile({
                id: guid(), name: 'AV1 4K (CRF 32)', codec: 'av1', preset: null, hardwareAcceleration: 'none',
                rateControl: { mode: 'Quality', quality: 32 }, outputRetention: 'AlwaysKeep',
            });
            const hd = normalizeProfile({
                id: guid(), name: 'AV1 1080p and below (CRF 35)', codec: 'av1', preset: null, hardwareAcceleration: 'none',
                rateControl: { mode: 'Quality', quality: 35 }, outputRetention: 'AlwaysKeep',
            });
            return {
                profiles: [fourK, hd],
                rules: [
                    normalizeRule({
                        id: guid(), name: '4K sources', profileId: fourK.id,
                        conditions: [
                            { field: 'Codec', operator: 'IsNot', values: ['av1'] },
                            { field: 'ResolutionClass', operator: 'Is', values: ['2160p+'] },
                        ],
                    }),
                    normalizeRule({ id: guid(), name: 'Everything else', profileId: hd.id, conditions: [{ field: 'Codec', operator: 'IsNot', values: ['av1'] }] }),
                    normalizeRule({ id: guid(), name: 'Already AV1 — leave alone', action: 'Skip', conditions: [{ field: 'Codec', operator: 'Is', values: ['av1'] }] }),
                ],
                defaultAction: 'UseSimpleSettings',
            };
        },
    },
    {
        key: 'hevc-saver',
        name: 'HEVC space saver',
        icon: 'fa-hard-drive',
        spec: 'HEVC · CRF 24 · software · always keep',
        description: 'Re-encodes older codecs to quality-based HEVC and skips sources already in HEVC or AV1.',
        build() {
            const hevc = normalizeProfile({
                id: guid(), name: 'HEVC quality (CRF 24)', codec: 'h265', preset: null, hardwareAcceleration: 'none',
                rateControl: { mode: 'Quality', quality: 24 }, outputRetention: 'AlwaysKeep',
            });
            return {
                profiles: [hevc],
                rules: [
                    normalizeRule({ id: guid(), name: 'Older codecs', profileId: hevc.id, conditions: [{ field: 'Codec', operator: 'NotIn', values: ['h265', 'av1'] }] }),
                    normalizeRule({ id: guid(), name: 'Modern codecs — leave alone', action: 'Skip', conditions: [{ field: 'Codec', operator: 'In', values: ['h265', 'av1'] }] }),
                ],
                defaultAction: 'UseSimpleSettings',
            };
        },
    },
    {
        key: 'libaom-expert',
        name: 'Fine-tuned libaom-av1',
        icon: 'fa-flask',
        badge: 'EXPERT',
        spec: 'libaom-av1 · CQ 32/35 · 10-bit · -aom-params',
        description: 'The community-requested archival policy: exact libaom-av1, GOP 300, custom -aom-params. Requires an FFmpeg build with libaom.',
        build() {
            const expertOptions = cq => [
                { option: '-aq-mode', values: ['1'] },
                { option: '-tune', values: ['ssim'] },
                { option: '-lag-in-frames', values: ['35'] },
                { option: '-arnr-max-frames', values: ['15'] },
                { option: '-arnr-strength', values: ['4'] },
                { option: '-aom-params', values: [`tune=ssim:cq-level=${cq.quality}:cpu-used=${cq.speed}:noise-sensitivity=2:tune-content=default:arnr-strength=4:arnr-maxframes=15:enable-qm=1:enable-chroma-deltaq=1:quant-b-adapt=1`] },
            ];
            const hd = normalizeProfile({
                id: guid(), name: 'AV1 libaom 1080p CQ 35', codec: 'av1',
                encoderSelection: 'Explicit', encoder: 'libaom-av1', hardwareAcceleration: 'none',
                rateControl: { mode: 'Quality', quality: 35 }, preset: '4', threads: 8,
                pixelFormat: 'yuv420p10le', gopSize: 300, downscaleTarget: '1080p',
                customOptions: expertOptions({ quality: 35, speed: 4 }), outputRetention: 'AlwaysKeep',
            });
            const fourK = normalizeProfile({
                id: guid(), name: 'AV1 libaom 4K CQ 32', codec: 'av1',
                encoderSelection: 'Explicit', encoder: 'libaom-av1', hardwareAcceleration: 'none',
                rateControl: { mode: 'Quality', quality: 32, targetKbps: 14000 }, preset: '3', threads: 12,
                pixelFormat: 'yuv420p10le', gopSize: 300, downscaleTarget: '4K',
                customOptions: expertOptions({ quality: 32, speed: 3 }), outputRetention: 'AlwaysKeep',
            });
            return {
                profiles: [hd, fourK],
                rules: [
                    normalizeRule({
                        id: guid(), name: 'Non-AV1 4K sources', profileId: fourK.id,
                        conditions: [
                            { field: 'Codec', operator: 'IsNot', values: ['av1'] },
                            { field: 'ResolutionClass', operator: 'Is', values: ['2160p+'] },
                        ],
                    }),
                    normalizeRule({
                        id: guid(), name: 'Other non-AV1 sources', profileId: hd.id,
                        conditions: [
                            { field: 'Codec', operator: 'IsNot', values: ['av1'] },
                            { field: 'ResolutionClass', operator: 'NotIn', values: ['2160p+'] },
                        ],
                    }),
                ],
                defaultAction: 'UseSimpleSettings',
            };
        },
    },
];

/**
 * Rehydrates a stored policy with fresh ids, remapping rule and default
 * references onto the new profile ids. Applied to user templates and imports
 * so repeated loads never cross-link a previous load's rules.
 */
export function materializePolicy(raw) {
    const normalized = normalizeAdvanced(raw);
    const idMap = new Map();
    normalized.profiles.forEach(profile => {
        const fresh = guid();
        idMap.set(String(profile.id).toLowerCase(), fresh);
        profile.id = fresh;
    });
    normalized.rules.forEach(rule => {
        rule.id = guid();
        rule.profileId = rule.profileId == null ? null : idMap.get(String(rule.profileId).toLowerCase()) ?? null;
    });
    normalized.defaultProfileId = normalized.defaultProfileId == null
        ? null : idMap.get(String(normalized.defaultProfileId).toLowerCase()) ?? null;
    return normalized;
}

// Renders with the same card anatomy as the Simple quality presets
// (settings/presets.js renderBuiltinPresets) so both galleries read as one system.
function renderTemplates() {
    const root = byId('advancedVideoTemplates');
    if (!root) return;
    const builtIn = ADVANCED_VIDEO_TEMPLATES.map(template => `
        <div class="col-12 col-md-6 col-xl-3">
            <button type="button" class="card w-100 h-100 text-start preset-card" data-template-key="${escapeHtml(template.key)}">
                <div class="card-body p-2">
                    <div class="d-flex align-items-center justify-content-between">
                        <div class="fw-bold"><i class="fas ${escapeHtml(template.icon)} me-2 text-primary"></i>${escapeHtml(template.name)}</div>
                        ${template.badge ? `<span class="badge bg-primary" style="font-size: 0.65rem;">${escapeHtml(template.badge)}</span>` : ''}
                    </div>
                    <div class="small text-muted mt-1" style="min-height: 2.5em;">${escapeHtml(template.description)}</div>
                    <div class="small text-info mt-1">${escapeHtml(template.spec)}</div>
                </div>
            </button>
        </div>`);
    const user = userTemplates.map(template => {
        const block = template.advancedVideo ?? {};
        const spec = `${(block.profiles ?? []).length} recipe(s) · ${(block.rules ?? []).length} rule(s)`;
        return `
        <div class="col-12 col-md-6 col-xl-3">
            <div class="card w-100 h-100 preset-card av-user-template">
                <button type="button" class="card-body p-2 text-start border-0 bg-transparent w-100" data-template-key="user:${escapeHtml(template.name)}">
                    <div class="d-flex align-items-center justify-content-between">
                        <div class="fw-bold text-truncate"><i class="fas fa-bookmark me-2 text-primary"></i>${escapeHtml(template.name)}</div>
                        <span class="badge bg-secondary" style="font-size: 0.65rem;">SAVED</span>
                    </div>
                    <div class="small text-muted mt-1" style="min-height: 2.5em;">Your saved policy. Loads with fresh ids; nothing applies until Validate &amp; Apply.</div>
                    <div class="small text-info mt-1">${escapeHtml(spec)}</div>
                </button>
                <button type="button" class="btn btn-sm btn-outline-danger av-user-template-delete" data-template-delete="${escapeHtml(template.name)}" title="Delete this saved template" aria-label="Delete template ${escapeHtml(template.name)}"><i class="fas fa-xmark"></i></button>
            </div>
        </div>`;
    });
    root.innerHTML = [...builtIn, ...user].join('');
}

async function loadUserTemplates() {
    try {
        const result = await settingsApi.advancedVideoTemplates.list();
        userTemplates = result.templates ?? [];
        renderTemplates();
    } catch { /* gallery still shows built-ins */ }
}

async function applyTemplate(key) {
    let name;
    let built;
    if (key.startsWith('user:')) {
        const stored = userTemplates.find(item => `user:${item.name}` === key);
        if (!stored) return;
        name = stored.name;
        built = materializePolicy(stored.advancedVideo);
    } else {
        const template = ADVANCED_VIDEO_TEMPLATES.find(item => item.key === key);
        if (!template) return;
        name = template.name;
        built = template.build();
    }
    if (staged.profiles.length || staged.rules.length) {
        const ok = await showConfirmModal(
            'Load Template',
            `Load "${name}"? This replaces the current advanced video draft. Nothing is saved until you press Validate & Apply.`,
            'Load');
        if (!ok) return;
    }
    staged = {
        ...staged,
        enabled: true,
        profiles: built.profiles,
        rules: built.rules,
        defaultAction: built.defaultAction,
        defaultProfileId: built.defaultProfileId ?? null,
    };
    selectedProfileId = staged.profiles[0]?.id ?? null;
    selectedRuleId = staged.rules[0]?.id ?? null;
    renderAll();
    validate();
    fetchImpact(true);
}

// ---------------------------------------------------------------------------
// Sharing: export/import a policy file, save the draft as a reusable template.
// ---------------------------------------------------------------------------

function exportPolicy() {
    const payload = JSON.stringify({ advancedVideo: clone(staged) }, null, 2);
    const blob = new Blob([payload], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = 'snacks-video-policy.json';
    link.click();
    URL.revokeObjectURL(url);
}

async function importPolicyFile(file) {
    let parsed;
    try {
        parsed = JSON.parse(await file.text());
    } catch {
        if (typeof showToast === 'function') showToast('Not a valid JSON file', 'danger');
        return;
    }
    // Accept both a full export ({advancedVideo: {...}}) and a bare block.
    const raw = parsed?.advancedVideo ?? parsed;
    if (!raw || typeof raw !== 'object' || (!Array.isArray(raw.profiles) && !Array.isArray(raw.rules))) {
        if (typeof showToast === 'function') showToast('This file does not contain an Advanced Video policy', 'danger');
        return;
    }
    if (staged.profiles.length || staged.rules.length) {
        const ok = await showConfirmModal('Import Policy',
            `Import "${file.name}"? This replaces the current advanced video draft. Nothing is saved until you press Validate & Apply.`, 'Import');
        if (!ok) return;
    }
    const built = materializePolicy(raw);
    staged = { ...staged, enabled: true, profiles: built.profiles, rules: built.rules, defaultAction: built.defaultAction, defaultProfileId: built.defaultProfileId };
    selectedProfileId = staged.profiles[0]?.id ?? null;
    selectedRuleId = staged.rules[0]?.id ?? null;
    renderAll();
    validate();
    fetchImpact(true);
}

async function saveDraftAsTemplate() {
    const name = value('advancedVideoTemplateName').trim();
    if (!name) return;
    try {
        const result = await settingsApi.advancedVideoTemplates.save(name, clone(staged));
        userTemplates = result.templates ?? userTemplates;
        renderTemplates();
        byId('advancedVideoSaveTemplateRow').style.display = 'none';
        if (typeof showToast === 'function') showToast(`Saved template "${name}"`, 'success');
    } catch (error) {
        if (typeof showToast === 'function') showToast(`Could not save template: ${error.message}`, 'danger');
    }
}

// ---------------------------------------------------------------------------
// Library impact: the staged policy run against every tracked video, live.
// ---------------------------------------------------------------------------

let lastImpact = null;
let lastImpactKey = null;
let impactTimer = null;
let argsTimer = null;

const PROFILE_PALETTE = ['#8b5cf6', '#a78bfa', '#6d28d9', '#c4b5fd', '#7c3aed'];

function bucketColor(bucket, profileOrdinal) {
    if (bucket.blocked) return 'var(--danger)';
    switch (bucket.action) {
        case 'Skip': return 'var(--gray-400)';
        case 'UseSimpleSettings': return '#3b82f6';
        case 'MuxOnly': return '#14b8a6';
        default: return PROFILE_PALETTE[profileOrdinal % PROFILE_PALETTE.length];
    }
}

export function fmtBytes(bytes) {
    if (!bytes || bytes <= 0) return null;
    if (bytes >= 1e12) return `${(bytes / 1e12).toFixed(1)} TB`;
    if (bytes >= 1e9) return `${(bytes / 1e9).toFixed(1)} GB`;
    if (bytes >= 1e6) return `${(bytes / 1e6).toFixed(0)} MB`;
    return `${Math.round(bytes / 1e3)} kB`;
}

function bucketLabel(bucket) {
    if (bucket.blocked) return `Blocked: ${bucket.blockingReason}`;
    if (bucket.action === 'TranscodeWithProfile') return `Encode with “${bucket.profileName ?? 'missing recipe'}”`;
    const phrase = actionPhrase(bucket.action, null);
    return phrase.charAt(0).toUpperCase() + phrase.slice(1);
}

function impactStateKey() {
    return JSON.stringify({ e: staged.enabled, p: staged.profiles, r: staged.rules, a: staged.defaultAction, d: staged.defaultProfileId, q: impactFileQuery });
}

function scheduleImpact(delay = 800) {
    clearTimeout(impactTimer);
    byId('advancedVideoImpact')?.classList.add('av-impact-pending');
    impactTimer = setTimeout(() => fetchImpact(), delay);
}

async function fetchImpact(force = false) {
    const root = byId('advancedVideoImpact');
    if (!root) return;
    if (!staged.enabled || (!staged.profiles.length && !staged.rules.length)) {
        lastImpact = null;
        lastImpactKey = null;
        renderImpact(null);
        return;
    }
    const key = impactStateKey();
    if (!force && key === lastImpactKey) {
        root.classList.remove('av-impact-pending');
        return;
    }
    try {
        const result = await settingsApi.impactAdvancedVideo(staged, impactFileQuery || null);
        lastImpact = result;
        lastImpactKey = key;
        renderImpact(result);
        renderRuleList(); // shadow chips and count badges live on the flow cards
    } catch (error) {
        renderImpact({ error: error.message });
    }
}

function renderImpact(result) {
    const root = byId('advancedVideoImpact');
    if (!root) return;
    root.classList.remove('av-impact-pending');
    renderImpactBadges();
    if (!staged.enabled) {
        root.innerHTML = '<div class="small text-muted border rounded p-3">Enable advanced video policies to preview their effect on your library.</div>';
        return;
    }
    if (!result) {
        root.innerHTML = '<div class="small text-muted border rounded p-3">Add a rule or recipe — the effect on your library appears here automatically.</div>';
        return;
    }
    if (result.error) {
        root.innerHTML = `<div class="alert alert-danger py-2 small mb-0">Impact preview failed: ${escapeHtml(result.error)}</div>`;
        return;
    }
    if (result.valid === false) {
        root.innerHTML = '<div class="alert alert-warning py-2 small mb-0"><i class="fas fa-triangle-exclamation me-1"></i>Fix the validation problems shown below to preview the library impact.</div>';
        return;
    }
    if (!result.analyzed) {
        root.innerHTML = '<div class="small text-muted border rounded p-3">Snacks has not scanned any videos yet. Add a watched folder or run a scan, then check back — meanwhile the hypothetical tester below works without a library.</div>';
        return;
    }

    let profileOrdinal = -1;
    const colored = result.buckets.map(bucket => {
        if (!bucket.blocked && bucket.action === 'TranscodeWithProfile') profileOrdinal++;
        return { bucket, color: bucketColor(bucket, Math.max(profileOrdinal, 0)) };
    });

    const bar = colored.map(({ bucket, color }) =>
        `<span class="av-impact-seg" style="width:${Math.max((bucket.count / result.analyzed) * 100, 1.5)}%;background:${color}" title="${escapeHtml(`${bucket.count} — ${bucketLabel(bucket)}`)}"></span>`).join('');

    const legend = colored.map(({ bucket, color }) => {
        const today = fmtBytes(bucket.totalBytes);
        const projected = fmtBytes(bucket.projectedBytes);
        const size = today
            ? `<span class="text-muted"> · ${today} today${projected ? ` → ~${projected} after` : ''}</span>`
            : '';
        const settled = bucket.alreadyProcessedCount > 0
            ? `<span class="text-muted"> · ${bucket.alreadyProcessedCount} already processed</span>`
            : '';
        const via = bucket.ruleNames?.length ? `<span class="text-muted"> · via ${bucket.ruleNames.map(escapeHtml).join(', ')}</span>` : '';
        const samples = (bucket.samples ?? []).map(sample => {
            const dims = sample.width && sample.height ? ` · ${sample.width}×${sample.height}` : '';
            const rule = sample.ruleName ? ` <span class="text-muted">(${escapeHtml(sample.ruleName)})</span>` : '';
            return `<li class="text-truncate">${escapeHtml(sample.fileName)} <span class="text-muted">— ${escapeHtml(sample.codec || 'unknown')}${dims}</span>${rule}</li>`;
        }).join('');
        return `<div class="av-impact-row">
            <span class="av-impact-swatch" style="background:${color}"></span>
            <span class="av-impact-count">${bucket.count}</span>
            <div class="flex-grow-1">
                <span class="${bucket.blocked ? 'text-danger' : ''}">${escapeHtml(bucketLabel(bucket))}</span>${size}${settled}${via}
                ${samples ? `<details class="av-impact-samples"><summary class="small text-muted">show examples</summary><ul class="small mb-0">${samples}</ul></details>` : ''}
            </div>
        </div>`;
    }).join('');

    const matches = (result.fileMatches ?? []).map(match => {
        const outcome = match.blocked
            ? `<span class="text-danger">blocked — ${escapeHtml(match.blockingReason ?? '')}</span>`
            : escapeHtml(actionPhrase(match.action, match.profileName));
        const rule = match.ruleName ? ` <span class="text-muted">(rule ${escapeHtml(match.ruleName)})</span>` : '';
        return `<li class="text-truncate"><span class="fw-semibold">${escapeHtml(match.fileName)}</span> → ${outcome}${rule}</li>`;
    }).join('');
    const matchBlock = impactFileQuery
        ? `<div class="av-impact-matches border rounded p-2 mb-2 small">
            ${matches ? `<ul class="mb-0">${matches}</ul>` : `No tracked video name contains “${escapeHtml(impactFileQuery)}”.`}
           </div>`
        : '';

    const truncated = result.truncated
        ? `<div class="small text-warning mt-1"><i class="fas fa-circle-info me-1"></i>${result.sampled
            ? `Showing a random sample of ${result.analyzed} of ${result.totalVideoFiles} videos.`
            : `Showing the first ${result.analyzed} of ${result.totalVideoFiles} videos.`}</div>`
        : '';

    const reeval = showReevalPrompt
        ? `<div class="alert alert-success py-2 small mt-2 mb-0 d-flex align-items-center gap-2">
            <i class="fas fa-circle-check"></i>
            <span class="flex-grow-1">Policy applied. Files already in the catalog keep their current status until you re-evaluate them.</span>
            <button type="button" class="btn btn-sm btn-success" id="advancedVideoReevaluate"><i class="fas fa-rotate me-1"></i>Re-evaluate library</button>
           </div>`
        : '<div class="small text-muted mt-2"><i class="fas fa-circle-info me-1"></i>Applying a policy never reprocesses existing files by itself — use <strong>Re-evaluate</strong> when old decisions should be reconsidered.</div>';

    root.innerHTML = `
        ${matchBlock}
        <div class="small text-muted mb-2">${result.analyzed} video${result.analyzed === 1 ? '' : 's'} in your library, decided by the staged flow:</div>
        <div class="av-impact-bar" role="img" aria-label="Breakdown of policy outcomes across the library">${bar}</div>
        <div class="mt-2">${legend}</div>${truncated}${reeval}`;
}

// ---------------------------------------------------------------------------
// Measured reality: what applied policies have actually done (EncodeHistory).
// ---------------------------------------------------------------------------

async function loadMeasured() {
    try {
        measured = await settingsApi.measuredAdvancedVideo();
    } catch {
        measured = null;
    }
    renderMeasured();
}

function renderMeasured() {
    const root = byId('advancedVideoMeasured');
    if (!root) return;
    const profiles = measured?.profiles ?? [];
    if (!profiles.length) {
        root.innerHTML = '';
        return;
    }
    const rows = profiles.map(profile => {
        const saved = fmtBytes(profile.bytesSaved);
        const inBytes = fmtBytes(profile.originalBytes);
        const outBytes = fmtBytes(profile.encodedBytes);
        const stagedProfile = staged.profiles.find(p =>
            String(p.id).toLowerCase() === String(profile.profileId ?? '').toLowerCase() || p.name === profile.profileName);
        const target = stagedProfile?.rateControl?.mode === 'Bitrate' && profile.avgEncodedKbps
            ? ` · target ${stagedProfile.rateControl.targetKbps} → measured ${profile.avgEncodedKbps} kb/s`
            : profile.avgEncodedKbps ? ` · avg output ${profile.avgEncodedKbps} kb/s` : '';
        const discarded = profile.discarded > 0 ? ` · ${profile.discarded} discarded (not smaller)` : '';
        return `<div class="av-impact-row">
            <span class="av-impact-swatch" style="background: var(--success)"></span>
            <span class="av-impact-count">${profile.jobs}</span>
            <div class="flex-grow-1">
                <span>“${escapeHtml(profile.profileName)}”</span>
                <span class="text-muted">${inBytes && outBytes ? ` · ${inBytes} → ${outBytes}` : ''}${saved ? ` · saved ${saved}` : ''}${escapeHtml(target)}${discarded}</span>
            </div>
        </div>`;
    }).join('');
    root.innerHTML = `
        <div class="small fw-semibold mb-1"><i class="fas fa-scale-balanced me-1 text-success"></i>Measured so far — completed encodes by recipe</div>
        ${rows}`;
}

/** Per-card "N files" badges on the flow, from the latest impact pass. */
function renderImpactBadges() {
    const counts = lastImpact?.ruleCounts ?? null;
    const fresh = counts && staged.enabled && lastImpactKey === impactStateKey();
    document.querySelectorAll('[data-rule-count]').forEach(node => {
        const id = String(node.dataset.ruleCount).toLowerCase();
        const count = fresh ? Object.entries(counts).find(([key]) => key.toLowerCase() === id)?.[1] ?? 0 : null;
        node.classList.toggle('d-none', count == null);
        if (count != null) node.textContent = `${count} file${count === 1 ? '' : 's'}`;
    });
    const defaultBadge = byId('advancedVideoDefaultCount');
    if (defaultBadge) {
        const count = fresh ? lastImpact.unmatchedCount ?? 0 : null;
        defaultBadge.classList.toggle('d-none', count == null);
        if (count != null) defaultBadge.textContent = `${count} file${count === 1 ? '' : 's'}`;
    }
}

/** Debounced live FFmpeg-argument strip for the recipe being edited. */
function scheduleArgsPreview(delay = 500) {
    clearTimeout(argsTimer);
    argsTimer = setTimeout(async () => {
        const strip = byId('advancedVideoProfileArgs');
        const profile = selectedProfile();
        if (!strip || !profile) return;
        try {
            const result = await settingsApi.validateAdvancedVideo(staged, { profileId: profile.id });
            if (result.valid === false) {
                const firstError = (result.diagnostics ?? []).find(d => String(d.severity).toLowerCase() === 'error');
                strip.textContent = firstError ? `⚠ ${firstError.message}` : '⚠ Configuration is invalid.';
            } else {
                strip.textContent = result.preview
                    ? `${result.encoder ? `-c:v ${result.encoder} ` : ''}${result.preview}`
                    : 'No generated video arguments (Custom mode — add guarded options below).';
            }
        } catch {
            strip.textContent = '—';
        }
    }, delay);
}

function sampleFacts() {
    const width = nullableInteger('advancedVideoSampleWidth');
    const height = nullableInteger('advancedVideoSampleHeight');
    const smaller = width && height ? Math.min(width, height) : null;
    const resolutionClass = smaller == null ? null : smaller >= 2160 ? '2160p+'
        : smaller >= 1440 ? '1440p' : smaller >= 1080 ? '1080p' : smaller >= 720 ? '720p' : 'SD';
    const nullableBoolean = id => {
        const raw = value(id, '');
        return raw === 'true' ? true : raw === 'false' ? false : null;
    };
    return {
        codec: nullableText('advancedVideoSampleCodec'), width, height, resolutionClass,
        bitrateKbps: nullableInteger('advancedVideoSampleBitrate'),
        fileSizeBytes: nullableInteger('advancedVideoSampleFileSize'),
        durationSeconds: nullableInteger('advancedVideoSampleDuration'),
        pixelFormat: nullableText('advancedVideoSamplePixelFormat'),
        bitDepth: nullableInteger('advancedVideoSampleBitDepth'),
        isHdr: nullableBoolean('advancedVideoSampleHdr'),
        // Match the persisted Snacks 4K flag (the existing pipeline defines it
        // as a coded width above 1920); resolution-class matching remains based
        // on the smaller coded dimension.
        is4K: nullableBoolean('advancedVideoSample4K') ?? (width != null ? width > 1920 : null),
    };
}

function renderValidation(result) {
    const root = byId('advancedVideoDiagnostics');
    const diagnostics = [...(result?.diagnostics ?? [])];
    if (customParseError) diagnostics.unshift({ severity: 'Error', path: 'advancedVideo.profiles.customOptions', code: 'invalid_json_values', message: customParseError });
    root.innerHTML = diagnostics.length ? diagnostics.map(item => {
        const severity = String(item.severity).toLowerCase();
        const kind = severity === 'error' ? 'danger' : severity === 'warning' ? 'warning' : 'info';
        return `<div class="alert alert-${kind} py-1 px-2 mb-1 small"><strong>${escapeHtml(item.code || item.severity)}:</strong> ${escapeHtml(item.message)} <code>${escapeHtml(item.path || '')}</code></div>`;
    }).join('') : '<div class="alert alert-success py-1 px-2 mb-1 small"><i class="fas fa-check me-1"></i>Configuration is valid.</div>';
    const plan = result?.plan;
    const summary = plan ? `${plan.action}${plan.ruleName ? ` · rule ${plan.ruleName}` : ''}${plan.profileName ? ` · profile ${plan.profileName}` : ''}` : '';
    // The server previews the selected profile's arguments, which need not be
    // the profile the sample facts matched — name whose arguments these are.
    const argumentsFor = selectedProfile()?.name ?? plan?.profileName ?? null;
    const previewLabel = result?.preview && argumentsFor ? `Arguments for profile “${argumentsFor}”:` : '';
    byId('advancedVideoPreview').textContent = [summary, result?.encoder ? `Encoder: ${result.encoder}` : '', previewLabel, result?.preview || 'No video arguments for this action.'].filter(Boolean).join('\n');
}

async function validate({ includeSample = true } = {}) {
    syncProfileFromForm();
    syncRuleFromForm();
    if (customParseError) {
        renderValidation({ diagnostics: [] });
        return { valid: false };
    }
    try {
        const result = await settingsApi.validateAdvancedVideo(staged, {
            profileId: selectedProfileId,
            sourceFacts: includeSample ? sampleFacts() : null,
        });
        renderValidation(result);
        if (includeSample && result.plan) {
            // Light up where the hypothetical file lands in the flow.
            lastSampleMatch = result.plan.ruleId ?? 'default';
            renderRuleList();
        }
        return result;
    } catch (error) {
        renderValidation({ diagnostics: [{ severity: 'Error', code: 'validation_request_failed', path: '', message: error.message }] });
        return { valid: false };
    }
}

async function applyStaged() {
    const result = await validate({ includeSample: false });
    if (!result.valid) return;
    const before = committed;
    committed = clone(staged);
    try {
        if (readOptions) await settingsApi.save(readOptions());
        previousApplied = clone(before);
        byId('advancedVideoRestorePrevious')?.classList.remove('d-none');
        showReevalPrompt = true;
        staged = clone(committed);
        renderAll();
        fetchImpact(true);
        loadMeasured();
        document.dispatchEvent(new CustomEvent('snacks:advanced-video-profiles-changed'));
        if (typeof showToast === 'function') showToast('Advanced video policies applied', 'success');
    } catch (error) {
        committed = before;
        staged = clone(committed);
        renderAll();
        renderValidation({ diagnostics: [{ severity: 'Error', code: 'save_failed', path: 'advancedVideo', message: error.message }] });
    }
}

async function loadCatalog() {
    try {
        const [catalog, scan] = await Promise.all([settingsApi.getVideoEncoders(), autoScanApi.getConfig()]);
        encoders = catalog.encoders ?? [];
        folderReferences = new Map();
        for (const folder of scan.directories ?? []) {
            if (typeof folder !== 'object') continue;
            const overrides = folder.encodingOverrides;
            const id = overrides?.advancedVideoProfileId;
            if (!id) continue;
            const key = String(id).toLowerCase();
            folderReferences.set(key, [...(folderReferences.get(key) ?? []), folder.path]);
        }
        renderAll();
    } catch {
        // The editor remains usable and unavailable explicit encoders surface on validation.
    }
}

function bindEvents() {
    byId('advancedVideoEnabled')?.addEventListener('change', event => { staged.enabled = event.target.checked; renderAll(false); });
    byId('advancedVideoCreateProfile')?.addEventListener('click', () => {
        const profile = profileFromSimple(readOptions?.() ?? {});
        staged.profiles.push(profile); selectedProfileId = profile.id; renderAll();
    });
    byId('advancedVideoDuplicateProfile')?.addEventListener('click', () => {
        const source = selectedProfile(); if (!source) return;
        const profile = clone(source); profile.id = guid(); profile.name = `${source.name} copy`;
        staged.profiles.push(profile); selectedProfileId = profile.id; renderAll();
    });
    byId('advancedVideoDeleteProfile')?.addEventListener('click', () => {
        const profile = selectedProfile(); if (!profile) return;
        const refs = profileReferences(profile.id);
        if (refs.length) { renderValidation({ diagnostics: [{ severity: 'Error', code: 'profile_referenced', path: 'advancedVideo.profiles', message: `Reassign ${refs.join(', ')} before deleting this profile.` }] }); return; }
        staged.profiles = staged.profiles.filter(item => item.id !== profile.id);
        selectedProfileId = staged.profiles[0]?.id ?? null; renderAll();
    });
    byId('advancedVideoProfileList')?.addEventListener('click', event => {
        const button = event.target.closest('[data-profile-id]'); if (!button) return;
        syncProfileFromForm(); selectedProfileId = button.dataset.profileId; renderAll();
    });
    byId('advancedVideoProfileEditor')?.addEventListener('input', event =>
        syncProfileFromForm({ rateChanged: event.target.id === 'advancedVideoRateMode' }));
    byId('advancedVideoProfileEditor')?.addEventListener('change', event => syncProfileFromForm({ rateChanged: event.target.id === 'advancedVideoRateMode' }));
    byId('advancedVideoAddOption')?.addEventListener('click', () => { const profile = selectedProfile(); if (!profile) return; syncProfileFromForm(); profile.customOptions.push({ option: '', values: [] }); renderProfileEditor(); markDirty(); });
    byId('advancedVideoCustomOptions')?.addEventListener('click', event => {
        const button = event.target.closest('[data-remove-custom]'); if (!button) return;
        const profile = selectedProfile(); if (!profile) return;
        profile.customOptions.splice(Number(button.dataset.removeCustom), 1); renderProfileEditor(); markDirty();
    });

    byId('advancedVideoCreateRule')?.addEventListener('click', () => {
        const rule = normalizeRule({ id: guid(), name: 'New rule', profileId: staged.profiles[0]?.id ?? null, conditions: [{ field: 'Codec', operator: 'IsNot', values: ['av1'] }] });
        staged.rules.push(rule); selectedRuleId = rule.id; renderAll();
    });
    byId('advancedVideoRuleList')?.addEventListener('click', event => {
        const move = event.target.closest('[data-rule-move]');
        if (move) {
            const from = Number(move.dataset.ruleIndex); const to = move.dataset.ruleMove === 'up' ? from - 1 : from + 1;
            if (to >= 0 && to < staged.rules.length) [staged.rules[from], staged.rules[to]] = [staged.rules[to], staged.rules[from]];
            renderRuleList(); markDirty(); return;
        }
        const button = event.target.closest('[data-rule-id]'); if (!button) return;
        syncRuleFromForm(); selectedRuleId = button.dataset.ruleId; renderAll();
    });
    byId('advancedVideoRuleList')?.addEventListener('dragstart', event => {
        const row = event.target.closest('[data-rule-row]');
        if (!row) return;
        syncRuleFromForm();
        draggedRuleId = row.dataset.ruleRow;
        row.classList.add('opacity-50');
        event.dataTransfer.effectAllowed = 'move';
        event.dataTransfer.setData('text/plain', draggedRuleId);
    });
    byId('advancedVideoRuleList')?.addEventListener('dragover', event => {
        const target = event.target.closest('[data-rule-row]');
        if (!draggedRuleId || !target) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = 'move';
        document.querySelectorAll('#advancedVideoRuleList .av-drop-target')
            .forEach(row => { if (row !== target) row.classList.remove('av-drop-target'); });
        if (target.dataset.ruleRow !== draggedRuleId) target.classList.add('av-drop-target');
    });
    byId('advancedVideoRuleList')?.addEventListener('drop', event => {
        const target = event.target.closest('[data-rule-row]');
        if (!draggedRuleId || !target || target.dataset.ruleRow === draggedRuleId) return;
        event.preventDefault();
        const from = staged.rules.findIndex(rule => rule.id === draggedRuleId);
        const to = staged.rules.findIndex(rule => rule.id === target.dataset.ruleRow);
        if (from < 0 || to < 0) return;
        const [moved] = staged.rules.splice(from, 1);
        staged.rules.splice(to, 0, moved);
        draggedRuleId = null;
        renderRuleList();
        markDirty();
    });
    byId('advancedVideoRuleList')?.addEventListener('dragend', () => {
        draggedRuleId = null;
        document.querySelectorAll('#advancedVideoRuleList [data-rule-row]')
            .forEach(row => row.classList.remove('opacity-50', 'av-drop-target'));
    });
    byId('advancedVideoRuleEditor')?.addEventListener('input', () => syncRuleFromForm());
    byId('advancedVideoRuleEditor')?.addEventListener('change', event => {
        syncRuleFromForm();
        if (event.target.dataset.conditionField === 'operator' || event.target.dataset.conditionField === 'field') renderRuleEditor();
    });
    byId('advancedVideoAddCondition')?.addEventListener('click', () => { const rule = selectedRule(); if (!rule) return; syncRuleFromForm(); rule.conditions.push({ field: 'Codec', operator: 'Is', values: ['av1'] }); renderRuleEditor(); renderRuleList(); markDirty(); });
    byId('advancedVideoConditions')?.addEventListener('click', event => { const button = event.target.closest('[data-remove-condition]'); const rule = selectedRule(); if (!button || !rule) return; rule.conditions.splice(Number(button.dataset.removeCondition), 1); renderRuleEditor(); renderRuleList(); markDirty(); });
    byId('advancedVideoDuplicateRule')?.addEventListener('click', () => { const source = selectedRule(); if (!source) return; const rule = clone(source); rule.id = guid(); rule.name = `${source.name} copy`; staged.rules.push(rule); selectedRuleId = rule.id; renderAll(); });
    byId('advancedVideoDeleteRule')?.addEventListener('click', () => { const rule = selectedRule(); if (!rule) return; staged.rules = staged.rules.filter(item => item.id !== rule.id); selectedRuleId = staged.rules[0]?.id ?? null; renderAll(); });

    byId('advancedVideoDefaultAction')?.addEventListener('change', event => { staged.defaultAction = event.target.value; if (staged.defaultAction !== ACTION_PROFILE) staged.defaultProfileId = null; renderDefaults(); markDirty(); });
    byId('advancedVideoDefaultProfile')?.addEventListener('change', event => { staged.defaultProfileId = event.target.value || null; markDirty(); });
    byId('advancedVideoTemplates')?.addEventListener('click', async event => {
        const del = event.target.closest('[data-template-delete]');
        if (del) {
            const name = del.dataset.templateDelete;
            const ok = await showConfirmModal('Delete Template', `Delete the saved template "${name}"?`, 'Delete');
            if (!ok) return;
            try {
                const result = await settingsApi.advancedVideoTemplates.remove(name);
                userTemplates = result.templates ?? [];
                renderTemplates();
            } catch (error) {
                if (typeof showToast === 'function') showToast(`Could not delete template: ${error.message}`, 'danger');
            }
            return;
        }
        const button = event.target.closest('[data-template-key]');
        if (button) applyTemplate(button.dataset.templateKey);
    });
    byId('advancedVideoImpactRefresh')?.addEventListener('click', () => fetchImpact(true));
    let impactSearchTimer = null;
    byId('advancedVideoImpactSearch')?.addEventListener('input', event => {
        clearTimeout(impactSearchTimer);
        impactSearchTimer = setTimeout(() => {
            impactFileQuery = event.target.value.trim();
            fetchImpact(true);
        }, 400);
    });
    byId('advancedVideoImpact')?.addEventListener('click', async event => {
        if (!event.target.closest('#advancedVideoReevaluate')) return;
        const ok = await showConfirmModal('Re-evaluate Library',
            'Re-run the decision flow over the existing catalog? Files whose decision changes are re-queued or re-skipped accordingly.', 'Re-evaluate');
        if (!ok) return;
        try {
            await settingsApi.reevaluate();
            showReevalPrompt = false;
            fetchImpact(true);
            if (typeof showToast === 'function') showToast('Library re-evaluation started', 'success');
        } catch (error) {
            if (typeof showToast === 'function') showToast(`Re-evaluate failed: ${error.message}`, 'danger');
        }
    });
    byId('advancedVideoExportPolicy')?.addEventListener('click', exportPolicy);
    byId('advancedVideoImportPolicy')?.addEventListener('click', () => byId('advancedVideoImportFile')?.click());
    byId('advancedVideoImportFile')?.addEventListener('change', event => {
        const file = event.target.files?.[0];
        event.target.value = '';
        if (file) importPolicyFile(file);
    });
    byId('advancedVideoSaveTemplate')?.addEventListener('click', () => {
        const row = byId('advancedVideoSaveTemplateRow');
        if (!row) return;
        row.style.display = 'flex';
        byId('advancedVideoTemplateName')?.focus();
    });
    byId('advancedVideoTemplateSaveConfirm')?.addEventListener('click', saveDraftAsTemplate);
    byId('advancedVideoTemplateName')?.addEventListener('keydown', event => { if (event.key === 'Enter') saveDraftAsTemplate(); });
    byId('advancedVideoTemplateSaveCancel')?.addEventListener('click', () => { byId('advancedVideoSaveTemplateRow').style.display = 'none'; });
    byId('advancedVideoRestorePrevious')?.addEventListener('click', () => {
        if (!previousApplied) return;
        staged = clone(previousApplied);
        selectedProfileId = staged.profiles[0]?.id ?? null;
        selectedRuleId = staged.rules[0]?.id ?? null;
        renderAll();
        if (typeof showToast === 'function') showToast('Previous policy staged — press Validate & Apply to keep it', 'info');
    });
    byId('advancedVideoQualitySlider')?.addEventListener('input', event =>
        setInput('advancedVideoQuality', event.target.value));
    byId('advancedVideoValidate')?.addEventListener('click', () => validate());
    byId('advancedVideoApply')?.addEventListener('click', applyStaged);
    byId('advancedVideoCancel')?.addEventListener('click', () => { staged = clone(committed); customParseError = null; renderAll(); renderValidation({ diagnostics: [] }); });
    document.addEventListener('snacks:advanced-video-profiles-changed', loadCatalog);
}

export function initAdvancedVideoEditor({ readEncoderOptions } = {}) {
    if (initialized || !byId('advancedVideoWorkspace')) return;
    initialized = true;
    readOptions = readEncoderOptions ?? null;
    bindEvents();
    renderTemplates();
    renderAll();
    loadCatalog();
    loadUserTemplates();
    loadMeasured();
}
