/**
 * Cluster Logs page.
 *
 * Lets the operator pick any node in the cluster and live-tail its
 * operations log without ssh-ing into the box. Polls
 * /api/diagnostics/log?nodeId=...&lines=... every few seconds; the master
 * proxies remote requests through the cluster RPC channel, so this module
 * never has to know whether the target is local or remote.
 *
 * Companion to the per-node "Download logs" button on the queue page —
 * both surfaces share the same /api/diagnostics/* endpoints.
 */

import { escapeHtml }      from '../utils/dom.js';
import { registerPage }    from '../core/navigation.js';
import { clusterApi, diagnosticsApi } from '../api.js';
import { streamDownload }  from '../utils/download.js';

const POLL_MS = 5000;

let nodeSelect      = null;
let linesSelect     = null;
let autoRefreshChk  = null;
let refreshBtn      = null;
let downloadBtn     = null;
let statusEl        = null;
let errorEl         = null;
let contentEl       = null;

let pollTimer       = null;
let abortCtrl       = null;
let lastFetchedAt   = null;
let nodes           = [];     // [{nodeId, hostname, role, status}]
let localNodeId     = null;

/** Returns the selected node id, or null when no node is chosen. */
function currentNodeId() {
    return nodeSelect?.value || null;
}

/** Returns the selected line count, defaulting to 200. */
function currentLines() {
    return parseInt(linesSelect?.value || '200', 10);
}

/** Updates the download anchor href so the browser saves from the right node. */
function refreshDownloadHref() {
    if (!downloadBtn) return;
    const id = currentNodeId();
    // Pass the id even for the local node — server treats local as "no proxy needed"
    // and the bundle is still labeled with the right hostname.
    downloadBtn.href = diagnosticsApi.logsZipUrl(id);
}

/** Replaces the status line text. */
function setStatus(text) {
    if (!statusEl) return;
    statusEl.textContent = text || '';
}

/** Renders or hides the inline error banner. */
function setError(message) {
    if (!errorEl) return;
    if (!message) {
        errorEl.classList.add('d-none');
        errorEl.textContent = '';
    } else {
        errorEl.classList.remove('d-none');
        errorEl.textContent = message;
    }
}

/**
 * Replaces the <pre> body with the supplied lines, preserving scroll-at-bottom
 * behavior so the live tail keeps following new output unless the user has
 * scrolled up to inspect older lines.
 */
function renderLines(lines) {
    if (!contentEl) return;
    const wasAtBottom =
        contentEl.scrollHeight - contentEl.scrollTop - contentEl.clientHeight < 50;
    contentEl.innerHTML = (lines || [])
        .map(line => `<div>${escapeHtml(line)}</div>`)
        .join('');
    if (wasAtBottom) contentEl.scrollTop = contentEl.scrollHeight;
}

/**
 * Populates the node dropdown from the cluster status payload, preserving the
 * current selection across reloads when possible. The /api/cluster-admin/status
 * payload puts the local node at the top level (nodeId + nodeName + role) and
 * peers in `nodes[]`; on workers the master is included in `nodes[]` already.
 */
function populateNodeDropdown(payload) {
    nodes = [];

    if (payload?.nodeId) {
        localNodeId = payload.nodeId;
        nodes.push({
            nodeId:   payload.nodeId,
            hostname: payload.nodeName || 'This Machine',
            role:     payload.role || 'standalone',
        });
    }
    for (const n of (payload?.nodes || [])) {
        if (!n?.nodeId || n.nodeId === localNodeId) continue;
        nodes.push({
            nodeId:   n.nodeId,
            hostname: n.hostname || n.nodeId,
            role:     n.role || 'node',
        });
    }

    const previous = currentNodeId();
    nodeSelect.innerHTML = nodes.map(n => {
        const labelRole = n.role === 'master' ? ' (master)' : (n.role === 'standalone' ? '' : ' (worker)');
        return `<option value="${escapeHtml(n.nodeId)}">${escapeHtml(n.hostname)}${labelRole}</option>`;
    }).join('');

    const params = new URLSearchParams(location.search);
    const fromUrl = params.get('nodeId');
    const desired = (previous && nodes.some(n => n.nodeId === previous)) ? previous
                  : (fromUrl  && nodes.some(n => n.nodeId === fromUrl))  ? fromUrl
                  : (localNodeId || nodes[0]?.nodeId || '');
    if (desired) nodeSelect.value = desired;
}

