/**
 * Folder-picker modal.
 *
 * A tree-style directory browser used in two modes:
 *
 *   1. **Input-target mode** (the common case). Any element with class
 *      `.folder-picker-btn` and a `data-target="someInputId"` attribute opens
 *      the picker; on confirm the chosen path is written into the target
 *      input's `value` and `input`/`change` events are dispatched.
 *
 *   2. **Callback mode**, invoked programmatically via {@link pickFolder}.
 *      The caller supplies a callback that receives the chosen path, and no
 *      DOM input is populated — useful for flows like "add a watched
 *      directory" that don't correspond to a form field.
 *
 * Interaction: clicking a row drills into the folder and selects it;
 * pressing Enter in the manual-path input commits that path directly.
 */

import { libraryApi } from '../api.js';
import { escapeHtml } from '../utils/dom.js';


// ---------------------------------------------------------------------------
// Module state
// ---------------------------------------------------------------------------

/** Id of the `<input>` that will receive the chosen path (input-target mode). */
let targetInputId = null;

/**
 * Callback invoked with the chosen path on confirm (callback mode). Set by
 * {@link pickFolder} instead of an input id when the caller wants to react
 * to the selection directly rather than populate a form input.
 *
 * @type {((path: string) => void) | null}
 */
let onConfirmCallback = null;

/** Current directory being browsed, or null when viewing root directories. */
let currentPath   = null;

/** Currently selected path (may be distinct from `currentPath`). */
let selectedPath  = null;


// ---------------------------------------------------------------------------
// Internal helpers
// ---------------------------------------------------------------------------

/**
 * Shorthand for `document.getElementById`.
 * @param {string} id
 * @returns {HTMLElement|null}
 */
function el(id) {
    return document.getElementById(id);
}


// ---------------------------------------------------------------------------
// Navigation
// ---------------------------------------------------------------------------

/**
 * Loads and renders the list of library root directories.
 */
async function loadRoot() {
    const list = el('folderPickerList');
    list.innerHTML = '<div class="text-center text-muted py-4"><div class="spinner-border spinner-border-sm"></div></div>';

    try {
        const data = await libraryApi.getDirectories();
        currentPath = null;
        el('folderPickerBreadcrumb').textContent = data.rootPath || '/';
        render(data.directories, null);
    } catch (err) {
        list.innerHTML = `<div class="text-danger small">Failed to load: ${escapeHtml(err.message)}</div>`;
    }
}

/**
 * Loads and renders the subdirectories of `path`.
 *
 * @param {string} path Directory to drill into.
 */
async function loadSubtree(path) {
    const list = el('folderPickerList');
    list.innerHTML = '<div class="text-center text-muted py-4"><div class="spinner-border spinner-border-sm"></div></div>';

    try {
        const data = await libraryApi.getSubdirectories(path);
        currentPath = path;
        el('folderPickerBreadcrumb').textContent = path;

        // The folder we just drilled into becomes the tentative selection.
        selectPath(path);

        render(data.directories, data.parentPath);
    } catch (err) {
        list.innerHTML = `<div class="text-danger small">Failed: ${escapeHtml(err.message)}</div>`;
    }
}

// ---------------------------------------------------------------------------
// Rendering
// ---------------------------------------------------------------------------

/**
 * Renders a list of directories into the picker, prepending a navigation
 * row (either "up" or "back-to-top") when appropriate.
 *
 * @param {Array<{name?:string, path:string}>|null|undefined} dirs
 * @param {string|null|undefined} parentPath  Non-null → render an "up" row
 *                                            pointing at this path.
 */
function render(dirs, parentPath) {
    const list = el('folderPickerList');
    list.innerHTML = '';

    // Prepend "up" when we know the parent path, or "back to top" when we
    // drilled in from the root view.
    if (parentPath !== undefined && parentPath !== null) {
        const up = document.createElement('div');
        up.className = 'folder-picker-row';
        up.innerHTML = `<i class="fas fa-level-up-alt"></i><span class="folder-picker-name text-muted">.. (up)</span>`;
        up.addEventListener('click', () => loadSubtree(parentPath));
        list.appendChild(up);
    } else if (currentPath) {
        const up = document.createElement('div');
        up.className = 'folder-picker-row';
        up.innerHTML = `<i class="fas fa-home"></i><span class="folder-picker-name text-muted">(back to top)</span>`;
        up.addEventListener('click', loadRoot);
        list.appendChild(up);
    }

    if (!dirs || dirs.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'text-muted text-center py-3 small';
        empty.textContent = 'No subfolders here.';
        list.appendChild(empty);
    }

    for (const d of (dirs || [])) {
        const row = document.createElement('div');
        row.className = 'folder-picker-row';
        if (selectedPath === d.path) row.classList.add('active');
        row.innerHTML = `
            <i class="fas fa-folder"></i>
            <span class="folder-picker-name">${escapeHtml(d.name || d.path)}</span>
            <i class="fas fa-chevron-right folder-picker-expand" aria-hidden="true"></i>`;

        // Clicking anywhere on the row drills into that folder, which also
        // sets it as the current selection (see loadSubtree).
        row.addEventListener('click', () => loadSubtree(d.path));

        list.appendChild(row);
    }
}


