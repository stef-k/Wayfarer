/** Shared Trip sidebar arithmetic; preferences describe intent, never a viewport clamp. */
export const SIDEBAR_MIN = 420;
export const SIDEBAR_DESKTOP = 900;
/** @typedef {{mode: 'standard'|'half'|'full'|'custom', width: number}} SidebarPreference */
/** @returns {SidebarPreference} */
export const defaultSidebarPreference = () => ({ mode: 'standard', width: 500 });

/** Missing, malformed and blocked storage all retain the familiar Standard width. */
export const readSidebarPreference = (key, storage = () => window.localStorage) => {
    try {
        const value = JSON.parse(storage().getItem(key));
        if (!value || !['standard', 'half', 'full', 'custom'].includes(value.mode)) return defaultSidebarPreference();
        if (value.mode === 'custom' && (!Number.isFinite(value.width) || value.width < SIDEBAR_MIN)) return defaultSidebarPreference();
        return { mode: value.mode, width: value.mode === 'custom' ? value.width : 500 };
    } catch { return defaultSidebarPreference(); }
};

/** A denied write cannot prevent the active session from changing layout. */
export const writeSidebarPreference = (key, preference, storage = () => window.localStorage) => {
    try { storage().setItem(key, JSON.stringify(preference)); } catch { /* Session state remains authoritative. */ }
};

/** Presets follow the workspace; only rendering clamps an intended Custom width. */
export const sidebarWidth = (preference, workspace) => {
    const available = Math.max(0, workspace);
    const intended = preference.mode === 'full' ? available : preference.mode === 'half' ? available / 2
        : preference.mode === 'custom' ? preference.width : 500;
    return Math.min(available, Math.max(Math.min(SIDEBAR_MIN, available), intended));
};

/** Keyboard resizing uses 20px steps (Shift: 100px), Home minimum and End workspace. */
export const sidebarKeyWidth = (event, width, workspace) => {
    const step = event.shiftKey ? 100 : 20;
    const next = { ArrowLeft: width - step, ArrowRight: width + step, Home: SIDEBAR_MIN, End: workspace }[event.key];
    return next === undefined ? null : Math.min(workspace, Math.max(Math.min(SIDEBAR_MIN, workspace), next));
};

/**
 * Owns capture and one reversible resize attempt; hiding/breakpoint changes cancel it.
 * @param {HTMLElement} handle
 * @param {{getPreference:()=>SidebarPreference, getWidth:()=>number, getMax:()=>number,
 * change:(preference:SidebarPreference)=>void, commit:()=>void, active:(active:boolean)=>void}} options
 */
export const bindSidebarResize = (handle, options) => {
    let attempt = null;
    const finish = (cancel = false) => {
        if (!attempt) return;
        const previous = attempt;
        attempt = null;
        if (handle.hasPointerCapture(previous.id)) handle.releasePointerCapture(previous.id);
        if (cancel) options.change(previous.preference);
        else options.commit();
        options.active(false);
    };
    const down = event => {
        if (event.button !== 0 || attempt) return;
        event.preventDefault();
        event.stopPropagation();
        handle.focus({ preventScroll: true });
        attempt = { id: event.pointerId, x: event.clientX, width: options.getWidth(), preference: { ...options.getPreference() } };
        handle.setPointerCapture(event.pointerId);
        options.active(true);
    };
    const move = event => {
        if (!attempt || event.pointerId !== attempt.id) return;
        event.preventDefault();
        const width = Math.min(options.getMax(), Math.max(SIDEBAR_MIN, attempt.width + event.clientX - attempt.x));
        options.change({ mode: 'custom', width });
    };
    const up = event => { if (event.pointerId === attempt?.id) finish(); };
    const cancel = () => finish(true);
    const key = event => {
        if (event.key === 'Escape' && attempt) { event.preventDefault(); cancel(); return; }
        const width = sidebarKeyWidth(event, options.getWidth(), options.getMax());
        if (width === null) return;
        event.preventDefault();
        event.stopPropagation();
        finish(true);
        options.change({ mode: 'custom', width });
        options.commit();
    };
    const listeners = { pointerdown: down, pointermove: move, pointerup: up, pointercancel: cancel, lostpointercapture: cancel, keydown: key };
    Object.entries(listeners).forEach(([name, handler]) => handle.addEventListener(name, handler));
    window.addEventListener('blur', cancel);
    return {
        cancel,
        dispose: () => {
            cancel();
            Object.entries(listeners).forEach(([name, handler]) => handle.removeEventListener(name, handler));
            window.removeEventListener('blur', cancel);
        }
    };
};
