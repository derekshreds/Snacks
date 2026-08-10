/**
 * Authentication settings panel.
 *
 * Lets the user enable/disable local auth and set/update the admin username
 * and password. The password field is intentionally blanked after every
 * load/save so it never holds a hashed or stale value.
 */

import { authApi } from '../../api.js';


// ---------------------------------------------------------------------------
// Read / write
// ---------------------------------------------------------------------------

/**
 * Populates the form from the persisted auth config. Silent on failure
 * because the page may legitimately be loaded pre-auth.
 */
async function load() {
    try {
        const cfg = await authApi.getConfig();

        document.getElementById('authEnabled').checked = !!cfg.enabled;
        document.getElementById('authUsername').value  = cfg.username || '';
        document.getElementById('authPassword').value  = '';

        document.getElementById('authEnabledHint').textContent = cfg.hasPassword
            ? 'A password is set. Leave the password field blank to keep it.'
            : 'No password set yet. Enter one below to enable sign-in.';

        const envHint = document.getElementById('envApiKeyHint');
        if (envHint) envHint.style.display = cfg.envApiKeySet ? '' : 'none';

        await Promise.all([loadApiKey(), loadEmbedConfig()]);
    } catch { /* auth may already gate this */ }
}

function renderEmbedUrl(token) {
    const input = document.getElementById('embedUrlValue');
    if (!input) return;
    input.value = token
        ? `${window.location.origin}/iframe/homarr?embedToken=${encodeURIComponent(token)}`
        : '';
}

async function loadEmbedConfig() {
    try {
        const data = await authApi.getEmbedConfig();
        renderEmbedUrl(data.embedToken || '');
        const origins = document.getElementById('iframeAllowedOrigins');
        if (origins) origins.value = (data.iframeAllowedOrigins || []).join('\n');
    } catch { /* gated pre-auth, same as the rest of the panel */ }
}

/**
 * Fetches the stored API key into the masked field. The field stays
 * type=password until the user clicks reveal.
 */
async function loadApiKey() {
    try {
        const data  = await authApi.getApiKey();
        const input = document.getElementById('apiKeyValue');
        if (input) input.value = data.apiKey || '';
    } catch { /* gated pre-auth, same as the rest of the panel */ }
}

/**
 * Persists the auth settings. A blank password means "keep the existing one."
 */
async function save() {
    const enabled  = document.getElementById('authEnabled').checked;
    const username = document.getElementById('authUsername').value.trim();
    const password = document.getElementById('authPassword').value;

    try {
        const data = await authApi.save(enabled, username, password || null);

        if (!data.success) {
            showToast(data.error || 'Save failed', 'danger');
            return;
        }

        // Auth just became (or stayed) required — force a login round-trip so
        // the user actually exercises the credentials they just configured.
        if (data.authRequired) {
            window.location.href = '/Auth/Login';
            return;
        }

        showToast('Auth settings saved', 'success');

        // Clear the password field and reload so the hint reflects the new state.
        document.getElementById('authPassword').value = '';
        load();
    } catch (e) {
        showToast('Save failed: ' + e.message, 'danger');
    }
}


// ---------------------------------------------------------------------------
// Sign-out
// ---------------------------------------------------------------------------

/**
 * Signs the user out and navigates to the login page. The navigation still
 * happens even if the logout request fails (e.g. the session expired).
 */
async function signOut() {
    try {
        await authApi.logout();
    } catch { /* navigation will still occur */ }
    window.location.href = '/Auth/Login';
}


// ---------------------------------------------------------------------------
// API key actions
// ---------------------------------------------------------------------------

async function generateApiKey() {
    try {
        const data  = await authApi.generateApiKey();
        const input = document.getElementById('apiKeyValue');
        if (input) {
            input.value = data.apiKey || '';
            input.type  = 'text'; // show the fresh key so it can be copied immediately
        }
        showToast('New API key generated — the old key no longer works', 'success');
    } catch (e) {
        showToast('Generate failed: ' + e.message, 'danger');
    }
}

async function deleteApiKey() {
    try {
        await authApi.deleteApiKey();
        const input = document.getElementById('apiKeyValue');
        if (input) input.value = '';
        showToast('API key removed', 'success');
    } catch (e) {
        showToast('Remove failed: ' + e.message, 'danger');
    }
}

