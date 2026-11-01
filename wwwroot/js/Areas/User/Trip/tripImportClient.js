/** Safe local messages; file/exception text never becomes HTML or retry authority. */
export const genericImportFailure = 'The import could not be applied. Your file and choices are retained.';
export const uncertainImport = 'The import outcome could not be confirmed. Check My Trips before starting another import. Sending this file again is held in this tab.';
const safeCodes = new Set(['invalid_file', 'validation_failed', 'invalid_kml', 'import_review_required', 'import_review_stale', 'import_choices_invalid', 'import_loss_consent_required', 'generic_kml_processing_limit', 'generic_route_geometry_unsupported']);
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** One multipart request, with exact selected bytes and protected per-target intent. */
export const requestTripImport = async (file, mode, handlers, approval = null, reviewOnly = false) => {
    const body = new FormData();
    body.append('file', file);
    body.append('mode', mode);
    if (approval) {
        body.append('reviewToken', approval.token);
        approval.choices.forEach(choice => body.append('choices', choice));
    }
    try {
        const response = await handlers.fetchImpl.call(globalThis, reviewOnly ? '/User/Trip/ReviewImport' : '/User/Trip/Import', {
            method: 'POST', credentials: 'same-origin', cache: 'no-store',
            headers: { RequestVerificationToken: document.querySelector('input[name="__RequestVerificationToken"]')?.value || '' }, body
        });
        const payload = await response.json();
        if (response.redirected) return { status: reviewOnly ? 'error' : 'uncertain' };
        if (reviewOnly && response.ok && payload?.status === 'review' && typeof payload.review?.token === 'string'
            && uuid.test(payload.review.tripId) && Array.isArray(payload.review.planning)) return payload;
        if (!reviewOnly && payload?.status === 'success' && (response.ok ?? true) && uuid.test(payload.tripId)
            && payload.redirectUrl === `/User/Trip/Edit/${payload.tripId}`) return payload;
        if (!reviewOnly && payload?.status === 'duplicate' && (response.ok ?? true) && uuid.test(payload.tripId)) return payload;
        if (payload?.status === 'error' && safeCodes.has(payload.code) && response.status >= 400 && response.status < 500)
            return { status: 'error', code: payload.code };
        return { status: reviewOnly ? 'error' : 'uncertain' };
    } catch { return { status: reviewOnly ? 'error' : 'uncertain' }; }
};

/** Compatibility entry point for callers that do not own a mounted replacement review. */
export const submitTripImport = async (file, mode, handlers) => {
    const result = await requestTripImport(file, mode, handlers);
    if (result.status === 'success') {
        handlers.showNotices?.(Array.isArray(result.notices) ? result.notices : []);
        handlers.onRedirect(result.redirectUrl);
    } else if (result.status === 'duplicate') handlers.onDuplicate();
    else handlers.showError(result.status === 'uncertain' ? uncertainImport : genericImportFailure);
};

/** Owns the selected File, review and choices; a lost mutating reply holds every import action across reload. */
export const createTripImporter = (handlers, { storage = null, owner = '' } = {}) => {
    const holdKey = `wayfarer:native-import-pending:${owner}`;
    const state = { file: null, review: null, choices: new Map(), busy: false, uncertain: false, cancelled: false, message: '' };
    try { state.uncertain = storage?.getItem(holdKey) === 'pending'; }
    catch { state.uncertain = true; }
    const publish = () => handlers.onState?.(state);
    const setHold = pending => {
        try {
            if (pending) storage?.setItem(holdKey, 'pending');
            else storage?.removeItem(holdKey);
            return true;
        } catch { return false; }
    };
    const run = async (mode, reviewOnly = false, approval = null) => {
        if (!state.file || state.busy || state.uncertain) return;
        state.busy = true;
        state.cancelled = false;
        if (!reviewOnly && !setHold(true)) {
            state.uncertain = true; state.busy = false; publish(); return;
        }
        publish();
        const result = await requestTripImport(state.file, mode, handlers, approval, reviewOnly);
        state.busy = false;
        state.message = '';
        if (result.status === 'uncertain') state.uncertain = true;
        else if (!reviewOnly && !setHold(false)) state.uncertain = true;
        if (result.status === 'review') {
            state.review = result.review;
            // Retain explicit intent on fresh review; the user must inspect and apply again.
            for (const row of result.review.planning) {
                const previous = state.choices.get(row.key);
                if (!row.choices.some(c => c.action === previous)) state.choices.set(row.key, row.state === 'removed' ? '' : 'keep');
            }
        } else if (result.status === 'success') {
            handlers.showNotices?.(Array.isArray(result.notices) ? result.notices : []);
            handlers.onRedirect(result.redirectUrl);
        } else if (result.status === 'duplicate') handlers.onDuplicate();
        else if (result.status === 'error') {
            state.message = result.code === 'import_review_stale' ? 'The Trip or file changed. Refresh the review, inspect the current content, then apply your choices.' : genericImportFailure;
            handlers.showError(state.message);
        }
        publish();
    };
    const api = {
        state,
        upload: async (file, mode = 'Auto') => {
            if (state.busy || state.uncertain) return;
            state.file = file; state.review = null; state.choices.clear();
            await run(mode);
        },
        update: () => run('Upsert', true),
        apply: () => {
            if (!state.review) return;
            const choices = state.review.planning.flatMap(row => {
                const permit = row.choices.find(c => c.action === state.choices.get(row.key));
                return permit ? [permit.token] : [];
            });
            return run('Upsert', false, { token: state.review.token, choices });
        },
        choose: (key, action) => { if (!state.busy && !state.uncertain) { state.choices.set(key, action); publish(); } },
        createCopy: () => run('CreateNew'),
        cancel: () => { if (!state.busy) { state.cancelled = true; publish(); } }
    };
    if (state.uncertain) state.message = uncertainImport;
    return api;
};
