import type { Evidence } from './receiptApi';
import { keysAre, rejectDuplicateMembers } from './receiptV2';
import { object, utc, validStructureResult } from './receiptStructureResults';
import { isId, structureContract, structureRoute, type StructureRoute, type Target } from './structureRoutes';

const fields = ['contract', 'subjectId', 'adapter', 'kind', 'tripId', 'target', 'requestDigest', 'operationId', 'executeBeforeUtc'] as const;
const header = 'X-Wayfarer-Trip-Attempt';
export type StructurePermit = Omit<Record<typeof fields[number], string>, 'target'> & { target: Target; proof: string };
export type StructureDispatch = { permit: StructurePermit; route: StructureRoute; body: string };

const refusals: Readonly<Record<string, number>> = {
  'mutation-request-invalid': 400, 'region-deletion-request-invalid': 400, 'place-deletion-request-invalid': 400,
  'mutation-region-protected': 403, 'mutation-operation-forbidden': 403, 'mutation-scope-unavailable': 404,
  'mutation-base-stale': 409, 'mutation-scope-stale': 409, 'mutation-dependency-stale': 409,
  'mutation-contention': 409, 'mutation-review-required': 409, 'region-deletion-preview-stale': 409,
  'place-deletion-preview-stale': 409, 'area-deletion-preview-stale': 409, 'unassigned_region_conflict': 409
};

const headers = (token: string): Record<string, string> => ({
  Accept: 'application/json', 'Content-Type': 'application/json', RequestVerificationToken: token
});

/** Frames the contract, typed target and the precise UTF-8 request. */
async function requestDigest(account: string, tripId: string, route: StructureRoute, body: string): Promise<string> {
  const encoder = new TextEncoder();
  const frames = [structureContract, route.kind, 'web', account, tripId, route.target.type, route.target.id, body].map(value => encoder.encode(value));
  const bytes = new Uint8Array(frames.reduce((length, frame) => length + 4 + frame.length, 0));
  const view = new DataView(bytes.buffer);
  let offset = 0;
  for (const frame of frames) {
    view.setUint32(offset, frame.length);
    bytes.set(frame, offset + 4);
    offset += 4 + frame.length;
  }
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
  return 'sha256:' + Array.from(digest, value => value.toString(16).padStart(2, '0')).join('');
}

/** Parses the full nested wire object with exact decoded member names. */
async function read(response: Response): Promise<Record<string, unknown>> {
  const raw = await response.text();
  rejectDuplicateMembers(raw, false);
  const parsed: unknown = JSON.parse(raw);
  if (!object(parsed)) throw new Error('Invalid typed Trip receipt');
  return parsed;
}

const sameTarget = (value: unknown, target: Target): boolean =>
  object(value) && keysAre(value, ['type', 'id']) && value.type === target.type && value.id === target.id;

/** Requests a permit for the already frozen typed payload. */
export async function issueStructure(route: StructureRoute, account: string, tripId: string, token: string,
  payload: object): Promise<StructureDispatch> {
  const body = JSON.stringify({ receiptContract: structureContract, ...payload });
  const digest = await requestDigest(account, tripId, route, body);
  const response = await fetch(route.path + '/operations', { method: 'POST', credentials: 'same-origin',
    headers: headers(token), body: JSON.stringify({ contract: structureContract, requestDigest: digest }) });
  if (response.status !== 200 || response.redirected) throw new Error('Protected Trip save is unavailable. Keep your draft and reload.');
  const permit = await read(response);
  if (!keysAre(permit, [...fields, 'proof']) || permit.contract !== structureContract || permit.subjectId !== account ||
      permit.tripId !== tripId || permit.adapter !== 'web' || permit.kind !== route.kind || !sameTarget(permit.target, route.target) ||
      permit.requestDigest !== digest || !isId(permit.operationId) || !utc(permit.executeBeforeUtc) ||
      typeof permit.proof !== 'string' || !/^[A-Za-z0-9_-]{1,4096}$/u.test(permit.proof))
    throw new Error('Invalid typed Trip permit');
  return { route, permit: permit as StructurePermit, body };
}

