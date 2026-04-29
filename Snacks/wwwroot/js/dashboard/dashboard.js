/**
 * Encode Dashboard.
 *
 * Reads the EncodeHistory ledger via /api/dashboard/* and renders the
 * hero-stats strip, the savings-over-time area chart, per-device workload
 * stripe, recent-encodes / top-savings tables, codec mix donut, and per-node
 * throughput bars. All charts are hand-rolled SVG so they pick up the dark
 * theme variables directly — no chart library to fight on color matching.
 *
 * SignalR's `EncodeHistoryAdded` event refreshes the dashboard in place
 * whenever a new row is appended, so the page stays live without polling.
 */

import { escapeHtml }       from '../utils/dom.js';
import { ConnectionStatus } from '../core/connection-status.js';
import { PauseControl }     from '../queue/pause-control.js';

// ---------------------------------------------------------------------------
// Color palette (matches site.css CSS variables)
// ---------------------------------------------------------------------------

/** Per-device colors used everywhere — chips, donut segments, stripes. */
const DEVICE_COLORS = Object.freeze({
    nvidia:  '#76b900',
    intel:   '#0071c5',
    amd:     '#ed1c24',
    apple:   '#cbd5e1',
    cpu:     '#8b5cf6',
    unknown: '#6b7280',
});

/** Output codec colors for the donut. */
const CODEC_COLORS = Object.freeze({
    h265:    '#8b5cf6',
    hevc:    '#8b5cf6',
    h264:    '#0ea5e9',
    avc:     '#0ea5e9',
    av1:     '#10b981',
    unknown: '#6b7280',
});

const cssVar = (name, fallback) =>
    getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;


// ---------------------------------------------------------------------------
// Formatters
// ---------------------------------------------------------------------------

/** Compact bytes formatter (e.g. "3.2 GB"). Empty when value <= 0. */
function fmtBytes(bytes) {
    if (!bytes || bytes <= 0) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
    let i = 0;
    let v = bytes;
    while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
    return `${v < 10 ? v.toFixed(2) : v < 100 ? v.toFixed(1) : Math.round(v)} ${units[i]}`;
}

/** Compact duration formatter for encode-seconds totals (e.g. "12h 34m"). */
function fmtHoursMinutes(seconds) {
    if (!seconds || seconds <= 0) return '0m';
    const totalMinutes = Math.floor(seconds / 60);
    const h = Math.floor(totalMinutes / 60);
    const m = totalMinutes % 60;
    if (h === 0) return `${m}m`;
    if (h < 100) return `${h}h ${m}m`;
    return `${h}h`;
}

/** Realtime multiplier for an encode (content-seconds / encode-seconds), e.g. "2.4×". */
function fmtRealtime(content, encode) {
    if (!content || !encode || encode <= 0) return '—';
    const x = content / encode;
    return `${x < 10 ? x.toFixed(1) : Math.round(x)}×`;
}

/** Relative time (e.g. "2 minutes ago"). */
function fmtRelative(ts) {
    const t = new Date(ts);
    const seconds = Math.floor((Date.now() - t.getTime()) / 1000);
    if (seconds < 60)        return `${seconds}s ago`;
    if (seconds < 3600)      return `${Math.floor(seconds / 60)}m ago`;
    if (seconds < 86400)     return `${Math.floor(seconds / 3600)}h ago`;
    if (seconds < 86400 * 7) return `${Math.floor(seconds / 86400)}d ago`;
    return t.toLocaleDateString();
}

/** Percent saved given (original, encoded). */
function fmtRatio(original, encoded) {
    if (!original || original <= 0) return '—';
    const saved = original - (encoded || 0);
    return `${Math.max(0, Math.round((saved / original) * 100))}%`;
}


// ---------------------------------------------------------------------------
// SVG primitives
// ---------------------------------------------------------------------------

