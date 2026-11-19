// A navigation can consult several beforeunload listeners. All receive the same event.
const key = Symbol.for('wayfarer.languageNavigation');
// Vite and the regular shell can load separate copies of this module.
const state = globalThis[key] ??= { allowedUnload: false, allowedEvents: new WeakSet(), draftOwners: new Set() };

/** Registers a read-only query of a mounted workspace's draft state. */
export const registerLanguageDraft = (owner) => {
    state.draftOwners.add(owner);
    return () => state.draftOwners.delete(owner);
};

/** Queries only current owners and never invokes an editor's close or discard workflow. */
export const hasLanguageDraft = () => [...state.draftOwners].some(owner => owner());

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
