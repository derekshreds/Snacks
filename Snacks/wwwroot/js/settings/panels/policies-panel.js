/**
 * Policies tab - "Quick Setups" picker.
 *
 * Two layers of UI, deliberately separated so a non-expert never has to look
 * at the second one:
 *
 *   LAYER 1 (default): outcome bullets.
 *     "What this does to your library:" with 3-6 plain-English bullets
 *     populated from policy.OutcomeBullets. This is the entire experience
 *     for the persona who doesn't know what HEVC means.
 *
 *   LAYER 2 (disclosure): per-field diff table.
 *     "Show technical settings" reveals the 25-row Setting | Current | Selected
 *     table for power users. In preview mode it filters to changed rows only;
 *     in steady mode it shows the full active policy.
 *
 * Three modes:
 *   STEADY     - dropdown shows active policy, settings unchanged. No footer action.
 *   DRIFTED    - dropdown shows active policy, settings have been tweaked. Footer
 *                offers "Reset to {name}".
 *   PREVIEWING - dropdown shows a different policy. Footer offers "Use {name}" /
 *                "Never mind". Outcome heading flips to "Switching to {name} will:".
 *
 * Microcopy is per the UX writer:
 *   - "Now using" / "Trying out" (not "Active" / "Previewing")
 *   - "Use this setup" (not "Apply policy")
 *   - "Reset to {name}" (not "Revert to {name}")
 *   - "Never mind" (not "Cancel")
 *   - "Show technical settings" (not "Show all differences")
 *
 * Don't soften further - the wording carries the mental model.
 */

import { policiesApi, settingsApi }                from '../../api.js';
import { restoreEncoderOptions, getEncoderOptions } from '../encoder-form.js';
import { showConfirmModal }                         from '../../utils/modal-controller.js';
import { escapeHtml }                               from '../../utils/dom.js';


function toast(message, type) {
    if (typeof window !== 'undefined' && typeof window.showToast === 'function') {
        window.showToast(message, type);
    }
}

/** Sentinel option value for the "+ Save current as..." dropdown entry. */
const SAVE_AS_SENTINEL = '__save-as__';


// ---------------------------------------------------------------------------
// Module state
// ---------------------------------------------------------------------------

const state = {
    policies:        /** @type {Array<object>} */ ([]),
    active:          /** @type {{id: string|null, name: string, modified: boolean}} */ ({ id: null, name: 'Custom', modified: false }),
    currentSettings: /** @type {object} */ ({}),
    selectedId:      /** @type {string|null} */ (null),
    advancedOpen:    false,
};


function findPolicy(id) {
    return id ? state.policies.find(p => (p.id || p.Id) === id) || null : null;
}

function pick(obj, ...keys) {
    if (!obj) return undefined;
    for (const k of keys) {
        if (obj[k] !== undefined) return obj[k];
        const lower = k.charAt(0).toLowerCase() + k.slice(1);
        if (obj[lower] !== undefined) return obj[lower];
    }
    return undefined;
}


// ---------------------------------------------------------------------------
// Field catalogue for the advanced (per-field) diff table.
// ---------------------------------------------------------------------------

function fmtBool(v)   { return v ? 'On' : 'Off'; }
function fmtList(v)   { return Array.isArray(v) && v.length > 0 ? v.join(', ') : '(all kept)'; }
function fmtKbps(v)   { return v != null ? `${v} kbps` : '-'; }
function fmtPct(v)    { return v != null ? `${v}% above target` : '-'; }
function fmtMult(v)   { return v != null ? `${v}x` : '-'; }
function fmtHw(v) {
    switch ((v || '').toLowerCase()) {
        case 'auto':   return 'Auto-detect';
        case 'intel':  return 'Intel Quick Sync';
        case 'amd':    return 'AMD AMF';
        case 'nvidia': return 'NVIDIA NVENC';
        case 'apple':  return 'Apple VideoToolbox';
        case 'none':   return 'Software (CPU only)';
        default:       return v || '-';
    }
}
function fmtOutputs(outs) {
    if (!Array.isArray(outs) || outs.length === 0) return '(none)';
    return outs.map(o => {
        const codec   = (o.Codec   || o.codec   || '?').toUpperCase();
        const layout  = o.Layout   || o.layout  || 'Source';
        const bitrate = o.BitrateKbps ?? o.bitrateKbps;
        const bits    = bitrate ? ` ${bitrate}k` : '';
        return `${codec} ${layout}${bits}`;
    }).join(', ');
}

