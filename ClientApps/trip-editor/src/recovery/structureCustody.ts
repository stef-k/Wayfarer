import { access, CustodyRefusal, readCustody, requireGeneration, type Custody } from './custody';
import type { Evidence, TerminalState } from './receiptApi';
import { lookupStructure, type StructureDispatch } from './receiptStructure';
import { footprintsOverlap, structureFootprint, type Footprint } from './structureFootprint';
import type { StructureRoute } from './structureRoutes';
import { object } from './receiptStructureResults';
import type { EditorTripState } from '../types';

export type StructureDraft = { route: StructureRoute; localId?: string; payload: object; generation: number; review?: boolean };
export type StructureAttempt = StructureDispatch & { key: string; generation: number; footprint: Footprint };
export type DependentReservation = { id: string; path: string; method: string; footprint: Footprint };
export type StructureCustody = {
  drafts: Record<string, StructureDraft>;
  attempts: StructureAttempt[];
  outcomes: Record<string, Evidence & { state: TerminalState }>;
  dependents: DependentReservation[];
};

type Scope = { account: string; tripId: string };
export const emptyStructure = (): StructureCustody => ({ drafts: {}, attempts: [], outcomes: {}, dependents: [] });
const state = (row: Custody): StructureCustody => row.structure ??= emptyStructure();
const possible = (row: StructureCustody): Footprint[] => [...row.attempts, ...row.dependents].map(value => value.footprint);
const terminal = (evidence: Evidence): evidence is Evidence & { state: TerminalState } =>
  ['applied', 'checkedNoOp', 'terminalUnapplied', 'retired'].includes(evidence.state);

/** Persists the exact typed original and desired intent with its own local generation. */
export function persistStructure(scope: Scope, route: StructureRoute, payload: object, localId?: string): Promise<{ row: Custody; key: string }> {
  return access(scope.account, scope.tripId, row => {
    const drafts = state(row).drafts;
    const prefix = [route.kind, route.target.type, route.target.id];
    const key = JSON.stringify([...prefix, ...(localId === undefined ? [] : [localId])]);
    const prior = drafts[key];
    if (!prior || JSON.stringify(prior.payload) !== JSON.stringify(payload))
      drafts[key] = { route, localId, payload, generation: ++row.generation };
    return { next: row, value: { row, key } };
  });
}

/** Makes the final scope, generation, overlap and exact frozen-attempt checks in one strict transaction. */
export function reserveStructure(view: Custody, key: string, frozen: StructureDispatch, scopeCurrent: () => boolean): Promise<StructureAttempt> {
  return access(view.account, view.tripId, row => {
    requireGeneration(row, view.generation);
    const custody = state(row);
    const draft = custody.drafts[key];
    const { permit, route, body } = frozen;
    const expected = view.structure?.drafts[key];
    if (!scopeCurrent() || row.account !== view.account || row.tripId !== view.tripId || !draft || draft.generation !== expected?.generation ||
        permit.subjectId !== view.account || permit.tripId !== view.tripId || route.path !== draft.route.path ||
        route.method !== draft.route.method || JSON.stringify(draft.payload) !== JSON.stringify(withoutMarker(body)))
      throw new CustodyRefusal('stale');
    if (draft.review) throw new CustodyRefusal('review');
    const footprint = structureFootprint(route, draft.payload);
    if (possible(custody).some(prior => footprintsOverlap(footprint, prior))) throw new CustodyRefusal('held');
    const attempt = { ...frozen, key, generation: draft.generation, footprint };
    custody.attempts.push(attempt);
    return { next: row, value: attempt };
  });
}

/** Writes a dependent dispatch reservation with the same overlap decision within the same transaction. */
export function reserveDependent(scope: Scope, reservation: DependentReservation, scopeCurrent: () => boolean): Promise<void> {
  return access(scope.account, scope.tripId, row => {
    if (!scopeCurrent() || row.account !== scope.account || row.tripId !== scope.tripId) throw new CustodyRefusal('stale');
    const custody = state(row);
    if (possible(custody).some(prior => footprintsOverlap(reservation.footprint, prior))) throw new CustodyRefusal('held');
    custody.dependents.push(reservation);
    return { next: row, value: undefined };
  });
}

