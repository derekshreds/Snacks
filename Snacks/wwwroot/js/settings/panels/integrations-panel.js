/**
 * Integrations settings panel (Plex / Jellyfin / Sonarr / Radarr / Lidarr / Luna).
 *
 * Reads/writes the integration config via {@link integrationsApi}, exposes a
 * per-provider "Test" button that pings the backend to confirm the supplied
 * URL + credential are valid, and manages the outbound Luna connection.
 */

import { integrationsApi }               from '../../api.js';
import { applyEnvLocks, addEnvLockNote } from '../env-locks.js';
import { formatLunaCapabilitySummary }   from '../luna-status.js';

let lunaServiceUrl = 'https://veryluna.com';


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
        lidarr: {
            enabled: chk('lidarrEnabled'),
            baseUrl: val('lidarrBaseUrl'),
            apiKey:  val('lidarrApiKey'),
        },
        tvdb: {
            enabled: chk('tvdbEnabled'),
            apiKey:  val('tvdbApiKey'),
            pin:     val('tvdbPin'),
        },
        tmdb: {
            enabled: chk('tmdbEnabled'),
            apiKey:  val('tmdbApiKey'),
        },
        luna: {
            enabled:             chk('lunaEnabled'),
            baseUrl:             lunaServiceUrl,
            allowLibraryReads:   chk('lunaAllowLibraryReads'),
            allowLibraryChanges: chk('lunaAllowLibraryChanges'),
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

        setVal('lidarrEnabled',   cfg.lidarr?.enabled);
        setVal('lidarrBaseUrl',   cfg.lidarr?.baseUrl);
        setVal('lidarrApiKey',    cfg.lidarr?.apiKey);

        setVal('tvdbEnabled',     cfg.tvdb?.enabled);
        setVal('tvdbApiKey',      cfg.tvdb?.apiKey);
        setVal('tvdbPin',         cfg.tvdb?.pin);

        setVal('tmdbEnabled',     cfg.tmdb?.enabled);
        setVal('tmdbApiKey',      cfg.tmdb?.apiKey);

        const officialLunaUrl = cfg._lunaOfficialBaseUrl || 'https://veryluna.com';
        const customLunaUrlAllowed = cfg._lunaCustomUrlAllowed === true;
        lunaServiceUrl = customLunaUrlAllowed ? (cfg.luna?.baseUrl || officialLunaUrl) : officialLunaUrl;
        setVal('lunaEnabled',             cfg.luna?.enabled);
        setVal('lunaAllowLibraryReads',   cfg.luna?.allowLibraryReads);
        setVal('lunaAllowLibraryChanges', cfg.luna?.allowLibraryChanges);
        const lunaServiceEndpoint = document.getElementById('lunaServiceEndpoint');
        if (lunaServiceEndpoint) {
            // The UI always advertises the official site. A private test URL is
            // process configuration, not a user-facing alternative endpoint.
            lunaServiceEndpoint.textContent = officialLunaUrl;
            lunaServiceEndpoint.href = officialLunaUrl;
        }
        applyLocks(cfg._envLocked);
        await loadLunaStatus();
    } catch { /* endpoint may be gated by auth */ }
}

/**
 * Locks controls driven by SNACKS_INTEG_* env vars. Paths arrive as
 * `section.property` (e.g. "plex.token"); ids follow `sectionProperty`
 * except the divergent rescan/token names mapped below.
 */