// Labels in the advanced diff table must MATCH the user-facing labels on the
// corresponding settings tabs verbatim. Otherwise a user sees a diff row change,
// goes hunting on the Audio/Video/General tab for that setting, and can't find
// it. Treat this list as a strict mirror of the cshtml labels - if a tab's
// label is renamed there, rename it here.
//
// H264Profile and H264Level are policy-only (no UI control on any tab); they
// use technical names since the power-user audience for the advanced disclosure
// recognises them, and they're auto-hidden when both sides are empty.
const FIELDS = [
    // General tab
    { label: 'Output Format',                                  key: 'Format',                       fmt: v => (v || '-').toString().toUpperCase() },
    { label: 'Video Codec',                                    key: 'Codec',                        fmt: v => (v || '-').toString().toUpperCase() },
    { label: 'Hardware Acceleration',                          key: 'HardwareAcceleration',         fmt: fmtHw },
    { label: 'Target Bitrate (kbps)',                          key: 'TargetBitrate',                fmt: fmtKbps },
    { label: 'Strict Bitrate Control',                         key: 'StrictBitrate',                fmt: fmtBool },
    { label: '4K Bitrate Multiplier',                          key: 'FourKBitrateMultiplier',       fmt: fmtMult },
    { label: 'Skip 4K Videos',                                 key: 'Skip4K',                       fmt: fmtBool },
    { label: 'Skip Threshold',                                 key: 'SkipPercentAboveTarget',       fmt: fmtPct },
    { label: 'Replace Original Files',                         key: 'DeleteOriginalFile',           fmt: fmtBool },
    { label: 'Retry on Failure',                               key: 'RetryOnFail',                  fmt: fmtBool },
    // Output Directory and Local Transcode Scratch Directory are intentionally NOT
    // shown here. They're machine-local paths that policies never carry - see
    // EncoderOptions.ClearMachineLocalFields. Showing them would imply they switch
    // with the policy when they don't (and shouldn't).

    // Video tab
    { label: 'Downscale Policy',                               key: 'DownscalePolicy' },
    { label: 'Downscale Target',                               key: 'DownscaleTarget' },
    { label: 'Tonemap HDR → SDR',                              key: 'TonemapHdrToSdr',              fmt: fmtBool },
    { label: 'FFmpeg Preset',                                  key: 'FfmpegQualityPreset' },
    { label: 'Remove Black Borders',                           key: 'RemoveBlackBorders',           fmt: fmtBool },

    // Mux tab
    { label: 'Encoding Mode',                                  key: 'EncodingMode' },
    { label: 'Mux Touches',                                    key: 'MuxStreams' },

    // Audio tab
    { label: 'Audio Languages to Keep',                        key: 'AudioLanguagesToKeep',         fmt: fmtList },
    { label: 'Auto-keep original performed language',          key: 'KeepOriginalLanguage',         fmt: fmtBool },
    { label: 'Preserve original audio tracks',                 key: 'PreserveOriginalAudio',        fmt: fmtBool },
    { label: 'Output formats to add',                          key: 'AudioOutputs',                 fmt: fmtOutputs },

    // Subtitles tab
    { label: 'Subtitle Languages to Keep',                     key: 'SubtitleLanguagesToKeep',      fmt: fmtList },
    { label: 'Exclude hearing-impaired (SDH/CC) subtitles',    key: 'ExcludeSdhSubtitles',          fmt: fmtBool },
    { label: 'Extract subtitles to sidecar files',             key: 'ExtractSubtitlesToSidecar',    fmt: fmtBool },
    { label: 'Convert image-based subtitles to SRT (OCR)',     key: 'ConvertImageSubtitlesToSrt',   fmt: fmtBool },
    { label: 'Pass through image-based subtitles (MKV only)',  key: 'PassThroughImageSubtitlesMkv', fmt: fmtBool },

    // Policy-only - no UI control on any tab. Auto-hidden when both sides are empty.
    { label: 'H.264 Profile',                                  key: 'H264Profile',                  fmt: v => v || '(libx264 default)' },
    { label: 'H.264 Level',                                    key: 'H264Level',                    fmt: v => v || '(auto)' },
];


