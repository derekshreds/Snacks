/**
 * Authentication settings panel.
 *
 * Lets the user enable/disable local auth and set/update the admin username
 * and password. The password field is intentionally blanked after every
 * load/save so it never holds a hashed or stale value.
 */

import { authApi } from '../../api.js';
import { showConfirmModal } from '../../utils/modal-controller.js';


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

        renderApiKeyState(!!cfg.hasApiKey);
    } catch { /* auth may already gate this */ }
}

// ---------------------------------------------------------------------------
// API key (external dashboards)
// ---------------------------------------------------------------------------

/** Updates the button labels and status hint to reflect whether a key exists. */
function renderApiKeyState(hasKey) {
    document.getElementById('generateApiKeyBtnLabel').textContent =
        hasKey ? 'Regenerate API key' : 'Generate API key';
    document.getElementById('revokeApiKeyBtn').style.display = hasKey ? '' : 'none';
    document.getElementById('apiKeyStatus').textContent =
        hasKey ? 'A key is configured.' : 'No key configured.';
    document.getElementById('apiKeyDisplayRow').style.display = 'none';
    document.getElementById('apiKeyValue').value = '';
}

async function generateApiKey() {
    const hadKey = document.getElementById('revokeApiKeyBtn').style.display !== 'none';
    if (hadKey) {
        const ok = await showConfirmModal(
            'Regenerate API key',
            '<p>Generating a new key will <strong>revoke the existing one immediately</strong>. ' +
            'Any external dashboards using the current key will stop working until you update them.</p>',
            'Regenerate',
        );
        if (!ok) return;
    }

    try {
        const data = await authApi.generateApiKey();
        document.getElementById('apiKeyValue').value = data.key || '';
        document.getElementById('apiKeyDisplayRow').style.display = '';
        renderApiKeyStateAfterGenerate();
        showToast('API key generated', 'success');
    } catch (e) {
        showToast('Generate failed: ' + e.message, 'danger');
    }
}

/** Like renderApiKeyState(true) but keeps the just-generated key visible. */
function renderApiKeyStateAfterGenerate() {
    document.getElementById('generateApiKeyBtnLabel').textContent = 'Regenerate API key';
    document.getElementById('revokeApiKeyBtn').style.display = '';
    document.getElementById('apiKeyStatus').textContent = 'A key is configured.';
}

async function revokeApiKey() {
    const ok = await showConfirmModal(
        'Revoke API key',
        '<p>External dashboards (Homarr, Glance, …) using this key will <strong>lose access immediately</strong>.</p>',
        'Revoke',
    );
    if (!ok) return;

    try {
        await authApi.revokeApiKey();
        renderApiKeyState(false);
        showToast('API key revoked', 'success');
    } catch (e) {
        showToast('Revoke failed: ' + e.message, 'danger');
    }
}

async function copyApiKey() {
    const value = document.getElementById('apiKeyValue').value;
    if (!value) return;
    try {
        await navigator.clipboard.writeText(value);
        showToast('Copied to clipboard', 'success');
    } catch {
        document.getElementById('apiKeyValue').select();
        showToast('Copy failed — selected the value so you can copy manually', 'warning');
    }
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
// Public entry points
// ---------------------------------------------------------------------------

/**
 * Wires the panel's DOM controls. Safe to call once at startup.
 */
export function initAuthPanel() {
    document.getElementById('saveAuthConfig')?.addEventListener('click', save);
    document.getElementById('signOutBtn')    ?.addEventListener('click', signOut);
    document.getElementById('generateApiKeyBtn')?.addEventListener('click', generateApiKey);
    document.getElementById('revokeApiKeyBtn')  ?.addEventListener('click', revokeApiKey);
    document.getElementById('copyApiKeyBtn')    ?.addEventListener('click', copyApiKey);
}

/** Lazy data load, invoked when the settings modal is first opened. */
export const loadAuthPanel = load;
