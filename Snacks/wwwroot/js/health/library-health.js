/**
 * Library Health page.
 *
 * Renders the file-level issues surfaced by /api/library/health — videos with
 * no audio track, no decodable video stream, zero duration, failed encodes,
 * and failed deep-verifications — with summary-card filters, a search box, a
 * per-row "Verify" action (bounded ffmpeg decode samples), and rolling-verify
 * coverage stats. Filtering, search, and pagination are SERVER-side: category
 * counts are whole-library SQL COUNTs and the table pages 100 rows at a time,
 * so the page stays honest and fast on 500k-file libraries.
 */

import { libraryApi }       from '../api.js';
import { escapeHtml }       from '../utils/dom.js';
import { registerPage }     from '../core/navigation.js';
import { showConfirmModal } from '../utils/modal-controller.js';


/** Maps issue keys to badge labels + styles (status pills from site.css). */
const ISSUE_BADGE = {
    'no-audio':      ['status-uploading', 'No audio'],
    'no-video':      ['status-failed',    'No video'],
    'no-duration':   ['status-failed',    'No duration'],
    'failed':        ['status-failed',    'Encode failed'],
    'verify-failed': ['status-failed',    'Verify failed'],
};

const PAGE_SIZE = 100;

let items = [];           // current page of report rows
let totalItems = 0;       // total rows for the active filter
let verifyFailedCount = 0;// whole-library verify-failed count (drives the Reset button on any tab)
let issueFilter = 'all';  // active summary-card filter
let page = 0;
let searchTimer = null;
let refreshGen = 0;       // stale-response guard: only the newest fetch may render


// ---------------------------------------------------------------------------
// Formatters
// ---------------------------------------------------------------------------

function fmtSize(bytes) {
    if (!bytes || bytes <= 0) return '—';
    return bytes > 1e9
        ? (bytes / 1e9).toFixed(1) + ' GB'
        : (bytes / 1e6).toFixed(0) + ' MB';
}

function fmtDuration(seconds) {
    if (!seconds || seconds <= 0) return '—';
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    return h > 0 ? `${h}h ${m}m` : `${m}m`;
}


// ---------------------------------------------------------------------------
// Library insights (composition bars)
// ---------------------------------------------------------------------------

/** Stable colors per slice position; labels vary too much to hard-map. */
const SLICE_COLORS = ['#8b5cf6', '#0ea5e9', '#10b981', '#f59e0b', '#ef4444', '#6b7280'];

/** Renders one labeled distribution as horizontal proportion bars. */
function renderSlices(containerId, slices, total) {
    const el = document.getElementById(containerId);
    if (!el) return;
    if (!slices?.length || !total) {
        el.innerHTML = '<div class="small text-muted"><em>No data yet — run a library scan.</em></div>';
        return;
    }
    el.innerHTML = slices.slice(0, 6).map((s, i) => {
        const pct = Math.max(1, Math.round((s.count / total) * 100));
        const bytes = s.bytes > 0 ? ` · ${fmtSize(s.bytes)}` : '';
        return `
            <div class="mb-2">
                <div class="d-flex justify-content-between small">
                    <span>${escapeHtml(s.label)}</span>
                    <span class="text-muted">${s.count.toLocaleString()} (${pct}%)${bytes}</span>
                </div>
                <div class="progress" style="height: 6px;">
                    <div class="progress-bar" style="width: ${pct}%; background: ${SLICE_COLORS[i % SLICE_COLORS.length]};"></div>
                </div>
            </div>`;
    }).join('');
}

async function loadInsights() {
    try {
        const data = await libraryApi.getInsights();
        const card = document.getElementById('libraryInsightsCard');
        if (!card) return; // page swapped away mid-fetch
        card.style.display = '';

        const parts = [`${data.totalFiles.toLocaleString()} files`, fmtSize(data.totalBytes)];
        if (data.hdrFiles > 0)   parts.push(`${data.hdrFiles.toLocaleString()} HDR`);
        if (data.musicFiles > 0) parts.push(`${data.musicFiles.toLocaleString()} music`);
        document.getElementById('insightsTotals').textContent = parts.join(' · ');

        const videoTotal = data.totalFiles - data.musicFiles;
        // Status labels are raw enum names (e.g. "NoSavings"); space the compound
        // ones so they read as "No Savings". Codec/resolution labels need no change.
        const statuses = (data.statuses || []).map(s => ({
            ...s,
            label: s.label.replace(/([a-z0-9])([A-Z])/g, '$1 $2'),
        }));
        renderSlices('insightsCodecs',      data.codecs,      data.totalFiles);
        renderSlices('insightsResolutions', data.resolutions, videoTotal);
        renderSlices('insightsStatuses',    statuses,         data.totalFiles);
    } catch (err) {
        console.error('Library insights failed', err);
    }
}


