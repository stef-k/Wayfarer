import type { EditorTripMetadata, ValidationProblemDetails } from '../types';
import { captureMutationPublication, EditorValidationError } from '../api/tripEditorApi';
import { keysAre, rejectDuplicateMembers, uuid, v2Contract, validDigests, validTags, validV2Refusal, validV2Success, validValidationErrors,
  webWriteRoute, type WebOperation } from './receiptV2';

export const contract = 'trip-metadata-receipt-1';
const ownerPublications = new WeakMap<EditorTripMetadata, (result: object) => void>();
const proofHeader = 'X-Wayfarer-Trip-Attempt';
const correlation = ['contract', 'subjectId', 'adapter', 'kind', 'tripId', 'operationId', 'requestDigest', 'executeBeforeUtc'] as const;

export type Permit = Record<typeof correlation[number], string> & { proof: string };
export type TerminalState = 'applied' | 'checkedNoOp' | 'terminalUnapplied' | 'retired';
export type Evidence = Record<string, unknown> & { state: TerminalState | 'unobserved' | 'unresolved'; errors?: Record<string, string[]> };
export type Attempt = { permit: Permit; body: string; submitted: Record<string, number>; bases?: Record<string, unknown> };

/** The Web transport only sends the exact frozen UTF-8 representation. */
export const freezeRequest = (request: object): string => JSON.stringify({ receiptContract: v2Contract, ...request });

/** Hashes four-byte lengths and UTF-8 frames in the published order. */
async function requestDigest(account: string, tripId: string, body: string, kind: WebOperation): Promise<string> {
  const encoder = new TextEncoder();
  const parts = [v2Contract, kind, 'web', account, tripId, body].map(value => encoder.encode(value));
  const bytes = new Uint8Array(parts.reduce((total, part) => total + 4 + part.length, 0));
  const view = new DataView(bytes.buffer);
  let offset = 0;
  for (const part of parts) {
    view.setUint32(offset, part.length);
    bytes.set(part, offset + 4);
    offset += 4 + part.length;
  }
  const digest = await crypto.subtle.digest('SHA-256', bytes);
  return 'sha256:' + Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('');
}

/** Parses a receipt with duplicate detection appropriate to its retained version. */
async function strictObject(response: Response, legacy = false): Promise<Record<string, unknown>> {
  const raw = await response.text();
  const parsed = JSON.parse(raw);
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new Error('Invalid Trip receipt object');
  rejectDuplicateMembers(raw, legacy);
  return parsed as Record<string, unknown>;
}

const timestamp = (value: unknown): value is string => typeof value === 'string' &&
  /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/u.test(value) && Number.isFinite(Date.parse(value));

const operationUrl = (endpoint: string, tripId: string, kind: string): string => `${webWriteRoute(endpoint, tripId, kind)[0]}/operations`;
const writeHeaders = (token: string): Record<string, string> => ({
  Accept: 'application/json', 'Content-Type': 'application/json', RequestVerificationToken: token
});

/** Obtains a permit for an immutable body while keeping antiforgery outside the identity. */
export async function issuePermit(endpoint: string, token: string, account: string, tripId: string, body: string, kind: WebOperation = 'metadata'): Promise<Permit> {
  const digest = await requestDigest(account, tripId, body, kind);
  const response = await fetch(operationUrl(endpoint, tripId, kind), {
    method: 'POST', credentials: 'same-origin', headers: writeHeaders(token),
    body: JSON.stringify({ contract: v2Contract, requestDigest: digest })
  });
  if (!response.ok || response.redirected) throw new Error('Trip receipt issuance is unavailable');
  const result = await strictObject(response);
  if (!keysAre(result, [...correlation, 'proof']) || result.contract !== v2Contract || result.subjectId !== account ||
      result.adapter !== 'web' || result.kind !== kind || result.tripId !== tripId || result.requestDigest !== digest ||
      typeof result.operationId !== 'string' || !uuid.test(result.operationId) || !timestamp(result.executeBeforeUtc) ||
      typeof result.proof !== 'string' || !/^[A-Za-z0-9_-]{1,4096}$/u.test(result.proof)) throw new Error('Invalid Trip receipt permit');
  return result as Permit;
}

