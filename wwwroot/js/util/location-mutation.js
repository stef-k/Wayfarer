/** Protected Location client: durable originals/drafts, exact attempts, verified receipts and read-only recovery. */
const contract = 'location-mutation-1';
const normalization = 'location-values-1';
const encoder = new TextEncoder();
const subject = () => document.getElementById('browser-antiforgery')?.dataset.subjectId;
const partition = id => `${contract}:${location.origin}:${subject()}:${id}`;
const retainAttempt = (id, attempt) => {
    const latest = locationWork(id);
    const index = latest.attempts.findIndex(item => item.actionId === attempt.actionId);
    if (index < 0) throw new Error('Local attempt evidence is unavailable.');
    latest.attempts[index] = attempt;
    persist(id, latest);
};
const editorIdentity = () => {
    const key = `${contract}:editor`;
    let identity = sessionStorage.getItem(key);
    if (!identity) { identity = crypto.randomUUID(); sessionStorage.setItem(key, identity); }
    return identity;
};
const token = () => document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
const exactKeys = (value, required, optional = []) => value && typeof value === 'object' && !Array.isArray(value)
    && required.every(key => Object.hasOwn(value, key)) && Object.keys(value).every(key => [...required, ...optional].includes(key));
/** Validates Gregorian UTC wire strings and preserves all seven digits emitted by .NET DateTime for ordering. */
const utcInstantKey = value => {
    if (typeof value !== 'string') return null;
    const match = /^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d{1,7}))?Z$/.exec(value);
    if (!match || value.startsWith('0000')) return null;
    const instant = new Date(`${match[1]}Z`);
    if (!Number.isFinite(instant.getTime()) || instant.toISOString().slice(0, 19) !== match[1]) return null;
    return `${match[1]}.${(match[2] || '').padEnd(7, '0')}Z`;
};

/** Only fully bound, typed no-row observations can authorize retry or exclude old future effects. */
const validateLocationObservation = (receipt, attempt) => {
    const expired = receipt?.code === 'operationHistoryExpired' && receipt.futureEffects === false && receipt.historicalOutcome === 'unknown';
    const waiting = receipt?.code === 'operationNotObserved' && receipt.futureEffects === true;
    if (!expired && !waiting) return false;
    const observed = utcInstantKey(receipt.observedAtUtc), expires = utcInstantKey(receipt.expiresAtUtc);
    return !!observed && !!expires && receipt.contract === contract && receipt.subjectId === attempt.subjectId
        && receipt.locationId === attempt.id && receipt.baseId === attempt.baseId && receipt.actionId === attempt.actionId
        && receipt.state === 'unknown' && receipt.expiresAtUtc === attempt.expiresAtUtc
        && exactKeys(receipt, ['contract', 'subjectId', 'locationId', 'baseId', 'actionId', 'observedAtUtc', 'state', 'code', 'futureEffects', 'expiresAtUtc', ...(expired ? ['historicalOutcome'] : [])])
        && (expired ? observed >= expires : observed < expires);
};
const persist = (id, state) => {
    if (state.draft) state.draftsByEditor = { ...state.draftsByEditor, [editorIdentity()]: state.draft };
    if (state.settledGeneration) state.settledByEditor = { ...state.settledByEditor, [editorIdentity()]: state.settledGeneration };
    const bytes = JSON.stringify(state);
    localStorage.setItem(partition(id), bytes);
    if (localStorage.getItem(partition(id)) !== bytes) throw new Error('Draft was not saved. Keep this page open.');
};
/** Existing evidence is account/server partitioned and never discarded because of age. */
export const locationWork = id => {
    const state = JSON.parse(localStorage.getItem(partition(id)) || '{"attempts":[]}');
    state.draft = state.draftsByEditor?.[editorIdentity()] || state.draft;
    state.settledGeneration = state.settledByEditor?.[editorIdentity()] || state.settledGeneration;
    return state;
};

