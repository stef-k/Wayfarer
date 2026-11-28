import { dependentFootprint } from './structureFootprint';
import { releaseDependent, reserveDependent } from './structureCustody';
import { dependentTarget, dispatchAuthority, dispatchHeader, dispatchVersion, isId } from './structureRoutes';
import { object } from './receiptStructureResults';

type Dispatch = { complete: () => Promise<void>; response: Response };

/** Reserves the exact dependent invocation before sending its versioned write. */
export function sendDependent(endpoint: string, url: string, init: RequestInit): Promise<Dispatch> | null {
  const target = dependentTarget(endpoint, '00000000-0000-0000-0000-000000000000', url, init.method ?? '');
  return target ? dispatchDependent(endpoint, url, init) : null;
}

/** Owns the durable reservation and transport of one recognized dependent write. */
async function dispatchDependent(endpoint: string, url: string, init: RequestInit): Promise<Dispatch> {
  const scope = dispatchAuthority(endpoint);
  if (!scope.advertised) throw new Error('Reload the editor before saving this Trip change.');
  const target = dependentTarget(endpoint, scope.tripId, url, init.method ?? '')!;
  const id = crypto.randomUUID();
  const current = (): boolean => {
    try { return dispatchAuthority(endpoint) === scope; } catch { return false; }
  };
  await reserveDependent(scope, { id, path: url, method: init.method!, footprint: dependentFootprint(target, url.startsWith(endpoint + '/planning/')) }, current);
  if (!current()) {
    await releaseDependent(scope, id);
    throw new Error('Trip scope changed before dispatch.');
  }
  const headers = new Headers(init.headers);
  headers.set(dispatchHeader, dispatchVersion);
  const response = await fetch(url, { ...init, headers });
  return { response, complete: () => releaseDependent(scope, id) };
}

const refusalCodes: Record<string, number> = {
  'trip-write-upgrade-required': 409, 'mutation-authority-unavailable': 401,
  'mutation-scope-unavailable': 404, 'mutation-request-invalid': 400, 'mutation-base-stale': 409,
  'mutation-scope-stale': 409, 'mutation-contention': 409
};
const segmentConflicts = new Set(['segment-write-conflict', 'segment-aggregate-stale', 'segment-notes-stale',
  'segment-route-stale', 'segment-route-clear-confirmation-required', 'segment-route-clear-confirmation-stale']);

/** Recognizes a routed Segment refusal before releasing only that invocation's reservation. */
export function dependentRefusal(response: Response, problem: unknown, method: string, url: string): boolean {
  if (response.redirected || !object(problem)) return false;
  const { code, status, errors, title } = problem;
  if (response.status === 409 && typeof code === 'string' && segmentConflicts.has(code))
    return segmentConflict(response, problem, method, url);
  if (status !== response.status || ['success', 'applied'].some(key => Object.hasOwn(problem, key) && problem[key] !== false))
    return false;
  if (status === 400 && title === 'One or more validation errors occurred.' && object(errors))
    return Object.keys(errors).length > 0 && Object.values(errors).every(items =>
      Array.isArray(items) && items.length > 0 && items.every(item => typeof item === 'string'));
  if (code === undefined) return (status === 404 && title === 'Not Found') ||
    (status === 401 && title === 'Unauthorized') || (status === 403 && title === 'Forbidden.');
  return typeof code === 'string' && Object.hasOwn(refusalCodes, code) && refusalCodes[code] === status;
}

/** Qualifies the existing Segment conflict DTO, which has no ProblemDetails status property. */
function segmentConflict(response: Response, problem: Record<string, unknown>, method: string, url: string): boolean {
  const { code, operation, currentSegment, warning, expiresAt } = problem;
  if (Object.keys(problem).some(key => !['code', 'operation', 'currentSegment', 'warning', 'expiresAt'].includes(key))) return false;
  if (!object(currentSegment) || !isId(currentSegment.id) || typeof warning !== 'string') return false;
  if (method === 'POST') return operation === 'create' && code === 'segment-write-conflict' && expiresAt === null;
  if (method !== 'PUT' || operation !== 'update' || currentSegment.id !== url.split('/').at(-1)) return false;
  if (code === 'segment-route-clear-confirmation-required' || code === 'segment-route-clear-confirmation-stale')
    return typeof expiresAt === 'string' && Number.isFinite(Date.parse(expiresAt)) &&
      !!response.headers.get('X-Wayfarer-Clear-Route-Confirmation');
  return expiresAt === null;
}

/** Verifies the required Segment acknowledgement and target before declaring its write terminal. */
export function dependentSegmentSuccess(response: Response, result: unknown, method: string, url: string): boolean {
  if (response.redirected || !object(result) || result.success !== true || !object(result.data) ||
      !object(result.affected) || !object(result.deletedIds) || !Array.isArray(result.warnings)) return false;
  const leaf = url.split('/').at(-1);
  if (method === 'DELETE')
    return result.data.segmentId === leaf && Array.isArray(result.deletedIds.segments) && result.deletedIds.segments.includes(leaf);
  if (method === 'PUT' && leaf === 'order') {
    const order = result.data.segmentOrder;
    const affected = result.affected.segmentOrder;
    return Array.isArray(order) && order.every(isId) && new Set(order).size === order.length &&
      Array.isArray(affected) && affected.length === order.length && affected.every((id, index) => id === order[index]);
  }
  const id = result.data.id;
  const expected = method === 'PATCH' ? url.split('/').at(-2) : leaf;
  return isId(id) && (method === 'POST' || id === expected) && Array.isArray(result.affected.segments) &&
    result.affected.segments.some(row => object(row) && row.id === id);
}
