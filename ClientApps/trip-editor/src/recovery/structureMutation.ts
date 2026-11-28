import type { EditorMutationResult, EditorTripState } from '../types';
import { CustodyRefusal, readCustody } from './custody';
import { issueStructure, sendStructure } from './receiptStructure';
import { object } from './receiptStructureResults';
import { persistStructure, readStagedStructure, rememberStructure, reserveStructure, settleStructure, StaleStructureDraft,
  targetRow, type StructureDraftVersion } from './structureCustody';
import { readStructureOwner, StructureOwnerUnavailable } from './structureOwner';
import { dispatchAuthority, structureContract, structureRoute, type StructureRoute } from './structureRoutes';

export const structureCustodyChanged = 'trip-structure-custody-changed';
export const structureOwnerLost = 'trip-structure-owner-unavailable';
const stagedIntents = new Map<string, Promise<StructureDraftVersion>>();

const revisionKey = (scope: { account: string; tripId: string }, route: StructureRoute, localId?: string): string =>
  JSON.stringify([scope.account, scope.tripId, route.kind, route.target.type, route.target.id, localId]);

export class StructureRefused extends Error {
  constructor(readonly code: string) { super('The earlier Trip save was refused. Review current values before saving again.'); }
}

/** Verifies the staged intent, obtains its exact permit, then reserves the final dispatch in one transaction. */
export async function sendStructureMutation<T>(endpoint: string, token: string, route: StructureRoute, payload: object,
  localId?: string): Promise<EditorMutationResult<T>> {
  const scope = dispatchAuthority(endpoint);
  const current = (): boolean => {
    try { return dispatchAuthority(endpoint) === scope; } catch { return false; }
  };
  if (legacyRegionHold(scope.account, scope.tripId) !== null) throw new CustodyRefusal('held');
  const revision = revisionKey(scope, route, localId);
  const staged = stagedIntents.get(revision);
  const canDispatch = (): boolean => current() && stagedIntents.get(revision) === staged;
  let attempt: Awaited<ReturnType<typeof reserveStructure>>;
  try {
    const { row, key } = staged
      ? await readStagedStructure(scope, route, payload, await staged)
      : await stageNewSubmission(scope, route, payload, localId);
    if (!canDispatch()) throw new CustodyRefusal('stale');
    const frozen = await issueStructure(route, scope.account, scope.tripId, token, payload);
    attempt = await reserveStructure(row, key, frozen, canDispatch);
  } catch (error) {
    if (!(error instanceof CustodyRefusal) || error.reason !== 'stale') throw error;
    window.dispatchEvent(new Event(structureCustodyChanged));
    throw new StaleStructureDraft();
  }
  window.dispatchEvent(new Event(structureCustodyChanged));
  if (!canDispatch()) throw new CustodyRefusal('stale');
  const evidence = await sendStructure(endpoint, token, attempt);
  const before = await rememberStructure(scope, attempt, evidence);
  if (!before.structure?.outcomes[attempt.permit.operationId]) throw new CustodyRefusal('held');

  let graph: EditorTripState;
  try { graph = await readStructureOwner(endpoint, scope.account, scope.tripId); }
  catch (error) {
    if (error instanceof StructureOwnerUnavailable && current()) window.dispatchEvent(new Event(structureOwnerLost));
    throw new Error(evidence.state === 'applied' || evidence.state === 'checkedNoOp'
      ? 'Saved, current details unavailable. Use Check server.' : 'Current Trip details are unavailable. Use Check server.');
  }
  try {
    await settleStructure(before, graph, current);
  } finally {
    window.dispatchEvent(new Event(structureCustodyChanged));
  }
  if (evidence.state !== 'applied' && evidence.state !== 'checkedNoOp')
    throw new StructureRefused(String(evidence.code ?? evidence.state));
  const updated = await readCustody(scope.account, scope.tripId);
  if (!current() || updated.structure?.drafts[attempt.key] && updated.structure.drafts[attempt.key].generation > attempt.generation)
    throw new CustodyRefusal('stale');
  return projectReceipt<T>(route, graph, evidence.result);
}

/** An untouched create may start its own local draft. Delete and direct reorder controls stage at submission. */
function stageNewSubmission(scope: ReturnType<typeof dispatchAuthority>, route: StructureRoute, payload: object, localId?: string) {
  const { kind } = route;
  if (kind.endsWith('-fields') || kind === 'place-save' || kind === 'region-order' || kind === 'area-geometry')
    throw new CustodyRefusal('stale');
  return persistStructure(scope, route, payload, localId, kind.endsWith('-create'));
}

