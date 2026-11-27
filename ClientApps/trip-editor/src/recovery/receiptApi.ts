import type { EditorMetadataConditionalRequest, EditorTripMetadata } from '../types';

export const contract = 'trip-metadata-receipt-1';
const proofHeader = 'X-Wayfarer-Trip-Attempt';
const correlation = ['contract', 'subjectId', 'adapter', 'kind', 'tripId', 'operationId', 'requestDigest', 'executeBeforeUtc'] as const;
const digestPattern = /^sha256:[0-9a-f]{64}$/u;
const uuid = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u;

export type Permit = Record<typeof correlation[number], string> & { proof: string };
export type TerminalState = 'applied' | 'checkedNoOp' | 'terminalUnapplied' | 'retired';
export type Evidence = Record<string, unknown> & { state: TerminalState | 'unobserved' | 'unresolved' };
export type Attempt = { permit: Permit; body: string; submitted: Record<string, number> };

/** The Web transport only sends the exact frozen UTF-8 representation. */
export const freezeRequest = (request: EditorMetadataConditionalRequest): string =>
  JSON.stringify({ receiptContract: contract, expected: request.expected, changes: request.changes });

/** Hashes four-byte lengths and UTF-8 frames in the published order. */
async function requestDigest(account: string, tripId: string, body: string): Promise<string> {
  const encoder = new TextEncoder();
  const parts = [contract, 'metadata', 'web', account, tripId, body].map(value => encoder.encode(value));
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

/** Rejects duplicate keys even when a member uses an escaped spelling. V1 has no repeated keys across objects. */
async function strictObject(response: Response): Promise<Record<string, unknown>> {
  const raw = await response.text();
  const parsed = JSON.parse(raw);
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new Error('Invalid Trip receipt object');
  const seen = new Set<string>();
  for (const match of raw.matchAll(/("(?:[^"\\]|\\[\s\S])*")\s*(:)?/gu)) {
    if (!match[2]) continue;
    const key = JSON.parse(match[1]) as string;
    if (seen.has(key)) throw new Error('Duplicate Trip receipt member');
    seen.add(key);
  }
  return parsed as Record<string, unknown>;
}

const keysAre = (object: object, keys: readonly string[]): boolean =>
  Object.keys(object).length === keys.length && keys.every(key => Object.hasOwn(object, key));

const timestamp = (value: unknown): value is string => typeof value === 'string' &&
  /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/u.test(value) && Number.isFinite(Date.parse(value));

const operationUrl = (endpoint: string): string => `${endpoint}/metadata/conditional/operations`;
const writeHeaders = (token: string): Record<string, string> => ({
  Accept: 'application/json', 'Content-Type': 'application/json', RequestVerificationToken: token
});

/** Obtains a permit for an immutable body while keeping antiforgery outside the identity. */
export async function issuePermit(endpoint: string, token: string, account: string, tripId: string, body: string): Promise<Permit> {
  const digest = await requestDigest(account, tripId, body);
  const response = await fetch(operationUrl(endpoint), {
    method: 'POST', credentials: 'same-origin', headers: writeHeaders(token),
    body: JSON.stringify({ contract, requestDigest: digest })
  });
  if (!response.ok || response.redirected) throw new Error('Trip receipt issuance is unavailable');
  const result = await strictObject(response);
  if (!keysAre(result, [...correlation, 'proof']) || result.contract !== contract || result.subjectId !== account ||
      result.adapter !== 'web' || result.kind !== 'metadata' || result.tripId !== tripId || result.requestDigest !== digest ||
      typeof result.operationId !== 'string' || !uuid.test(result.operationId) || !timestamp(result.executeBeforeUtc) ||
      typeof result.proof !== 'string' || !/^[A-Za-z0-9_-]{1,4096}$/u.test(result.proof)) throw new Error('Invalid Trip receipt permit');
  return result as Permit;
}

/** Transport success alone cannot settle a draft. All evidence is correlated to the retained permit. */
async function readEvidence(response: Response, attempt: Attempt): Promise<Evidence> {
  if (response.redirected || ![200, 409].includes(response.status)) throw new Error('Trip save confirmation is unavailable');
  const value = await strictObject(response);
  if (!correlation.every(key => value[key] === attempt.permit[key])) throw new Error('Uncorrelated Trip receipt');
  const { state, historicalOutcome, futureEffects, code } = value;
  const common = [...correlation, 'state', 'historicalOutcome', 'futureEffects'];
  const terminal = [...common, 'completedAtUtc', 'receiptRetainUntilUtc'];
  if (state === 'applied' || state === 'checkedNoOp') {
    const fields = Object.keys(attempt.submitted).map(field => field === 'notesHtml' ? 'notes' : field);
    if (response.status === 200 && historicalOutcome === state && futureEffects === false && validRetention(value) &&
        timestamp(value.updatedAtUtc) && keysAre(value, [...terminal, 'updatedAtUtc', 'fieldDigests']) &&
        validDigests(value.fieldDigests, fields)) return { ...value, state };
  } else if (state === 'terminalUnapplied') {
    if (historicalOutcome === 'unapplied' && futureEffects === false && validRetention(value) &&
        code === 'mutation-base-stale' && keysAre(value, [...terminal, 'code'])) return { ...value, state };
  } else if (response.status === 200 && historicalOutcome === 'unknown' && timestamp(value.observedAtUtc)) {
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

/** A success must cover exactly the fields in this attempt. */
const validDigests = (value: unknown, fields: string[]): boolean =>
  !!value && typeof value === 'object' && !Array.isArray(value) && keysAre(value, fields) &&
  Object.values(value).every(digest => typeof digest === 'string' && digestPattern.test(digest));

/** Calls PATCH once with the saved bytes and issued proof. */
export async function sendAttempt(endpoint: string, token: string, attempt: Attempt): Promise<Evidence> {
  const response = await fetch(`${endpoint}/metadata/conditional`, {
    method: 'PATCH', credentials: 'same-origin', headers: { ...writeHeaders(token), [proofHeader]: attempt.permit.proof },
    body: attempt.body
  });
  return readEvidence(response, attempt);
}

/** Recovery never renews the permit or dispatches an effect. */
export async function lookupAttempt(endpoint: string, attempt: Attempt): Promise<Evidence> {
  const response = await fetch(`${operationUrl(endpoint)}/${attempt.permit.operationId}`, {
    credentials: 'same-origin', cache: 'no-store', headers: { [proofHeader]: attempt.permit.proof }
  });
  if (!response.ok) throw new Error('Trip save lookup is unavailable');
  return readEvidence(response, attempt);
}

/** Describes verified target/session loss without retaining any owner payload. */
export class OwnerScopeUnavailable extends Error {}

/** Reads comparison values through the owner route without requiring the Trip graph. */
export async function readOwnerMetadata(endpoint: string, account: string, tripId: string): Promise<EditorTripMetadata> {
  const reply = await fetch(`${endpoint}/metadata/conditional`, { credentials: 'same-origin', cache: 'no-store' });
  if (reply.redirected || [401, 403, 404].includes(reply.status)) throw new OwnerScopeUnavailable();
  if (!reply.ok) throw new Error('Current Trip values are unavailable');
  const result = await reply.json() as { subjectId: string; metadata: EditorTripMetadata };
  if (result.subjectId !== account) throw new OwnerScopeUnavailable();
  const metadata = result.metadata;
  if (metadata.id !== tripId || typeof metadata.originalValues?.name !== 'string' ||
      !(metadata.originalValues.notesHtml === null || typeof metadata.originalValues.notesHtml === 'string'))
    throw new Error('Current Trip comparison values are unavailable');
  return metadata;
}
