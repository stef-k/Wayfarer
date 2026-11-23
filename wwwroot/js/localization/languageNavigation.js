// A navigation can consult several beforeunload listeners. All receive the same event.
const key = Symbol.for('wayfarer.languageNavigation');
// Vite and the regular shell can load separate copies of this module.
const state = globalThis[key] ??= { allowedUnload: false, allowedEvents: new WeakSet(), draftOwners: new Set(), holds: new Set() };

/** Registers a read-only query of a mounted workspace's draft state. */
export const registerLanguageDraft = (owner) => {
    state.draftOwners.add(owner);
    return () => state.draftOwners.delete(owner);
};

/** Allows the selector to appear after the document's workspace has installed its owners. */
export const enableWorkspaceLanguage = () => {
    state.workspaceReady = true;
    window.dispatchEvent(new Event('wayfarer:language-availability'));
};

/** Checks readiness even if the workspace mounted before the shell selector initialized. */
export const isWorkspaceLanguageReady = () => state.workspaceReady === true;

/** Queries only current owners and never invokes an editor's close or discard workflow. */
export const hasLanguageDraft = () => [...state.draftOwners].some(owner => owner());

/** Temporarily withholds the preference action while a feature owns a required inspection. */
export const holdLanguageNavigation = () => {
    const hold = Symbol();
    state.holds.add(hold);
    window.dispatchEvent(new Event('wayfarer:language-availability'));
    return () => {
        state.holds.delete(hold);
        window.dispatchEvent(new Event('wayfarer:language-availability'));
    };
};

/** A pending operation or unresolved mounted inspection takes precedence over discard consent. */
export const isLanguageNavigationHeld = () => state.holds.size > 0;

/** Explicitly permits the one unload initiated by the successful preference request. */
export const navigateInLanguage = (destination, navigate = url => url === location.href ? location.reload() : location.assign(url)) => {
    state.allowedUnload = true;
    try {
        navigate(destination);
    } catch (error) {
        state.allowedUnload = false;
        throw error;
    }
};

/** Consumes permission once while allowing every owner to recognize that same unload event. */
export const isLanguageUnload = (event) => {
    if (state.allowedUnload) {
        state.allowedUnload = false;
        state.allowedEvents.add(event);
    }
    return state.allowedEvents.has(event);
};

/** Resolves only same-origin destinations supplied by the current document. */
export const languageDestination = (current, safeGet) => {
    const from = new URL(current);
    const target = safeGet ? new URL(safeGet, from) : from;
    return target.origin === from.origin ? target.href : null;
};
