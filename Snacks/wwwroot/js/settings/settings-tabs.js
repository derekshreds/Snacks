/**
 * Horizontal settings-tab scroller.
 *
 * Gives the `.settings-tabs` strip inside `.settings-tabs-wrapper` scroll
 * arrows that appear only when the strip actually overflows, and keeps the
 * active tab visible by scrolling it into view on selection. Mouse-wheel
 * gestures with a dominant vertical component are translated to horizontal
 * scroll so a normal wheel can scroll the tab bar.
 */


/**
 * Wires up the settings-tab scroller. Safe to call once per page load.
 *
 * Expects the DOM to contain:
 *   - `.settings-tabs-wrapper`     — outer element (root of lookups)
 *   - `.settings-tabs`             — scrollable strip
 *   - `.settings-tab-arrow-left`   — left scroll button
 *   - `.settings-tab-arrow-right`  — right scroll button
 *   - `.nav-link` children inside the strip
 *
 * No-op when the wrapper element is not present.
 */
export function initSettingsTabs() {
    const wrapper = document.querySelector('.settings-tabs-wrapper');
    if (!wrapper) return;

    const nav      = wrapper.querySelector('.settings-tabs');
    const leftBtn  = wrapper.querySelector('.settings-tab-arrow-left');
    const rightBtn = wrapper.querySelector('.settings-tab-arrow-right');

    /**
     * Recomputes whether the arrows should be visible and/or disabled
     * given the current scroll position.
     */
    const update = () => {
        const overflow = nav.scrollWidth > nav.clientWidth + 1;
        leftBtn.classList.toggle('visible',  overflow);
        rightBtn.classList.toggle('visible', overflow);

        leftBtn.classList.toggle('disabled',  nav.scrollLeft <= 1);
        rightBtn.classList.toggle('disabled', nav.scrollLeft + nav.clientWidth >= nav.scrollWidth - 1);
    };

    const scrollBy = (delta) => nav.scrollBy({ left: delta, behavior: 'smooth' });

    leftBtn.addEventListener('click',  () => scrollBy(-nav.clientWidth * 0.6));
    rightBtn.addEventListener('click', () => scrollBy( nav.clientWidth * 0.6));

    nav.addEventListener('scroll', update, { passive: true });
    window.addEventListener('resize', update);

    // The tab strip lives inside a modal that's `display: none` until the
    // user opens Settings. On first init the nav's clientWidth is 0, so the
    // overflow check can't know whether arrows are needed. Observe size
    // changes on the nav so `update` fires as soon as layout becomes real.
    if (typeof ResizeObserver !== 'undefined') {
        new ResizeObserver(update).observe(nav);
    }

    // Translate dominant vertical wheel movement into horizontal scroll
    // so a normal mouse wheel can scroll the tab strip.
    nav.addEventListener('wheel', (e) => {
        if (Math.abs(e.deltaY) <= Math.abs(e.deltaX)) return;
        nav.scrollLeft += e.deltaY;
        e.preventDefault();
    }, { passive: false });

    // Keep the active tab in view whenever it changes.
    nav.querySelectorAll('.nav-link').forEach(link => {
        link.addEventListener('shown.bs.tab', () =>
            link.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'center' }));

        // The Bootstrap `shown.bs.tab` event fires after the click handler
        // on some versions — fall back to a deferred scroll on click as well.
        link.addEventListener('click', () =>
            setTimeout(
                () => link.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'center' }),
                10,
            ));
    });

    // Kick off an initial update; the `setTimeout` fallback covers browsers
    // that haven't finished layout by the time `requestAnimationFrame` fires.
    requestAnimationFrame(update);
    setTimeout(update, 100);
}

/**
 * Shows or hides tab items and content panes based on the current cluster
 * role. Each element with a `data-role-visible="master standalone"` attribute
 * lists the roles for which it should render. Applied whenever the role
 * changes (cluster settings saved, initial page load, etc.).
 *
 * When the currently-active tab becomes hidden for the new role, activates
 * the first visible tab instead so the user isn't left staring at an empty
 * pane.
 *
 * @param {'master'|'node'|'standalone'} role
 */
export function applySettingsRoleVisibility(role) {
    const effective = role || 'standalone';
    document.querySelectorAll('[data-role-visible]').forEach(el => {
        const allowed = (el.dataset.roleVisible || '').split(/\s+/).filter(Boolean);
        el.style.display = allowed.includes(effective) ? '' : 'none';
    });

    // If the currently-active tab is no longer visible, switch to the first
    // visible sibling so the settings modal always shows something.
    const nav = document.querySelector('.settings-tabs');
    if (!nav) return;

    const activeBtn = nav.querySelector('.nav-link.active');
    const activeLi  = activeBtn?.closest('.nav-item');
    if (activeLi && activeLi.style.display === 'none') {
        const firstVisible = nav.querySelector('.nav-item:not([style*="display: none"]) .nav-link');
        firstVisible?.click();
    }
}
