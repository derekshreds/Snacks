/**
 * Directory analyze (dry-run) modal.
 *
 * Renders the per-file Queue/Mux/Skip predictions returned by
 * `libraryApi.analyzeDirectory` and lets the user proceed with the matching
 * `processDirectory` call without re-typing anything. Triggered by the
 * "Analyze (Dry Run)" button inside the library modal — the LibraryBrowser
 * owns the trigger and calls into us with the directory + recursion choice.
 */

import { libraryApi }        from '../api.js';
import { escapeHtml }        from '../utils/dom.js';
import { openModal, closeModal } from '../utils/modal-controller.js';
import { getEncoderOptions } from '../settings/encoder-form.js';


// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const MODAL_ID = 'analyzeResultsModal';

/** Decisions that produce a real encode pass — used for "Will Encode" totals. */
const ENCODE_SET = new Set(['Queue', 'Shrink']);

/** DB-status decisions returned when a previous run already touched the file. */
const DONE_SET   = new Set(['AlreadyCompleted', 'AlreadyFailed', 'AlreadyCancelled']);

/**
 * Maps each backend decision to a `[badgeClass, badgeLabel]` pair. The badge
 * classes reuse the queue-item status pills defined in site.css.
 */
const DECISION_BADGE = {
    Queue:            ['status-pending',   'Encode'],
    Shrink:           ['status-pending',   'Shrink'],
    Copy:             ['status-uploading', 'Copy'],
    Mux:              ['status-uploading', 'Mux'],
    Skip:             ['status-cancelled', 'Skip'],
    Excluded:         ['status-cancelled', 'Excluded'],
    AlreadyCompleted: ['status-completed', 'Done'],
    AlreadyFailed:    ['status-failed',    'Failed'],
    AlreadyCancelled: ['status-cancelled', 'Cancelled'],
    Error:            ['status-failed',    'Error'],
};


// ---------------------------------------------------------------------------
// AnalyzeModal
// ---------------------------------------------------------------------------

export class AnalyzeModal {

    constructor() {
        /** @type {{dirPath: string, recursive: boolean, options: object, results: any[], filter: string} | null} */
        this._ctx = null;

        /** AbortController for the in-flight analyze request; null when idle. */
        this._abort = null;
    }


    // ---- Init ----

    /**
     * Wires the modal-internal handlers. Safe to call once at startup. The
     * trigger button itself is owned by `LibraryBrowser` — it calls
     * {@link AnalyzeModal#open} when clicked.
     */
    init() {
        document.getElementById('analyzeProceed')   ?.addEventListener('click', () => this._proceed());
        document.getElementById('analyzeFilterText')?.addEventListener('input', () => this._renderTable());

        // Watching the backdrop's class list catches every close path uniformly:
        // the X button, the footer Close button, Escape, and backdrop click. The
        // user's "close cancels the running task" expectation applies to all of
        // them, so we abort here rather than wiring each dismiss path individually.
        const modalEl = document.getElementById(MODAL_ID);
        if (modalEl) {
            new MutationObserver(() => {
                if (!modalEl.classList.contains('open')) this._cancelInFlight();
            }).observe(modalEl, { attributes: true, attributeFilter: ['class'] });
        }
    }

    /** Aborts any in-flight analyze fetch and clears its controller. No-op when idle. */
    _cancelInFlight() {
        if (this._abort) {
            this._abort.abort();
            this._abort = null;
        }
    }


    // ---- Open / fetch ----

    /**
     * Opens the modal for `dirPath` and runs the analysis. Closes silently if
     * `dirPath` is empty (the trigger button only fires from a valid library
     * context, so that is just a guard against double-clicks).
     *
     * @param {string}  dirPath           Absolute directory path to analyze.
     * @param {boolean} [recursive=true]  Whether to descend into subdirectories.
     */
    async open(dirPath, recursive = true) {
        if (!dirPath) {
            showToast('No directory available', 'warning');
            return;
        }

        // A previous run might still be in flight (user reopened before the first
        // request finished) — abort it so its late-arriving response can't render
        // into the modal we're about to reset.
        this._cancelInFlight();

        const options = getEncoderOptions('settings');
        const dirName = dirPath.split(/[/\\]/).filter(Boolean).pop() || dirPath;

        this._ctx = { dirPath, recursive, options, results: [], filter: 'all' };

        document.getElementById('analyzeTargetLabel').textContent =
            dirName + (recursive ? ' (recursive)' : '');
        document.getElementById('analyzeLoading').style.display = '';
        document.getElementById('analyzeContent').style.display = 'none';
        document.getElementById('analyzeError')  .style.display = 'none';

        // Reset Proceed to its initial state — otherwise it briefly shows the
        // count from the previous directory's results until the new fetch lands.
        const proceedBtn = document.getElementById('analyzeProceed');
        proceedBtn.disabled  = true;
        proceedBtn.innerHTML = '<i class="fas fa-folder-plus me-2"></i>Proceed with Conversion';

        openModal(MODAL_ID);

        const controller = new AbortController();
        this._abort = controller;
        try {
            const data = await libraryApi.analyzeDirectory(dirPath, recursive, options, controller.signal);
            // Late responses from a since-aborted request must not paint over a
            // newer one — `_abort` is reassigned on each call, so identity check.
            if (this._abort !== controller) return;
            this._ctx.results = data.results || [];
            this._renderResults();
        } catch (err) {
            if (err.name === 'AbortError') return; // user closed the modal — silent
            document.getElementById('analyzeLoading').style.display = 'none';
            const errEl = document.getElementById('analyzeError');
            errEl.textContent  = 'Analysis failed: ' + err.message;
            errEl.style.display = '';
        } finally {
            if (this._abort === controller) this._abort = null;
        }
    }


