/**
 * Shared DOM utilities used across the frontend modules.
 */


/**
 * HTML-escapes `text` for safe insertion into `innerHTML` — in BOTH element
 * content and attribute positions.
 *
 * Quotes must be escaped here: call sites routinely interpolate this into
 * double-quoted attributes (`title="..."`, `data-path="..."`, `value="..."`),
 * and the previous textContent/innerHTML round-trip left `"` and `'` intact,
 * letting a crafted filename break out of the attribute and inject an event
 * handler (XSS reachable by anyone who can drop a file into a watched library).
 *
 * @param {unknown} text Value to escape (coerced to string).
 * @returns {string}     Escaped HTML, safe for content and attribute contexts.
 */
export function escapeHtml(text) {
    return String(text ?? '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}
