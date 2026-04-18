/**
 * Shared DOM utilities used across the frontend modules.
 */


/**
 * HTML-escapes `text` for safe insertion into `innerHTML`.
 *
 * Delegates to the browser by round-tripping through a detached `<div>`'s
 * `textContent` / `innerHTML`, which handles every character the renderer
 * would otherwise interpret as markup.
 *
 * @param {unknown} text Value to escape (coerced to string by the DOM).
 * @returns {string}     Escaped HTML.
 */
export function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}