// ---------------------------------------------------------------------------
// Selection & commit
// ---------------------------------------------------------------------------

/**
 * Marks `path` as the tentative selection.
 *
 * @param {string}  path
 * @param {boolean} [closeOnSelect=false] When true, commits and closes the modal.
 */
function selectPath(path, closeOnSelect = false) {
    selectedPath = path;
    el('folderPickerSelection').textContent = `Selected: ${path}`;
    el('folderPickerConfirm').disabled = false;

    document.querySelectorAll('.folder-picker-row.active')
            .forEach(r => r.classList.remove('active'));

    if (closeOnSelect) confirm();
}

/**
 * Commits the tentative selection by either invoking the callback
 * (callback mode) or populating the target input (input-target mode), and
 * closes the modal. No-op when no selection or destination has been set.
 */
function confirm() {
    if (!selectedPath) return;

    if (onConfirmCallback) {
        const cb = onConfirmCallback;
        const path = selectedPath;
        onConfirmCallback = null;
        el('folderPickerModal').classList.remove('open');
        cb(path);
        return;
    }

    if (!targetInputId) return;

    const input = document.getElementById(targetInputId);
    if (input) {
        input.value = selectedPath;
        input.dispatchEvent(new Event('change', { bubbles: true }));
        input.dispatchEvent(new Event('input',  { bubbles: true }));
    }

    el('folderPickerModal').classList.remove('open');
}

/**
 * Prepares the picker chrome (selection label, confirm button, manual-path
 * input) from an initial value and opens the modal.
 *
 * @param {string} initialValue Seeds the selection; empty string for none.
 */
function openModalWithState(initialValue) {
    selectedPath = initialValue || null;
    currentPath  = null;

    el('folderPickerSelection').textContent = initialValue
        ? `Selected: ${initialValue}`
        : 'No folder selected';
    el('folderPickerConfirm').disabled = !initialValue;
    el('folderPickerManualPath').value = initialValue;
    el('folderPickerModal').classList.add('open');

    loadRoot();
}

/**
 * Opens the picker in input-target mode, seeding the selection from the
 * target input's current value if it has one.
 *
 * @param {string} inputId Id of the `<input>` to populate on confirm.
 */
function open(inputId) {
    targetInputId     = inputId;
    onConfirmCallback = null;
    openModalWithState(document.getElementById(inputId)?.value || '');
}

/**
 * Opens the picker in callback mode. The supplied callback is invoked with
 * the chosen path when the user confirms; nothing happens if the user
 * cancels the dialog.
 *
 * @param {(path: string) => void} onConfirm
 * @param {string} [initialValue='']       Optional initial value for the picker.
 */
export function pickFolder(onConfirm, initialValue = '') {
    targetInputId     = null;
    onConfirmCallback = onConfirm;
    openModalWithState(initialValue);
}


// ---------------------------------------------------------------------------
// Public entry point
// ---------------------------------------------------------------------------

/**
 * Installs a document-level click handler for `.folder-picker-btn` triggers
 * and wires the modal's own controls (backdrop click, Confirm button,
 * manual-path Enter-to-confirm).
 */
export function initFolderPicker() {
    document.addEventListener('click', (e) => {
        const btn = e.target.closest('.folder-picker-btn');
        if (!btn) return;
        e.preventDefault();
        open(btn.dataset.target);
    });

    const modal = el('folderPickerModal');
    if (!modal) return;

    // Click outside the modal content (but still inside its wrapper) closes it.
    modal.querySelector('.snacks-modal-wrapper').addEventListener('mousedown', function (e) {
        if (e.target === this) modal.classList.remove('open');
    });

    el('folderPickerConfirm').addEventListener('click', confirm);

    // Enter in the manual-path input commits that path directly.
    el('folderPickerManualPath').addEventListener('keydown', (e) => {
        if (e.key !== 'Enter') return;
        e.preventDefault();
        const path = e.target.value.trim();
        if (path) selectPath(path, true);
    });
}
