/**
 * Networking settings panel.
 *
 * Loads and saves cluster transfer-throttling config (concurrency caps,
 * bandwidth caps, chunk size) via {@link networkingApi}. Master-only — the
 * tab is hidden on slave nodes via the data-role-visible attribute.
 */

import { networkingApi } from '../../api.js';

const FIELDS = [
    ['MaxConcurrentUploads',           'NetMaxConcurrentUploads'],
    ['MaxConcurrentUploadsPerNode',    'NetMaxConcurrentUploadsPerNode'],
    ['MaxConcurrentDownloads',         'NetMaxConcurrentDownloads'],
    ['MaxConcurrentDownloadsPerNode',  'NetMaxConcurrentDownloadsPerNode'],
    ['MaxUploadMBps',                  'NetMaxUploadMBps'],
    ['MaxUploadMBpsPerNode',           'NetMaxUploadMBpsPerNode'],
    ['MaxDownloadMBps',                'NetMaxDownloadMBps'],
    ['MaxDownloadMBpsPerNode',         'NetMaxDownloadMBpsPerNode'],
    ['ChunkSizeMB',                    'NetChunkSizeMB'],
];

/**
 * Reads a numeric input by id, defaulting to 0 on missing/invalid and
 * clamping to the input's own min/max attributes. Without the clamp, a
 * cleared chunk-size field saved 0 (below the documented 4 MB minimum and
 * rejected server-side), and out-of-range typed values 400'd on save.
 */
function readNum(id) {
    const el = document.getElementById(id);
    if (!el) return 0;
    let n = Number.parseInt(el.value, 10);
    if (!Number.isFinite(n)) n = Number.parseInt(el.min, 10) || 0;
    const min = Number.parseInt(el.min, 10);
    const max = Number.parseInt(el.max, 10);
    if (Number.isFinite(min)) n = Math.max(min, n);
    if (Number.isFinite(max)) n = Math.min(max, n);
    return n;
}

/** Writes a numeric value into an input. */
function writeNum(id, v) {
    const el = document.getElementById(id);
    if (!el) return;
    el.value = (v ?? 0).toString();
}

/**
 * Initialise the Networking settings panel: hooks Save, populates fields
 * from the server, and binds the save button. Safe to call once per page
 * load. Idempotent — additional calls are ignored.
 *
 * @param {string} prefix Optional form-field id prefix (default `settings`).
 */
export function initNetworkingPanel(prefix = 'settings') {
    const saveBtn = document.getElementById(`${prefix}NetSaveBtn`);
    if (!saveBtn || saveBtn.dataset.bound === '1') return;
    saveBtn.dataset.bound = '1';

    // Load existing config on first activation. We could lazy-load only when
    // the tab opens, but the payload is tiny — fetching at panel init keeps
    // the code simple and fields populated for power-users who tab through.
    networkingApi.getConfig()
        .then(cfg => {
            for (const [serverKey, fieldKey] of FIELDS) {
                writeNum(`${prefix}${fieldKey}`, cfg?.[lc(serverKey)] ?? cfg?.[serverKey]);
            }
        })
        .catch(err => {
            console.warn('Networking: failed to load config', err);
        });

    saveBtn.addEventListener('click', async () => {
        const status = document.getElementById(`${prefix}NetSaveStatus`);
        const cfg = {};
        for (const [serverKey, fieldKey] of FIELDS) {
            cfg[serverKey] = readNum(`${prefix}${fieldKey}`);
        }

        saveBtn.disabled = true;
        if (status) {
            status.textContent = 'Saving…';
            status.classList.remove('text-danger', 'text-success');
        }
        try {
            await networkingApi.saveConfig(cfg);
            if (status) {
                status.textContent = 'Saved.';
                status.classList.add('text-success');
            }
        } catch (err) {
            const msg = err?.body?.error || err?.message || 'Save failed';
            if (status) {
                status.textContent = msg;
                status.classList.add('text-danger');
            }
        } finally {
            saveBtn.disabled = false;
        }
    });
}

/**
 * Camel-case the first letter of a PascalCase server key — `getJson`
 * deserialises with PascalCase from System.Text.Json's default policy on
 * `[ApiController]` returns, but if the server is using camelCase via a
 * future policy the lookup still resolves. Cheap belt-and-suspenders.
 */
function lc(s) {
    return s ? s.charAt(0).toLowerCase() + s.slice(1) : s;
}