/** Issues one tail fetch and renders the result. Cancels any in-flight call. */
async function fetchTail() {
    const id = currentNodeId();
    if (!id) return;

    if (abortCtrl) abortCtrl.abort();
    abortCtrl = new AbortController();

    try {
        const url = `/api/diagnostics/log?lines=${currentLines()}` +
                    `&nodeId=${encodeURIComponent(id)}`;
        const resp = await fetch(url, { signal: abortCtrl.signal });
        if (!resp.ok) {
            // Surface a structured error from the proxy when available.
            let message = `Fetch failed (${resp.status})`;
            try {
                const data = await resp.json();
                if (data?.error) {
                    message = data.error;
                    if (data.lastSeen) {
                        const seen = new Date(data.lastSeen);
                        message += ` — last seen ${seen.toLocaleString()}`;
                    }
                }
            } catch { /* non-JSON response */ }
            setError(message);
            return;
        }

        const data = await resp.json();
        setError('');

        if (data?.available === false) {
            renderLines([]);
            setStatus(`No log file yet under ${data.logsDir || 'logs/'}`);
        } else {
            renderLines(data.lines || []);
            lastFetchedAt = new Date();
            const wrote = data.lastWriteUtc ? new Date(data.lastWriteUtc).toLocaleString() : '';
            setStatus(`${data.logFile || ''} • ${data.lineCount || 0} lines • last write ${wrote} • fetched ${lastFetchedAt.toLocaleTimeString()}`);
        }
    } catch (err) {
        if (err.name === 'AbortError') return;
        setError(err.message || String(err));
    }
}

/** Starts the polling timer using the auto-refresh checkbox state. */
function startPolling() {
    stopPolling();
    if (!autoRefreshChk?.checked) return;
    pollTimer = setInterval(fetchTail, POLL_MS);
}

/** Stops the polling timer if running. */
function stopPolling() {
    if (pollTimer) {
        clearInterval(pollTimer);
        pollTimer = null;
    }
}

/** Initial mount: load nodes, render, and start polling. */
async function onMount() {
    nodeSelect      = document.getElementById('clusterLogNode');
    linesSelect     = document.getElementById('clusterLogLines');
    autoRefreshChk  = document.getElementById('clusterLogAutoRefresh');
    refreshBtn      = document.getElementById('clusterLogRefreshBtn');
    downloadBtn     = document.getElementById('clusterLogDownloadBtn');
    statusEl        = document.getElementById('clusterLogStatus');
    errorEl         = document.getElementById('clusterLogError');
    contentEl       = document.getElementById('clusterLogContent');
    if (!nodeSelect) return;

    nodeSelect.addEventListener('change', () => {
        refreshDownloadHref();
        fetchTail();
    });
    linesSelect?.addEventListener('change', fetchTail);
    refreshBtn?.addEventListener('click', fetchTail);
    autoRefreshChk?.addEventListener('change', startPolling);

    downloadBtn?.addEventListener('click', (e) => {
        e.preventDefault();
        streamDownload(downloadBtn.href, downloadBtn, 'snacks-logs.zip')
            .catch(() => { /* toast already raised */ });
    });

    setStatus('Loading nodes…');
    try {
        const status = await clusterApi.getStatus();
        populateNodeDropdown(status);
    } catch (err) {
        setError(`Could not load cluster status: ${err.message}`);
    }

    refreshDownloadHref();
    await fetchTail();
    startPolling();
}

/** Tear down before navigation so we don't leak the polling timer. */
function onUnmount() {
    stopPolling();
    if (abortCtrl) {
        abortCtrl.abort();
        abortCtrl = null;
    }
    nodeSelect = linesSelect = autoRefreshChk = null;
    refreshBtn = downloadBtn = statusEl = errorEl = contentEl = null;
}

registerPage('cluster-logs', { mount: onMount, unmount: onUnmount });
