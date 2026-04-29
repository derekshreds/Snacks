/**
 * SPA navigation shell.
 *
 * Keeps the navbar, modals, and SignalR connection alive across page changes
 * by intercepting clicks on `<a data-spa-link>` links, fetching the target
 * route via fetch(), and swapping the `#page-content` element's innerHTML
 * instead of doing a full document reload. Each rendered page declares its
 * name through `ViewData["Page"]` (rendered as `data-page` on
 * `#page-content`); page modules register init/destroy callbacks against
 * that name and the shell drives the lifecycle.
 *
 * No framework here on purpose — Snacks is a small app and a 100-line
 * navigator is easier to reason about than pulling in a router library.
 */


/** @type {Map<string, { mount?: () => void, unmount?: () => void }>} */
const PAGES = new Map();

/** Currently-mounted page name (matches `#page-content[data-page]`). */
let currentPage = null;


/**
 * Registers lifecycle callbacks for a page name. Called by each page module
 * at import time. `mount` runs every time the page is shown (initial load
 * AND SPA navigation back to it); `unmount` runs before the page's content
 * is replaced, giving the page a chance to tear down timers/subscriptions
 * that would leak across navigations.
 *
 * @param {string} name
 * @param {{ mount?: () => void, unmount?: () => void }} hooks
 */
export function registerPage(name, hooks) {
    PAGES.set(name, hooks);

    // If the page is already in the DOM at registration time (initial load),
    // fire its mount immediately. Page modules are imported by main.js after
    // DOMContentLoaded, so the document is already parsed at this point.
    if (currentPage === null) {
        const root = document.getElementById('page-content');
        const dom  = root?.dataset.page || '';
        if (dom === name) {
            currentPage = name;
            try { hooks.mount?.(); }
            catch (err) { console.error(`Page mount failed (${name}):`, err); }
        }
    }
}


/**
 * Initializes the SPA shell. Wires the click interceptor and `popstate`
 * handler, and fires the initial page's mount if a module was registered
 * before the shell started.
 */
export function initNavigation() {
    document.addEventListener('click', onLinkClick);
    window.addEventListener('popstate', () => navigateTo(location.pathname, { push: false }));

    // First-paint mount: a page module may have registered before initNavigation
    // ran (we register at module import; init runs after); make sure its mount
    // fires for the initial route.
    const root = document.getElementById('page-content');
    const initial = root?.dataset.page || '';
    if (initial && currentPage === null && PAGES.has(initial)) {
        currentPage = initial;
        try { PAGES.get(initial).mount?.(); }
        catch (err) { console.error(`Page mount failed (${initial}):`, err); }
    }
}


/**
 * Click handler — intercepts `<a data-spa-link>` clicks (left-button, no
 * modifier keys) and routes them through `navigateTo`. Anything else falls
 * through to the browser's default navigation.
 */
function onLinkClick(e) {
    if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;

    const a = e.target.closest('a[data-spa-link]');
    if (!a) return;

    const href = a.getAttribute('href');
    if (!href || href.startsWith('http') || href.startsWith('//') || a.target === '_blank') return;

    e.preventDefault();
    navigateTo(href, { push: true });
}


/**
 * Fetches the target URL, extracts the new `#page-content` block from the
 * response HTML, and swaps it in. Calls the outgoing page's `unmount` and
 * the incoming page's `mount` so timers/subscriptions cycle cleanly.
 *
 * @param {string} url
 * @param {{ push?: boolean }} [opts] When false, suppresses pushState (used by popstate).
 */
export async function navigateTo(url, { push = true } = {}) {
    try {
        const resp = await fetch(url, { headers: { 'X-SPA-Request': '1' } });
        if (!resp.ok) {
            // Hard fall back to a real navigation on non-OK so the user
            // isn't left looking at the previous page.
            location.href = url;
            return;
        }
        const html = await resp.text();
        const doc  = new DOMParser().parseFromString(html, 'text/html');
        const next = doc.getElementById('page-content');
        if (!next) {
            location.href = url;
            return;
        }

        // Tear down the outgoing page first.
        const outgoing = PAGES.get(currentPage);
        try { outgoing?.unmount?.(); }
        catch (err) { console.error(`Page unmount failed (${currentPage}):`, err); }

        // Swap content and update history before mount so the new page can
        // consult `location` if it wants to (e.g. parse query params).
        const root = document.getElementById('page-content');
        root.innerHTML       = next.innerHTML;
        root.dataset.page    = next.dataset.page || '';

        const newTitle = doc.querySelector('title')?.textContent;
        if (newTitle) document.title = newTitle;

        if (push) history.pushState({}, '', url);

        currentPage = root.dataset.page || null;

        // Mount the incoming page.
        const incoming = PAGES.get(currentPage);
        try { incoming?.mount?.(); }
        catch (err) { console.error(`Page mount failed (${currentPage}):`, err); }

        // Reset scroll the way a real navigation would. This avoids the
        // dashboard inheriting the queue page's scroll position.
        window.scrollTo(0, 0);
    } catch (err) {
        console.error('SPA navigation failed:', err);
        location.href = url;
    }
}