    // ---- Rendering ----

    /**
     * Builds the summary cards, filter tabs, and initial table body from the
     * results currently in `_ctx`.
     */
    _renderResults() {
        const ctx = this._ctx;
        if (!ctx) return;

        document.getElementById('analyzeLoading').style.display = 'none';
        document.getElementById('analyzeContent').style.display = '';

        // Bucket counts and rough byte totals for the summary cards.
        const counts = {
            all: ctx.results.length,
            Queue: 0, Shrink: 0, Copy: 0, Mux: 0, Skip: 0, Excluded: 0,
            AlreadyCompleted: 0, AlreadyFailed: 0, AlreadyCancelled: 0, Error: 0,
        };
        let encodeSize = 0, copySize = 0, muxSize = 0, skipSize = 0;

        for (const r of ctx.results) {
            counts[r.decision] = (counts[r.decision] || 0) + 1;
            if      (ENCODE_SET.has(r.decision)) encodeSize += r.sizeBytes || 0;
            else if (r.decision === 'Copy')      copySize   += r.sizeBytes || 0;
            else if (r.decision === 'Mux')       muxSize    += r.sizeBytes || 0;
            else if (r.decision === 'Skip')      skipSize   += r.sizeBytes || 0;
        }

        const encodeTotal  = counts.Queue + counts.Shrink;
        const doneTotal    = counts.AlreadyCompleted + counts.AlreadyFailed + counts.AlreadyCancelled;
        // "Will Process" = anything the real run would actually act on (encode, copy, or mux pass).
        const willProcess  = encodeTotal + counts.Copy + counts.Mux;
        const skipPlusExcl = counts.Skip + counts.Excluded;

        document.getElementById('analyzeSummary').innerHTML = [
            renderCard('fa-film',    'Total Files',  ctx.results.length, '',                                       '--border-color'),
            renderCard('fa-cog',     'Will Encode',  encodeTotal,        encodeSize > 0 ? formatSize(encodeSize) : '', '--bs-primary'),
            renderCard('fa-random',  'Mux Pass',     counts.Mux,         muxSize    > 0 ? formatSize(muxSize)    : '', '--bs-info'),
            renderCard('fa-copy',    'Copy / Remux', counts.Copy,        copySize   > 0 ? formatSize(copySize)   : '', '--bs-info'),
            renderCard('fa-forward', 'Will Skip',    skipPlusExcl,       skipSize   > 0 ? formatSize(skipSize)   : '', '--bs-warning'),
            renderCard('fa-check',   'Already Done', doneTotal,          '',                                       '--bs-success'),
        ].join('');

        const tabs = [
            { key: 'all',    label: `All (${counts.all})` },
            { key: 'Encode', label: `Encode (${encodeTotal})` },
            { key: 'Mux',    label: `Mux (${counts.Mux})` },
            { key: 'Copy',   label: `Copy (${counts.Copy})` },
            { key: 'Skip',   label: `Skip (${skipPlusExcl})` },
            { key: 'Done',   label: `Already Done (${doneTotal})` },
        ];
        if (counts.Error) tabs.push({ key: 'Error', label: `Error (${counts.Error})` });

        const tabsEl = document.getElementById('analyzeFilterTabs');
        tabsEl.innerHTML = tabs.map(t =>
            `<button type="button" class="btn btn-outline-secondary ${ctx.filter === t.key ? 'active' : ''}" data-filter="${t.key}">${t.label}</button>`,
        ).join('');
        tabsEl.querySelectorAll('button').forEach(btn => {
            btn.addEventListener('click', () => {
                ctx.filter = btn.dataset.filter;
                this._renderResults();
            });
        });

        const proceedBtn = document.getElementById('analyzeProceed');
        proceedBtn.disabled = willProcess === 0;
        proceedBtn.innerHTML = willProcess > 0
            ? `<i class="fas fa-folder-plus me-2"></i>Proceed (${willProcess} file${willProcess === 1 ? '' : 's'})`
            : `<i class="fas fa-folder-plus me-2"></i>Nothing to Process`;

        this._renderTable();
    }