function equalish(a, b) {
    if (a === b) return true;
    if (a == null && b == null) return true;
    if (Array.isArray(a) && Array.isArray(b)) {
        if (a.length !== b.length) return false;
        return a.every((x, i) => {
            const y = b[i];
            if (x && typeof x === 'object') return JSON.stringify(x) === JSON.stringify(y);
            return x === y;
        });
    }
    return false;
}


// ---------------------------------------------------------------------------
// Modes
// ---------------------------------------------------------------------------

function currentMode() {
    if (!state.selectedId)                     return 'steady';
    if (state.selectedId === state.active.id) {
        return state.active.modified ? 'drifted' : 'steady';
    }
    return 'previewing';
}

/** Returns the list of field deltas between current settings and the selected policy. */
function computeDeltas(selectedPolicy) {
    if (!selectedPolicy) return [];
    const opts = selectedPolicy.options || selectedPolicy.Options;
    const out  = [];
    for (const field of FIELDS) {
        const l = pick(state.currentSettings, field.key);
        const r = pick(opts, field.key);
        if (!equalish(l, r)) out.push(field);
    }
    return out;
}


// ---------------------------------------------------------------------------
// Render: dropdown + status line + bullets + drifted banner + advanced + footer
// ---------------------------------------------------------------------------

function render() {
    renderDropdown();
    renderStatusLine();
    renderOutcomeBullets();
    renderDriftedBanner();
    renderAdvancedSection();
    renderFooter();
    renderManageMenu();
}


function renderDropdown() {
    const sel = document.getElementById('policiesSelect');
    if (!sel) return;

    sel.innerHTML = '';

    const builtIns = state.policies.filter(p =>  (p.builtIn || p.BuiltIn));
    const customs  = state.policies.filter(p => !(p.builtIn || p.BuiltIn));

    const append = (policy) => {
        const opt = document.createElement('option');
        const id  = policy.id || policy.Id;
        opt.value = id;
        const isActive = id === state.active.id;
        const suffix = (isActive && state.active.modified) ? ' (customized)' : '';
        opt.textContent = (policy.name || policy.Name) + suffix;
        sel.appendChild(opt);
    };

    builtIns.forEach(append);

    if (customs.length > 0) {
        const sep = document.createElement('option');
        sep.disabled    = true;
        sep.textContent = '── Your setups ──';
        sel.appendChild(sep);
        customs.forEach(append);
    }

    // Save-current-as sentinel always last, after a separator.
    const sep2 = document.createElement('option');
    sep2.disabled    = true;
    sep2.textContent = '────────────';
    sel.appendChild(sep2);

    const saveAs = document.createElement('option');
    saveAs.value         = SAVE_AS_SENTINEL;
    saveAs.textContent   = '+ Save these settings as my own setup…';
    saveAs.dataset.action = 'save-as';
    sel.appendChild(saveAs);

    if (!state.selectedId || (state.selectedId !== SAVE_AS_SENTINEL && !findPolicy(state.selectedId))) {
        state.selectedId = state.active.id;
    }
    sel.value = state.selectedId || '';
}


function renderStatusLine() {
    const badge   = document.getElementById('policiesStatusBadge');
    const tagline = document.getElementById('policiesTagline');
    const recBadge = document.getElementById('policiesRecommendedBadge');
    if (!badge || !tagline) return;

    const sel  = findPolicy(state.selectedId);
    const mode = currentMode();

    // Badge: "Now using" (steady or drifted) vs "Trying out" (previewing).
    if (mode === 'previewing') {
        badge.innerHTML = '<i class="fas fa-eye text-info me-1"></i><strong>Trying out</strong>';
    } else {
        badge.innerHTML = '<i class="fas fa-circle-check text-success me-1"></i><strong>Now using</strong>';
    }

    // Tagline: from the selected policy's description. Hide the line entirely
    // when empty (along with its preceding dot separator) - "No description for
    // this setup" reads accusatorily; an empty line reads clean.
    const desc = sel ? (sel.description || sel.Description || '').trim() : '';
    tagline.textContent = desc;
    // Toggle the dot separator that sits between the badge and the tagline.
    const dotSeparator = tagline.previousElementSibling;
    if (dotSeparator && dotSeparator.classList.contains('text-muted')) {
        dotSeparator.style.display = desc ? '' : 'none';
    }
    tagline.style.display = desc ? '' : 'none';

    // Recommended badge shows only when the selected policy is marked recommended.
    if (recBadge) {
        const isRec = sel && (sel.recommended || sel.Recommended);
        recBadge.style.display = isRec ? '' : 'none';
    }
}


