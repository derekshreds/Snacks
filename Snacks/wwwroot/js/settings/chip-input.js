/**
 * Chip-input widget.
 *
 * Turns a `<div class="chip-input">` containing a `.chip-new` text input and
 * a `.chip-list` container into a tag-editor component. Users type a value
 * and press Enter (or type a comma) to commit it as a chip; Backspace on an
 * empty input removes the last chip. Duplicates (case-insensitive) are
 * silently ignored.
 *
 * Each commit fires a bubbling `change` event on the root element so callers
 * can observe edits without polling. Current chip values are read/written
 * via {@link getChipValues} and {@link setChipValues} by element id.
 */


// ---------------------------------------------------------------------------
// Internal helpers
// ---------------------------------------------------------------------------

/**
 * HTML-escapes a value for safe insertion into innerHTML.
 *
 * @param {unknown} s Value to escape (coerced to string).
 * @returns {string}  Escaped HTML.
 */
function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({
        '&':  '&amp;',
        '<':  '&lt;',
        '>':  '&gt;',
        '"':  '&quot;',
        "'":  '&#39;',
    }[c]));
}

/**
 * Appends a chip element representing `value` to `list` and wires its
 * remove-button click handler. The chip's data-value preserves the
 * original (unescaped) user input.
 *
 * @param {HTMLElement} list  The `.chip-list` container to append into.
 * @param {string}      value The value to display on the chip.
 */
function addChip(list, value) {
    const chip = document.createElement('span');
    chip.className = 'chip';
    chip.dataset.value = value;
    chip.innerHTML = `${escapeHtml(value)} <button type="button" class="chip-remove" aria-label="Remove">&times;</button>`;

    chip.querySelector('.chip-remove').addEventListener('click', () => {
        const root = list.closest('.chip-input');
        chip.remove();
        root.dispatchEvent(new Event('change', { bubbles: true }));
    });

    list.appendChild(chip);
}


// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

/**
 * Initializes a single `.chip-input` root element.
 *
 * Idempotent: re-invoking on an already-initialized root is a no-op. Wires
 * up Enter/comma to commit, Backspace (on empty input) to pop, and blur to
 * flush any pending text as a chip.
 *
 * @param {HTMLElement} root The `.chip-input` container element.
 */
export function initChipInput(root) {
    if (!root || root._chipInited) return;
    root._chipInited = true;

    const input = root.querySelector('.chip-new');
    const list  = root.querySelector('.chip-list');
    if (!input || !list) return;

    /**
     * Commits the current input text as one or more chips (comma-separated),
     * skipping duplicates (case-insensitive) and empty entries.
     */
    const commit = () => {
        const raw = input.value.trim();
        if (!raw) return;

        const existing = new Set(
            Array.from(list.querySelectorAll('.chip'))
                 .map(c => c.dataset.value.toLowerCase())
        );

        const parts = raw.split(',').map(s => s.trim()).filter(Boolean);
        for (const part of parts) {
            if (existing.has(part.toLowerCase())) continue;
            addChip(list, part);
            existing.add(part.toLowerCase());
        }

        input.value = '';
        root.dispatchEvent(new Event('change', { bubbles: true }));
    };

    input.addEventListener('keydown', (e) => {
        // Enter or comma commits the current input text.
        if (e.key === 'Enter' || e.key === ',') {
            e.preventDefault();
            commit();
            return;
        }

        // Backspace on an empty input removes the most recently added chip.
        if (e.key === 'Backspace' && input.value === '') {
            const last = list.querySelector('.chip:last-child');
            if (last) {
                last.remove();
                root.dispatchEvent(new Event('change', { bubbles: true }));
            }
        }
    });

    input.addEventListener('blur', commit);
}

/**
 * Initializes every `.chip-input` element within `scope`.
 *
 * @param {ParentNode} [scope=document] Root to scan for `.chip-input` nodes.
 */
export function initAllChipInputs(scope = document) {
    scope.querySelectorAll('.chip-input').forEach(initChipInput);
}

/**
 * Replaces the chips inside the `.chip-input` with element id `rootId`
 * with the supplied `values`. Initializes the root if needed.
 *
 * @param {string} rootId The id of the `.chip-input` root element.
 * @param {Iterable<string>|null|undefined} values The new chip values.
 */
export function setChipValues(rootId, values) {
    const root = document.getElementById(rootId);
    if (!root) return;

    initChipInput(root);

    const list = root.querySelector('.chip-list');
    list.innerHTML = '';

    for (const v of (values || [])) {
        addChip(list, String(v));
    }
}

/**
 * Returns the current chip values (strings) from the `.chip-input` with
 * element id `rootId`, or an empty array if the root is not found.
 *
 * @param {string} rootId The id of the `.chip-input` root element.
 * @returns {string[]}    Current chip values in display order.
 */
export function getChipValues(rootId) {
    const root = document.getElementById(rootId);
    if (!root) return [];

    return Array.from(root.querySelectorAll('.chip')).map(c => c.dataset.value);
}