// ---------------------------------------------------------------------------
// Rendering
// ---------------------------------------------------------------------------

function renderSummary(summary) {
    document.getElementById('healthCountAll').textContent          = summary.total.toLocaleString();
    document.getElementById('healthCountNoAudio').textContent      = summary.noAudio.toLocaleString();
    document.getElementById('healthCountNoVideo').textContent      = summary.noVideo.toLocaleString();
    document.getElementById('healthCountNoDuration').textContent   = summary.noDuration.toLocaleString();
    document.getElementById('healthCountFailed').textContent       = summary.failed.toLocaleString();
    document.getElementById('healthCountVerifyFailed').textContent = (summary.verifyFailed ?? 0).toLocaleString();
    verifyFailedCount = summary.verifyFailed ?? 0;

    // Rolling-verify coverage line: "12,400 of 515,000 files deep-verified".
    const coverage = document.getElementById('healthVerifyCoverage');
    if (coverage) {
        coverage.textContent = summary.totalScanned > 0
            ? `${(summary.verifiedCount ?? 0).toLocaleString()} of ${summary.totalScanned.toLocaleString()} files deep-verified`
            : '';
    }
}

function renderTable() {
    const wrap  = document.getElementById('healthTableWrap');
    const empty = document.getElementById('healthEmpty');
    const shown = items.length;

    document.getElementById('healthShownCount').textContent = totalItems > shown
        ? `(${(page * PAGE_SIZE + 1).toLocaleString()}–${(page * PAGE_SIZE + shown).toLocaleString()} of ${totalItems.toLocaleString()})`
        : `(${totalItems.toLocaleString()})`;

    // Delete-all acts on the whole active filter, not just this page — only offer it
    // when something is actually flagged.
    const delAll = document.getElementById('healthDeleteAllBtn');
    if (delAll) {
        delAll.style.display = totalItems > 0 ? '' : 'none';
        delAll.innerHTML = `<i class="fas fa-trash me-1"></i>Delete All (${totalItems.toLocaleString()})`;
    }

    // "Reset Verify Failures" clears the verify flag rather than deleting, and always acts on the
    // whole verify-failed set. Show it on the All tab (so it's discoverable) and the Verify-failed
    // tab (its natural home) — but not on unrelated tabs (no-audio, etc.) where it'd be confusing.
    const resetBtn = document.getElementById('healthResetVerifyBtn');
    if (resetBtn) {
        const showReset = (issueFilter === 'all' || issueFilter === 'verify-failed') && verifyFailedCount > 0;
        resetBtn.style.display = showReset ? '' : 'none';
        if (showReset)
            resetBtn.innerHTML = `<i class="fas fa-rotate-left me-1"></i>Reset Verify Failures (${verifyFailedCount.toLocaleString()})`;
    }

    renderPager();

    if (shown === 0) {
        wrap.style.display  = 'none';
        empty.style.display = '';
        document.getElementById('healthEmptyMsg').textContent = totalItems === 0
            ? 'No file-level issues found. Your library looks healthy.'
            : 'No flagged files match the current filter.';
        return;
    }
    empty.style.display = 'none';
    wrap.style.display  = '';

    document.getElementById('healthTableBody').innerHTML = items.map((r, idx) => `
        <tr data-health-row="${idx}">
            <td class="dash-file" style="max-width: 420px;" title="${escapeHtml(r.filePath)}">
                <div class="dash-file-inner">
                    <span class="dash-fileicon"><i class="fas ${r.kind === 'Music' ? 'fa-music' : 'fa-file-video'}"></i></span>
                    <div style="min-width:0;">
                        <div class="dash-filename">${escapeHtml(r.fileName)}</div>
                        <div class="small text-muted text-truncate">${escapeHtml(r.directory)}</div>
                    </div>
                </div>
            </td>
            <td class="small text-muted" style="white-space: nowrap;">
                ${[r.codec ? escapeHtml(r.codec) : '', r.width ? `${r.width}×${r.height}` : '', fmtDuration(r.duration), fmtSize(r.sizeBytes)].filter(Boolean).join(' · ')}
            </td>
            <td>
                <div class="d-flex flex-wrap align-items-center gap-1">
                    ${r.issues.map(renderIssueBadge).join('')}
                </div>
                ${r.failureReason ? `<div class="small text-muted mt-1">${escapeHtml(r.failureReason)}</div>` : ''}
                ${r.verifyResult && r.verifyResult !== 'ok'
                    ? `<div class="small text-danger mt-1"><i class="fas fa-triangle-exclamation me-1"></i>${escapeHtml(r.verifyResult)}</div>`
                    : ''}
                <div class="small mt-1" data-verify-result style="display:none;"></div>
            </td>
            <td style="white-space: nowrap;">
                <div class="d-flex gap-1">
                    <button type="button" class="btn btn-sm btn-outline-info" data-verify-path="${escapeHtml(r.filePath)}" title="Run ffmpeg decode samples to confirm whether this file is damaged">
                        <i class="fas fa-stethoscope me-1"></i>Verify
                    </button>
                    ${r.verifyResult && r.verifyResult !== 'ok'
                        ? `<button type="button" class="btn btn-sm btn-outline-warning" data-reset-path="${escapeHtml(r.filePath)}" title="Clear this file's failed-verification flag (no deletion) so rolling verification re-checks it">
                        <i class="fas fa-rotate-left"></i>
                    </button>`
                        : ''}
                    <button type="button" class="btn btn-sm btn-outline-danger" data-delete-path="${escapeHtml(r.filePath)}" data-delete-name="${escapeHtml(r.fileName)}" title="Delete this file from disk and remove it from the library">
                        <i class="fas fa-trash"></i>
                    </button>
                </div>
            </td>
        </tr>`).join('');
}

