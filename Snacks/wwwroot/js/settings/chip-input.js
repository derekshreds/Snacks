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
 *
 * ISO-language mode: when the root has `data-source="iso-languages"`, input
 * is normalized through {@link toTwoLetter} on commit (so `eng`/`English` both
 * become `en`), unknown tokens are rejected with a brief shake animation, and
 * a suggestion dropdown is rendered beneath the input while typing.
 */

import { suggest, toTwoLetter, nameFor } from './iso-languages.js';


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
 * @param {HTMLElement} list   The `.chip-list` container to append into.
 * @param {string}      value  The value to display on the chip.
 * @param {string|null} [tooltip]  Optional tooltip text for the chip.
 */
function addChip(list, value, tooltip = null) {
    const chip = document.createElement('span');
    chip.className = 'chip';
    chip.dataset.value = value;
    if (tooltip) chip.title = tooltip;
    chip.innerHTML = `${escapeHtml(value)} <button type="button" class="chip-remove" aria-label="Remove">&times;</button>`;

    chip.querySelector('.chip-remove').addEventListener('click', () => {
        const root = list.closest('.chip-input');
        chip.remove();
        root.dispatchEvent(new Event('change', { bubbles: true }));
    });

    list.appendChild(chip);
}

/**
 * Briefly flashes a red outline on the input to signal a rejected commit.
 *
 * @param {HTMLInputElement} input
 */
function flashInvalid(input) {
    input.classList.add('chip-input-invalid');
    setTimeout(() => input.classList.remove('chip-input-invalid'), 600);
}


// ---------------------------------------------------------------------------
// ISO suggestion dropdown
// ---------------------------------------------------------------------------

/**
 * Attaches (and returns) the singleton suggestion dropdown for an ISO-mode
 * chip-input root. Idempotent.
 *
 * @param {HTMLElement} root
 * @returns {HTMLElement}
 */
function ensureSuggestionBox(root) {
    let box = root.querySelector('.chip-suggestions');
    if (box) return box;

    box = document.createElement('div');
    box.className = 'chip-suggestions';
    box.style.display = 'none';
    root.appendChild(box);
    return box;
}

/**
 * Renders the suggestion list for the current query and wires click/hover.
 *
 * @param {HTMLElement}      box
 * @param {HTMLInputElement} input
 * @param {Function}         onPick  Called with the chosen 2-letter code.
 */
