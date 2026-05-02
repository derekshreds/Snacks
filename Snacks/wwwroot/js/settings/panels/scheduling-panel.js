/**
 * Per-node transcode-scheduling settings panel.
 *
 * Renders one collapsible section per cluster node (master first, then
 * workers), each containing a list of "allowed-to-transcode" windows. Each
 * window is a row of 7 day-of-week toggle chips plus a start/end time pair,
 * modeled on the audio-fan-out row pattern in `_AudioSettings.cshtml` /
 * `encoder-form.js`.
 *
 * Save flow: every mutation (chip click, time edit, row add/remove) marks
 * that node dirty and schedules a debounced POST of the node's full
 * NodeSettings payload. We keep a cache of every node's last-loaded
 * settings so we don't clobber co-existing fields (Only4K / Exclude4K /
 * EncodingOverrides / DeviceSettings) when persisting a schedule edit.
 *
 * Master clock: fetched once on panel open and ticked client-side from the
 * browser's monotonic clock. Master's reported time is parsed as if it were
 * UTC and formatted as UTC, so we display the master's wall clock no matter
 * what timezone the browser is in.
 */

import { clusterApi } from '../../api.js';


// ---------------------------------------------------------------------------
// Module state
// ---------------------------------------------------------------------------

/**
 * Per-node cache of the full NodeSettings object as last seen from the
 * server. Keyed by NodeId. Used both to render the initial UI and to merge
 * schedule edits back into the full payload on save.
 *
 * @type {Object<string, object>}
 */
const settingsCache = {};

/** Per-node debounce handles for save-on-edit. @type {Object<string, number>} */
const saveTimers = {};

/** Master-clock state captured at last fetch. */
let masterClock = null;   // { masterWallEpochMs, fetchedAtMs, displayName, isUtc }

/** Interval id for the master clock ticker. */
let clockTickId = null;

/** Per-instance form prefix (matches the cshtml's @ViewData["FormPrefix"]). */
const PREFIX = 'settings';


// ---------------------------------------------------------------------------
// DOM helpers
// ---------------------------------------------------------------------------

/** @returns {HTMLElement|null} */
const byId = (id) => document.getElementById(id);

/** @returns {HTMLElement|null} */
const nodeListEl = () => byId(`${PREFIX}SchedulingNodeList`);


// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/**
 * One-time DOM wiring. Hooks the settings modal "open" event so the panel
 * refreshes whenever the user opens settings (catches new nodes that joined
 * since the last open).
 */
export function initSchedulingPanel() {
    const modal = document.getElementById('settingsModal');
    if (!modal) return;

    // Stop the master-clock ticker when the modal closes — there's no point
    // updating an off-screen DOM, and leaving an interval running across
    // session lifetimes leaks work for no benefit.
    new MutationObserver(() => {
        const open = modal.classList.contains('open');
        if (!open && clockTickId != null) {
            clearInterval(clockTickId);
            clockTickId = null;
        }
    }).observe(modal, { attributes: true, attributeFilter: ['class'] });
}

/**
 * Fetches the cluster status, per-node settings, and master time, then
 * renders the panel. Safe to call repeatedly (subsequent calls re-fetch and
 * re-render, dropping any uncommitted edits — but edits are auto-saved on
 * change, so there shouldn't be any).
 */
export async function loadSchedulingPanel() {
    const list = nodeListEl();
    if (!list) return;

    try {
        const [status, settings, masterTime] = await Promise.all([
            clusterApi.getStatus().catch(() => null),
            clusterApi.getNodeSettings().catch(() => ({ nodes: {} })),
            clusterApi.getMasterTime().catch(() => null),
        ]);

        applyMasterTime(masterTime);

        // Cache every per-node settings entry so save-merging has the full
        // object to work from. Server returns either { nodes: {...} } or
        // an empty object on first run.
        for (const k of Object.keys(settingsCache)) delete settingsCache[k];
        const nodesCfg = settings?.nodes || {};
        for (const [nodeId, ns] of Object.entries(nodesCfg)) settingsCache[nodeId] = ns;

        renderNodes(status, settingsCache);
    } catch (err) {
        console.error('Scheduling panel: failed to load', err);
    }
}


// ---------------------------------------------------------------------------
// Master clock
// ---------------------------------------------------------------------------

/**
 * Stores the most recent master-time response and (re)starts the visible
 * clock ticker. The trick: we parse master's local time as if it were UTC,
 * then format it as UTC, so the displayed string reflects the master's
 * wall clock regardless of the browser's timezone.
 */