/** Reconstructs the literal route from retained identity and refuses an edited stored path. */
function verifyRoute(endpoint: string, dispatch: StructureDispatch): StructureRoute {
  const { permit, route } = dispatch;
  const current = structureRoute(endpoint, permit.tripId, permit.kind, permit.target);
  if (permit.contract !== structureContract || permit.adapter !== 'web' || current.path !== route.path ||
      current.method !== route.method || current.kind !== route.kind || !sameTarget(route.target, permit.target))
    throw new Error('Uncorrelated retained Trip attempt');
  return current;
}

/** Sends only the caller's reserved invocation, with no alternate writers or automatic retry. */
export async function sendStructure(endpoint: string, token: string, dispatch: StructureDispatch): Promise<Evidence> {
  const { path, method } = verifyRoute(endpoint, dispatch);
  const response = await fetch(path, { method, credentials: 'same-origin', cache: 'no-store',
    headers: { ...headers(token), [header]: dispatch.permit.proof }, body: dispatch.body });
  return readEvidence(response, dispatch, false);
}

/** Looks up history with the retained version and typed target even after the target disappears. */
export async function lookupStructure(endpoint: string, dispatch: StructureDispatch): Promise<Evidence> {
  const { path } = verifyRoute(endpoint, dispatch);
  const response = await fetch(`${path}/operations/${dispatch.permit.operationId}`, {
    credentials: 'same-origin', cache: 'no-store', headers: { [header]: dispatch.permit.proof }
  });
  return readEvidence(response, dispatch, true);
}

/** Requires the exact correlated state, status, evidence lifetime and operation result before settlement. */
async function readEvidence(response: Response, dispatch: StructureDispatch, lookup: boolean): Promise<Evidence> {
  if (response.redirected || lookup && response.status !== 200) throw new Error('Typed Trip lookup unavailable');
  const value = await read(response);
  const { permit, route, body } = dispatch;
  if (!fields.every(key => key === 'target' ? sameTarget(value[key], permit.target) : value[key] === permit[key]))
    throw new Error('Uncorrelated typed Trip receipt');
  const { state, historicalOutcome, futureEffects, code } = value;
  const common = [...fields, 'state', 'historicalOutcome', 'futureEffects'];
  const terminal = [...common, 'completedAtUtc', 'receiptRetainUntilUtc'];
  const retention = utc(value.completedAtUtc) && utc(value.receiptRetainUntilUtc) &&
    Date.parse(value.receiptRetainUntilUtc) - Math.max(Date.parse(value.completedAtUtc), Date.parse(permit.executeBeforeUtc)) >= 30 * 86400000;
  if ((state === 'applied' || state === 'checkedNoOp') && response.status === 200 && historicalOutcome === state &&
      futureEffects === false && retention && keysAre(value, [...terminal, 'updatedAtUtc', 'result']) &&
      utc(value.updatedAtUtc) && validStructureResult(value.result, route, JSON.parse(body))) return { ...value, state };
  if (state === 'terminalUnapplied' && historicalOutcome === 'unapplied' && futureEffects === false && retention &&
      typeof code === 'string' && Object.hasOwn(refusals, code) && (lookup || response.status === refusals[code]) &&
      keysAre(value, [...terminal, 'code'])) return { ...value, state };
  if (historyObservation(response.status, lookup, value, common)) return value as Evidence;
  throw new Error('Trip save evidence is unavailable. Use Check server.');
}

/** Permits only qualified observations with the appropriate uncertainty and effect axis. */
function historyObservation(status: number, lookup: boolean, value: Record<string, unknown>, common: string[]): boolean {
  if (value.historicalOutcome !== 'unknown' || !utc(value.observedAtUtc)) return false;
  if (value.state === 'unresolved')
    return (lookup || status === 503) && value.futureEffects === null && keysAre(value, [...common, 'observedAtUtc', 'code']) &&
      ['trip-attempt-in-flight', 'trip-attempt-awaiting-retirement'].includes(value.code as string);
  return (lookup || status === 409) && keysAre(value, [...common, 'observedAtUtc']) &&
    (value.state === 'unobserved' && value.futureEffects === true || value.state === 'retired' && value.futureEffects === false);
}