function renderPager() {
    const pager = document.getElementById('healthPager');
    if (!pager) return;
    const totalPages = Math.max(1, Math.ceil(totalItems / PAGE_SIZE));
    pager.style.display = totalPages > 1 ? 'flex' : 'none';
    if (totalPages > 1) {
        document.getElementById('healthPageLabel').textContent = `Page ${page + 1} of ${totalPages.toLocaleString()}`;
        document.getElementById('healthPagePrev').disabled = page <= 0;
        document.getElementById('healthPageNext').disabled = page >= totalPages - 1;
    }
}

function renderIssueBadge(issue) {
    const [cls, label] = ISSUE_BADGE[issue] || ['status-cancelled', issue];
    return `<span class="status-badge ${cls}">${label}</span>`;
}


// ---------------------------------------------------------------------------
// Actions
// ---------------------------------------------------------------------------

async function onVerifyClick(btn) {
    const path = btn.dataset.verifyPath;
    const row  = btn.closest('tr');
    const out  = row?.querySelector('[data-verify-result]');
    if (!path || !out || btn.disabled) return;

    btn.disabled  = true;
    const prev    = btn.innerHTML;
    btn.innerHTML = '<i class="fas fa-spinner fa-spin me-1"></i>Verifying…';
    out.style.display = '';
    out.className = 'small mt-1 text-muted';
    out.textContent = 'Decoding samples at the start, middle, and end of the file…';

    try {
        const result = await libraryApi.verifyFile(path);
        if (result.ok) {
            out.className   = 'small mt-1 text-success';
            out.innerHTML   = '<i class="fas fa-circle-check me-1"></i>Decode check passed — the file plays back cleanly.';
        } else {
            out.className = 'small mt-1 text-danger';
            out.innerHTML = '<i class="fas fa-triangle-exclamation me-1"></i>Problems found:<br>' +
                result.issues.map(i => `<span class="text-muted">· ${escapeHtml(i)}</span>`).join('<br>');
        }
    } catch (err) {
        out.className   = 'small mt-1 text-danger';
        out.textContent = 'Verification failed: ' + err.message;
    } finally {
        btn.disabled  = false;
        btn.innerHTML = prev;
    }
}

