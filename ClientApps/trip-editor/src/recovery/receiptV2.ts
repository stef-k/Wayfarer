import type { Attempt } from './receiptApi';

export const v2Contract = 'trip-metadata-receipt-2';
export const digestPattern = /^sha256:[0-9a-f]{64}$/u;
export const uuid = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u;
export type WebOperation = 'metadata' | 'tags-replace' | 'tags-attach' | 'tags-detach' | 'editor-sharing' | 'viewer-sharing';

export const keysAre = (object: object, keys: readonly string[]): boolean =>
  Object.keys(object).length === keys.length && keys.every(key => Object.hasOwn(object, key));

const object = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value);

/** Verifies each JSON object's decoded keys, including objects inside receipt arrays. */
export function rejectDuplicateMembers(raw: string, legacy: boolean): void {
  const global = new Set<string>();
  const scopes: Set<string>[] = [];
  for (const match of raw.matchAll(/("(?:[^"\\]|\\[\s\S])*")\s*(:)?|([{}])/gu)) {
    if (match[3] === '{') scopes.push(new Set());
    if (match[3] === '}') scopes.pop();
    if (!match[2]) continue;
    const scope = legacy ? global : scopes.at(-1);
    const key = JSON.parse(match[1]) as string;
    if (!scope || scope.has(key)) throw new Error('Duplicate Trip receipt member');
    scope.add(key);
  }
}

/** A proof is interpreted only on the write path and method for its retained kind. */
export function webWriteRoute(editor: string, tripId: string, kind: string): [string, string] {
  const user = `/User/Trip/${tripId}`;
  switch (kind) {
    case 'metadata': return [`${editor}/metadata/conditional`, 'PATCH'];
    case 'tags-replace': return [`${editor}/tags/conditional`, 'PUT'];
    case 'tags-attach': return [`${user}/Tags/attach/conditional`, 'POST'];
    case 'tags-detach': return [`${user}/Tags/detach/conditional`, 'POST'];
    case 'editor-sharing': return [`${editor}/share-progress/conditional`, 'PATCH'];
    case 'viewer-sharing': return [`${user}/share-progress/conditional`, 'POST'];
    default: throw new Error('Unknown Trip receipt route');
  }
}

/** Exact unique identity rows, ordered as emitted by the server. */
export function validTags(value: unknown, names: boolean): boolean {
  if (!Array.isArray(value)) return false;
  let previous = '';
  const ids = new Set<string>();
  const slugs = new Set<string>();
  for (const entry of value) {
    if (!object(entry) || !keysAre(entry, names ? ['id', 'slug', 'name'] : ['id', 'slug']) ||
        typeof entry.id !== 'string' || !uuid.test(entry.id) || typeof entry.slug !== 'string' || !entry.slug ||
        (names && (typeof entry.name !== 'string' || !entry.name))) return false;
    const key = entry.slug + '\0' + entry.id;
    if (previous >= key || ids.has(entry.id) || slugs.has(entry.slug)) return false;
    ids.add(entry.id);
    slugs.add(entry.slug);
    previous = key;
  }
  return true;
}

const deltas = ['addedJoins', 'removedJoins', 'createdCatalogue', 'deletedCatalogue'] as const;
const validTagResult = (value: unknown, state: unknown): boolean =>
  object(value) && keysAre(value, [...deltas, 'resultingTags']) && validTags(value.resultingTags, true) &&
  deltas.every(key => validTags(value[key], false)) &&
  (state === 'applied') === deltas.some(key => (value[key] as unknown[]).length > 0);

const validSharing = (value: unknown, requested: unknown): boolean =>
  object(value) && keysAre(value, ['isPublic', 'shareProgressEnabled']) &&
  typeof value.isPublic === 'boolean' && typeof value.shareProgressEnabled === 'boolean' &&
  value.shareProgressEnabled === requested && (value.isPublic || !value.shareProgressEnabled);

const validPrivacy = (value: unknown): boolean =>
  object(value) && keysAre(value, ['shareProgressEnabled', 'disabledShareProgress']) &&
  value.shareProgressEnabled === false && typeof value.disabledShareProgress === 'boolean';

export const validDigests = (value: unknown, fields: string[]): boolean =>
  object(value) && keysAre(value, fields) &&
  Object.values(value).every(digest => typeof digest === 'string' && digestPattern.test(digest));

/** Checks the operation-specific success envelope and selected effects. */
export function validV2Success(value: Record<string, unknown>, attempt: Attempt, terminal: string[],
  timestamp: (value: unknown) => boolean): boolean {
  const body = JSON.parse(attempt.body);
  const { kind } = attempt.permit;
  if (kind.startsWith('tags-')) return keysAre(value, [...terminal, 'tagResult']) && validTagResult(value.tagResult, value.state);
  if (!timestamp(value.updatedAtUtc)) return false;
  const stamped = [...terminal, 'updatedAtUtc'];
  if (kind === 'editor-sharing' || kind === 'viewer-sharing')
    return keysAre(value, [...stamped, 'sharingResult']) && validSharing(value.sharingResult, body.enabled);
  if (kind !== 'metadata') return false;
  const fields = Object.keys(body.changes).filter(field => field !== 'notesHtml' || body.changes[field] !== null)
    .map(field => field === 'notesHtml' ? 'notes' : field);
  const privacy = body.changes.isPublic === false;
  return keysAre(value, [...stamped, 'fieldDigests', ...privacy ? ['privacyEffect'] : []]) &&
    validDigests(value.fieldDigests, fields) && (!privacy || validPrivacy(value.privacyEffect));
}

/** Matches a recorded refusal to its own kind and execution HTTP status. */
function refusalStatus(kind: string, code: unknown): number | null {
  if (code === 'mutation-request-invalid') return 400;
  if (code === 'mutation-contention') return 409;
  if (kind === 'metadata' && code === 'mutation-base-stale') return 409;
  if (kind === 'metadata' && code === 'publication-policy-required') return 400;
  if ((kind === 'editor-sharing' || kind === 'viewer-sharing') && code === 'share-progress-requires-public') return 400;
  if (kind === 'tags-detach' && code === 'tag-not-on-trip') return 404;
  return null;
}

/** Checks the status and exact members of a v2 refusal, including any field validation detail. */
export function validV2Refusal(value: Record<string, unknown>, kind: string, status: number, lookup: boolean, members: string[]): boolean {
  const expectedStatus = refusalStatus(kind, value.code);
  if (expectedStatus === null || status !== (lookup ? 200 : expectedStatus)) return false;
  if (value.code !== 'mutation-request-invalid') return keysAre(value, members);
  return keysAre(value, [...members, 'errors']) && validValidationErrors(value.errors);
}

/** Validates the single field and message emitted by the request validators. */
export function validValidationErrors(value: unknown): boolean {
  if (!object(value)) return false;
  const fields = Object.entries(value);
  if (fields.length !== 1) return false;
  const [[field, messages]] = fields;
  return !!field && Array.isArray(messages) && messages.length === 1 && typeof messages[0] === 'string' && !!messages[0];
}