const NS = 'http://www.w3.org/2000/svg';
const el = (name, attrs = {}, children = []) => {
    const node = document.createElementNS(NS, name);
    for (const [k, v] of Object.entries(attrs)) {
        if (v == null) continue;
        node.setAttribute(k, v);
    }
    for (const child of children) node.appendChild(child);
    return node;
};


// ---------------------------------------------------------------------------
// Sparkline (hero cards)
// ---------------------------------------------------------------------------

/**
 * Renders a tiny area sparkline into the given SVG element.
 * viewBox is fixed at 120×32 so the host can size it freely with CSS.
 */
function renderSpark(svg, values, color) {
    while (svg.firstChild) svg.removeChild(svg.firstChild);
    if (!values || values.length === 0) return;

    const W = 120, H = 32;
    const max = Math.max(1, ...values);
    const step = values.length > 1 ? W / (values.length - 1) : 0;

    const points = values.map((v, i) => {
        const x = i * step;
        const y = H - (v / max) * (H - 4) - 2;
        return [x, y];
    });

    // Filled gradient area
    const gid = `g_${Math.random().toString(36).slice(2)}`;
    const defs = el('defs');
    const grad = el('linearGradient', { id: gid, x1: 0, x2: 0, y1: 0, y2: 1 });
    grad.appendChild(el('stop', { offset: '0%',   'stop-color': color, 'stop-opacity': '0.55' }));
    grad.appendChild(el('stop', { offset: '100%', 'stop-color': color, 'stop-opacity': '0' }));
    defs.appendChild(grad);
    svg.appendChild(defs);

    const areaPath = `M0,${H} ${points.map(([x, y]) => `L${x},${y}`).join(' ')} L${W},${H} Z`;
    svg.appendChild(el('path', { d: areaPath, fill: `url(#${gid})` }));

    const linePath = `M${points.map(([x, y]) => `${x},${y}`).join(' L')}`;
    svg.appendChild(el('path', { d: linePath, fill: 'none', stroke: color, 'stroke-width': '1.5' }));
}


// ---------------------------------------------------------------------------
// Savings-over-time area chart
// ---------------------------------------------------------------------------

/**
 * Hand-rolled area chart for the savings time-series. Points are bytes-saved
 * per UTC day; an animated path stroke keeps the chart feeling alive even
 * when the data is static.
 */
