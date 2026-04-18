/**
 * Notifications settings panel.
 *
 * Manages the list of notification destinations (webhooks / email) plus the
 * per-event toggles. Destinations live only in memory while the panel is
 * open; the "Save" button pushes the full list (plus event toggles) to the
 * server in one payload.
 */

import { notificationsApi } from '../../api.js';
import { escapeHtml }       from '../../utils/dom.js';


// ---------------------------------------------------------------------------
// Module state
// ---------------------------------------------------------------------------

/**
 * Current list of notification destinations.
 *
 * @type {Array<{ name?: string, type?: string, url: string, enabled?: boolean }>}
 */
let destinations = [];


// ---------------------------------------------------------------------------
// Rendering
// ---------------------------------------------------------------------------

/**
 * Rebuilds the destinations list from {@link destinations} and wires the
 * per-row toggle/remove/test buttons.
 */
function render() {
    const container = document.getElementById('notificationDestinations');
    if (!container) return;

    if (destinations.length === 0) {
        container.innerHTML = '<div class="text-muted text-center py-2"><small>No destinations configured</small></div>';
        return;
    }

    container.innerHTML = '';
    destinations.forEach((d, idx) => {
        const row = document.createElement('div');
        row.className = 'notification-dest-row';
        row.innerHTML = `
            <span class="badge bg-secondary">${escapeHtml(d.type || 'webhook')}</span>
            <div class="form-check form-switch m-0">
                <input class="form-check-input" type="checkbox" ${d.enabled !== false ? 'checked' : ''} data-idx="${idx}" data-toggle-dest>
            </div>
            <strong class="small">${escapeHtml(d.name || '')}</strong>
            <span class="dest-url text-muted">${escapeHtml(d.url)}</span>
            <button class="btn btn-sm btn-outline-primary" data-test-dest="${idx}" title="Send test"><i class="fas fa-vial"></i></button>
            <button class="btn btn-sm btn-outline-danger"  data-remove-dest="${idx}" title="Remove"><i class="fas fa-trash"></i></button>`;
        container.appendChild(row);
    });

    // Per-row event wiring.
    container.querySelectorAll('[data-toggle-dest]').forEach(el =>
        el.addEventListener('change', (e) => {
            destinations[+e.target.dataset.idx].enabled = e.target.checked;
        }));

    container.querySelectorAll('[data-remove-dest]').forEach(el =>
        el.addEventListener('click', (e) => {
            destinations.splice(+e.currentTarget.dataset.removeDest, 1);
            render();
        }));

    container.querySelectorAll('[data-test-dest]').forEach(el =>
        el.addEventListener('click', async (e) => {
            await testDestination(destinations[+e.currentTarget.dataset.testDest]);
        }));
}


// ---------------------------------------------------------------------------
// Read / write config
// ---------------------------------------------------------------------------

/**
 * Populates `destinations` and the per-event checkboxes from the server.
 * Silently ignores failures (the endpoint is auth-gated).
 */
async function load() {
    try {
        const cfg = await notificationsApi.getConfig();
        destinations = cfg.destinations || [];

        const ev  = cfg.events || {};
        const set = (id, val) => {
            const el = document.getElementById(id);
            if (el) el.checked = !!val;
        };

        // Sensible defaults for events the user is likely to want on by default.
        set('notifEventEncodeStarted',   ev.encodeStarted);
        set('notifEventEncodeCompleted', ev.encodeCompleted ?? true);
        set('notifEventEncodeFailed',    ev.encodeFailed    ?? true);
        set('notifEventScanCompleted',   ev.scanCompleted);
        set('notifEventNodeOffline',     ev.nodeOffline     ?? true);

        render();
    } catch { /* auth-gated */ }
}

/**
 * Persists destinations + event toggles in one POST.
 */
async function save() {
    const chk = (id) => !!document.getElementById(id)?.checked;

    const body = {
        destinations,
        events: {
            encodeStarted:   chk('notifEventEncodeStarted'),
            encodeCompleted: chk('notifEventEncodeCompleted'),
            encodeFailed:    chk('notifEventEncodeFailed'),
            scanCompleted:   chk('notifEventScanCompleted'),
            nodeOffline:     chk('notifEventNodeOffline'),
        },
    };

    try {
        await notificationsApi.saveConfig(body);
        showToast('Notifications saved', 'success');
    } catch (e) {
        showToast('Save failed: ' + e.message, 'danger');
    }
}


// ---------------------------------------------------------------------------
// Destination actions
// ---------------------------------------------------------------------------

/**
 * Sends a test payload to a single destination and surfaces the result via
 * a toast.
 *
 * @param {{ name?: string, type?: string, url: string, enabled?: boolean }} dest
 */
async function testDestination(dest) {
    try {
        const data = await notificationsApi.test(dest);
        const msg  = data.message || (data.success ? 'Test dispatched' : 'Test failed');
        showToast(msg, data.success ? 'success' : 'danger');
    } catch (e) {
        showToast('Test failed: ' + e.message, 'danger');
    }
}

/**
 * Appends a new destination from the "add new" form to the in-memory list.
 */
function addDestination() {
    const name = document.getElementById('newNotifName').value.trim();
    const type = document.getElementById('newNotifType').value;
    const url  = document.getElementById('newNotifUrl').value.trim();

    if (!url) {
        showToast('URL required', 'warning');
        return;
    }

    destinations.push({ name, type, url, enabled: true });

    document.getElementById('newNotifName').value = '';
    document.getElementById('newNotifUrl').value  = '';

    render();
}


// ---------------------------------------------------------------------------
// Public entry points
// ---------------------------------------------------------------------------

/**
 * Wires the panel's DOM controls. Safe to call once at startup.
 */
export function initNotificationsPanel() {
    document.getElementById('addNotifDestination')  ?.addEventListener('click', addDestination);
    document.getElementById('saveNotificationConfig')?.addEventListener('click', save);
}

/** Lazy data load, invoked when the settings modal is first opened. */
export const loadNotificationsPanel = load;