/** Seven framed exact UTF-8 values; this digest is computed before the first possible handoff. */
export const locationRequestFingerprint = async (owner, id, baseId, actionId, adapter, body) => {
    const frames = [contract.replace('mutation-1', 'mutation-request-1'), owner, String(id), baseId, actionId, adapter, body].map(value => encoder.encode(value));
    const bytes = new Uint8Array(frames.reduce((size, frame) => size + 4 + frame.length, 0));
    let offset = 0;
    for (const frame of frames) {
        new DataView(bytes.buffer).setUint32(offset, frame.length); offset += 4;
        bytes.set(frame, offset); offset += frame.length;
    }
    const digest = await crypto.subtle.digest('SHA-256', bytes);
    return `sha256:${Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('')}`;
};

/** Ordinary 404/auth/transport failures supply no absence or deletion proof. */
export const readLocationDetail = async id => {
    const response = await fetch(`/api/location/history/${id}?viewerZone=UTC`, { cache: 'no-store', credentials: 'same-origin' });
    if (!response.ok) throw new Error(`Location detail unavailable (${response.status}). Your draft is retained.`);
    const detail = await response.json();
    const evidence = detail.locationMutation;
    if (detail.subjectId !== subject() || detail.record?.locationId !== id || evidence?.contract !== contract
        || evidence.normalization !== normalization || !['pending', 'closed'].includes(evidence.originalCreateAddressPublication))
        throw new Error('Location evidence could not be verified. Your draft is retained.');
    return detail;
};

/** Strict known-row acknowledgement validation against locally persisted pre-send evidence. */
export const validateLocationReceipt = (receipt, attempt) => {
    const common = ['contract', 'normalization', 'adapter', 'subjectId', 'locationId', 'uuid', 'baseId', 'actionId', 'kind', 'fingerprint', 'receiptRetainUntilUtc', 'state', 'futureEffects'];
    const equal = receipt?.contract === contract && receipt.normalization === normalization && receipt.adapter === attempt.adapter
        && receipt.subjectId === attempt.subjectId && receipt.locationId === attempt.id && receipt.uuid === attempt.uuid
        && receipt.baseId === attempt.baseId && receipt.actionId === attempt.actionId && receipt.kind === attempt.kind
        && receipt.fingerprint === attempt.fingerprint && !!utcInstantKey(receipt.receiptRetainUntilUtc);
    if (!equal) return false;
    if (receipt.state === 'pending') return exactKeys(receipt, [...common, 'observedAtUtc', 'executeBeforeUtc', 'retryAfterSeconds'])
        && receipt.futureEffects === true && receipt.retryAfterSeconds === 2
        && !!utcInstantKey(receipt.observedAtUtc) && !!utcInstantKey(receipt.executeBeforeUtc);
    if (!utcInstantKey(receipt.completedAtUtc) || receipt.futureEffects !== false) return false;
    if (receipt.state === 'rejected') return exactKeys(receipt, [...common, 'completedAtUtc', 'code'], ['staleGroups'])
        && ['locationBaseStale', 'operationExecutionExpired', 'mutationInvalid', 'activityInvalid', 'baseUnavailable', 'baseNormalizationUnsupported', 'locationDeleted', 'locationUnavailable'].includes(receipt.code)
        && (!Object.hasOwn(receipt, 'staleGroups') || Array.isArray(receipt.staleGroups) && receipt.staleGroups.every(group => ['uuid', 'position', 'address', 'eventUtc', 'activityTypeId', 'notesHtml', 'altitude', 'content'].includes(group)));
    return receipt.state === 'applied' && exactKeys(receipt, [...common, 'completedAtUtc', 'changed', 'detailUrl', ...(attempt.kind === 'update' ? ['addressOutcome', 'providerOutcome'] : [])])
        && typeof receipt.changed === 'boolean' && receipt.detailUrl === (attempt.kind === 'delete' ? null : `/api/location/history/${attempt.id}`)
        && (attempt.kind === 'delete' || ['unchanged', 'replaced', 'cleared', 'manual'].includes(receipt.addressOutcome)
            && ['notRequested', 'succeeded', 'unavailable'].includes(receipt.providerOutcome));
};

/** Persist a newer draft independently of an older immutable attempt, including across reloads. */
export const retainLocationDraft = (id, draft) => {
    if (!subject()) throw new Error('Your session has ended. Keep your draft before leaving.');
    const state = locationWork(id);
    const signature = JSON.stringify(draft);
    if (state.draft?.signature !== signature) state.draft = { signature, generation: crypto.randomUUID(), value: draft };
    persist(id, state);
    return state.draft.generation;
};

