import type { EditorTripState } from '../types';
import { keysAre, rejectDuplicateMembers } from './receiptV2';
import { object } from './receiptStructureResults';
import { isId } from './structureRoutes';

export class StructureOwnerUnavailable extends Error {}

/** Reads exact current owner state, rejecting log-in redirects and attributed or incomplete snapshots. */
export async function readStructureOwner(endpoint: string, account: string, tripId: string): Promise<EditorTripState> {
  const reply = await fetch(endpoint, { credentials: 'same-origin', cache: 'no-store', headers: { Accept: 'application/json' } });
  if (reply.redirected || [401, 403, 404].includes(reply.status)) throw new StructureOwnerUnavailable();
  if (!reply.ok) throw new Error('Current Trip values are unavailable');
  const raw = await reply.text();
  rejectDuplicateMembers(raw, false);
  const graph: unknown = JSON.parse(raw);
  if (!object(graph) || graph.ownerId !== account || graph.tripId !== tripId) throw new StructureOwnerUnavailable();
  if (!object(graph.metadata) || graph.metadata.id !== tripId ||
      !['regionsById', 'placesById', 'areasById', 'segmentsById', 'placeOrderByRegionId', 'areaOrderByRegionId', 'visitProgress', 'options']
        .every(key => object(graph[key])) || !['regionOrder', 'segmentOrder'].every(key => Array.isArray(graph[key])))
    throw new Error('Current Trip comparisons are incomplete');
  return graph as unknown as EditorTripState;
}

/** Captures the Area's exact edit-start material independently of display defaults. */
export function areaMaterial(graph: EditorTripState, id: string) {
  const area = graph.areasById[id];
  const value = area?.originalValues;
  if (!value || !Object.hasOwn(area, 'planning') ||
      !['displayOrder', 'regionId', 'name', 'notesHtml', 'fillHex', 'geometry'].every(key => Object.hasOwn(value, key)) ||
      !(value.displayOrder === null || Number.isInteger(value.displayOrder)) || value.regionId !== area.regionId ||
      typeof value.geometry !== 'string' || !/^area-polygon-1:[0-9A-F]{64}$/u.test(value.geometry))
    throw new Error('Original Area comparison values are unavailable. Reload and review the area.');
  return { id, regionId: value.regionId, displayOrder: value.displayOrder!, name: value.name, notes: value.notesHtml,
    fillHex: value.fillHex, geometry: value.geometry, planning: area.planning ?? null };
}

/** Requires the exact complete Area sibling set published for this selected Region. */
export function areaMaterials(graph: EditorTripState, regionId: string) {
  const ids = graph.areaOrderByRegionId[regionId] ?? [];
  if (!graph.regionsById[regionId] || new Set(ids).size !== ids.length ||
      ids.length !== Object.values(graph.areasById).filter(area => area.regionId === regionId).length)
    throw new Error('Original Area membership is unavailable. Reload and review the Region.');
  return ids.map(id => {
    const area = areaMaterial(graph, id);
    if (area.regionId !== regionId) throw new Error('Original Area scope changed.');
    return area;
  });
}

/** Reviews the deletion against the captured owner originals and exact target before any final consent. */
export async function prepareAreaDeletion(endpoint: string, token: string, graph: EditorTripState, id: string) {
  const area = areaMaterial(graph, id);
  const areas = areaMaterials(graph, area.regionId).map(({ id, displayOrder }) => ({ id, displayOrder }));
  const reply = await fetch(`${endpoint}/areas/${id}/deletion-preview`, {
    method: 'POST', cache: 'no-store', credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json', RequestVerificationToken: token },
    body: JSON.stringify({ expected: { area, areas } })
  });
  if (reply.status !== 200 || reply.redirected) throw new Error('Area preview unavailable. Reload and review the area.');
  const raw = await reply.text();
  rejectDuplicateMembers(raw, false);
  const value: unknown = JSON.parse(raw);
  if (!object(value) || !keysAre(value, ['contract', 'tripId', 'areaId', 'regionId', 'name', 'hasPlanning', 'confirmation']) ||
      value.contract !== 'trip-region-place-area-receipt-1' || value.tripId !== graph.tripId || value.areaId !== id ||
      !isId(value.regionId) || value.regionId !== area.regionId || value.name !== area.name || value.hasPlanning !== (area.planning !== null) ||
      !object(value.confirmation) || !keysAre(value.confirmation, ['token', 'expiresAt']) ||
      typeof value.confirmation.token !== 'string' || !value.confirmation.token ||
      typeof value.confirmation.expiresAt !== 'string' || Date.parse(value.confirmation.expiresAt) <= Date.now())
    throw new Error('Area preview is incomplete. No deletion was authorized.');
  return { name: area.name, hasPlanning: value.hasPlanning as boolean, confirmationToken: value.confirmation.token };
}
