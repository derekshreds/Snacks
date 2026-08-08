/**
 * Env-lock rendering for settings controls.
 *
 * Config GET endpoints return an `_envLocked` array of camelCase dotted paths
 * (e.g. `["codec", "music.bitrateKbps"]`) for values currently driven by
 * SNACKS_SET_* / SNACKS_SCAN_* / SNACKS_INTEG_* environment variables. Those
 * controls are disabled with a lock badge — the server enforces the lock
 * regardless (env values are re-applied on load and stripped from saves), so
 * this is purely honest UI.
 */

const LOCK_REASON = 'Set by an environment variable in your container/compose config';


/**
 * Disables a single form control and tags it with a lock badge on its label.
 * Idempotent — safe to call on every panel (re)load.
 *
 * @param {HTMLElement|null} node
 * @param {string} [reason]
 */
export function lockControl(node, reason = LOCK_REASON) {
    if (!node) return;

    node.disabled = true;
    node.title    = reason;
    node.classList.add('env-locked');

    const label = node.closest('.form-check')?.querySelector('label')
        ?? (node.id ? document.querySelector(`label[for="${node.id}"]`) : null);
    if (label && !label.querySelector('.env-locked-badge')) {
        const badge = document.createElement('i');
        badge.className = 'fas fa-lock env-locked-badge ms-1 text-muted small';
        badge.title     = reason;
        label.appendChild(badge);
    }
}

/**
 * Locks a composite widget (chip inputs, row editors) by disabling every
 * control inside it and blocking pointer interaction on the container.
 *
 * @param {HTMLElement|null} container
 * @param {string} [reason]
 */
export function lockContainer(container, reason = LOCK_REASON) {
    if (!container) return;

    container.classList.add('env-locked-container');
    container.title = reason;
    container.querySelectorAll('input, select, button, textarea')
        .forEach((node) => { node.disabled = true; });
}

/**
 * Applies locks for every path the server reported, resolving each path to
 * its element(s) through the caller's map.
 *
 * @param {string[]|undefined} lockedPaths          `_envLocked` from a config GET.
 * @param {(path: string) => HTMLElement|HTMLElement[]|null} resolve
 *        Maps a camelCase dotted path to the control(s) rendering it.
 *        Container elements (`.chip-input`, or anything without a `disabled`
 *        property that has children) should be returned as-is — they are
 *        detected and locked with {@link lockContainer}.
 * @returns {boolean} Whether any lock was applied.
 */
export function applyEnvLocks(lockedPaths, resolve) {
    let any = false;
    for (const path of lockedPaths ?? []) {
        const resolved = resolve(path);
        for (const node of [resolved].flat()) {
            if (!node) continue;
            any = true;
            if ('disabled' in node && node.tagName !== 'FIELDSET') lockControl(node);
            else                                                   lockContainer(node);
        }
    }
    return any;
}

/**
 * Appends a one-line "managed by environment variables" note to a panel,
 * once, when at least one of its controls is locked.
 *
 * @param {HTMLElement|null} parent
 */
export function addEnvLockNote(parent) {
    if (!parent || parent.querySelector('.env-locked-note')) return;

    const note = document.createElement('div');
    note.className   = 'env-locked-note form-text mt-2';
    note.innerHTML   = '<i class="fas fa-lock me-1"></i>Some settings are managed by environment variables and can only be changed in your container configuration.';
    parent.appendChild(note);
}