function renderSavingsChart(svg, daily) {
    while (svg.firstChild) svg.removeChild(svg.firstChild);
    if (!daily || daily.length === 0) return;

    const W = 800, H = 280;
    const padL = 56, padR = 16, padT = 16, padB = 32;
    const innerW = W - padL - padR;
    const innerH = H - padT - padB;

    const values = daily.map(d => d.bytesSaved || 0);
    const max = Math.max(1, ...values);

    // Round max up to a clean tick boundary so the y-axis labels look chosen, not arbitrary.
    const niceMax = (() => {
        const exp = Math.pow(10, Math.floor(Math.log10(max)));
        const m = max / exp;
        const rounded = m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10;
        return rounded * exp;
    })();

    const xFor = i => padL + (daily.length > 1 ? (i / (daily.length - 1)) * innerW : 0);
    const yFor = v => padT + innerH - (v / niceMax) * innerH;

    // Gridlines + Y labels
    const ticks = 4;
    for (let t = 0; t <= ticks; t++) {
        const y = padT + (t / ticks) * innerH;
        const value = niceMax * (1 - t / ticks);
        svg.appendChild(el('line', {
            x1: padL, x2: W - padR, y1: y, y2: y,
            stroke: cssVar('--border-color', '#2a2a2e'), 'stroke-width': 1,
            'stroke-dasharray': t === ticks ? '' : '2,3',
        }));
        svg.appendChild(el('text', {
            x: padL - 8, y: y + 4,
            'text-anchor': 'end',
            fill: cssVar('--text-muted', '#5c5c66'),
            'font-size': 10, 'font-family': 'system-ui',
        })).textContent = fmtBytes(value);
    }

    // X labels — first, last, and ~3 in between
    const xLabelIndices = daily.length <= 1
        ? [0]
        : [0, Math.floor(daily.length * 0.25), Math.floor(daily.length * 0.5), Math.floor(daily.length * 0.75), daily.length - 1];
    for (const i of xLabelIndices) {
        const date = new Date(daily[i].day);
        const label = date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
        svg.appendChild(el('text', {
            x: xFor(i), y: H - padB + 18,
            'text-anchor': 'middle',
            fill: cssVar('--text-muted', '#5c5c66'),
            'font-size': 10, 'font-family': 'system-ui',
        })).textContent = label;
    }

    // Gradient under the area
    const primary = cssVar('--primary', '#8b5cf6');
    const gid = 'savings-gradient';
    const defs = el('defs');
    const grad = el('linearGradient', { id: gid, x1: 0, x2: 0, y1: 0, y2: 1 });
    grad.appendChild(el('stop', { offset: '0%',   'stop-color': primary, 'stop-opacity': '0.4' }));
    grad.appendChild(el('stop', { offset: '100%', 'stop-color': primary, 'stop-opacity': '0' }));
    defs.appendChild(grad);
    svg.appendChild(defs);

    // Area + line built from a smoothed catmull-rom-ish path. With single-day
    // datasets we degenerate to a flat line at the value.
    if (daily.length === 1) {
        const y = yFor(values[0]);
        svg.appendChild(el('rect', { x: padL, y: y, width: innerW, height: padT + innerH - y, fill: `url(#${gid})` }));
        svg.appendChild(el('line', { x1: padL, x2: W - padR, y1: y, y2: y, stroke: primary, 'stroke-width': 2 }));
    } else {
        const linePts = daily.map((d, i) => [xFor(i), yFor(d.bytesSaved || 0)]);
        const linePath = `M${linePts.map(([x, y]) => `${x},${y}`).join(' L')}`;
        const areaPath = `${linePath} L${xFor(daily.length - 1)},${padT + innerH} L${padL},${padT + innerH} Z`;

        svg.appendChild(el('path', { d: areaPath, fill: `url(#${gid})` }));
        svg.appendChild(el('path', { d: linePath, fill: 'none', stroke: primary, 'stroke-width': 2 }));

        // Highlight the most recent day
        const last = linePts[linePts.length - 1];
        svg.appendChild(el('circle', { cx: last[0], cy: last[1], r: 4, fill: primary }));
        svg.appendChild(el('circle', { cx: last[0], cy: last[1], r: 7, fill: primary, 'fill-opacity': '0.25' }));
    }

    // Hover layer — invisible rectangles over each day publish a tooltip.
    const tooltip = ensureTooltip();
    daily.forEach((d, i) => {
        const w = innerW / daily.length;
        const x = xFor(i) - w / 2;
        const hit = el('rect', {
            x, y: padT, width: w, height: innerH, fill: 'transparent',
        });
        hit.addEventListener('mouseenter', e => {
            const date = new Date(d.day).toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
            tooltip.innerHTML = `<strong>${date}</strong><br>` +
                `Saved: <span style="color: ${primary}">${fmtBytes(d.bytesSaved)}</span><br>` +
                `Files: ${d.encodes}<br>` +
                `Encode time: ${fmtHoursMinutes(d.encodeSeconds)}`;
            tooltip.style.display = 'block';
            const rect = svg.getBoundingClientRect();
            tooltip.style.left = `${rect.left + window.scrollX + (xFor(i) / W) * rect.width}px`;
            tooltip.style.top  = `${rect.top  + window.scrollY + (yFor(d.bytesSaved || 0) / H) * rect.height - 60}px`;
        });
        hit.addEventListener('mouseleave', () => { tooltip.style.display = 'none'; });
        svg.appendChild(hit);
    });
}