function renderSuggestions(box, input, onPick) {
    const items = suggest(input.value);
    if (items.length === 0) {
        box.style.display = 'none';
        box.innerHTML     = '';
        return;
    }

    box.innerHTML = items.map((e, i) => `
        <div class="chip-suggestion${i === 0 ? ' active' : ''}" data-value="${escapeHtml(e.twoLetter)}">
            <span class="chip-suggestion-code">${escapeHtml(e.twoLetter)}</span>
            <span class="chip-suggestion-name">${escapeHtml(e.name)}</span>
        </div>
    `).join('');

    box.style.display = 'block';

    box.querySelectorAll('.chip-suggestion').forEach(node => {
        node.addEventListener('mouseenter', () => {
            box.querySelectorAll('.chip-suggestion').forEach(n => n.classList.remove('active'));
            node.classList.add('active');
        });
        node.addEventListener('mousedown', (e) => {
            // mousedown (not click) so we fire before the input's blur handler.
            e.preventDefault();
            onPick(node.dataset.value);
        });
    });
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

    const isoMode    = root.dataset.source === 'iso-languages';
    const suggestBox = isoMode ? ensureSuggestionBox(root) : null;

    /**
     * Returns `{ value, tooltip }` if `raw` should become a chip, otherwise
     * `null`. In ISO mode rejects unknown languages; in plain mode accepts
     * anything non-empty.
     */
    const resolveToken = (raw) => {
        if (!isoMode) return { value: raw, tooltip: null };

        const two = toTwoLetter(raw);
        if (!two) return null;
        return { value: two, tooltip: nameFor(two) };
    };

    /**
     * Commits the current input text as one or more chips (comma-separated),
     * skipping duplicates (case-insensitive) and empty entries.
     *
     * In ISO mode: every token passes through {@link toTwoLetter}; a single
     * unrecognized token aborts the whole commit (so the user sees the red
     * flash and can fix their input instead of silently losing it).
     */
    const commit = () => {
        const raw = input.value.trim();
        if (!raw) return;

        const parts = raw.split(',').map(s => s.trim()).filter(Boolean);
        const resolved = [];
        for (const part of parts) {
            const r = resolveToken(part);
            if (!r) {
                flashInvalid(input);
                if (suggestBox) {
                    suggestBox.style.display = 'none';
                    suggestBox.innerHTML     = '';
                }
                return;
            }
            resolved.push(r);
        }

        const existing = new Set(
            Array.from(list.querySelectorAll('.chip'))
                 .map(c => c.dataset.value.toLowerCase())
        );

        for (const { value, tooltip } of resolved) {
            if (existing.has(value.toLowerCase())) continue;
            addChip(list, value, tooltip);
            existing.add(value.toLowerCase());
        }

        input.value = '';
        if (suggestBox) {
            suggestBox.style.display = 'none';
            suggestBox.innerHTML     = '';
        }
        root.dispatchEvent(new Event('change', { bubbles: true }));
    };

    /** Commits `two` (already a canonical 2-letter code) from the suggestion box. */
    const commitFromSuggestion = (two) => {
        input.value = two;
        commit();
        input.focus();
    };

    input.addEventListener('keydown', (e) => {
        if (isoMode && suggestBox && suggestBox.style.display !== 'none') {
            const items = Array.from(suggestBox.querySelectorAll('.chip-suggestion'));
            const idx   = items.findIndex(n => n.classList.contains('active'));

            if (e.key === 'ArrowDown') {
                e.preventDefault();
                if (items.length === 0) return;
                items[idx]?.classList.remove('active');
                items[Math.min(idx + 1, items.length - 1)].classList.add('active');
                return;
            }
            if (e.key === 'ArrowUp') {
                e.preventDefault();
                if (items.length === 0) return;
                items[idx]?.classList.remove('active');
                items[Math.max(idx - 1, 0)].classList.add('active');
                return;
            }
            if (e.key === 'Tab') {
                const active = items[idx] ?? items[0];
                if (active) {
                    e.preventDefault();
                    input.value = active.dataset.value;
                    renderSuggestions(suggestBox, input, commitFromSuggestion);
                    return;
                }
            }
        }

        // Enter or comma commits the current input text.
        if (e.key === 'Enter' || e.key === ',') {
            e.preventDefault();

            if (isoMode && suggestBox && suggestBox.style.display !== 'none') {
                const active = suggestBox.querySelector('.chip-suggestion.active');
                if (active) {
                    commitFromSuggestion(active.dataset.value);
                    return;
                }
            }

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

        if (e.key === 'Escape' && suggestBox) {
            suggestBox.style.display = 'none';
            suggestBox.innerHTML     = '';
        }
    });

    if (isoMode && suggestBox) {
        input.addEventListener('input',  () => renderSuggestions(suggestBox, input, commitFromSuggestion));
        input.addEventListener('focus',  () => renderSuggestions(suggestBox, input, commitFromSuggestion));
    }

    input.addEventListener('blur', () => {
        // Defer so a suggestion mousedown can register before we wipe the box.
        setTimeout(() => {
            if (suggestBox) {
                suggestBox.style.display = 'none';
                suggestBox.innerHTML     = '';
            }
            commit();
        }, 150);
    });
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

    const list    = root.querySelector('.chip-list');
    const isoMode = root.dataset.source === 'iso-languages';
    list.innerHTML = '';

    for (const v of (values || [])) {
        const raw = String(v);
        if (isoMode) {
            const two = toTwoLetter(raw) ?? raw;
            addChip(list, two, nameFor(two));
        } else {
            addChip(list, raw, null);
        }
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
