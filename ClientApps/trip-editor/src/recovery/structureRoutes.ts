import { uuid } from './receiptV2';

export const structureContract = 'trip-region-place-area-receipt-1';
export const dispatchHeader = 'X-Wayfarer-Trip-Dispatch';
export const dispatchVersion = '3';
export type Target = { type: 'trip' | 'region' | 'place' | 'area' | 'segment'; id: string };

export const isId = (value: unknown): value is string => typeof value === 'string' && uuid.test(value) &&
  value !== '00000000-0000-0000-0000-000000000000';

const routes = {
  'region-fields': ['region', 'PATCH', 'regions/:id/conditional'],
  'region-create': ['trip', 'POST', 'regions/conditional'],
  'region-order': ['trip', 'PUT', 'regions/order'],
  'region-delete': ['region', 'POST', 'regions/:id/deletion'],
  'place-fields': ['place', 'PATCH', 'places/:id/conditional'],
  'place-create': ['region', 'POST', 'regions/:id/places/conditional'],
  'place-save': ['place', 'PUT', 'places/:id'],
  'place-order': ['region', 'PUT', 'regions/:id/places/order'],
  'place-delete': ['place', 'POST', 'places/:id/deletion'],
  'area-fields': ['area', 'PATCH', 'areas/:id/conditional'],
  'area-geometry': ['area', 'PUT', 'areas/:id/geometry'],
  'area-create': ['region', 'POST', 'regions/:id/areas'],
  'area-order': ['region', 'PUT', 'regions/:id/areas/order'],
  'area-delete': ['area', 'DELETE', 'areas/:id']
} as const;

export type StructureKind = keyof typeof routes;
export type StructureRoute = { kind: StructureKind; target: Target; method: string; path: string };

type EditorAuthority = { account: string; tripId: string; endpoint: string; advertised: boolean };
let active: EditorAuthority | null = null;

/** The owner bootstrap binds the advertised protocol to a single account and workspace. */
export function acceptDispatchVersion(endpoint: string, account: string | undefined, tripId: string,
  advertised: string | null): void {
  const mount = document.getElementById('trip-editor-app');
  active = account && isId(tripId) && mount?.dataset.accountId === account && mount.dataset.tripId === tripId
    ? { account, tripId, endpoint, advertised: advertised === dispatchVersion } : null;
}

/** Rechecks the mounted root so a delayed attempt cannot send after a workspace change. */
export function dispatchAuthority(endpoint: string): EditorAuthority {
  const root = document.getElementById('trip-editor-app');
  if (!active || active.endpoint !== endpoint || root?.dataset.accountId !== active.account || root.dataset.tripId !== active.tripId)
    throw new Error('Current Trip edit authority is unavailable. Reload the editor.');
  return active;
}

/** Maps protected writes to exact literal method/path pairs independently of the caller's route text. */
export function structureRoute(endpoint: string, tripId: string, kind: string, target: Target): StructureRoute {
  if (!Object.hasOwn(routes, kind) || !isId(target.id)) throw new Error('Unknown protected Trip route');
  const operation = kind as StructureKind;
  const [type, method, suffix] = routes[operation];
  if (target.type !== type || type === 'trip' && target.id !== tripId) throw new Error('Uncorrelated protected Trip target');
  return { kind: operation, target, method, path: `${endpoint}/${suffix.replace(':id', target.id)}/receipt` };
}

/** Identifies the existing control's operation without treating prepare and preview as writes. */
export function findStructureRoute(endpoint: string, tripId: string, url: string, verb: string): StructureRoute | null {
  if (!url.startsWith(endpoint + '/')) return null;
  const suffix = url.slice(endpoint.length + 1);
  for (const [kind, [type, method, pattern]] of Object.entries(routes)) {
    if (verb !== method) continue;
    if (type === 'trip' && suffix === pattern) return structureRoute(endpoint, tripId, kind, { type, id: tripId });
    const id = new RegExp(`^${pattern.replace(':id', '([^/]+)')}$`, 'u').exec(suffix)?.[1];
    if (id && isId(id)) return structureRoute(endpoint, tripId, kind, { type, id });
  }
  return null;
}

/** Identifies the dependent Planning or Segment dispatch only, leaving advisory requests outside custody. */
export function dependentTarget(endpoint: string, tripId: string, url: string, method: string): Target | null {
  const suffix = url.startsWith(endpoint + '/') ? url.slice(endpoint.length + 1) : '';
  const planning = /^planning\/(trip|region|place|area|segment)\/([^/]+)$/u.exec(suffix);
  if (method === 'PATCH' && planning && isId(planning[2])) return { type: planning[1] as Target['type'], id: planning[2] };
  if (method === 'POST' && suffix === 'segments' || method === 'PUT' && suffix === 'segments/order')
    return { type: 'segment', id: tripId };
  const segment = /^segments\/([^/]+)(\/conditional)?$/u.exec(suffix);
  if (segment && isId(segment[1]) && (segment[2] ? method === 'PATCH' : ['PUT', 'DELETE'].includes(method)))
    return { type: 'segment', id: segment[1] };
  return null;
}