/** Lazily creates the shared tooltip element. */
function ensureTooltip() {
    let tip = document.getElementById('dashTooltip');
    if (!tip) {
        tip = document.createElement('div');
        tip.id = 'dashTooltip';
        tip.className = 'dash-tooltip';
        document.body.appendChild(tip);
    }
    return tip;
}


// ---------------------------------------------------------------------------
// Device workload (per-device bars)
// ---------------------------------------------------------------------------

function renderDeviceUtil(container, devices) {
    container.innerHTML = '';
    if (!devices || devices.length === 0) {
        container.innerHTML = '<div class="text-muted small text-center py-4"><em>No encodes yet in this window.</em></div>';
        return;
    }

    const totalSeconds = devices.reduce((s, d) => s + (d.encodeSeconds || 0), 0) || 1;

    for (const d of devices) {
        const color = DEVICE_COLORS[d.deviceId] || DEVICE_COLORS.unknown;
        const pct = ((d.encodeSeconds || 0) / totalSeconds) * 100;

        const row = document.createElement('div');
        row.className = 'device-row';
        row.innerHTML = `
            <div class="device-row-head">
                <span class="device-name"><span class="dot" style="background: ${color}"></span>${escapeHtml(d.deviceId.toUpperCase())}</span>
                <span class="device-stats">
                    ${d.encodes} files &middot;
                    ${fmtHoursMinutes(d.encodeSeconds)} &middot;
                    ${fmtBytes(d.bytesSaved)} saved &middot;
                    ${fmtRealtime(d.contentSeconds, d.encodeSeconds)} realtime
                </span>
            </div>
            <div class="device-bar">
                <div class="device-bar-fill" style="width: ${pct.toFixed(1)}%; background: linear-gradient(90deg, ${color}, ${color}aa)"></div>
            </div>`;
        container.appendChild(row);
    }
}


// ---------------------------------------------------------------------------
// Codec donut
// ---------------------------------------------------------------------------

function renderCodecDonut(svg, legend, codecs) {
    while (svg.firstChild) svg.removeChild(svg.firstChild);
    legend.innerHTML = '';
    if (!codecs || codecs.length === 0) {
        legend.innerHTML = '<div class="text-muted small"><em>No encodes yet.</em></div>';
        return;
    }

    const total = codecs.reduce((s, c) => s + (c.encodes || 0), 0);
    if (total === 0) return;

    const cx = 100, cy = 100, r = 80, innerR = 56;
    let angle = -Math.PI / 2;

    for (const c of codecs) {
        const sliceFrac = c.encodes / total;
        if (sliceFrac <= 0) continue;
        const next = angle + sliceFrac * Math.PI * 2;
        const color = CODEC_COLORS[c.codec.toLowerCase()] || CODEC_COLORS.unknown;

        const x0 = cx + Math.cos(angle) * r;
        const y0 = cy + Math.sin(angle) * r;
        const x1 = cx + Math.cos(next)  * r;
        const y1 = cy + Math.sin(next)  * r;
        const ix1 = cx + Math.cos(next)  * innerR;
        const iy1 = cy + Math.sin(next)  * innerR;
        const ix0 = cx + Math.cos(angle) * innerR;
        const iy0 = cy + Math.sin(angle) * innerR;
        const large = sliceFrac > 0.5 ? 1 : 0;

        const d = `
            M ${x0} ${y0}
            A ${r} ${r} 0 ${large} 1 ${x1} ${y1}
            L ${ix1} ${iy1}
            A ${innerR} ${innerR} 0 ${large} 0 ${ix0} ${iy0}
            Z`;
        svg.appendChild(el('path', { d, fill: color, 'fill-opacity': '0.92' }));
        angle = next;
    }

    // Center label
    svg.appendChild(el('text', {
        x: cx, y: cy - 4,
        'text-anchor': 'middle',
        fill: cssVar('--text-primary', '#e4e4e7'),
        'font-size': 24, 'font-weight': 600, 'font-family': 'system-ui',
    })).textContent = total;
    svg.appendChild(el('text', {
        x: cx, y: cy + 16,
        'text-anchor': 'middle',
        fill: cssVar('--text-muted', '#5c5c66'),
        'font-size': 11, 'font-family': 'system-ui',
    })).textContent = total === 1 ? 'encode' : 'encodes';

    // Legend
    for (const c of codecs) {
        const color = CODEC_COLORS[c.codec.toLowerCase()] || CODEC_COLORS.unknown;
        const pct = (c.encodes / total) * 100;
        const item = document.createElement('div');
        item.className = 'codec-legend-item';
        item.innerHTML = `
            <span class="dot" style="background: ${color}"></span>
            <strong>${escapeHtml(c.codec.toUpperCase())}</strong>
            <span class="codec-legend-pct">${pct.toFixed(0)}%</span>
            <span class="codec-legend-sub">${c.encodes} files &middot; ${fmtBytes(c.bytesSaved)}</span>`;
        legend.appendChild(item);
    }
}


