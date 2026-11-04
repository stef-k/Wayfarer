import { bindSidebarResize, readSidebarPreference, sidebarWidth, SIDEBAR_MIN, SIDEBAR_DESKTOP, writeSidebarPreference } from './sidebarWidth.js';

/** Owns the live Razor panels without replacing their hierarchy, search or selection owners. */
export const createViewerSidebar = (root, map, { isEmbed, isPrint }) => {
    const legend = root.querySelector('#sidebar-primary');
    const details = root.querySelector('#sidebar-secondary');
    const key = 'wayfarer.trip-viewer.sidebar';
    let preference = readSidebarPreference(key);
    let collapsed = isEmbed || isPrint;
    let hidden = false;
    let detailsActive = false;
    let appliedOffset = 0;
    const desktop = window.matchMedia(`(min-width: ${SIDEBAR_DESKTOP + 1}px)`);
    const phone = window.matchMedia('(max-width: 575.98px)');
    const controls = [];
    const makeControls = panel => {
        const bar = document.createElement('div');
        bar.className = 'viewer-sidebar-controls bg-body border-bottom';
        bar.innerHTML = `<label class="viewer-sidebar-width">Sidebar width <select aria-label="Sidebar width" class="form-select form-select-sm">
            <option value="standard">Standard</option><option value="half">Half</option><option value="full">Full</option>
            <option value="custom" disabled>Custom</option></select></label>
            <button type="button" class="btn btn-outline-secondary btn-sm" aria-label="Hide sidebar" aria-expanded="true" aria-controls="sidebar-primary sidebar-secondary">Hide</button>`;
        panel.prepend(bar);
        const select = bar.querySelector('select');
        select.addEventListener('change', () => {
            resizer.cancel();
            preference = { mode: select.value, width: 500 };
            writeSidebarPreference(key, preference);
            render();
        });
        bar.querySelector('button').addEventListener('click', hide);
        controls.push(select);
    };
    const showButton = document.createElement('button');
    showButton.id = 'btn-show-sidebar';
    showButton.className = 'btn btn-light border fw-semibold shadow-lg';
    showButton.textContent = 'MAP LEGEND';
    showButton.setAttribute('aria-label', 'Show sidebar');
    showButton.setAttribute('aria-expanded', 'false');
    showButton.setAttribute('aria-controls', 'sidebar-primary sidebar-secondary');
    root.append(showButton);
    const handle = document.createElement('div');
    handle.className = 'viewer-sidebar-resizer';
    handle.tabIndex = 0;
    handle.setAttribute('role', 'separator');
    handle.setAttribute('aria-label', 'Resize sidebar');
    handle.setAttribute('aria-orientation', 'vertical');
    handle.setAttribute('aria-controls', 'sidebar-primary sidebar-secondary');
    handle.title = 'Resize sidebar: arrows 20px, Shift arrows 100px, Home minimum, End full width, Escape cancels drag';
    root.append(handle);
    const content = document.createElement('div');
    content.dataset.detailsContent = '';
    details.append(content);
    const width = () => desktop.matches ? sidebarWidth(preference, root.clientWidth) : Math.min(500, root.clientWidth);
    const visible = () => !isPrint && !hidden && (detailsActive || !collapsed);
    // Full and phone panels overlay a live, nonzero map instead of offsetting an invisible viewport.
    const visibleWidth = () => !visible() || phone.matches || width() >= root.clientWidth ? 0 : width();

    /** Delta-only synchronous offsets cannot leave obsolete transition callbacks behind. */
    const render = () => {
        root.style.setProperty('--legend-w', `${width()}px`);
        root.dataset.sidebarMode = preference.mode;
        legend.dataset.collapsed = String(hidden || collapsed);
        legend.inert = !visible() || detailsActive;
        legend.setAttribute('aria-hidden', String(legend.inert));
        details.classList.toggle('open', detailsActive && visible());
        details.inert = !detailsActive || !visible();
        details.setAttribute('aria-hidden', String(details.inert));
        showButton.hidden = isPrint || visible();
        handle.hidden = isPrint || !desktop.matches || !visible();
        handle.style.left = `${Math.max(0, width() - 8)}px`;
        handle.setAttribute('aria-valuemin', String(SIDEBAR_MIN));
        handle.setAttribute('aria-valuemax', String(root.clientWidth));
        handle.setAttribute('aria-valuenow', String(Math.round(width())));
        handle.setAttribute('aria-valuetext', `${preference.mode}, ${Math.round(width())} pixels`);
        controls.forEach(select => { select.value = preference.mode; });
        const nextOffset = Math.round(visibleWidth() / 2);
        const delta = nextOffset - appliedOffset;
        appliedOffset = nextOffset;
        if (delta) map.stop();
        map.invalidateSize({ animate: false });
        if (delta) map.panBy([-delta, 0], { animate: false });
    };
    /** Hiding retains details content and selection; focus moves to the reachable Show action. */
    function hide() {
        resizer.cancel();
        hidden = true;
        render();
        showButton.focus({ preventScroll: true });
    }
    makeControls(legend.querySelector('header'));
    makeControls(details);
    const resizer = bindSidebarResize(handle, {
        getPreference: () => preference, getWidth: width, getMax: () => root.clientWidth,
        change: value => { preference = value; render(); },
        commit: () => writeSidebarPreference(key, preference),
        active: value => root.classList.toggle('sidebar-resizing', value)
    });
    showButton.addEventListener('click', () => {
        resizer.cancel();
        hidden = false;
        collapsed = false;
        render();
        (detailsActive ? controls[1] : controls[0]).focus({ preventScroll: true });
    });
    const resize = () => { resizer.cancel(); render(); };
    const observer = new ResizeObserver(resize);
    observer.observe(root);
    desktop.addEventListener('change', resize);
    phone.addEventListener('change', resize);
    // A permalink already describes the actual camera, including its sidebar offset.
    const query = new URLSearchParams(location.search);
    if (query.has('lat') && query.has('lon')) appliedOffset = Math.round(visibleWidth() / 2);
    render();
    return {
        visibleWidth,
        /** Offset the destination itself; navigation needs no delayed pan callback. */
        centerFor: (target, zoom) => map.unproject(map.project(target, zoom).subtract([Math.round(visibleWidth() / 2), 0]), zoom),
        openDetails: html => {
            resizer.cancel();
            content.innerHTML = html;
            detailsActive = true;
            hidden = false;
            render();
        },
        closeDetails: () => {
            resizer.cancel();
            detailsActive = false;
            render();
            (visible() ? controls[0] : showButton).focus({ preventScroll: true });
        },
        dispose: () => {
            resizer.dispose();
            observer.disconnect();
            desktop.removeEventListener('change', resize);
            phone.removeEventListener('change', resize);
        }
    };
};
