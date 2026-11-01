/** Safe local messages; file/exception text never becomes HTML or retry authority. */
export const genericImportFailure = 'The import could not be applied. Your file and choices are retained.';
export const uncertainImport = 'The import outcome could not be confirmed. The earlier import may already have succeeded. Check My Trips for inspection; a visit or matching data cannot confirm its outcome. Further imports are held in this tab until you explicitly abandon the unresolved local attempt. This hold lasts only for this tab session: it is not shared with other tabs or devices, and closing the tab can lose it.';
export const abandonImportConfirmation = 'Abandon the unresolved local attempt? The earlier import may already have succeeded. This discards the retained file, review and choices without resending them. It does not cancel or roll back server work. The earlier outcome remains unknown. You must select a file again to begin a separate import; repeating the import may create another copy.';
const abandonedImport = 'The earlier import outcome is still unknown. Its local attempt was abandoned without cancelling or rolling back server work. Select a file to begin a separate import; repeating the import may create another copy.';
const safeCodes = new Set(['invalid_file', 'validation_failed', 'invalid_kml', 'import_review_required', 'import_review_stale', 'import_choices_invalid', 'import_loss_consent_required', 'generic_kml_processing_limit', 'generic_route_geometry_unsupported']);
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
/** Contradictory or extended outcome fields are not a safe negative acknowledgement. */
const onlyKeys = (payload, keys) => Object.keys(payload).every(key => keys.includes(key));

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
        if (reviewOnly && response.status === 200 && payload?.status === 'review' && typeof payload.review?.token === 'string'
            && uuid.test(payload.review.tripId) && Array.isArray(payload.review.planning)) return payload;
        if (!reviewOnly && response.status === 200 && payload?.status === 'success' && Array.isArray(payload.notices)
            && onlyKeys(payload, ['status', 'tripId', 'redirectUrl', 'notices']) && uuid.test(payload.tripId)
            && payload.redirectUrl === `/User/Trip/Edit/${payload.tripId}`) return payload;
        if (!reviewOnly && response.status === 200 && payload?.status === 'duplicate'
            && onlyKeys(payload, ['status', 'tripId']) && uuid.test(payload.tripId)) return payload;
        if (payload?.status === 'error' && onlyKeys(payload, ['status', 'code', 'message'])
            && safeCodes.has(payload.code) && response.status >= 400 && response.status < 500)
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
    const state = { file: null, review: null, choices: new Map(), busy: false, uncertain: false, cancelled: false, reviewStale: false, message: '' };
    let heldAttempt = null;
    try { heldAttempt = storage?.getItem(holdKey) || null; state.uncertain = heldAttempt !== null; }
    catch { state.uncertain = true; }
    const publish = () => handlers.onState?.(state);
    const setHold = value => {
        try {
            if (value) storage?.setItem(holdKey, value);
            else storage?.removeItem(holdKey);
            heldAttempt = value;
            return true;
        } catch { return false; }
    };
    // A reloaded page can abandon the old request while its former page is still awaiting a reply.
    const ownsHold = value => heldAttempt === value && (!storage || storage.getItem(holdKey) === value);
    const run = async (mode, reviewOnly = false, approval = null) => {
        if (!state.file || state.busy || state.uncertain || state.cancelled) return;
        state.busy = true;
        state.cancelled = false;
        const requestHold = reviewOnly ? null : `pending:${crypto.randomUUID()}`;
        if (!reviewOnly && !setHold(requestHold)) {
            state.uncertain = true; state.busy = false; publish(); return;
        }
        publish();
        const result = await requestTripImport(state.file, mode, handlers, approval, reviewOnly);
        try { if (!reviewOnly && !ownsHold(requestHold)) return; }
        catch { state.busy = false; state.uncertain = true; publish(); return; }
        state.busy = false;
        state.message = '';
        if (result.status === 'uncertain') state.uncertain = true;
        else if (!reviewOnly && !setHold(null)) state.uncertain = true;
        if (result.status === 'review') {
            state.review = result.review;
            state.reviewStale = false;
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
            if (!reviewOnly && mode === 'Upsert') state.reviewStale = true;
            state.message = result.code === 'import_review_stale' ? 'The Trip or file changed. Refresh the review, inspect the current content, then apply your choices.' : genericImportFailure;
            handlers.showError(state.message);
        }
        publish();
    };
    const api = {
        state,
        upload: async (file, mode = 'Auto') => {
            if (state.busy || state.uncertain) return;
            state.file = file; state.review = null; state.choices.clear(); state.cancelled = false; state.reviewStale = false;
            await run(mode);
        },
        update: () => run('Upsert', true),
        apply: () => {
            if (!state.review || state.reviewStale) return;
            const choices = state.review.planning.flatMap(row => {
                const permit = row.choices.find(c => c.action === state.choices.get(row.key));
                return permit ? [permit.token] : [];
            });
            return run('Upsert', false, { token: state.review.token, choices });
        },
        choose: (key, action) => { if (!state.busy && !state.uncertain) { state.choices.set(key, action); publish(); } },
        createCopy: () => run('CreateNew'),
        cancel: () => { if (!state.busy) { state.cancelled = true; publish(); } },
        /** Explicit confirmation retires local intent only; no server outcome or cancellation is inferred. */
        abandon: confirmed => {
            if (confirmed !== true || !state.uncertain || state.busy) return false;
            try { if (!ownsHold(heldAttempt)) return false; }
            catch { return false; }
            if (!setHold(null)) return false;
            state.file = null; state.review = null; state.choices.clear();
            state.uncertain = false; state.cancelled = false; state.reviewStale = false;
            state.message = abandonedImport;
            publish();
            return true;
        }
    };
    if (state.uncertain) state.message = uncertainImport;
    return api;
};