function renderOutcomeBullets() {
    const section = document.getElementById('policiesOutcomeSection');
    const heading = document.getElementById('policiesOutcomeHeading');
    const list    = document.getElementById('policiesOutcomeList');
    const tpl     = document.getElementById('policiesBulletTemplate');
    if (!section || !list || !tpl) return;

    const sel    = findPolicy(state.selectedId);
    const mode   = currentMode();
    const bullets = sel ? (sel.outcomeBullets || sel.OutcomeBullets || []) : [];

    // Heading flips by mode.
    if (mode === 'previewing' && sel) {
        const name = sel.name || sel.Name || 'this setup';
        heading.textContent = `Switching to ${name} will:`;
    } else {
        heading.textContent = 'What this does to your library:';
    }

    list.replaceChildren();

    if (bullets.length === 0) {
        // Custom user policies often have no bullets. Hide the section rather than
        // showing an empty list - the tech-settings disclosure still gives them
        // everything they need.
        section.style.display = 'none';
        return;
    }

    section.style.display = '';
    for (const text of bullets) {
        const node = tpl.content.firstElementChild.cloneNode(true);
        node.querySelector('[data-field="text"]').textContent = text;
        list.appendChild(node);
    }
}


function renderDriftedBanner() {
    const banner = document.getElementById('policiesDriftedBanner');
    const text   = document.getElementById('policiesDriftedBannerText');
    if (!banner || !text) return;

    if (currentMode() === 'drifted') {
        banner.style.display = '';
        const name = state.active.name || 'this setup';
        text.textContent = `You started from ${name} but customized it. Your changes are already saved.`;
    } else {
        banner.style.display = 'none';
    }
}


function renderAdvancedSection() {
    const section = document.getElementById('policiesAdvancedSection');
    const toggle  = document.getElementById('policiesAdvancedToggle');
    const chev    = document.getElementById('policiesAdvancedChevron');
    const label   = document.getElementById('policiesAdvancedToggleLabel');
    if (!section || !toggle) return;

    const mode    = currentMode();
    const sel     = findPolicy(state.selectedId);
    const deltas  = mode === 'previewing' ? computeDeltas(sel) : [];

    // Label encodes mode-specific count when relevant.
    if (mode === 'previewing' && deltas.length > 0) {
        label.textContent = `Show technical settings (${deltas.length} ${deltas.length === 1 ? 'change' : 'changes'})`;
    } else {
        label.textContent = 'Show technical settings';
    }

    // Toggle expand/collapse. The chevron rotation is driven by the
    // [aria-expanded="true"] selector in the scoped CSS - no icon-class swap here.
    section.style.display = state.advancedOpen ? '' : 'none';
    toggle.setAttribute('aria-expanded', state.advancedOpen ? 'true' : 'false');

    if (state.advancedOpen) renderTable();
}