/** One cross-tab owner serializes new identities and possible handoffs; failed persistence prevents sending. */
export const saveLocationMutation = async (id, detail, kind, changes, expected, draft = {}) => {
    if (!navigator.locks) throw new Error('This browser cannot safely save this draft. Keep it open in a supported browser.');
    return navigator.locks.request(partition(id), async () => {
        await validateLocationWork(id, locationWork(id));
        const generation = retainLocationDraft(id, { kind, changes, expected, detail, draft });
        let state = locationWork(id);
        let attempt = state.attempts.find(item => item.actionId === state.active);
        if (!attempt && state.settledGeneration === generation) {
            const settled = state.attempts.find(item => item.generation === generation && item.receipt?.state === 'applied');
            if (settled) return acceptResponse(id, state, settled, settled.receipt);
        }
        let renewedDetail = null;
        if (attempt?.mayHaveAttempted === false) {
            // The persisted pre-handoff fence proves this body never left this browser.
            attempt.cancelledBeforeHandoff = true; retainAttempt(id, attempt);
            state = locationWork(id); state.active = null; persist(id, state); attempt = null;
        }
        if (attempt) {
            const recovered = await inspectAttempt(id, state, attempt, true);
            if (recovered.excluded) {
                const fresh = await readLocationDetail(id);
                if (!originalsMatch(expected, fresh.locationMutation.baseValues)) {
                    if (kind === 'update' && selectedSimpleStateSatisfied(changes, expected, fresh.locationMutation.baseValues)) {
                        const latest = locationWork(id);
                        if (latest.draft?.generation !== generation) return { success: false, terminal: true, message: 'Newer typing is retained and still needs review.' };
                        latest.currentObservation = { generation, observedAtUtc: fresh.observedAtUtc, historicalOutcome: 'unknown' };
                        latest.active = null; latest.settledGeneration = generation; latest.fresh = fresh; persist(id, latest);
                        return { success: true, currentStateSatisfied: true, detail: fresh, message: 'Current state satisfies this draft; historical outcome remains unknown.' };
                    }
                    return { success: false, message: 'Old future effects are excluded; current content differs from your originals. Your draft is retained. Review current history before an informed new conditional edit.', terminal: true };
                }
                // Historical outcome stays Unknown. Re-admit the retained originals under fresh authority.
                const latest = locationWork(id);
                latest.active = null; persist(id, latest); renewedDetail = fresh;
            } else {
                if (!recovered.terminal) return recovered;
                if (attempt.generation === generation) return recovered;
                const fresh = await readLocationDetail(id);
                if (!originalsMatch(expected, fresh.locationMutation.baseValues))
                    return { success: false, terminal: true, message: 'The earlier action is resolved. Your newer draft is retained; review it against current history before saving.', earlierResolved: true };
                const latest = locationWork(id); latest.active = null; persist(id, latest); renewedDetail = fresh;
            }
        }
        // Renew only never-attempted authority; expected originals are deliberately retained unchanged.
        let authority = (renewedDetail || detail).locationMutation;
        if (Date.parse(authority.expiresAtUtc) <= Date.now()) authority = (await readLocationDetail(id)).locationMutation;
        const body = JSON.stringify({ contract, normalization, adapter: 'web-edit', baseHandle: authority.baseHandle, kind, expected, ...(kind === 'update' ? { changes } : {}) });
        const actionId = crypto.randomUUID();
        attempt = { id, subjectId: subject(), uuid: detail.locationMutation.baseValues.uuid.value, baseId: authority.baseId, actionId,
            adapter: 'web-edit', kind, body, baseHandle: authority.baseHandle, expiresAtUtc: authority.expiresAtUtc, generation, mayHaveAttempted: false,
            fingerprint: await locationRequestFingerprint(subject(), id, authority.baseId, actionId, 'web-edit', body) };
        state = locationWork(id); state.attempts.push(attempt); state.active = actionId; persist(id, state);
        // Durable before fetch; a thrown fetch never restores never-attempted status.
        attempt.mayHaveAttempted = true; persist(id, state);
        try {
            const response = await fetch(attemptUrl(attempt), { method: 'POST', credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json', RequestVerificationToken: token() }, body: attempt.body });
            return await acceptResponse(id, state, attempt, await response.json());
        } catch {
            return inspectAttempt(id, state, attempt);
        }
    });
};
const attemptUrl = attempt => `/api/location/${attempt.id}/mutations/${attempt.baseId}/${attempt.actionId}`;

/** Recovery does not replay an uncertain body or invent a new action; terminal evidence survives failed detail reads. */
const inspectAttempt = async (id, state, attempt, retry = false) => {
    try {
        if (attempt.receipt?.futureEffects === false) return acceptResponse(id, state, attempt, attempt.receipt);
        const response = await fetch(attemptUrl(attempt), { cache: 'no-store', credentials: 'same-origin', headers: { 'X-Location-Base': attempt.baseHandle } });
        const receipt = await response.json();
        if (validateLocationObservation(receipt, attempt) && receipt.code === 'operationHistoryExpired') {
            // Exclusion alone cannot authorize rebasing or a replacement. Keep all command/original evidence.
            attempt.exclusion = receipt; retainAttempt(id, attempt);
            return { success: false, excluded: true, message: 'Historical outcome unknown; old future effects are excluded. Your draft and originals are retained.' };
        }
        if (retry && validateLocationObservation(receipt, attempt) && receipt.code === 'operationNotObserved') {
            const retried = await fetch(attemptUrl(attempt), { method: 'POST', credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json', RequestVerificationToken: token() }, body: attempt.body });
            return acceptResponse(id, state, attempt, await retried.json());
        }
        return await acceptResponse(id, state, attempt, receipt);
    } catch { return { success: false, message: 'Outcome unknown. Your draft and exact attempt are retained. Save again to check recovery.' }; }
};

/** Only a matching generation can publish success; an old receipt never consumes newer typing. */
const acceptResponse = async (id, state, attempt, receipt) => {
    if (!validateLocationReceipt(receipt, attempt)) return { success: false, message: 'Outcome could not be verified. Your draft and attempt are retained.' };
    attempt.receipt = receipt; retainAttempt(id, attempt);
    if (receipt.state === 'pending') return { success: false, message: 'Save is pending. Your draft is retained. Save again to check recovery.' };
    if (receipt.state === 'rejected') return { success: false, terminal: true, message: `Save rejected (${receipt.code}). Your draft and originals are retained. Review current history.`, receipt };
    let fresh = null;
    if (attempt.kind !== 'delete') {
        try { fresh = await readLocationDetail(id); }
        catch { return { success: false, terminal: true, applied: true, message: 'The save committed. Current detail is unavailable; your draft and receipt are retained. Save again to refresh.' }; }
    }
    const latest = locationWork(id);
    if (latest.draft?.generation !== attempt.generation) return { success: false, terminal: true, applied: true, message: 'The earlier save committed. Newer typing is retained and still needs saving.' };
    latest.active = null; latest.settledGeneration = attempt.generation; latest.fresh = fresh;
    persist(id, latest);
    return { success: true, terminal: true, receipt, detail: fresh, message: attempt.kind === 'delete' ? 'Location deleted.' : 'Location saved.' };
};

/** Observe deletion content before showing the existing confirmation; never refresh proof merely to pass stale consent. */
export const prepareLocationDeletion = async id => {
    const preview = await navigator.locks.request(partition(id), async () => {
        const existing = locationWork(id);
        await validateLocationWork(id, existing);
        const active = existing.attempts.find(item => item.actionId === existing.active);
        const retained = existing.draft?.value;
        const original = active && retained?.kind === 'delete' ? retained.detail : null;
        const recovered = active ? await inspectAttempt(id, existing, active) : null;
        const renew = recovered?.excluded || recovered?.receipt?.state === 'rejected';
        return { detail: renew || !original ? await readLocationDetail(id) : original, original, renew,
            historicalUnknown: recovered?.excluded === true };
    });
    const { detail } = preview;
    const execute = async () => {
        // The existing confirmation explicitly consents to inspected current content, after predecessor exclusion.
        if (preview.renew) await adoptLocationOriginals(id, detail);
        return saveLocationMutation(id, detail, 'delete', null, {
            uuid: detail.locationMutation.baseValues.uuid, content: detail.locationMutation.baseValues.deleteContent
        });
    };
    const prior = preview.renew ? `The earlier action cannot produce future effects${preview.historicalUnknown ? '; its historical outcome remains unknown' : ''}. This is a new conditional deletion. Previously observed detail: ${JSON.stringify(preview.original?.record)}. ` : '';
    execute.message = `${prior}Delete the observed Location #${id}? This cannot be undone. Stored detail: ${JSON.stringify(detail.record)}. Unavailable facts: ${JSON.stringify(detail.unavailableFields)}.`;
    return execute;
};

/** An informed user may adopt inspected current originals only after all predecessor future effects are excluded. */
export const adoptLocationOriginals = async (id, inspected) => navigator.locks.request(partition(id), async () => {
    const state = locationWork(id);
    await validateLocationWork(id, state);
    const active = state.attempts.find(item => item.actionId === state.active);
    if (active && active.receipt?.futureEffects !== false && active.exclusion?.futureEffects !== false)
        throw new Error('The earlier action may still execute. Recover it before adopting current facts.');
    if (inspected.subjectId !== subject() || inspected.record?.locationId !== id) throw new Error('Current facts have the wrong scope.');
    state.active = null;
    state.adopted = inspected;
    persist(id, state);
});

/** Conservative typed exact comparison of Web's already-normalized observed originals, never a local sanitizer. */
const sameValue = (left, right) => {
    if (left === null || right === null || typeof left !== 'object' || typeof right !== 'object') return left === right;
    return Object.keys(left).length === Object.keys(right).length && Object.keys(left).every(key => Object.hasOwn(right, key) && sameValue(left[key], right[key]));
};
const originalsMatch = (expected, values) => Object.entries(expected).every(([group, slot]) => {
    const current = values[group === 'content' ? 'deleteContent' : group];
    return slot?.state === 'known' && current?.state === 'known' && sameValue(slot.value, current.value);
});

/** Only complete, value-exact simple footprints settle without a receipt; provider/time dependencies require their fuller proof. */
const selectedSimpleStateSatisfied = (changes, expected, values) => {
    if (expected.uuid?.state !== 'known' || values.uuid?.state !== 'known' || !sameValue(expected.uuid.value, values.uuid.value)) return false;
    return Object.entries(changes).every(([name, change]) => {
        const group = { notes: 'notesHtml', activity: 'activityTypeId', altitude: 'altitude' }[name];
        if (!group || values[group]?.state !== 'known') return false;
        const desired = change.op === 'clear' ? null : change.html ?? change.id ?? change.value;
        return sameValue(desired, values[group].value);
    });
};

/** Missing/corrupt local bindings are recovery debt, never permission to send a new or altered command. */
const validateLocationWork = async (id, state) => {
    const fail = () => { throw new Error('Saved command evidence is unavailable or inconsistent. Keep your draft; no save was sent.'); };
    if (!Array.isArray(state.attempts) || state.active && !state.attempts.some(item => item.actionId === state.active)) fail();
    for (const attempt of state.attempts) {
        if (attempt.id !== id || attempt.subjectId !== subject() || !['owner', 'web-edit'].includes(attempt.adapter)
            || typeof attempt.body !== 'string' || typeof attempt.mayHaveAttempted !== 'boolean') fail();
        let body;
        try { body = JSON.parse(attempt.body); } catch { fail(); }
        if (body.contract !== contract || body.normalization !== normalization || body.adapter !== attempt.adapter
            || body.baseHandle !== attempt.baseHandle || body.kind !== attempt.kind
            || body.expected?.uuid?.state !== 'known' || body.expected.uuid.value !== attempt.uuid
            || await locationRequestFingerprint(attempt.subjectId, id, attempt.baseId, attempt.actionId, attempt.adapter, attempt.body) !== attempt.fingerprint) fail();
        if (attempt.receipt && !validateLocationReceipt(attempt.receipt, attempt)) fail();
        if (attempt.cancelledBeforeHandoff && attempt.mayHaveAttempted !== false) fail();
        const exclusion = attempt.exclusion;
        if (exclusion && !(validateLocationObservation(exclusion, attempt) && exclusion.code === 'operationHistoryExpired')) fail();
        if (attempt.mayHaveAttempted && attempt.receipt?.futureEffects !== false && !exclusion && state.active !== attempt.actionId) fail();
    }
};