/** Deletes a single flagged file from disk (after confirmation), then refreshes. */
async function onDeleteClick(btn) {
    const path = btn.dataset.deletePath;
    const name = btn.dataset.deleteName || path;
    if (!path || btn.disabled) return;

    const ok = await showConfirmModal(
        'Delete File',
        `Permanently delete <strong>${escapeHtml(name)}</strong> from disk? ` +
        `This removes it from the library; it can't be undone. If you re-download it later, the next scan will pick it up fresh.`,
        'Delete');
    if (!ok) return;

    btn.disabled  = true;
    btn.innerHTML = '<i class="fas fa-spinner fa-spin"></i>';
    try {
        await libraryApi.deleteFlaggedFile(path);
        showToast(`Deleted "${name}"`, 'success');
        refresh();
    } catch (err) {
        showToast('Delete failed: ' + err.message, 'danger');
        btn.disabled  = false;
        btn.innerHTML = '<i class="fas fa-trash"></i>';
    }
}

/** Deletes every flagged file matching the active filter + search (after confirmation). */
async function onDeleteAll() {
    if (totalItems <= 0) return;
    const scope = issueFilter === 'all' ? 'flagged' : `"${issueFilter}"`;
    const ok = await showConfirmModal(
        'Delete All Flagged Files',
        `Permanently delete all <strong>${totalItems.toLocaleString()}</strong> ${escapeHtml(scope)} file(s) from disk? <br /><br />` +
        `This removes them from the library and <strong>cannot be undone</strong>. Re-downloaded files are picked up fresh on the next scan.`,
        'Delete All');
    if (!ok) return;

    const btn = document.getElementById('healthDeleteAllBtn');
    if (btn) { btn.disabled = true; btn.innerHTML = '<i class="fas fa-spinner fa-spin me-1"></i>Deleting…'; }
    try {
        const res = await libraryApi.deleteAllFlagged({
            filter: issueFilter === 'all' ? null : issueFilter,
            q:      document.getElementById('healthFilterText')?.value || null,
        });
        let msg = `Deleted ${res.deleted.toLocaleString()} file(s)`;
        if (res.failed)  msg += `, ${res.failed.toLocaleString()} could not be deleted`;
        if (res.capped)  msg += ' (capped — run again to continue)';
        showToast(msg, res.failed ? 'warning' : 'success');
    } catch (err) {
        showToast('Delete failed: ' + err.message, 'danger');
    } finally {
        page = 0;
        refresh();
    }
}

/** Clears the failed-verification flag on the whole verify-failed set (after confirmation), from any tab. */
async function onResetVerify() {
    if (verifyFailedCount <= 0) return;
    const ok = await showConfirmModal(
        'Reset Verify Failures',
        `Clear the failed-verification flag on all <strong>${verifyFailedCount.toLocaleString()}</strong> file(s)? <br /><br />` +
        `Nothing is deleted — the files are marked unverified and moved to the front of the rolling-verification queue, ` +
        `so any genuinely broken file will re-appear here after it's re-checked (rolling verification must be enabled in Settings). <br /><br />` +
        `Use this to clear stale or false failures.`,
        'Reset');
    if (!ok) return;

    const btn = document.getElementById('healthResetVerifyBtn');
    if (btn) { btn.disabled = true; btn.innerHTML = '<i class="fas fa-spinner fa-spin me-1"></i>Resetting…'; }
    try {
        // Always the verify-failed set, regardless of the active tab; a search term still narrows it.
        const res = await libraryApi.resetVerifyFlagged({
            filter: 'verify-failed',
            q:      document.getElementById('healthFilterText')?.value || null,
        });
        showToast(`Reset ${res.reset.toLocaleString()} file(s) for re-verification`, 'success');
    } catch (err) {
        showToast('Reset failed: ' + err.message, 'danger');
    } finally {
        page = 0;
        refresh();
    }
}

/** Clears the failed-verification flag on a single file, then refreshes. */
async function onResetRowClick(btn) {
    const path = btn.dataset.resetPath;
    if (!path || btn.disabled) return;

    btn.disabled  = true;
    const prev    = btn.innerHTML;
    btn.innerHTML = '<i class="fas fa-spinner fa-spin"></i>';
    try {
        const res = await libraryApi.resetVerifyFile(path);
        if (!res.success) throw new Error('no matching file');
        showToast('Verification flag cleared', 'success');
        refresh();
    } catch (err) {
        showToast('Reset failed: ' + err.message, 'danger');
        btn.disabled  = false;
        btn.innerHTML = prev;
    }
}