/** Removes only this qualified terminal invocation's reservation. */
export function releaseDependent(scope: Scope, id: string): Promise<void> {
  return access(scope.account, scope.tripId, row => {
    const custody = state(row);
    custody.dependents = custody.dependents.filter(item => item.id !== id);
    return { next: row, value: undefined };
  });
}

/** Retains receipt evidence even when the next owner read or this tab fails. */
export function rememberStructure(scope: Scope, attempt: StructureAttempt, evidence: Evidence): Promise<Custody> {
  return access(scope.account, scope.tripId, row => {
    const custody = state(row);
    const id = attempt.permit.operationId;
    if (!Object.hasOwn(custody.outcomes, id) && terminal(evidence) &&
        custody.attempts.some(candidate => candidate.permit.operationId === id))
      custody.outcomes[id] = evidence;
    return { next: row, value: row };
  });
}

/** Reads only the version-bound historical attempt routes. */
export async function observeStructure(row: Custody, endpoint: string): Promise<Custody> {
  for (const attempt of row.structure?.attempts ?? []) {
    if (row.structure?.outcomes[attempt.permit.operationId]) continue;
    const evidence = await lookupStructure(endpoint, attempt).catch(() => null);
    if (evidence) row = await rememberStructure(row, attempt, evidence);
  }
  return row;
}

/** Reconciles only known history and only clears submitted generations still current after the owner read. */
export function settleStructure(view: Custody, graph: EditorTripState, scopeCurrent: () => boolean): Promise<Custody> {
  return access(view.account, view.tripId, row => {
    requireGeneration(row, view.generation);
    if (!scopeCurrent() || graph.ownerId !== row.account || graph.tripId !== row.tripId) throw new CustodyRefusal('stale');
    const custody = state(row);
    custody.attempts = custody.attempts.filter(attempt => {
      const id = attempt.permit.operationId;
      const result = view.structure?.outcomes[id];
      if (!result) return true;
      const saved = result.state === 'applied' || result.state === 'checkedNoOp';
      const draft = custody.drafts[attempt.key];
      if (saved && createdTargetUnavailable(attempt, result, graph)) return true;
      if (draft?.generation === attempt.generation) {
        if (saved) delete custody.drafts[attempt.key];
        else draft.review = true;
        row.generation++;
      }
      return false;
    });
    return { next: row, value: row };
  });
}

/** Keeps a saved create's generated identity and overlap hold when its current row cannot be loaded. */
function createdTargetUnavailable(attempt: StructureAttempt, result: Evidence, graph: EditorTripState): boolean {
  if (!attempt.route.kind.endsWith('-create')) return false;
  if (!object(result.result) || typeof result.result.id !== 'string') return true;
  return !targetRow(graph, attempt.route.kind.slice(0, -7), result.result.id);
}

/** Reads current entities only from their exact typed identities. */
export function targetRow(graph: EditorTripState, type: string, id: string) {
  if (type === 'region') return graph.regionsById[id];
  if (type === 'place') return graph.placesById[id];
  if (type === 'area') return graph.areasById[id];
  return undefined;
}

/** Allows explicitly discarding only local intent and never clears outstanding evidence or dependent reservations. */
export function discardStructure(view: Custody, key: string): Promise<Custody> {
  return access(view.account, view.tripId, row => {
    requireGeneration(row, view.generation);
    if (Object.hasOwn(state(row).drafts, key)) {
      delete state(row).drafts[key];
      row.generation++;
    }
    return { next: row, value: row };
  });
}

/** Lets a later deliberate review preserve the desired intent while making a new local generation. */
export function reviewStructure(view: Custody, key: string): Promise<Custody> {
  return access(view.account, view.tripId, row => {
    requireGeneration(row, view.generation);
    const draft = state(row).drafts[key];
    if (!draft?.review) throw new CustodyRefusal('stale');
    draft.review = false;
    draft.generation = ++row.generation;
    return { next: row, value: row };
  });
}

export const readStructureCustody = (scope: Scope): Promise<Custody> => readCustody(scope.account, scope.tripId);

/** The persisted body must contain exactly the draft plus its transport marker. */
function withoutMarker(body: string): unknown {
  const { receiptContract: _marker, ...payload } = JSON.parse(body) as Record<string, unknown>;
  return payload;
}
