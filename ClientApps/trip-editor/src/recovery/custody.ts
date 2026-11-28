import type { EditorMetadataOriginalValues, EditorTag, EditorTripMetadata } from '../types';
import type { MetadataDraft } from '../components/metadataDraft';
import type { Attempt, Evidence, Permit, TerminalState } from './receiptApi';

export type SettingsField = keyof EditorMetadataOriginalValues | 'tags' | 'shareProgressEnabled';
export type DraftValue = { desired: Partial<MetadataDraft>; baseline: Partial<MetadataDraft>; original?: unknown; policyOriginal?: unknown };
export type DraftFields = Partial<Record<SettingsField, DraftValue>>;
export type DraftEntry = DraftValue & { generation: number };
export type DraftEntries = Partial<Record<SettingsField, DraftEntry>>;
type Settlement = { submitted: Record<string, number>; evidence: Evidence & { state: TerminalState } };
export type Custody = {
  account: string;
  tripId: string;
  generation: number;
  fields: DraftEntries;
  attempts: Attempt[];
  outcomes: Record<string, Settlement>;
  review: SettingsField[];
};

type Change<T> = { next: Custody; value: T };

export class CustodyRefusal extends Error {
  constructor(readonly reason: 'stale' | 'held' | 'review') { super(reason); }
}

const empty = (account: string, tripId: string): Custody => ({
  account, tripId, generation: 0, fields: {}, attempts: [], outcomes: {}, review: []
});

const same = (a: unknown, b: unknown): boolean => JSON.stringify(a) === JSON.stringify(b);
const asField = (key: string): SettingsField => key as SettingsField;

/** One IndexedDB transaction owns cross-tab generation and operation checks. */
async function access<T>(account: string, tripId: string, update: (row: Custody) => Change<T>, write = true): Promise<T> {
  const db = await openCustody();
  try {
    return await new Promise<T>((resolve, reject) => {
      const transaction = write
        ? db.transaction('drafts', 'readwrite', { durability: 'strict' })
        : db.transaction('drafts');
      const store = transaction.objectStore('drafts');
      let result: T;
      const request = store.get([account, tripId]);
      request.onsuccess = () => {
        try {
          const change = update(request.result ?? empty(account, tripId));
          if (write) store.put(change.next);
          result = change.value;
        } catch (error) {
          transaction.abort();
          reject(error);
        }
      };
      transaction.oncomplete = () => resolve(result);
      transaction.onabort = () => reject(transaction.error ?? new Error('Trip draft storage aborted'));
    });
  } finally {
    db.close();
  }
}

/** Upgrades the existing store in one transaction. Existing records keep their frozen v1 wire identity. */
function openCustody(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open('wayfarer-trip-metadata', 2);
    let blocked = false;
    request.onupgradeneeded = event => {
      if (event.oldVersion !== 0) return;
      const store = request.result.createObjectStore('drafts', { keyPath: ['account', 'tripId'] });
      store.createIndex('account', 'account');
    };
    request.onsuccess = () => {
      request.result.onversionchange = () => request.result.close();
      if (blocked) request.result.close();
      else resolve(request.result);
    };
    request.onerror = () => reject(request.error);
    request.onblocked = () => {
      blocked = true;
      reject(new Error('Trip draft storage is blocked'));
    };
  });
}

/** A stale tab cannot replace or clear another tab's generation. */
function requireGeneration(row: Custody, generation: number): void {
  if (row.generation !== generation) throw new CustodyRefusal('stale');
}

/** Publication compares policy and overlaps with sharing, including its derived privacy effect. */
export function overlapping(fields: readonly string[]): SettingsField[] {
  const effects = new Set(fields.map(asField));
  if (effects.has('isPublic')) {
    effects.add('shareProgressEnabled');
    effects.add('contributionPolicy');
  }
  return [...effects];
}

/** Includes pending effects and terminal observations still waiting for a fresh owner read. */
export const heldFields = (row: Custody): SettingsField[] => row.attempts.flatMap(attempt =>
  overlapping(Object.keys(attempt.submitted)));

/** Compares the published identity set independent of display order and names. */
export const tagBase = (tags: EditorTag[]): { id: string; slug: string }[] =>
  tags.map(({ id, slug }) => ({ id, slug })).sort((a, b) => a.id < b.id ? -1 : a.id > b.id ? 1 : 0);

/** Sharing depends on both publication and the stored sharing flag. */
export const sharingBase = (snapshot: EditorTripMetadata) => ({
  isPublic: snapshot.isPublic, shareProgressEnabled: snapshot.shareProgressEnabled
});

/** Keeps comparisons independent of display values, including nullable coordinates. */
export function ownerOriginal(current: EditorTripMetadata, field: SettingsField): unknown {
  if (field === 'tags') return current.tags && tagBase(current.tags);
  if (field === 'shareProgressEnabled') return sharingBase(current);
  return current.originalValues?.[field];
}

export const readCustody = (account: string, tripId: string): Promise<Custody> =>
  access(account, tripId, next => ({ next, value: next }), false);

/** Reads account entries even when Trips or their graphs are unavailable. */
export async function listCustody(account: string): Promise<Custody[]> {
  const db = await openCustody();
  try {
    return await new Promise((resolve, reject) => {
      const query = db.transaction('drafts').objectStore('drafts').index('account').getAll(account);
      query.onsuccess = () => resolve((query.result as Custody[]).filter(row =>
        row.attempts.length > 0 || Object.keys(row.fields).length > 0));
      query.onerror = () => reject(query.error);
    });
  } finally {
    db.close();
  }
}

