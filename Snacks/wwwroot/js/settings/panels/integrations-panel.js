/**
 * Integrations settings panel (Plex / Jellyfin / Sonarr / Radarr).
 *
 * Reads/writes the integration config via {@link integrationsApi} and
 * exposes a per-provider "Test" button that pings the backend to confirm
 * the supplied URL + credential are valid.
 */

import { integrationsApi } from '../../api.js';


// ---------------------------------------------------------------------------
// DOM helpers
// ---------------------------------------------------------------------------

/** Reads an input's value, coercing missing elements to `''`. */
function val(id) {
    return document.getElementById(id)?.value || '';
}

/** Reads a checkbox's state, coercing missing elements to `false`. */
function chk(id) {
    return !!document.getElementById(id)?.checked;
}

/** Writes `v` into an input (value or checked, depending on type). */
function setVal(id, v) {
    const el = document.getElementById(id);
    if (!el) return;

    if (el.type === 'checkbox') el.checked = !!v;
    else                        el.value   = v ?? '';
}


// ---------------------------------------------------------------------------
// Read / write integration config
// ---------------------------------------------------------------------------

/**
 * Gathers the form state into the shape expected by
 * `POST /api/integrations/config`.
 */
function buildConfig() {
    return {
        plex: {
            enabled:          chk('plexEnabled'),
            baseUrl:          val('plexBaseUrl'),
            token:            val('plexToken'),
            rescanOnComplete: chk('plexRescan'),
        },
        jellyfin: {
            enabled:          chk('jellyfinEnabled'),
            baseUrl:          val('jellyfinBaseUrl'),
            token:            val('jellyfinToken'),
            rescanOnComplete: chk('jellyfinRescan'),
        },
        sonarr: {
            enabled: chk('sonarrEnabled'),
            baseUrl: val('sonarrBaseUrl'),
            apiKey:  val('sonarrApiKey'),
        },
        radarr: {
            enabled: chk('radarrEnabled'),
            baseUrl: val('radarrBaseUrl'),
            apiKey:  val('radarrApiKey'),
        },
    };
}

/**
 * Populates the form from the persisted config. Silent on failure — the
 * endpoint is auth-gated, so we shouldn't surface errors on initial load
 * for anonymous users.
 */
async function load() {
    try {
        const cfg = await integrationsApi.getConfig();

        setVal('plexEnabled',     cfg.plex?.enabled);
        setVal('plexBaseUrl',     cfg.plex?.baseUrl);
        setVal('plexToken',       cfg.plex?.token);
        setVal('plexRescan',      cfg.plex?.rescanOnComplete);

        setVal('jellyfinEnabled', cfg.jellyfin?.enabled);
        setVal('jellyfinBaseUrl', cfg.jellyfin?.baseUrl);
        setVal('jellyfinToken',   cfg.jellyfin?.token);
        setVal('jellyfinRescan',  cfg.jellyfin?.rescanOnComplete);

        setVal('sonarrEnabled',   cfg.sonarr?.enabled);
        setVal('sonarrBaseUrl',   cfg.sonarr?.baseUrl);
        setVal('sonarrApiKey',    cfg.sonarr?.apiKey);

        setVal('radarrEnabled',   cfg.radarr?.enabled);
        setVal('radarrBaseUrl',   cfg.radarr?.baseUrl);
        setVal('radarrApiKey',    cfg.radarr?.apiKey);
    } catch { /* endpoint may be gated by auth */ }
}

/**
 * Persists the current form state.
 */
async function save() {
    try {
        await integrationsApi.saveConfig(buildConfig());
        showToast('Integrations saved', 'success');
    } catch (e) {
        showToast('Save failed: ' + e.message, 'danger');
    }
}


// ---------------------------------------------------------------------------
// Per-provider test
// ---------------------------------------------------------------------------

/**
 * Issues a test-connection call against the named provider and paints the
 * result into its per-row `<span class="small">` element.
 *
 * @param {'plex'|'jellyfin'|'sonarr'|'radarr'} service
 */
async function test(service) {
    const result = document.getElementById(`${service}TestResult`);
    result.textContent = 'Testing…';
    result.className   = 'small align-self-center text-muted';

    try {
        let data;
        switch (service) {
            case 'plex':
                data = await integrationsApi.testPlex(    val('plexBaseUrl'),     val('plexToken'));
                break;
            case 'jellyfin':
                data = await integrationsApi.testJellyfin(val('jellyfinBaseUrl'), val('jellyfinToken'));
                break;
            case 'sonarr':
                data = await integrationsApi.testSonarr(  val('sonarrBaseUrl'),   val('sonarrApiKey'));
                break;
            default: // radarr
                data = await integrationsApi.testRadarr(  val('radarrBaseUrl'),   val('radarrApiKey'));
                break;
        }

        result.textContent = data.message || (data.success ? 'OK' : 'Failed');
        result.className   = 'small align-self-center ' + (data.success ? 'text-success' : 'text-danger');
    } catch (e) {
        result.textContent = e.message;
        result.className   = 'small align-self-center text-danger';
    }
}


// ---------------------------------------------------------------------------
// Public entry points
// ---------------------------------------------------------------------------

/**
 * Wires the panel's DOM controls. Safe to call once at startup.
 */
export function initIntegrationsPanel() {
    document.getElementById('saveIntegrationConfig')?.addEventListener('click', save);
    document.getElementById('testPlex')    ?.addEventListener('click', () => test('plex'));
    document.getElementById('testJellyfin')?.addEventListener('click', () => test('jellyfin'));
    document.getElementById('testSonarr')  ?.addEventListener('click', () => test('sonarr'));
    document.getElementById('testRadarr')  ?.addEventListener('click', () => test('radarr'));
}

/** Lazy data load, invoked when the settings modal is first opened. */
export const loadIntegrationsPanel = load;