// ---------------------------------------------------------------------------
// Per-node throughput
// ---------------------------------------------------------------------------

function renderNodeThroughput(container, nodes) {
    container.innerHTML = '';
    if (!nodes || nodes.length === 0) {
        container.innerHTML = '<div class="text-muted small text-center py-4"><em>No encodes yet in this window.</em></div>';
        return;
    }
    const max = Math.max(...nodes.map(n => n.encodes || 0));

    for (const n of nodes) {
        const pct = max > 0 ? (n.encodes / max) * 100 : 0;
        const row = document.createElement('div');
        row.className = 'node-row';
        row.innerHTML = `
            <div class="node-row-head">
                <span class="node-name">${escapeHtml(n.hostname || n.nodeId || 'unknown')}</span>
                <span class="node-stats">${n.encodes} files &middot; ${fmtBytes(n.bytesSaved)} &middot; ${fmtHoursMinutes(n.encodeSeconds)}</span>
            </div>
            <div class="node-bar">
                <div class="node-bar-fill" style="width: ${pct.toFixed(1)}%"></div>
            </div>`;
        container.appendChild(row);
    }
}


// ---------------------------------------------------------------------------
// Tables (recent + top savings)
// ---------------------------------------------------------------------------

function renderRecentTable(rows) {
    const tbody = document.querySelector('#recentTable tbody');
    if (!tbody) return;
    if (!rows || rows.length === 0) {
        tbody.innerHTML = '<tr><td colspan="6" class="text-center text-muted py-4"><em>No encodes recorded yet.</em></td></tr>';
        return;
    }
    tbody.innerHTML = rows.map(r => {
        const color = DEVICE_COLORS[r.deviceId] || DEVICE_COLORS.unknown;
        const codecBefore = (r.originalCodec || '?').toUpperCase();
        const codecAfter  = (r.encodedCodec  || '?').toUpperCase();
        const savedColor  = r.bytesSaved > 0 ? 'var(--success)' : 'var(--text-muted)';
        return `
            <tr>
                <td class="dash-file">
                    <span class="dash-fileicon"><i class="fas fa-film"></i></span>
                    <div class="dash-filename" title="${escapeHtml(r.fileName)}">${escapeHtml(r.fileName)}</div>
                </td>
                <td class="dash-codec">
                    <span class="codec-pill">${escapeHtml(codecBefore)}</span>
                    <i class="fas fa-arrow-right text-muted dash-arrow"></i>
                    <span class="codec-pill codec-pill-after">${escapeHtml(codecAfter)}</span>
                </td>
                <td class="dash-size">
                    <div>${fmtBytes(r.originalSizeBytes)}</div>
                    <div class="text-muted small">→ ${fmtBytes(r.encodedSizeBytes || r.originalSizeBytes)}</div>
                </td>
                <td class="dash-saved" style="color: ${savedColor}">
                    <strong>${fmtBytes(r.bytesSaved)}</strong>
                    <div class="text-muted small">${fmtRatio(r.originalSizeBytes, r.encodedSizeBytes)}</div>
                </td>
                <td>
                    <span class="device-chip" style="--c: ${color}">${escapeHtml((r.deviceId || 'unknown').toUpperCase())}</span>
                </td>
                <td class="dash-when">${fmtRelative(r.completedAt)}</td>
            </tr>`;
    }).join('');
}