/** Supplies existing Editor controls with a qualified current snapshot and exact historical deletion IDs. */
function projectReceipt<T>(route: StructureRoute, graph: EditorTripState, receipt: unknown): EditorMutationResult<T> {
  if (!object(receipt)) throw new Error('Invalid stored Trip receipt.');
  const { kind, target } = route;
  const deletedIds = { regions: [] as string[], places: [] as string[], areas: [] as string[], segments: [] as string[], tags: [] };
  const affected: EditorMutationResult<T>['affected'] = { metadata: graph.metadata, regions: [], regionOrder: null,
    places: [], placeOrdersByRegionId: {}, areas: [], areaOrdersByRegionId: {}, segments: [], segmentOrder: null,
    tags: [], tagOrder: null, visitProgress: null, options: null };

  const orderedRegions = () => {
    affected.regionOrder = graph.regionOrder;
    affected.regions = graph.regionOrder.map(id => graph.regionsById[id]);
  };
  const orderedPlaces = (id: string) => {
    const order = graph.placeOrderByRegionId[id] ?? [];
    affected.placeOrdersByRegionId[id] = order;
    const seen = new Set(affected.places.map(row => row.id));
    affected.places.push(...order.filter(id => !seen.has(id)).map(id => graph.placesById[id]));
  };
  const orderedAreas = (id: string) => {
    const order = graph.areaOrderByRegionId[id] ?? [];
    affected.areaOrdersByRegionId[id] = order;
    affected.areas.push(...order.map(id => graph.areasById[id]));
  };
  const id = receipt.id as string;
  let data: unknown = null;
  if (kind.endsWith('-fields') || kind.endsWith('-create') || kind === 'place-save' || kind === 'area-geometry') {
    const type = kind.split('-')[0];
    data = targetRow(graph, type, id);
    if (!data) throw new Error('Saved, current details unavailable. Use Check server.');
    if (type === 'region') {
      if (kind === 'region-create') orderedRegions();
      else affected.regions = [graph.regionsById[id]];
    } else if (type === 'place') {
      affected.places = [graph.placesById[id]];
      if (kind === 'place-create' || receipt.regionId !== receipt.fromRegionId && kind === 'place-save') {
        if (kind === 'place-save') orderedPlaces(receipt.fromRegionId as string);
        orderedPlaces(graph.placesById[id].regionId);
      }
    } else if (kind === 'area-create') orderedAreas(graph.areasById[id].regionId);
    else affected.areas = [graph.areasById[id]];
  } else if (kind === 'region-order') {
    orderedRegions();
    data = { tripId: graph.tripId, regionOrder: affected.regionOrder };
  } else if (kind === 'place-order' || kind === 'area-order') {
    if (kind === 'place-order') orderedPlaces(target.id);
    else orderedAreas(target.id);
    data = { regionId: target.id, placeOrder: graph.placeOrderByRegionId[target.id], areaOrder: graph.areaOrderByRegionId[target.id] };
  } else {
    affected.visitProgress = graph.visitProgress;
    if (kind === 'region-delete') {
      orderedRegions();
      if (!graph.regionsById[id]) deletedIds.regions = [id];
      deletedIds.places = (receipt.deletedPlaceIds as string[]).filter(id => !graph.placesById[id]);
      deletedIds.areas = (receipt.deletedAreaIds as string[]).filter(id => !graph.areasById[id]);
      for (const order of receipt.placeOrders as { regionId: string }[]) orderedPlaces(order.regionId);
      for (const order of receipt.areaOrders as { regionId: string }[]) orderedAreas(order.regionId);
      data = { tripId: graph.tripId, sourceRegionId: id, places: receipt.places, areas: receipt.areas };
    } else if (kind === 'place-delete') {
      if (!graph.placesById[id]) deletedIds.places = [id];
      orderedPlaces(receipt.regionId as string);
      data = { tripId: graph.tripId, placeId: id, regionId: receipt.regionId };
    } else {
      if (!graph.areasById[id]) deletedIds.areas = [id];
      orderedAreas(receipt.regionId as string);
      data = { areaId: id, regionId: receipt.regionId };
    }
  }

  if (object(receipt.segmentEffects)) {
    const effects = receipt.segmentEffects;
    deletedIds.segments = (effects.deletedSegmentIds as string[]).filter(id => !graph.segmentsById[id]);
    affected.segments = (effects.updatedSegments as { id: string }[]).filter(row => graph.segmentsById[row.id])
      .map(row => graph.segmentsById[row.id]);
    if (effects.segmentOrder !== null) affected.segmentOrder = graph.segmentOrder;
  }
  const warnings = object(receipt.addressEffect) ? (receipt.addressEffect.warningCodes as string[]).map(code =>
    ({ code, message: '', entityType: 'place', entityId: id })) : [];
  return { contract: structureContract, success: true, data: data as T, affected, deletedIds, warnings };
}

/** Sends one explicitly confirmed Region disposition through its typed receipt route. */
export function submitRegionDeletion<T>(endpoint: string, tripId: string, sourceId: string, token: string,
  payload: object): Promise<EditorMutationResult<T>> {
  const route = structureRoute(endpoint, tripId, 'region-delete', { type: 'region', id: sourceId });
  return sendStructureMutation(endpoint, token, route, payload);
}

/** Retains an updated form payload without issuing, preparing or authorizing any request. */
export async function saveStructureIntent(endpoint: string, kind: string, target: StructureRoute['target'],
  payload: object, draftId?: string): Promise<void> {
  const scope = dispatchAuthority(endpoint);
  const route = structureRoute(endpoint, scope.tripId, kind, target);
  const revision = revisionKey(scope, route, draftId);
  const staged = persistStructure(scope, route, payload, draftId).then(({ key, generation }) => ({ key, generation }));
  stagedIntents.set(revision, staged);
  await staged;
  window.dispatchEvent(new Event(structureCustodyChanged));
}

/** An old hold has no proof and may only be acknowledged locally after inspecting the current Trip. */
export const legacyRegionHoldKey = (account: string, trip: string): string =>
  `wayfarer:region-deletion:v1:${location.origin}:${account}:${trip}`;
export const legacyRegionHold = (account: string, trip: string): string | null => localStorage.getItem(legacyRegionHoldKey(account, trip));
