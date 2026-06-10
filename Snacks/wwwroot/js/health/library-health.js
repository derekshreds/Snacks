/**
 * Library Health page.
 *
 * Renders the file-level issues surfaced by /api/library/health — videos with
 * no audio track, no decodable video stream, zero duration, and failed
 * encodes — with summary-card filters, a text filter, and a per-row "Verify"
 * action that runs bounded ffmpeg decode samples on the server to confirm
 * whether a flagged file is actually damaged.
 */

import { libraryApi }   from '../api.js';
import { escapeHtml }   from '../utils/dom.js';
import { registerPage } from '../core/navigation.js';


/** Maps issue keys to badge labels + styles (status pills from site.css). */
const ISSUE_BADGE = {
    'no-audio':    ['status-uploading', 'No audio'],
    'no-video':    ['status-failed',    'No video'],
    'no-duration': ['status-failed',    'No duration'],
    'failed':      ['status-failed',    'Encode failed'],
};

let items = [];           // full report rows
let issueFilter = 'all';  // active summary-card filter


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
        renderSlices('insightsCodecs',      data.codecs,      data.totalFiles);
        renderSlices('insightsResolutions', data.resolutions, videoTotal);
        renderSlices('insightsStatuses',    data.statuses,    data.totalFiles);
    } catch (err) {
        console.error('Library insights failed', err);
    }
}


// ---------------------------------------------------------------------------
// Rendering
// ---------------------------------------------------------------------------

function renderSummary(summary) {
    document.getElementById('healthCountAll').textContent        = summary.total.toLocaleString();
    document.getElementById('healthCountNoAudio').textContent    = summary.noAudio.toLocaleString();
    document.getElementById('healthCountNoVideo').textContent    = summary.noVideo.toLocaleString();
    document.getElementById('healthCountNoDuration').textContent = summary.noDuration.toLocaleString();
    document.getElementById('healthCountFailed').textContent     = summary.failed.toLocaleString();
}

function renderTable() {
    const text = (document.getElementById('healthFilterText')?.value || '').toLowerCase();
    const filtered = items.filter(i => {
        if (issueFilter !== 'all' && !i.issues.includes(issueFilter)) return false;
        if (text && !i.fileName.toLowerCase().includes(text) && !i.filePath.toLowerCase().includes(text)) return false;
        return true;
    });

    const wrap  = document.getElementById('healthTableWrap');
    const empty = document.getElementById('healthEmpty');
    document.getElementById('healthShownCount').textContent =
        filtered.length === items.length ? `(${items.length})` : `(${filtered.length} of ${items.length})`;

    if (filtered.length === 0) {
        wrap.style.display  = 'none';
        empty.style.display = '';
        document.getElementById('healthEmptyMsg').textContent = items.length === 0
            ? 'No file-level issues found. Your library looks healthy.'
            : 'No flagged files match the current filter.';
        return;
    }
    empty.style.display = 'none';
    wrap.style.display  = '';

    document.getElementById('healthTableBody').innerHTML = filtered.map((r, idx) => `
        <tr data-health-row="${idx}">
            <td class="text-truncate" style="max-width: 420px;" title="${escapeHtml(r.filePath)}">
                <i class="fas ${r.kind === 'Music' ? 'fa-music' : 'fa-file-video'} me-2 text-primary"></i>${escapeHtml(r.fileName)}
                <div class="small text-muted text-truncate">${escapeHtml(r.directory)}</div>
            </td>
            <td class="small text-muted" style="white-space: nowrap;">
                ${[r.codec ? escapeHtml(r.codec) : '', r.width ? `${r.width}×${r.height}` : '', fmtDuration(r.duration), fmtSize(r.sizeBytes)].filter(Boolean).join(' · ')}
            </td>
            <td>
                <div class="d-flex flex-wrap align-items-center gap-1">
                    ${r.issues.map(renderIssueBadge).join('')}
                </div>
                ${r.failureReason ? `<div class="small text-muted mt-1">${escapeHtml(r.failureReason)}</div>` : ''}
                <div class="small mt-1" data-verify-result style="display:none;"></div>
            </td>
            <td style="white-space: nowrap;">
                <button type="button" class="btn btn-sm btn-outline-info" data-verify-path="${escapeHtml(r.filePath)}" title="Run ffmpeg decode samples to confirm whether this file is damaged">
                    <i class="fas fa-stethoscope me-1"></i>Verify
                </button>
            </td>
        </tr>`).join('');
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


// ---------------------------------------------------------------------------
// Load
// ---------------------------------------------------------------------------

async function refresh() {
    const loading = document.getElementById('healthLoading');
    if (!loading) return;
    loading.style.display = '';
    document.getElementById('healthTableWrap').style.display = 'none';
    document.getElementById('healthEmpty').style.display = 'none';

    try {
        const data = await libraryApi.getHealth();
        // The user may have SPA-navigated away while the report loaded — the
        // health DOM is gone and every render below would null-deref.
        if (!document.getElementById('healthLoading')) return;
        items = data.items || [];
        renderSummary(data.summary || { total: 0, noAudio: 0, noVideo: 0, noDuration: 0, failed: 0 });
        loading.style.display = 'none';
        renderTable();
    } catch (err) {
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

/** Delegated click handler for the whole page (filter cards + verify buttons). */
function onPageClick(e) {
    const card = e.target.closest('.health-filter-card');
    if (card) {
        issueFilter = card.dataset.healthFilter || 'all';
        document.querySelectorAll('.health-filter-card').forEach(c =>
            c.classList.toggle('active', c === card));
        renderTable();
        return;
    }
    const verifyBtn = e.target.closest('[data-verify-path]');
    if (verifyBtn) onVerifyClick(verifyBtn);
    if (e.target.closest('#healthRefreshBtn')) refresh();
}

function onFilterInput(e) {
    if (e.target.id === 'healthFilterText') renderTable();
}

registerPage('library-health', {
    mount: () => {
        // Both listeners attach to the page root, which is replaced wholesale
        // on SPA navigation — no explicit unbind needed.
        const root = document.querySelector('.library-health-page');
        root?.addEventListener('click', onPageClick);
        root?.addEventListener('input', onFilterInput);
        issueFilter = 'all';
        refresh();
        loadInsights();
    },
    unmount: () => {
        items = [];
    },
});