function renderTopSavingsTable(rows) {
    const tbody = document.querySelector('#topSavingsTable tbody');
    if (!tbody) return;
    if (!rows || rows.length === 0) {
        tbody.innerHTML = '<tr><td colspan="3" class="text-center text-muted py-4"><em>No encodes recorded yet.</em></td></tr>';
        return;
    }
    tbody.innerHTML = rows.map(r => `
        <tr>
            <td class="dash-file">
                <span class="dash-fileicon"><i class="fas fa-film"></i></span>
                <div class="dash-filename" title="${escapeHtml(r.fileName)}">${escapeHtml(r.fileName)}</div>
            </td>
            <td class="dash-saved" style="color: var(--success)">
                <strong>${fmtBytes(r.bytesSaved)}</strong>
                <div class="text-muted small">${fmtBytes(r.originalSizeBytes)} → ${fmtBytes(r.encodedSizeBytes)}</div>
            </td>
            <td>
                <span class="ratio-pill">${fmtRatio(r.originalSizeBytes, r.encodedSizeBytes)}</span>
            </td>
        </tr>`).join('');
}


// ---------------------------------------------------------------------------
// Hero strip
// ---------------------------------------------------------------------------

function renderHero(summary, daily) {
    document.getElementById('heroBytesSaved').textContent = fmtBytes(summary.totalBytesSaved);
    const original = summary.totalOriginalBytes || 1;
    const ratio = Math.max(0, Math.round((summary.totalBytesSaved / original) * 100));
    document.getElementById('heroBytesSavedSub').textContent =
        `from ${fmtBytes(summary.totalOriginalBytes)} processed`;

    document.getElementById('heroFiles').textContent = summary.totalEncodes.toLocaleString();
    const skipped = summary.noSavingsEncodes || 0;
    document.getElementById('heroFilesSub').textContent =
        skipped > 0 ? `${skipped} no-savings, ${summary.fourKEncodes} were 4K` : `${summary.fourKEncodes} were 4K`;

    document.getElementById('heroHours').textContent = fmtHoursMinutes(summary.totalEncodeSeconds);
    document.getElementById('heroHoursSub').textContent =
        `${fmtRealtime(summary.totalContentSeconds, summary.totalEncodeSeconds)} realtime average`;

    document.getElementById('heroRatio').textContent = `${ratio}%`;
    document.getElementById('heroRatioSub').textContent =
        summary.totalEncodes > 0 ? `over ${summary.totalEncodes.toLocaleString()} encodes` : ' ';

    // Sparklines from the daily data — subtle reinforcement of the hero number.
    renderSpark(document.getElementById('sparkBytesSaved'), daily.map(d => d.bytesSaved), cssVar('--primary', '#8b5cf6'));
    renderSpark(document.getElementById('sparkFiles'),      daily.map(d => d.encodes),    cssVar('--info-color', '#8b5cf6'));
    renderSpark(document.getElementById('sparkHours'),      daily.map(d => d.encodeSeconds / 3600), cssVar('--warning', '#f59e0b'));

    const ratioByDay = daily.map(d => d.originalBytes > 0 ? (d.bytesSaved / d.originalBytes) * 100 : 0);
    renderSpark(document.getElementById('sparkRatio'), ratioByDay, cssVar('--success', '#10b981'));
}


