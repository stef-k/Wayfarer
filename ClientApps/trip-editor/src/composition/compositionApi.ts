import type { Action, Content, SavedComparison, Snapshot, SourceOption, Workspace } from './compositionTypes';
import { fieldValue, parentId } from './compositionModel';
import { placeDeletionMessage, validatePlaceDeletionPreview, type PlaceDeletionPreview } from '../components/placeDeletion';
import type { RegionDeletionPreview } from '../components/regionDeletionState';
import type { EditorTripState } from '../types';

/** Ordinary HTTP errors retain choices; transport failures explicitly leave the saved outcome unconfirmed. */
export class CompositionError extends Error {
  constructor(message: string, readonly status = 0, readonly code = '') { super(message); }
}
export const compositionApi = (token: string, send: typeof fetch = fetch) => {
  const request = async <T>(url: string, body?: unknown, method = body === undefined ? 'GET' : 'POST'): Promise<T> => {
    let response: Response;
    try {
      response = await send(url, { method, credentials: 'same-origin', cache: 'no-store', redirect: 'error',
        headers: { Accept: 'application/json', ...(body === undefined ? {} : { 'Content-Type': 'application/json', RequestVerificationToken: token }) },
        ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
    } catch { throw new CompositionError('The action could not be confirmed. Inspect or refresh the saved Trip before trying again.'); }
    if (response.status === 401 || response.status === 403) throw new CompositionError('Your session is unavailable. Sign in again; your choices remain in this account’s draft.', response.status);
    const value = await response.json().catch(() => null);
    if (!response.ok) throw new CompositionError(value?.detail ?? value?.title ?? `Request failed (${response.status}). Keep your choices and review current C.`, response.status, value?.code);
    if (value === null) throw new CompositionError('The response could not be read. Inspect or refresh your saved Trip before trying again.');
    return value as T;
  };
  const root = '/api/trip-compositions';
  const editor = (trip: string): string => `/api/trips/${encodeURIComponent(trip)}/editor`;
  return {
    request,
    read: (id: string) => request<Workspace>(`${root}/${encodeURIComponent(id)}`),
    list: (a?: string, b?: string) => request<SavedComparison[]>(`${root}?${new URLSearchParams({ ...(a ? { sourceA: a } : {}), ...(b ? { sourceB: b } : {}) })}`),
    sources: (search: string, ownedOnly: boolean) => request<SourceOption[]>(`${root}/sources?${new URLSearchParams({ search, ownedOnly: String(ownedOnly) })}`),
    create: (body: unknown, version: boolean) => request<{ tripId: string; compositionId: string | null; mediaOmitted: boolean }>(`${root}${version ? '/versions' : ''}`, body),
    apply: (id: string, body: Action) => request<{ compositionId: string; tripId: string; current: Snapshot; mappings: Workspace['mappings']; mediaOmitted: boolean }>(`${root}/${id}/actions`, body),
    finish: (id: string) => request<{ tripId: string; status: string }>(`${root}/${id}/finish`, {}),
    editorState: (trip: string) => request<EditorTripState>(editor(trip)),
    /** Previews only. No copying, coordinate mutation or automatic retry occurs during preparation. */
    prepare: async (workspace: Workspace, action: Action, approve: (message: string) => Promise<boolean>): Promise<boolean> => {
      const prepareItem = async (item: Action['items'][number]): Promise<boolean> => {
        const target = item.destinationId;
        if (item.decision === 'remove' && target && item.kind === 'place') {
          const preview = await request<PlaceDeletionPreview>(`${editor(workspace.tripId)}/places/${target}/deletion-preview`, {});
          validatePlaceDeletionPreview(preview, workspace.tripId, target, parentId(workspace.current, 'place', target) ?? '');
          if (!await approve(placeDeletionMessage(preview))) return false;
          item.conditionToken = preview.confirmation.token;
        } else if (item.decision === 'remove' && target && item.kind === 'region') {
          const p = await request<RegionDeletionPreview>(`${editor(workspace.tripId)}/regions/${target}/deletion-preview`, { places: item.places, areas: item.areas });
          if (p.tripId !== workspace.tripId || p.sourceRegionId !== target || p.places !== item.places || p.areas !== item.areas
            || !p.confirmation?.token || Date.parse(p.confirmation.expiresAt) <= Date.now()) throw new Error('Removal preview is unavailable. Review again.');
          const counts = p.consequences;
          if (!await approve(`Delete Region “${p.sourceRegionName}” and its Notes, cover, centre and planning. ${counts.movedPlaces.count} Places and ${counts.movedAreas.count} Areas move to ${p.destination.name ?? 'the reviewed destination'}. Delete ${counts.deletedPlaces.count} Places, ${counts.deletedAreas.count} Areas and ${counts.deletedSegments.count} endpoint Segments. Update ${counts.updatedSegments.count} routes and detach ${counts.detachedWaypoints.count} Via associations. Sources A/B remain unchanged. This cannot be undone.`)) return false;
          item.conditionToken = p.confirmation.token;
        } else if (item.decision === 'remove') {
          if (!await approve(`Permanently remove this ${item.kind} and its saved Notes and planning from C? Sources and retained credits stay. This cannot be undone.`)) return false;
        }
        const location = item.fields?.location;
        if (target && location && item.kind === 'place') {
          const before = location.expected as number[] | null;
          const desired = location.operation === 'useSource'
            ? fieldValue(action.source === 'a' ? workspace.sourceA : workspace.sourceB, 'place', item.sourceId, 'location') : location.value;
          const coordinate = (pair: number[] | null) => pair ? { latitude: pair[1], longitude: pair[0] } : null;
          const preview = await request<{ tripId: string; targetId: string; operation: string; conditionToken: string; affectedSegmentIds: string[] }>(
            `${editor(workspace.tripId)}/places/${target}/coordinates/prepare`,
            { expected: { regionId: item.expectedParentId, location: coordinate(before) }, location: coordinate(desired as number[] | null) });
          if (preview.tripId !== workspace.tripId || preview.targetId !== target || preview.operation !== 'coordinates' || !preview.conditionToken
            || !Array.isArray(preview.affectedSegmentIds)) throw new Error('Coordinate review is unavailable.');
          if (!await approve(`Use the selected coordinates? Review ${preview.affectedSegmentIds.length} affected route(s). Route anchors/measurements and detected enrichment are reconciled by ordinary Place rules; Address stays. No provider route will regenerate.`)) return false;
          item.conditionToken = preview.conditionToken;
        }
        if (target && item.fields?.route) {
          const state = await request<EditorTripState>(editor(workspace.tripId));
          const segment = state.segmentsById[target];
          if (!segment?.originalRouteToken) throw new Error('Current route evidence is unavailable. Open ordinary route editing and review again.');
          item.conditionToken = segment.originalRouteToken;
        }
        for (const child of item.children ?? []) if (!await prepareItem(child)) return false;
        return true;
      };
      for (const item of action.items) if (!await prepareItem(item)) return false;
      return true;
    }
  };
};

/** Return context is validated by the shell; source IDs and private content never enter these URLs. */
export const editorUrl = (trip: string, composition: string): string => `/User/Trip/Edit/${encodeURIComponent(trip)}?composition=${encodeURIComponent(composition)}`;