function applyLocks(lockedPaths) {
    const ID_MAP = {
        'plex.rescanOnComplete':     'plexRescan',
        'jellyfin.rescanOnComplete': 'jellyfinRescan',
    };
    const any = applyEnvLocks(lockedPaths, (path) => {
        const mapped = ID_MAP[path]
            ?? path.replace(/\.(\w)/, (_, c) => c.toUpperCase());
        return document.getElementById(mapped);
    });

    if (any) addEnvLockNote(document.getElementById('saveIntegrationConfig')?.parentElement);
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
// Luna connection
// ---------------------------------------------------------------------------

/**
 * Paints the connection badge, detail line, and disconnect button from a
 * token-free status payload returned by the Luna connector API.
 */
function renderLunaStatus(status) {
    const badge  = document.getElementById('lunaConnectionStatus');
    const detail = document.getElementById('lunaConnectionDetail');
    const email  = document.getElementById('lunaEmail');
    if (!badge || !detail) return;

    if (status.email && email && !email.value) email.value = status.email;

    badge.textContent = status.online ? 'Connected' : status.connected ? 'Waiting for Luna' : 'Not connected';
    badge.className = 'badge ' + (status.online ? 'text-bg-success' : status.connected ? 'text-bg-warning' : 'text-bg-secondary');

    detail.textContent = status.lastError
        ? status.lastError
        : status.connected
            ? formatLunaCapabilitySummary(status.capabilities)
            : 'Your password is never stored by Snacks.';
    detail.className = 'small ' + (status.lastError ? 'text-danger' : 'text-muted');
    const disconnect = document.getElementById('disconnectLuna');
    if (disconnect) disconnect.disabled = !status.connected;
}

/** Fetches live connector status; degrades to an "Unavailable" badge on failure. */
async function loadLunaStatus() {
    const badge = document.getElementById('lunaConnectionStatus');
    try {
        const status = await integrationsApi.getLunaStatus();
        renderLunaStatus(status);
    } catch {
        if (badge) {
            badge.textContent = 'Unavailable';
            badge.className = 'badge text-bg-secondary';
        }
    }
}

/**
 * Saves the panel (with Luna enabled), then exchanges the entered credentials
 * for a scoped connector session. The password field is cleared immediately —
 * it exists only for the single connect call.
 */
async function connectLuna() {
    const button = document.getElementById('connectLuna');
    const password = document.getElementById('lunaPassword');
    const passwordValue = val('lunaPassword');
    if (password) password.value = '';
    button.disabled = true;
    try {
        setVal('lunaEnabled', true);
        await integrationsApi.saveConfig(buildConfig());
        const status = await integrationsApi.connectLuna(lunaServiceUrl, val('lunaEmail'), passwordValue);
        renderLunaStatus(status);
        showToast('Snacks is connected to Luna', 'success');
    } catch (e) {
        showToast('Luna connection failed: ' + e.message, 'danger');
        await loadLunaStatus();
    } finally {
        button.disabled = false;
    }
}

/** Revokes the scoped Luna session and repaints the card from the fresh status. */
async function disconnectLuna() {
    const button = document.getElementById('disconnectLuna');
    button.disabled = true;
    try {
        const status = await integrationsApi.disconnectLuna();
        renderLunaStatus(status);
        showToast('Luna disconnected', 'success');
    } catch (e) {
        showToast('Could not disconnect Luna: ' + e.message, 'danger');
    } finally {
        button.disabled = false;
    }
}


// ---------------------------------------------------------------------------
// Per-provider test
// ---------------------------------------------------------------------------

/**
 * Issues a test-connection call against the named provider and paints the
 * result into its per-row `<span class="small">` element.
 *
 * @param {'plex'|'jellyfin'|'sonarr'|'radarr'|'lidarr'|'tvdb'|'tmdb'} service
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
            case 'radarr':
                data = await integrationsApi.testRadarr(  val('radarrBaseUrl'),   val('radarrApiKey'));
                break;
            case 'lidarr':
                data = await integrationsApi.testLidarr(  val('lidarrBaseUrl'),   val('lidarrApiKey'));
                break;
            case 'tvdb':
                data = await integrationsApi.testTvdb(    val('tvdbApiKey'),      val('tvdbPin'));
                break;
            default: // tmdb
                data = await integrationsApi.testTmdb(    val('tmdbApiKey'));
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
    document.getElementById('testLidarr')  ?.addEventListener('click', () => test('lidarr'));
    document.getElementById('testTvdb')    ?.addEventListener('click', () => test('tvdb'));
    document.getElementById('testTmdb')    ?.addEventListener('click', () => test('tmdb'));
    document.getElementById('connectLuna')   ?.addEventListener('click', connectLuna);
    document.getElementById('disconnectLuna')?.addEventListener('click', disconnectLuna);
}

/** Lazy data load, invoked when the settings modal is first opened. */
export const loadIntegrationsPanel = load;