// ---------------------------------------------------------------------------
// Top-level loader
// ---------------------------------------------------------------------------

async function fetchJson(url) {
    const r = await fetch(url, { headers: { Accept: 'application/json' } });
    if (!r.ok) throw new Error(`${url}: ${r.status}`);
    return r.json();
}

let currentRange = 30;
let refreshTimer = null;

async function refresh() {
    const days = currentRange;
    document.getElementById('savingsChartCaption').textContent = `last ${days} days`;
    try {
        const [summary, daily, devices, codecs, nodes, recent, top] = await Promise.all([
            fetchJson('/api/dashboard/summary'),
            fetchJson(`/api/dashboard/savings-over-time?days=${days}`),
            fetchJson(`/api/dashboard/device-utilization?days=${days}`),
            fetchJson(`/api/dashboard/codec-mix?days=${days}`),
            fetchJson(`/api/dashboard/node-throughput?days=${days}`),
            fetchJson('/api/dashboard/recent?limit=25'),
            fetchJson(`/api/dashboard/top-savings?limit=10&days=${days}`),
        ]);

        renderHero(summary, daily);
        renderSavingsChart(document.getElementById('savingsChart'), daily);
        renderDeviceUtil(document.getElementById('deviceUtilContainer'), devices);
        renderCodecDonut(document.getElementById('codecDonut'), document.getElementById('codecLegend'), codecs);
        renderNodeThroughput(document.getElementById('nodeThroughputContainer'), nodes);
        renderRecentTable(recent);
        renderTopSavingsTable(top);
        document.getElementById('recentCount').textContent =
            `${recent.length} most recent`;
    } catch (err) {
        console.error('Dashboard refresh failed', err);
    }
}


// ---------------------------------------------------------------------------
// Wire up range buttons + SignalR live updates
// ---------------------------------------------------------------------------

document.addEventListener('DOMContentLoaded', () => {
    document.querySelectorAll('.range-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('.range-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            currentRange = parseInt(btn.dataset.range, 10) || 30;
            refresh();
        });
    });

    // Wire the navbar's connection dot and pause button on this page too —
    // they live in the shared layout so every page gets them, but only the
    // queue page ran the bootstrap that hooked them up. Without this, the
    // dashboard's nav stayed at "orange dot / blank text / unbound pause
    // button" because no one was listening to SignalR's lifecycle here.
    const connectionStatus = new ConnectionStatus();
    const pauseControl     = new PauseControl();
    pauseControl.init();   // fetches authoritative state and wires click

    // The dashboard's live feed listens to the same hub the queue page uses.
    // We hook into the SignalR client provided by the global signalR.min.js
    // bundle and forward lifecycle to the indicator + pause control — so
    // pause changes from the queue page (or another tab) show here too.
    if (window.signalR) {
        try {
            const conn = new window.signalR.HubConnectionBuilder()
                .withUrl('/transcodingHub')
                .withAutomaticReconnect()
                .build();

            conn.onreconnecting(() => connectionStatus.setDisconnected());
            conn.onreconnected (() => connectionStatus.setConnected());
            conn.onclose       (() => connectionStatus.setDisconnected());

            conn.on('EncodeHistoryAdded', () => {
                if (refreshTimer) clearTimeout(refreshTimer);
                refreshTimer = setTimeout(refresh, 600);
            });
            conn.on('ClusterNodePaused', (paused) => pauseControl.setFromRemote(paused));

            conn.start()
                .then(() => connectionStatus.setConnected())
                .catch(err => {
                    console.warn('Dashboard SignalR connect failed:', err);
                    connectionStatus.setDisconnected();
                });
        } catch (err) {
            console.warn('Dashboard SignalR setup failed:', err);
            connectionStatus.setDisconnected();
        }
    } else {
        connectionStatus.setDisconnected();
    }

    refresh();
});
