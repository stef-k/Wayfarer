/** Protected Location client: durable originals/drafts, exact attempts, verified receipts and read-only recovery. */
const contract = 'location-mutation-1';
const normalization = 'location-values-1';
const encoder = new TextEncoder();
const subject = () => document.getElementById('browser-antiforgery')?.dataset.subjectId;
const partition = id => `${contract}:${location.origin}:${subject()}:${id}`;
const token = () => document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
const exactKeys = (value, required, optional = []) => value && typeof value === 'object' && !Array.isArray(value)
    && required.every(key => Object.hasOwn(value, key)) && Object.keys(value).every(key => [...required, ...optional].includes(key));
const persist = (id, state) => {
    const bytes = JSON.stringify(state);
    localStorage.setItem(partition(id), bytes);
    if (localStorage.getItem(partition(id)) !== bytes) throw new Error('Draft was not saved. Keep this page open.');
};
/** Existing evidence is account/server partitioned and never discarded because of age. */
export const locationWork = id => JSON.parse(localStorage.getItem(partition(id)) || '{"attempts":[]}');

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
    const response = await fetch(`/api/location/history/${id}`, { cache: 'no-store', credentials: 'same-origin' });
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
        && receipt.fingerprint === attempt.fingerprint && Number.isFinite(Date.parse(receipt.receiptRetainUntilUtc));
    if (!equal) return false;
    if (receipt.state === 'pending') return exactKeys(receipt, [...common, 'observedAtUtc', 'executeBeforeUtc', 'retryAfterSeconds'])
        && receipt.futureEffects === true && receipt.retryAfterSeconds === 2 && Number.isFinite(Date.parse(receipt.executeBeforeUtc));
    if (!Number.isFinite(Date.parse(receipt.completedAtUtc)) || receipt.futureEffects !== false) return false;
    if (receipt.state === 'rejected') return exactKeys(receipt, [...common, 'completedAtUtc', 'code'], ['staleGroups'])
        && ['locationBaseStale', 'operationExecutionExpired', 'mutationInvalid', 'activityInvalid', 'baseUnavailable', 'baseNormalizationUnsupported', 'locationDeleted', 'locationUnavailable'].includes(receipt.code)
        && (!receipt.staleGroups || Array.isArray(receipt.staleGroups) && receipt.staleGroups.every(group => ['uuid', 'position', 'address', 'eventUtc', 'activityTypeId', 'notesHtml', 'altitude', 'content'].includes(group)));
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
        const generation = retainLocationDraft(id, { kind, changes, expected, detail, draft });
        let state = locationWork(id);
        let attempt = state.attempts.find(item => item.actionId === state.active);
        if (attempt) {
            const recovered = await inspectAttempt(id, state, attempt);
            if (!recovered.terminal) return recovered;
            if (attempt.generation !== generation) return { success: false, message: 'The earlier action is resolved. Your newer draft is retained; review it against current history before saving.', earlierResolved: true };
            return recovered;
        }
        // Renew only never-attempted authority; expected originals are deliberately retained unchanged.
        let authority = detail.locationMutation;
        if (Date.parse(authority.expiresAtUtc) <= Date.now()) authority = (await readLocationDetail(id)).locationMutation;
        const body = JSON.stringify({ contract, normalization, adapter: 'web-edit', baseHandle: authority.baseHandle, kind, expected, ...(kind === 'update' ? { changes } : {}) });
        const actionId = crypto.randomUUID();
        attempt = { id, subjectId: subject(), uuid: detail.locationMutation.baseValues.uuid.value, baseId: authority.baseId, actionId,
            adapter: 'web-edit', kind, body, baseHandle: authority.baseHandle, generation, mayHaveAttempted: false,
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
const inspectAttempt = async (id, state, attempt) => {
    try {
        if (attempt.receipt?.futureEffects === false) return acceptResponse(id, state, attempt, attempt.receipt);
        const response = await fetch(attemptUrl(attempt), { cache: 'no-store', credentials: 'same-origin', headers: { 'X-Location-Base': attempt.baseHandle } });
        const receipt = await response.json();
        if (receipt.state === 'unknown' && receipt.contract === contract && receipt.subjectId === attempt.subjectId && receipt.locationId === id
            && receipt.baseId === attempt.baseId && receipt.actionId === attempt.actionId && receipt.code === 'operationHistoryExpired'
            && receipt.futureEffects === false && receipt.historicalOutcome === 'unknown') {
            // Exclusion alone cannot authorize rebasing or a replacement. Keep all command/original evidence.
            attempt.exclusion = receipt; persist(id, state);
            return { success: false, message: 'Historical outcome unknown; old future effects are excluded. Your draft and originals are retained. Review current history before an informed new conditional edit.' };
        }
        return await acceptResponse(id, state, attempt, receipt);
    } catch { return { success: false, message: 'Outcome unknown. Your draft and exact attempt are retained. Save again to check recovery.' }; }
};

/** Only a matching generation can publish success; an old receipt never consumes newer typing. */
const acceptResponse = async (id, state, attempt, receipt) => {
    if (!validateLocationReceipt(receipt, attempt)) return { success: false, message: 'Outcome could not be verified. Your draft and attempt are retained.' };
    attempt.receipt = receipt; persist(id, state);
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
    const existing = locationWork(id);
    const active = existing.attempts.find(item => item.actionId === existing.active);
    const retained = existing.draft?.value;
    const detail = active && retained?.kind === 'delete' ? retained.detail : await readLocationDetail(id);
    return () => saveLocationMutation(id, detail, 'delete', null, {
        uuid: detail.locationMutation.baseValues.uuid, content: detail.locationMutation.baseValues.deleteContent
    });
};