function toggleApiKeyVisibility() {
    const input = document.getElementById('apiKeyValue');
    if (input) input.type = input.type === 'password' ? 'text' : 'password';
}

async function copyApiKey() {
    const input = document.getElementById('apiKeyValue');
    if (!input?.value) { showToast('No API key to copy', 'warning'); return; }
    try {
        await navigator.clipboard.writeText(input.value);
        showToast('API key copied', 'success');
    } catch {
        // Clipboard API needs a secure context — fall back to select-for-copy.
        input.type = 'text';
        input.select();
        showToast('Press Ctrl/Cmd+C to copy', 'info');
    }
}


// ---------------------------------------------------------------------------
// Scoped iframe access
// ---------------------------------------------------------------------------

async function generateEmbedToken() {
    try {
        const data = await authApi.generateEmbedToken();
        renderEmbedUrl(data.embedToken || '');
        const input = document.getElementById('embedUrlValue');
        if (input) input.type = 'text';
        showToast('New iframe URL generated — the old URL no longer works', 'success');
    } catch (e) {
        showToast('Generate failed: ' + e.message, 'danger');
    }
}

async function deleteEmbedToken() {
    try {
        await authApi.deleteEmbedToken();
        renderEmbedUrl('');
        showToast('Iframe token revoked', 'success');
    } catch (e) {
        showToast('Revoke failed: ' + e.message, 'danger');
    }
}

async function saveEmbedOrigins() {
    const value = document.getElementById('iframeAllowedOrigins')?.value || '';
    const origins = value.split(/\r?\n|,/).map(origin => origin.trim()).filter(Boolean);
    try {
        const data = await authApi.saveEmbedOrigins(origins);
        const input = document.getElementById('iframeAllowedOrigins');
        if (input) input.value = (data.iframeAllowedOrigins || []).join('\n');
        showToast('Iframe origins saved', 'success');
    } catch (e) {
        showToast('Save failed: ' + e.message, 'danger');
    }
}

function toggleEmbedUrlVisibility() {
    const input = document.getElementById('embedUrlValue');
    if (input) input.type = input.type === 'password' ? 'text' : 'password';
}

async function copyEmbedUrl() {
    const input = document.getElementById('embedUrlValue');
    if (!input?.value) { showToast('No iframe URL to copy', 'warning'); return; }
    try {
        await navigator.clipboard.writeText(input.value);
        showToast('Iframe URL copied', 'success');
    } catch {
        input.type = 'text';
        input.select();
        showToast('Press Ctrl/Cmd+C to copy', 'info');
    }
}


// ---------------------------------------------------------------------------
// Public entry points
// ---------------------------------------------------------------------------

/**
 * Wires the panel's DOM controls. Safe to call once at startup.
 */
export function initAuthPanel() {
    document.getElementById('saveAuthConfig')    ?.addEventListener('click', save);
    document.getElementById('signOutBtn')        ?.addEventListener('click', signOut);
    document.getElementById('generateApiKeyBtn') ?.addEventListener('click', generateApiKey);
    document.getElementById('deleteApiKeyBtn')   ?.addEventListener('click', deleteApiKey);
    document.getElementById('revealApiKeyBtn')   ?.addEventListener('click', toggleApiKeyVisibility);
    document.getElementById('copyApiKeyBtn')     ?.addEventListener('click', copyApiKey);
    document.getElementById('generateEmbedTokenBtn') ?.addEventListener('click', generateEmbedToken);
    document.getElementById('deleteEmbedTokenBtn')   ?.addEventListener('click', deleteEmbedToken);
    document.getElementById('saveEmbedOriginsBtn')   ?.addEventListener('click', saveEmbedOrigins);
    document.getElementById('revealEmbedUrlBtn')     ?.addEventListener('click', toggleEmbedUrlVisibility);
    document.getElementById('copyEmbedUrlBtn')       ?.addEventListener('click', copyEmbedUrl);
}

/** Lazy data load, invoked when the settings modal is first opened. */
export const loadAuthPanel = load;