function renderTable() {
    const colSel    = document.getElementById('policiesColSelected');
    const colCurH   = document.getElementById('policiesColCurrent');
    const tbody     = document.getElementById('policiesTableBody');
    if (!tbody) return;

    const mode = currentMode();
    const showSelectedCol = (mode === 'previewing' || mode === 'drifted');
    const sel             = findPolicy(state.selectedId);

    if (colSel)  colSel.style.display = showSelectedCol ? '' : 'none';
    if (colCurH) colCurH.textContent  = showSelectedCol ? 'Current' : 'Value';
    if (colSel && showSelectedCol) {
        colSel.textContent = sel ? (sel.name || sel.Name) : 'Selected';
    }

    const tplSingle = document.getElementById('policiesRowSingleTemplate');
    const tplDiff   = document.getElementById('policiesRowDiffTemplate');
    tbody.replaceChildren();

    const left  = state.currentSettings;
    const right = sel ? (sel.options || sel.Options) : null;

    // In preview mode, show only rows that differ - it's a diff, not a full audit.
    // In steady mode there's only one column anyway, so we show all rows.
    const showOnlyChanged = (mode === 'previewing');

    for (const field of FIELDS) {
        const leftV  = pick(left,  field.key);
        const rightV = pick(right, field.key);
        const changed = !equalish(leftV, rightV);

        // Always skip empty H.264 profile/level pairs - they're noise.
        if ((field.key === 'H264Profile' || field.key === 'H264Level')
            && (leftV == null || leftV === '') && (rightV == null || rightV === '')) continue;

        if (!showSelectedCol) {
            tbody.appendChild(makeRow(tplSingle, field, rightV ?? leftV, null, false));
            continue;
        }

        if (showOnlyChanged && !changed) continue;
        tbody.appendChild(makeRow(tplDiff, field, leftV, rightV, changed));
    }

    // Show an empty-table placeholder if filtering left nothing.
    if (showSelectedCol && showOnlyChanged && tbody.children.length === 0) {
        const tr = document.createElement('tr');
        tr.innerHTML = '<td colspan="3" class="text-center text-muted small py-3">No technical differences from your current settings.</td>';
        tbody.appendChild(tr);
    }
}


function makeRow(template, field, leftValue, rightValue, changed) {
    const node = template.content.firstElementChild.cloneNode(true);
    const fmt  = field.fmt || (v => v == null ? '-' : String(v));

    node.querySelector('[data-field="label"]').textContent   = field.label;
    node.querySelector('[data-field="current"]').textContent = fmt(leftValue);

    const selCell = node.querySelector('[data-field="selected"]');
    if (selCell) {
        node.querySelector('[data-field="selected-value"]').textContent = fmt(rightValue);
        // Changed-row signal is carried by the scoped CSS class:
        // left-edge accent + bold weight on the Selected cell. No icons.
        if (changed) node.classList.add('policy-row-changed');
    }
    return node;
}


function renderFooter() {
    const hint    = document.getElementById('policiesFooterHint');
    const apply   = document.getElementById('policiesApplyBtn');
    const cancel  = document.getElementById('policiesCancelBtn');
    if (!apply || !cancel) return;

    const mode = currentMode();
    const sel  = findPolicy(state.selectedId);

    if (mode === 'steady') {
        hint.textContent = 'Edits to other tabs are saved as you go.';
        apply.disabled   = true;
        cancel.disabled  = true;
        apply.innerHTML  = '<i class="fas fa-check me-1"></i> Use this setup';
    } else if (mode === 'drifted') {
        hint.textContent = 'You can reset to the saved setup, or keep your changes.';
        apply.disabled   = false;
        cancel.disabled  = true;
        const name       = state.active.name || 'this setup';
        apply.innerHTML  = `<i class="fas fa-rotate-left me-1"></i> Reset to ${escapeHtml(name)}`;
    } else { // previewing
        const deltas = computeDeltas(sel);
        hint.textContent = 'Picking this will replace your current settings.';
        apply.disabled   = deltas.length === 0;
        cancel.disabled  = false;
        const name       = sel ? (sel.name || sel.Name) : 'this setup';
        apply.innerHTML  = `<i class="fas fa-check me-1"></i> Use ${escapeHtml(name)}`;
    }
}


function renderManageMenu() {
    const sel       = findPolicy(state.selectedId);
    const isBuiltIn = !!(sel && (sel.builtIn || sel.BuiltIn));

    setItemDisabled('rename',    !sel || isBuiltIn);
    setItemDisabled('delete',    !sel || isBuiltIn);
    setItemDisabled('duplicate', !sel);
    setItemDisabled('export',    !sel);
    setItemDisabled('import',    false);
}

function setItemDisabled(action, disabled) {
    const btn = document.querySelector(`[data-policies-action="${action}"]`);
    if (!btn) return;
    if (disabled) btn.setAttribute('disabled', '');
    else          btn.removeAttribute('disabled');
}


// ---------------------------------------------------------------------------
// Action handlers
// ---------------------------------------------------------------------------