// ---------------------------------------------------------------------------
// Load
// ---------------------------------------------------------------------------

async function refresh() {
    const loading = document.getElementById('healthLoading');
    if (!loading) return;
    loading.style.display = '';
    document.getElementById('healthTableWrap').style.display = 'none';
    document.getElementById('healthEmpty').style.display = 'none';

    const gen = ++refreshGen;
    try {
        const data = await libraryApi.getHealth({
            filter: issueFilter === 'all' ? null : issueFilter,
            q:      document.getElementById('healthFilterText')?.value || null,
            skip:   page * PAGE_SIZE,
            limit:  PAGE_SIZE,
        });
        // Two staleness checks: the user may have SPA-navigated away (DOM gone),
        // or clicked another filter card whose (faster) fetch already rendered —
        // a slow earlier response must not paint over it.
        if (gen !== refreshGen) return;
        if (!document.getElementById('healthLoading')) return;
        items      = data.items || [];
        totalItems = data.total ?? items.length;

        // The active filter shrank beneath the current page (e.g. category
        // switch) — clamp back to the last real page and refetch once.
        if (items.length === 0 && totalItems > 0 && page > 0) {
            page = Math.max(0, Math.ceil(totalItems / PAGE_SIZE) - 1);
            refresh();
            return;
        }
        renderSummary(data.summary || { total: 0, noAudio: 0, noVideo: 0, noDuration: 0, failed: 0, verifyFailed: 0, verifiedCount: 0, totalScanned: 0 });
        loading.style.display = 'none';
        renderTable();
    } catch (err) {
        if (gen !== refreshGen) return; // superseded — a newer fetch owns the UI
        const empty = document.getElementById('healthEmpty');
        if (!empty) return; // page swapped away mid-fetch
        loading.style.display = 'none';
        empty.style.display = '';
        document.getElementById('healthEmptyMsg').textContent = 'Failed to load health report: ' + err.message;
    }
}


// ---------------------------------------------------------------------------
// Page lifecycle
// ---------------------------------------------------------------------------

/** Delegated click handler for the whole page (filter cards, pager, verify buttons). */
function onPageClick(e) {
    const card = e.target.closest('.health-filter-card');
    if (card) {
        issueFilter = card.dataset.healthFilter || 'all';
        page = 0;
        document.querySelectorAll('.health-filter-card').forEach(c =>
            c.classList.toggle('active', c === card));
        refresh();
        return;
    }
    if (e.target.closest('#healthPagePrev')) { if (page > 0) { page--; refresh(); } return; }
    if (e.target.closest('#healthPageNext')) { page++; refresh(); return; }
    const verifyBtn = e.target.closest('[data-verify-path]');
    if (verifyBtn) { onVerifyClick(verifyBtn); return; }
    const resetRowBtn = e.target.closest('[data-reset-path]');
    if (resetRowBtn) { onResetRowClick(resetRowBtn); return; }
    const deleteBtn = e.target.closest('[data-delete-path]');
    if (deleteBtn) { onDeleteClick(deleteBtn); return; }
    if (e.target.closest('#healthResetVerifyBtn')) { onResetVerify(); return; }
    if (e.target.closest('#healthDeleteAllBtn')) { onDeleteAll(); return; }
    if (e.target.closest('#healthRefreshBtn')) refresh();
}

/** Debounced server-side search — each keystroke would otherwise be a query. */
function onFilterInput(e) {
    if (e.target.id !== 'healthFilterText') return;
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => { page = 0; refresh(); }, 350);
}

registerPage('library-health', {
    mount: () => {
        // Both listeners attach to the page root, which is replaced wholesale
        // on SPA navigation — no explicit unbind needed.
        const root = document.querySelector('.library-health-page');
        root?.addEventListener('click', onPageClick);
        root?.addEventListener('input', onFilterInput);
        issueFilter = 'all';
        page = 0;
        refresh();
        loadInsights();
    },
    unmount: () => {
        items = [];
        clearTimeout(searchTimer);
    },
});
