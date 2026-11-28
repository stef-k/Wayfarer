import { dependentFootprint } from './structureFootprint';
import { releaseDependent, reserveDependent } from './structureCustody';
import { dependentTarget, dispatchAuthority, dispatchHeader, dispatchVersion } from './structureRoutes';

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

/** Recognizes ordinary guarded no-effect refusals only for this returned invocation. */
export function dependentRefusal(response: Response, problem: unknown): boolean {
  if (response.redirected || !problem || typeof problem !== 'object') return false;
  const { code, status, errors, title } = problem as { code?: unknown; status?: unknown; errors?: unknown; title?: unknown };
  if (status !== response.status) return false;
  if (code === undefined && status === 400 && title === 'One or more validation errors occurred.' &&
      errors && typeof errors === 'object' && !Array.isArray(errors))
    return Object.keys(errors).length > 0 && Object.values(errors).every(items =>
      Array.isArray(items) && items.length > 0 && items.every(item => typeof item === 'string'));
  if (typeof code !== 'string') return false;
  if (code === 'trip-write-upgrade-required') return status === 409;
  if (code === 'mutation-authority-unavailable') return status === 401;
  if (code === 'mutation-scope-unavailable') return status === 404;
  return (status === 400 || status === 409) &&
    (code.startsWith('mutation-') || code.startsWith('planning-') || code.startsWith('segment-'));
}