async function handleSelectChange(value) {
    if (value === SAVE_AS_SENTINEL) {
        state.selectedId = state.active.id;
        render();
        await handleSaveCurrentAs();
        return;
    }
    state.selectedId = value;
    render();
}


function handleAdvancedToggle() {
    state.advancedOpen = !state.advancedOpen;
    renderAdvancedSection();
}


async function handleApply() {
    const mode = currentMode();

    if (mode === 'drifted') {
        if (!state.active.id) return;
        await applyPolicyById(state.active.id, /* reset */ true);
        return;
    }

    if (mode === 'previewing') {
        const sel = findPolicy(state.selectedId);
        if (!sel) return;
        await applyPolicyById(sel.id || sel.Id, /* reset */ false);
        return;
    }
}

async function applyPolicyById(id, reset) {
    try {
        await policiesApi.apply(id);
        await restoreEncoderOptions('settings');
        state.selectedId = id;
        await loadPoliciesPanel();
        const sel  = findPolicy(id);
        const name = sel ? (sel.name || sel.Name) : 'setup';
        toast(reset ? `Reset to "${name}".` : `Now using "${name}".`, 'success');
    } catch (e) {
        toast(`Failed: ${e.message}`, 'danger');
    }
}


function handleCancel() {
    if (currentMode() !== 'previewing') return;
    state.selectedId = state.active.id;
    render();
}


async function handleSaveCurrentAs() {
    const details = await promptPolicyDetails({
        title:      'Save these settings as your own setup',
        okLabel:    'Save setup',
        initial:    { name: '', description: '', bullets: [] },
        helpName:   'A short, scannable name. Other Snacks users will see this in their picker.',
    });
    if (!details) return;

    try {
        const options = getEncoderOptions('settings');
        const created = await policiesApi.create({
            name:           details.name,
            description:    details.description,
            outcomeBullets: details.bullets,
            options,
        });
        state.selectedId = created.id || created.Id;
        await loadPoliciesPanel();
        toast(`Saved "${details.name}".`, 'success');
    } catch (e) {
        toast(`Failed to save: ${e.message}`, 'danger');
    }
}


async function handleDuplicate() {
    const sel = findPolicy(state.selectedId);
    if (!sel) return;
    try {
        const dup = await policiesApi.duplicate(sel.id || sel.Id);
        state.selectedId = dup.id || dup.Id;
        await loadPoliciesPanel();
        toast(`Duplicated as "${dup.name || dup.Name}".`, 'success');
    } catch (e) {
        toast(`Failed to duplicate: ${e.message}`, 'danger');
    }
}


async function handleEditDetails() {
    const sel = findPolicy(state.selectedId);
    if (!sel) return;
    if (sel.builtIn || sel.BuiltIn) return;

    const details = await promptPolicyDetails({
        title:    'Edit setup details',
        okLabel:  'Save changes',
        initial: {
            name:        sel.name        || sel.Name        || '',
            description: sel.description || sel.Description || '',
            bullets:     sel.outcomeBullets || sel.OutcomeBullets || [],
        },
        helpName: 'The name shown in the picker.',
    });
    if (!details) return;

    try {
        await policiesApi.update(sel.id || sel.Id, {
            name:           details.name,
            description:    details.description,
            outcomeBullets: details.bullets,
            options:        sel.options || sel.Options,
        });
        await loadPoliciesPanel();
        toast('Details updated.', 'success');
    } catch (e) {
        toast(`Failed to update: ${e.message}`, 'danger');
    }
}


async function handleDelete() {
    const sel = findPolicy(state.selectedId);
    if (!sel) return;
    if (sel.builtIn || sel.BuiltIn) return;

    const name = sel.name || sel.Name;
    const confirmed = await showConfirmModal(
        'Delete this setup?',
        `Delete <strong>${escapeHtml(name)}</strong>? This can't be undone.`,
        'Delete',
    );
    if (!confirmed) return;

    try {
        await policiesApi.remove(sel.id || sel.Id);
        state.selectedId = state.active.id;
        await loadPoliciesPanel();
        toast('Setup deleted.', 'success');
    } catch (e) {
        toast(`Failed to delete: ${e.message}`, 'danger');
    }
}


