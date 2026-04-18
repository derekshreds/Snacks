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
    } catch { /* auth may already gate this */ }
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
}

/** Lazy data load, invoked when the settings modal is first opened. */
export const loadAuthPanel = load;
