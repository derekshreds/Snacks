/**
 * Global modal controller.
 *
 * Owns the app's opinionated modal behavior: backdrop-click dismissal,
 * Escape-key dismissal of the top-most open modal (by z-index), and a
 * declarative `data-snacks-dismiss="<modalId>"` attribute that lets any
 * element close a modal on click without wiring a handler.
 *
 * Also exposes a promise-returning `showConfirmModal` built on top of the
 * static `#confirmModal` element in the layout.
 */

import { escapeHtml } from './dom.js';


// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Attribute used on dismiss buttons to identify their target modal by id. */
const DISMISS_ATTR = 'data-snacks-dismiss';


// ---------------------------------------------------------------------------
// Open / close primitives
// ---------------------------------------------------------------------------

/**
 * Opens the modal with element id `id` by adding the `.open` class to its
 * backdrop. Silently ignores missing elements.
 *
 * @param {string} id Element id of the modal backdrop.
 */
export function openModal(id) {
    const el = document.getElementById(id);
    if (el) el.classList.add('open');
}

/**
 * Closes the modal with element id `id` by removing the `.open` class.
 * Silently ignores missing elements.
 *
 * @param {string} id Element id of the modal backdrop.
 */
export function closeModal(id) {
    const el = document.getElementById(id);
    if (el) el.classList.remove('open');
}

/**
 * Returns true if the modal with element id `id` is currently open.
 *
 * @param {string} id Element id of the modal backdrop.
 * @returns {boolean}
 */
export function isOpen(id) {
    return !!document.getElementById(id)?.classList.contains('open');
}


// ---------------------------------------------------------------------------
// Global controller setup
// ---------------------------------------------------------------------------

/**
 * Returns the ids of every currently-open modal, sorted by ascending z-index
 * (bottom-most first). Used to decide which modal the Escape key should close.
 *
 * @returns {string[]}
 */
function openModalIdsByZ() {
    return Array.from(document.querySelectorAll('.snacks-modal-backdrop.open'))
        .map(el => ({
            id: el.id,
            z:  parseInt(getComputedStyle(el).getPropertyValue('--snacks-modal-z')) || 9000,
        }))
        .sort((a, b) => a.z - b.z)
        .map(x => x.id);
}

/**
 * Installs the global modal behaviors. Idempotent: may be called more than
 * once without side effects.
 *
 * Wires three document-level listeners:
 *   1. `mousedown` on a modal wrapper's blank area closes the enclosing modal.
 *   2. `click` on any element with the dismiss attribute closes its target.
 *   3. `keydown` with Escape closes the top-most open modal.
 */
export function initModalController() {
    if (initModalController._inited) return;
    initModalController._inited = true;

    // Click outside the modal's content (but still inside its wrapper) closes it.
    document.addEventListener('mousedown', (e) => {
        const wrapper = e.target.closest('.snacks-modal-wrapper');
        if (!wrapper || wrapper !== e.target) return;

        const backdrop = wrapper.closest('.snacks-modal-backdrop');
        if (backdrop) backdrop.classList.remove('open');
    });

    // Declarative dismiss — any element with [data-snacks-dismiss="modalId"] closes that modal.
    document.addEventListener('click', (e) => {
        const btn = e.target.closest(`[${DISMISS_ATTR}]`);
        if (!btn) return;

        closeModal(btn.getAttribute(DISMISS_ATTR));
    });

    // Escape closes the top-most open modal (by z-index).
    document.addEventListener('keydown', (e) => {
        if (e.key !== 'Escape') return;

        const open = openModalIdsByZ();
        if (open.length > 0) closeModal(open[open.length - 1]);
    });
}


// ---------------------------------------------------------------------------
// Confirm modal
// ---------------------------------------------------------------------------

/**
 * Shows the shared confirm modal (`#confirmModal`) and resolves with `true`
 * when the user confirms, or `false` when the modal is closed (backdrop
 * click, Escape, or dismiss button).
 *
 * Each invocation clones the confirm button so any listener from a prior
 * invocation is dropped. The promise is also resolved via a MutationObserver
 * watching the backdrop's class list, covering dismissals that don't go
 * through the confirm button.
 *
 * @param {string} title              Displayed in the modal header (plain text; escaped).
 * @param {string} message            HTML body inserted verbatim — callers own the markup.
 * @param {string} [confirmText]      Label for the confirm button (default "Confirm").
 * @returns {Promise<boolean>}        Resolves `true` if confirmed, `false` otherwise.
 */
export function showConfirmModal(title, message, confirmText = 'Confirm') {
    return new Promise((resolve) => {
        const modalEl    = document.getElementById('confirmModal');
        const titleEl    = document.getElementById('confirmModalTitle');
        const bodyEl     = document.getElementById('confirmModalBody');
        const confirmBtn = document.getElementById('confirmModalConfirm');

        titleEl.innerHTML      = `<i class="fas fa-exclamation-triangle me-2"></i>${escapeHtml(title)}`;
        bodyEl.innerHTML       = message;
        confirmBtn.textContent = confirmText;

        // Clone the confirm button to drop any click listener from a previous invocation.
        const fresh = confirmBtn.cloneNode(true);
        confirmBtn.replaceWith(fresh);

        const done = (outcome) => {
            modalEl.classList.remove('open');
            observer.disconnect();
            resolve(outcome);
        };

        fresh.addEventListener('click', () => done(true), { once: true });

        // Any other dismissal path (backdrop click, Escape, dismiss button) toggles
        // the `.open` class off — observe it and resolve with `false` when that happens.
        const observer = new MutationObserver(() => {
            if (!modalEl.classList.contains('open')) done(false);
        });
        observer.observe(modalEl, { attributes: true, attributeFilter: ['class'] });

        modalEl.classList.add('open');
    });
}