function handleExport() {
    const sel = findPolicy(state.selectedId);
    if (!sel) return;
    policiesApi.exportOne(sel.id || sel.Id);
}


function handleImportClick() {
    document.getElementById('policiesImportFile')?.click();
}

async function handleImportFile(file) {
    if (!file) return;
    try {
        const text      = await file.text();
        const parsedDoc = JSON.parse(text);
        const result    = await policiesApi.importDocument(parsedDoc);
        await loadPoliciesPanel();
        const count = result?.count ?? 0;
        toast(`Imported ${count} ${count === 1 ? 'setup' : 'setups'}.`, 'success');
    } catch (e) {
        toast(`Import failed: ${e.message}`, 'danger');
    }
}


/**
 * Multi-field editor for a custom policy's details. Returns
 * `{ name, description, bullets[] }` on save, or null on cancel.
 * Used by both "Save current as new setup" and "Edit details" — the same
 * shape on both flows keeps the community-shareable surface consistent.
 *
 *   opts.title    - modal title
 *   opts.okLabel  - primary button text ("Save setup" / "Save changes")
 *   opts.initial  - { name, description, bullets } pre-population (bullets = string[])
 *   opts.helpName - one-line hint under the name field
 */
function promptPolicyDetails(opts) {
    return new Promise((resolve) => {
        const bulletText = Array.isArray(opts.initial.bullets) ? opts.initial.bullets.join('\n') : '';

        const host = document.createElement('div');
        host.className = 'snacks-modal-backdrop open policy-prompt-host';
        host.innerHTML = `
            <div class="snacks-modal-wrapper">
                <div class="snacks-modal-dialog" style="max-width: 36rem;">
                    <div class="modal-header">
                        <h5 class="modal-title">${escapeHtml(opts.title)}</h5>
                        <button type="button" class="btn-close" data-action="cancel" aria-label="Close"></button>
                    </div>
                    <div class="modal-body">
                        <div class="mb-3">
                            <label class="form-label small">Name</label>
                            <input class="form-control" type="text" data-field="name"
                                   placeholder="e.g. My living-room setup"
                                   value="${escapeHtml(opts.initial.name || '')}">
                            <div class="form-text">${escapeHtml(opts.helpName || '')}</div>
                        </div>
                        <div class="mb-3">
                            <label class="form-label small">One-line description <span class="text-muted">(optional)</span></label>
                            <input class="form-control" type="text" data-field="description"
                                   placeholder="Who is this setup for? What does it trade off?"
                                   value="${escapeHtml(opts.initial.description || '')}">
                            <div class="form-text">Shown under the picker. The first thing other people read when they open your shared setup.</div>
                        </div>
                        <div class="mb-2">
                            <label class="form-label small">What this does to your library <span class="text-muted">(optional)</span></label>
                            <textarea class="form-control" data-field="bullets" rows="5"
                                      placeholder="Files end up roughly half the size&#10;Plays on phones, TVs, browsers&#10;Keeps the original soundtrack">${escapeHtml(bulletText)}</textarea>
                            <div class="form-text">One outcome per line. Each line becomes a bullet on the setup card. Plain English; assume the reader has never encoded a video.</div>
                        </div>
                    </div>
                    <div class="modal-footer">
                        <button class="btn btn-secondary" data-action="cancel">Cancel</button>
                        <button class="btn btn-primary"   data-action="ok">${escapeHtml(opts.okLabel || 'Save')}</button>
                    </div>
                </div>
            </div>`;
        document.body.appendChild(host);

        const nameInput = host.querySelector('[data-field="name"]');
        const descInput = host.querySelector('[data-field="description"]');
        const bulletsEl = host.querySelector('[data-field="bullets"]');
        nameInput.focus();
        nameInput.select();

        const close = (value) => { host.remove(); resolve(value); };

        const submit = () => {
            const name = nameInput.value.trim();
            if (!name) {
                nameInput.classList.add('is-invalid');
                nameInput.focus();
                return;
            }
            const description = descInput.value.trim();
            const bullets = bulletsEl.value
                .split(/\r?\n/)
                .map(s => s.trim())
                .filter(s => s.length > 0);
            close({ name, description, bullets });
        };

        host.querySelectorAll('[data-action="cancel"]').forEach(b => b.addEventListener('click', () => close(null)));
        host.querySelector('[data-action="ok"]').addEventListener('click', submit);

        nameInput.addEventListener('input', () => nameInput.classList.remove('is-invalid'));
        // Enter in the name or description fields submits; Enter in the textarea inserts a line.
        nameInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); submit(); } if (e.key === 'Escape') close(null); });
        descInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); submit(); } if (e.key === 'Escape') close(null); });
        bulletsEl.addEventListener('keydown', (e) => { if (e.key === 'Escape') close(null); });
    });
}