function applyMasterTime(t) {
    if (!t) {
        const clockEl = byId(`${PREFIX}SchedulingMasterClock`);
        const tzEl    = byId(`${PREFIX}SchedulingMasterTz`);
        if (clockEl) clockEl.textContent = 'Master time unavailable';
        if (tzEl)    tzEl.textContent = '';
        return;
    }

    // Date.parse(...+'Z') treats the master's local-clock string as a UTC
    // instant. We never use this as a real UTC time — only as a portable
    // "wall-clock epoch" we can advance and format back as UTC.
    masterClock = {
        masterWallEpochMs: Date.parse(t.localTimeIso + 'Z'),
        fetchedAtMs:       Date.now(),
        displayName:       t.displayName || t.timeZoneId || 'unknown timezone',
        isUtc:             !!t.isUtc,
    };

    const tzEl     = byId(`${PREFIX}SchedulingMasterTz`);
    const warnEl   = byId(`${PREFIX}SchedulingMasterUtcWarning`);
    if (tzEl)   tzEl.textContent = masterClock.displayName;
    if (warnEl) warnEl.classList.toggle('d-none', !masterClock.isUtc);

    if (clockTickId != null) clearInterval(clockTickId);
    tickClock();
    clockTickId = setInterval(tickClock, 1000);
}

function tickClock() {
    if (!masterClock) return;
    const clockEl = byId(`${PREFIX}SchedulingMasterClock`);
    if (!clockEl) return;

    const delta  = Date.now() - masterClock.fetchedAtMs;
    const wall   = new Date(masterClock.masterWallEpochMs + delta);
    // Format as UTC so we read back the master's wall-clock components.
    clockEl.textContent = wall.toLocaleString(undefined, {
        timeZone: 'UTC',
        weekday:  'short',
        month:    'short',
        day:      'numeric',
        hour:     'numeric',
        minute:   '2-digit',
        second:   '2-digit',
        hour12:   true,
    });
}


// ---------------------------------------------------------------------------
// Rendering
// ---------------------------------------------------------------------------

/**
 * Builds a render-friendly list of every node we know about (the master/self
 * plus connected workers). The status payload may include the master itself
 * inside `nodes`, so we de-dupe by NodeId.
 */
function collectNodes(status) {
    const out = [];
    const seen = new Set();

    if (status?.nodeId) {
        out.push({
            nodeId:   status.nodeId,
            hostname: status.nodeName || 'This Machine',
            role:     'master',
            isSelf:   true,
        });
        seen.add(status.nodeId);
    }

    for (const n of (status?.nodes || [])) {
        if (!n?.nodeId || seen.has(n.nodeId)) continue;
        out.push({
            nodeId:   n.nodeId,
            hostname: n.hostname || n.nodeId,
            role:     n.role || 'node',
            isSelf:   false,
        });
        seen.add(n.nodeId);
    }

    // Master/self first, then workers alphabetically.
    out.sort((a, b) => {
        if (a.isSelf !== b.isSelf) return a.isSelf ? -1 : 1;
        return a.hostname.localeCompare(b.hostname);
    });

    return out;
}

function renderNodes(status, perNode) {
    const list = nodeListEl();
    if (!list) return;

    const nodes = collectNodes(status);
    list.replaceChildren();

    if (nodes.length === 0) {
        list.innerHTML = '<div class="text-muted text-center py-3"><small>No cluster nodes available</small></div>';
        return;
    }

    const tpl = byId(`${PREFIX}SchedulingNodeSectionTemplate`);
    if (!tpl) return;

    for (const node of nodes) {
        const section = tpl.content.firstElementChild.cloneNode(true);
        section.dataset.nodeId = node.nodeId;
        section.querySelector('[data-node-name]').textContent = node.hostname;
        section.querySelector('[data-node-role]').textContent = node.role;

        const rowsEl  = section.querySelector('[data-scheduling-rows]');
        const addBtn  = section.querySelector('[data-add-window]');
        const summary = section.querySelector('[data-schedule-summary]');

        const ns       = perNode[node.nodeId] || {};
        const schedule = Array.isArray(ns.schedule) ? ns.schedule
                       : Array.isArray(ns.Schedule) ? ns.Schedule
                       : [];

        for (const w of schedule) appendRow(rowsEl, w);

        addBtn.addEventListener('click', () => {
            // Default to Mon–Fri 22:00–06:00 — the canonical "evenings only"
            // pattern most users want when they reach for this feature.
            appendRow(rowsEl, { Days: [1, 2, 3, 4, 5], Start: '22:00', End: '06:00' });
            scheduleSave(node.nodeId, section);
        });

        updateSummary(section);
        list.appendChild(section);
    }
}

function appendRow(rowsEl, win) {
    const tpl = byId(`${PREFIX}SchedulingWindowRowTemplate`);
    if (!tpl) return;

    const row    = tpl.content.firstElementChild.cloneNode(true);
    const days   = normalizeDays(win?.Days ?? win?.days ?? []);
    const start  = win?.Start ?? win?.start ?? '22:00';
    const end    = win?.End   ?? win?.end   ?? '06:00';

    row.querySelectorAll('.day-chip').forEach(chip => {
        const d = parseInt(chip.dataset.day, 10);
        if (days.has(d)) chip.classList.add('active');

        chip.addEventListener('click', () => {
            chip.classList.toggle('active');
            triggerRowChange(row);
        });
    });

    row.querySelector('[data-field="Start"]').value = start;
    row.querySelector('[data-field="End"]').value   = end;

    row.querySelector('[data-remove]').addEventListener('click', () => {
        row.remove();
        triggerRowChange(rowsEl);
    });

    // Native time inputs fire 'change' on commit; that's the granularity we
    // want for the auto-save debounce.
    row.querySelectorAll('input[type="time"]').forEach(inp =>
        inp.addEventListener('change', () => triggerRowChange(row)));

    rowsEl.appendChild(row);
}