/** Commits only authored changes, giving changed fields the new durable revision. */
export const saveDraft = (view: Custody, fields: DraftFields, current?: EditorTripMetadata): Promise<Custody> =>
  access(view.account, view.tripId, row => {
    requireGeneration(row, view.generation);
    const nextFields: DraftEntries = {};
    let modified = Object.keys(fields).length !== Object.keys(row.fields).length;
    for (const [key, value] of Object.entries(fields)) {
      const prior = row.fields[asField(key)];
      const entry = prior && same(value, { desired: prior.desired, baseline: prior.baseline, original: prior.original, policyOriginal: prior.policyOriginal })
        ? prior : { ...value, generation: row.generation + 1 };
      nextFields[asField(key)] = entry;
      modified ||= entry !== prior;
    }
    if (modified) {
      row.generation++;
      row.fields = nextFields;
      row.review = current ? fieldsForReview(row, current, new Set(heldFields(row))) : row.review.filter(key => row.fields[key]);
    }
    return { next: row, value: row };
  });

/** Freezes the attempt with its submitted generation before data can reach fetch. */
export const authorize = (view: Custody, fields: string[], frozen?: { permit: Permit; body: string }): Promise<Attempt | null> =>
  access(view.account, view.tripId, row => {
    requireGeneration(row, view.generation);
    const effects = overlapping(fields);
    if (heldFields(row).some(key => effects.includes(key))) throw new CustodyRefusal('held');
    if (row.review.some(key => fields.includes(key))) throw new CustodyRefusal('review');
    if (!frozen) return { next: row, value: null };
    const submitted: Record<string, number> = {};
    for (const key of fields) {
      const entry = row.fields[asField(key)];
      if (!entry) throw new CustodyRefusal('stale');
      submitted[key] = entry.generation;
    }
    const bases = Object.fromEntries(fields.map(key => [key, row.fields[asField(key)]]));
    const attempt = { ...frozen, submitted, bases };
    row.attempts.push(attempt);
    return { next: row, value: attempt };
  });

/** Returns the validated historical dispositions whose owner comparison read is still pending. */
export const knownHistory = (row: Custody): TerminalState[] => Object.values(row.outcomes).map(result => result.evidence.state);

const isTerminal = (evidence: Evidence): evidence is Evidence & { state: TerminalState } =>
  evidence.state === 'applied' || evidence.state === 'checkedNoOp' || evidence.state === 'terminalUnapplied' || evidence.state === 'retired';

/** Stores only terminal disposition and captured revisions. Proof/body stay immutable until the owner read succeeds. */
export const remember = (view: Custody, attempt: Attempt, evidence: Evidence): Promise<Custody> =>
  access(view.account, view.tripId, row => {
    const id = attempt.permit.operationId;
    if (!row.outcomes[id] && row.attempts.some(candidate => candidate.permit.operationId === id) &&
        isTerminal(evidence)) {
      row.outcomes[id] = { submitted: attempt.submitted, evidence };
    }
    return { next: row, value: row };
  });

/** Reconciles only the selected evidence and current revisions after a fresh owner read. */
export const reconcile = (view: Custody, current: EditorTripMetadata): Promise<{ row: Custody; settled: TerminalState[] }> =>
  access(view.account, view.tripId, row => {
    requireGeneration(row, view.generation);
    const settled: TerminalState[] = [];
    const observed = new Set(view.attempts.filter(attempt => view.outcomes[attempt.permit.operationId])
      .map(attempt => attempt.permit.operationId));
    const blocked = new Set(row.attempts.filter(attempt => !observed.has(attempt.permit.operationId))
      .flatMap(attempt => overlapping(Object.keys(attempt.submitted))));
    const reconciled = new Set<string>();
    for (const attempt of row.attempts) {
      const id = attempt.permit.operationId;
      const result = row.outcomes[id];
      if (!observed.has(id) || !result) continue;
      if (overlapping(Object.keys(result.submitted)).some(key => blocked.has(key))) continue;
      if (result.evidence.state === 'applied' || result.evidence.state === 'checkedNoOp') {
        for (const [key, generation] of Object.entries(result.submitted)) {
          if (row.fields[asField(key)]?.generation !== generation) continue;
          delete row.fields[asField(key)];
          row.generation++;
        }
      }
      reconciled.add(id);
      settled.push(result.evidence.state);
      delete row.outcomes[id];
    }
    row.attempts = row.attempts.filter(attempt => !reconciled.has(attempt.permit.operationId));
    row.review = fieldsForReview(row, current, blocked);
    return { next: row, value: { row, settled } };
  });

/** Flags changed owner originals only when relevant effects have settled. */
function fieldsForReview(row: Custody, current: EditorTripMetadata, blocked: ReadonlySet<string>): SettingsField[] {
  return Object.entries(row.fields).filter(([key, entry]) => {
    if (blocked.has(key)) return false;
    const original = ownerOriginal(current, asField(key));
    if (original === undefined || entry.original === undefined || !same(entry.original, original)) return true;
    return key === 'isPublic' && entry.desired.isPublic === true &&
      !same(entry.policyOriginal, current.originalValues?.contributionPolicy);
  }).map(([key]) => asField(key));
}

/** Updates only explicitly adopted comparison bases, preserving the desired values. */
export const adoptBases = (view: Custody, accepted: DraftFields): Promise<Custody> =>
  access(view.account, view.tripId, row => {
    requireGeneration(row, view.generation);
    const selected = Object.entries(accepted);
    if (selected.some(([key]) => heldFields(row).includes(asField(key)))) throw new CustodyRefusal('held');
    for (const [key, field] of selected) {
      if (!row.review.includes(asField(key))) throw new CustodyRefusal('stale');
      row.fields[asField(key)] = { ...field, generation: ++row.generation };
    }
    row.review = row.review.filter(key => !Object.hasOwn(accepted, key));
    return { next: row, value: row };
  });