/** Legacy single-input prompt kept available for any caller that still wants it. */
function promptText(opts) {
    return new Promise((resolve) => {
        const host = document.createElement('div');
        host.className = 'snacks-modal-backdrop open policy-prompt-host';
        host.innerHTML = `
            <div class="snacks-modal-wrapper">
                <div class="snacks-modal-dialog" style="max-width: 28rem;">
                    <div class="modal-header">
                        <h5 class="modal-title">${escapeHtml(opts.title)}</h5>
                        <button type="button" class="btn-close" data-action="cancel" aria-label="Close"></button>
                    </div>
                    <div class="modal-body">
                        <label class="form-label small">${escapeHtml(opts.label)}</label>
                        <input class="form-control" type="text"
                               placeholder="${escapeHtml(opts.placeholder ?? '')}"
                               value="${escapeHtml(opts.initial ?? '')}">
                    </div>
                    <div class="modal-footer">
                        <button class="btn btn-secondary" data-action="cancel">Cancel</button>
                        <button class="btn btn-primary"   data-action="ok">OK</button>
                    </div>
                </div>
            </div>`;
        document.body.appendChild(host);

        const input = host.querySelector('input');
        input.focus();
        input.select();
        const close = (value) => { host.remove(); resolve(value); };

        host.querySelectorAll('[data-action="cancel"]').forEach(b => b.addEventListener('click', () => close(null)));
        host.querySelector('[data-action="ok"]').addEventListener('click', () => close(input.value));
        input.addEventListener('keydown', (e) => {
            if (e.key === 'Enter')  { e.preventDefault(); close(input.value); }
            if (e.key === 'Escape') { e.preventDefault(); close(null); }
        });
    });
}


// ---------------------------------------------------------------------------
// Public init / load
// ---------------------------------------------------------------------------

export function initPoliciesPanel() {
    const root = document.getElementById('policiesPanel');
    if (!root || root.dataset.bound === '1') return;
    root.dataset.bound = '1';

    document.getElementById('policiesSelect')        ?.addEventListener('change', (e) => handleSelectChange(e.target.value));
    document.getElementById('policiesApplyBtn')      ?.addEventListener('click', handleApply);
    document.getElementById('policiesCancelBtn')     ?.addEventListener('click', handleCancel);
    document.getElementById('policiesAdvancedToggle')?.addEventListener('click', handleAdvancedToggle);

    const dispatch = {
        // 'rename' is the legacy action id retained in the cshtml. It now opens the
        // full details editor (name + description + bullets), not just a name prompt.
        rename:    handleEditDetails,
        duplicate: handleDuplicate,
        delete:    handleDelete,
        export:    handleExport,
        import:    handleImportClick,
        'save-as': handleSaveCurrentAs,
    };
    document.querySelectorAll('[data-policies-action]').forEach(btn => {
        const fn = dispatch[btn.getAttribute('data-policies-action')];
        if (fn) btn.addEventListener('click', fn);
    });

    document.getElementById('policiesImportFile')?.addEventListener('change', async (e) => {
        const file = e.target.files?.[0];
        e.target.value = '';
        await handleImportFile(file);
    });
}


export async function loadPoliciesPanel() {
    try {
        const [policies, active, current] = await Promise.all([
            policiesApi.list(),
            policiesApi.active(),
            settingsApi.get(),
        ]);
        state.policies        = Array.isArray(policies) ? policies : [];
        state.active          = active || { id: null, name: 'Custom', modified: false };
        state.currentSettings = current || {};

        if (!state.selectedId || !findPolicy(state.selectedId)) {
            state.selectedId = state.active.id;
        }

        render();
    } catch (e) {
        toast(`Failed to load setups: ${e.message}`, 'danger');
    }
}