    /**
     * Renders only the table body for the current filter tab + text filter.
     * Cheap to call on every keystroke of the text filter.
     */
    _renderTable() {
        const ctx = this._ctx;
        if (!ctx) return;

        const textFilter = (document.getElementById('analyzeFilterText').value || '').toLowerCase();

        const filtered = ctx.results.filter(r => {
            if (ctx.filter === 'Encode' && !ENCODE_SET.has(r.decision))                       return false;
            if (ctx.filter === 'Mux'    && r.decision !== 'Mux')                              return false;
            if (ctx.filter === 'Copy'   && r.decision !== 'Copy')                             return false;
            if (ctx.filter === 'Skip'   && !(r.decision === 'Skip' || r.decision === 'Excluded')) return false;
            if (ctx.filter === 'Done'   && !DONE_SET.has(r.decision))                         return false;
            if (ctx.filter === 'Error'  && r.decision !== 'Error')                            return false;
            if (textFilter && !r.fileName.toLowerCase().includes(textFilter))                 return false;
            return true;
        });

        document.getElementById('analyzeTableBody').innerHTML = filtered.map(r => `
            <tr>
                <td class="text-truncate" style="max-width: 460px;" title="${escapeHtml(r.filePath)}">
                    <i class="fas fa-file-video me-2 text-primary"></i>${escapeHtml(r.fileName)}
                </td>
                <td class="small text-muted" style="white-space: nowrap;">${renderSource(r)}</td>
                <td>
                    <div class="d-flex align-items-start gap-2">
                        ${renderBadge(r.decision, r.borderline)}
                        <span class="small text-muted" style="line-height: 1.4;">${escapeHtml(r.reason)}</span>
                    </div>
                </td>
            </tr>`).join('');

        document.getElementById('analyzeEmpty').style.display = filtered.length === 0 ? '' : 'none';
    }


    // ---- Proceed ----

    /**
     * Closes the analyze modal + library modal and submits the directory to
     * `processDirectory`. Mirrors what `LibraryBrowser.processCurrentDirectory`
     * does — kept inline so we don't pin LibraryBrowser as a dependency.
     */
    async _proceed() {
        const ctx = this._ctx;
        if (!ctx) return;

        closeModal(MODAL_ID);
        closeModal('libraryModal');

        // Let the close animation paint before kicking off the scan.
        await new Promise(resolve => setTimeout(resolve, 100));

        const label = document.getElementById('analyzeTargetLabel').textContent;
        showToast(`Queuing files from "${label}"...`, 'info');

        try {
            const result = await libraryApi.processDirectory(ctx.dirPath, ctx.recursive, ctx.options);
            showToast(result.message, 'success');
        } catch (err) {
            showToast('Error processing directory: ' + err.message, 'danger');
        }
    }
}


// ---------------------------------------------------------------------------
// Render helpers
// ---------------------------------------------------------------------------

/** Pretty file-size formatter (GB above 1 GB, MB below). */
function formatSize(bytes) {
    return bytes > 1e9
        ? (bytes / 1e9).toFixed(1) + ' GB'
        : (bytes / 1e6).toFixed(0) + ' MB';
}

/** Renders one summary card. */
function renderCard(icon, label, primary, secondary, color) {
    return `
        <div class="col-6 col-md">
            <div class="card h-100" style="border-left: 3px solid var(${color});">
                <div class="card-body p-2">
                    <div class="d-flex align-items-center justify-content-between">
                        <div class="small text-muted text-uppercase" style="letter-spacing: 0.5px; font-size: 0.7rem;">${label}</div>
                        <i class="fas ${icon} text-muted" style="opacity: 0.5;"></i>
                    </div>
                    <div class="h4 mb-0 mt-1">${primary}</div>
                    <div class="small text-muted" style="min-height: 1.25em;">${secondary || '&nbsp;'}</div>
                </div>
            </div>
        </div>`;
}

/** Renders the source-metadata cell (codec · bitrate · resolution · size). */
function renderSource(r) {
    const parts = [];
    if (r.codec)       parts.push(escapeHtml(r.codec));
    if (r.bitrateKbps) parts.push(r.bitrateKbps.toLocaleString() + ' kbps');
    if (r.width)       parts.push(`${r.width}×${r.height}`);
    parts.push(formatSize(r.sizeBytes));
    return parts.join(' · ');
}

/** Renders the decision pill plus a borderline-warning icon when applicable. */
function renderBadge(decision, borderline) {
    const map = DECISION_BADGE[decision] || ['status-cancelled', decision];
    const tip = borderline
        ? ' <i class="fas fa-exclamation-triangle text-warning ms-1" title="Near skip threshold — real run remeasures video-only bitrate and may decide differently"></i>'
        : '';
    return `<span class="status-badge ${map[0]}">${map[1]}</span>${tip}`;
}