/** Transport success alone cannot settle a draft. All evidence is correlated to the retained permit. */
async function readEvidence(response: Response, attempt: Attempt, lookup: boolean): Promise<Evidence> {
  const legacy = attempt.permit.contract === contract;
  if ((!legacy && attempt.permit.contract !== v2Contract) || response.redirected || !(legacy ? [200, 409] : [200, 400, 404, 409]).includes(response.status)) throw new Error('Trip save confirmation is unavailable');
  const value = await strictObject(response, legacy);
  if (!correlation.every(key => value[key] === attempt.permit[key])) throw new Error('Uncorrelated Trip receipt');
  const { state, historicalOutcome, futureEffects, code } = value;
  const common = [...correlation, 'state', 'historicalOutcome', 'futureEffects'];
  const terminal = [...common, 'completedAtUtc', 'receiptRetainUntilUtc'];
  if (state === 'applied' || state === 'checkedNoOp') {
    const fields = Object.keys(attempt.submitted).map(field => field === 'notesHtml' ? 'notes' : field);
    const validResult = legacy ? timestamp(value.updatedAtUtc) && keysAre(value, [...terminal, 'updatedAtUtc', 'fieldDigests']) &&
      validDigests(value.fieldDigests, fields) : validV2Success(value, attempt, terminal, timestamp);
    if (response.status === 200 && historicalOutcome === state && futureEffects === false && validRetention(value) &&
        validResult) return { ...value, state };
  } else if (state === 'terminalUnapplied') {
    const validRefusal = legacy ? code === 'mutation-base-stale' && keysAre(value, [...terminal, 'code'])
      : validV2Refusal(value, attempt.permit.kind, response.status, lookup, [...terminal, 'code']);
    if (historicalOutcome === 'unapplied' && futureEffects === false && validRetention(value) && validRefusal)
      return { ...value, state };
  } else if ((legacy || lookup) && response.status === 200 && historicalOutcome === 'unknown' && timestamp(value.observedAtUtc)) {
    if ((state === 'unobserved' && futureEffects === true || state === 'retired' && futureEffects === false) &&
        keysAre(value, [...common, 'observedAtUtc'])) return { ...value, state };
    if (state === 'unresolved' && futureEffects === null &&
        (code === 'trip-attempt-in-flight' || code === 'trip-attempt-awaiting-retirement') &&
        keysAre(value, [...common, 'observedAtUtc', 'code'])) return { ...value, state };
  }
  throw new Error('Invalid Trip receipt evidence');
}

/** Checks the retention promise without assuming the client clock matches PostgreSQL. */
const validRetention = (value: Record<string, unknown>): boolean =>
  timestamp(value.completedAtUtc) && timestamp(value.receiptRetainUntilUtc) && timestamp(value.executeBeforeUtc) &&
  Date.parse(value.receiptRetainUntilUtc) - Math.max(Date.parse(value.completedAtUtc), Date.parse(value.executeBeforeUtc)) >= 30 * 86400000;

/** Calls the bound write method once with the saved bytes and issued proof. */
export async function sendAttempt(endpoint: string, token: string, attempt: Attempt): Promise<Evidence> {
  const [url, method] = webWriteRoute(endpoint, attempt.permit.tripId, attempt.permit.kind);
  const response = await fetch(url, {
    method, credentials: 'same-origin', headers: { ...writeHeaders(token), [proofHeader]: attempt.permit.proof },
    body: attempt.body
  });
  const invalid = response.status === 400 ? response.clone() : null;
  try { return await readEvidence(response, attempt, false); }
  catch (error) {
    // A validation problem can guide editing but supplies no settlement for this dispatch.
    const problem = await invalid?.json().catch(() => null) as ValidationProblemDetails | undefined;
    if (problem?.code === 'mutation-request-invalid' && (attempt.permit.contract === contract ? problem.errors
      : !Object.hasOwn(problem, 'operationId') && validValidationErrors(problem.errors))) throw new EditorValidationError(problem);
    throw error;
  }
}

/** Recovery never renews the permit or dispatches an effect. */
export async function lookupAttempt(endpoint: string, attempt: Attempt): Promise<Evidence> {
  const response = await fetch(`${operationUrl(endpoint, attempt.permit.tripId, attempt.permit.kind)}/${attempt.permit.operationId}`, {
    credentials: 'same-origin', cache: 'no-store', headers: { [proofHeader]: attempt.permit.proof }
  });
  if (!response.ok) throw new Error('Trip save lookup is unavailable');
  return readEvidence(response, attempt, true);
}

/** Describes verified target/session loss without retaining any owner payload. */
export class OwnerScopeUnavailable extends Error {}

/** Reads comparison values through the owner route without requiring the Trip graph. */
export async function readOwnerMetadata(endpoint: string, account: string, tripId: string, legacy = false): Promise<EditorTripMetadata> {
  const url = `${endpoint}/metadata/conditional${legacy ? '' : `?contract=${v2Contract}`}`;
  const publish = captureMutationPublication(url);
  const reply = await fetch(url, { credentials: 'same-origin', cache: 'no-store' });
  if (reply.redirected || [401, 403, 404].includes(reply.status)) throw new OwnerScopeUnavailable();
  if (!reply.ok) throw new Error('Current Trip values are unavailable');
  const result = await reply.json() as { subjectId: string; metadata: EditorTripMetadata; tags?: EditorTripMetadata['tags'] };
  if (result.subjectId !== account) throw new OwnerScopeUnavailable();
  const metadata = result.metadata;
  if (metadata.id !== tripId || typeof metadata.originalValues?.name !== 'string' ||
      !(metadata.originalValues.notesHtml === null || typeof metadata.originalValues.notesHtml === 'string'))
    throw new Error('Current Trip comparison values are unavailable');
  if (!legacy && (!validTags(result.tags, true) || typeof metadata.isPublic !== 'boolean' || typeof metadata.shareProgressEnabled !== 'boolean'))
    throw new Error('Current Trip comparison values are incomplete');
  const snapshot = legacy ? metadata : { ...metadata, tags: result.tags };
  ownerPublications.set(snapshot, publish);
  return snapshot;
}

/** Carries the owner read's dispatch boundary when publishing metadata alongside independent Planning state. */
export function markOwnerPublication(snapshot: EditorTripMetadata, result: object): void {
  ownerPublications.get(snapshot)?.(result);
}