/**
 * Walks up to the enclosing node section and triggers a debounced save +
 * summary refresh. The starting element can be any descendant of the section
 * (a row, a chip, the rows container, etc).
 */
function triggerRowChange(start) {
    const section = start?.closest?.('[data-scheduling-node]');
    if (!section) return;
    updateSummary(section);
    scheduleSave(section.dataset.nodeId, section);
}

/**
 * Reads the current row state for a node section into an array of
 * ScheduleWindow objects matching the server's casing (Days/Start/End).
 */
function readWindows(section) {
    const out = [];
    section.querySelectorAll('[data-window-row]').forEach(row => {
        const days = [];
        row.querySelectorAll('.day-chip.active').forEach(chip =>
            days.push(parseInt(chip.dataset.day, 10)));
        out.push({
            Days:  days,
            Start: row.querySelector('[data-field="Start"]')?.value || '00:00',
            End:   row.querySelector('[data-field="End"]')?.value   || '00:00',
        });
    });
    return out;
}


// ---------------------------------------------------------------------------
// Save (debounced) + summary
// ---------------------------------------------------------------------------

const SAVE_DEBOUNCE_MS = 600;

function scheduleSave(nodeId, section) {
    clearTimeout(saveTimers[nodeId]);
    saveTimers[nodeId] = setTimeout(() => saveNode(nodeId, section), SAVE_DEBOUNCE_MS);
}

async function saveNode(nodeId, section) {
    const windows = readWindows(section);

    // Server-side validation rejects empty-day windows; pre-filter so a
    // half-edited row (user added a window but hasn't picked any days yet)
    // doesn't trigger a 400 on every keystroke. Once they click a day chip
    // the next debounce tick re-saves with the row included.
    const filtered = windows.filter(w => w.Days && w.Days.length > 0);

    // Merge into the cached full NodeSettings so we don't clobber other
    // per-node settings (only4K, exclude4K, encoding overrides, devices).
    const cached = settingsCache[nodeId] || {};
    const payload = {
        nodeId,
        displayName:        cached.displayName       ?? cached.DisplayName       ?? null,
        only4K:             cached.only4K            ?? cached.Only4K            ?? null,
        exclude4K:          cached.exclude4K         ?? cached.Exclude4K         ?? null,
        encodingOverrides:  cached.encodingOverrides ?? cached.EncodingOverrides ?? null,
        deviceSettings:     cached.deviceSettings    ?? cached.DeviceSettings    ?? null,
        schedule:           filtered,
    };

    try {
        await clusterApi.saveNodeSettings(payload);
        // Refresh our cache so the next save preserves the new schedule.
        settingsCache[nodeId] = { ...(settingsCache[nodeId] || {}), schedule: filtered };
    } catch (err) {
        console.error(`Scheduling panel: save failed for ${nodeId}`, err);
        if (typeof showToast === 'function') {
            showToast(`Couldn't save schedule: ${err.message}`, 'danger');
        }
    }
}

const DAY_LETTERS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

function updateSummary(section) {
    const summaryEl = section.querySelector('[data-schedule-summary]');
    if (!summaryEl) return;

    const windows = readWindows(section).filter(w => w.Days.length > 0);
    if (windows.length === 0) {
        summaryEl.textContent = 'Always available';
        return;
    }

    const parts = windows.map(w => {
        const sortedDays = [...w.Days].sort((a, b) => a - b).map(d => DAY_LETTERS[d]).join(' ');
        const fullDay = w.Start === w.End;
        return fullDay
            ? `${sortedDays} all day`
            : `${sortedDays} ${w.Start}–${w.End}`;
    });

    let text = parts.join('; ');
    if (text.length > 60) text = text.slice(0, 57) + '…';
    summaryEl.textContent = text;
}


// ---------------------------------------------------------------------------
// Internal utilities
// ---------------------------------------------------------------------------

/**
 * Coerces a Days payload into a Set<number> of day-of-week values (0-6).
 * Tolerates both numeric and named-string inputs (the server emits names
 * like "Monday" when DayOfWeek serializes via JsonStringEnumConverter, but
 * may emit numbers without it).
 */
function normalizeDays(raw) {
    const out = new Set();
    const NAMES = { sunday: 0, monday: 1, tuesday: 2, wednesday: 3, thursday: 4, friday: 5, saturday: 6 };
    for (const v of (raw || [])) {
        if (typeof v === 'number' && v >= 0 && v <= 6) out.add(v);
        else if (typeof v === 'string') {
            const n = NAMES[v.toLowerCase()];
            if (n != null) out.add(n);
            else if (/^\d+$/.test(v) && +v >= 0 && +v <= 6) out.add(+v);
        }
    }
    return out;
}

