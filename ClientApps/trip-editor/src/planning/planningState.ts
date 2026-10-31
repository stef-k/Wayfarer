import { computed, onMounted, onUnmounted, provide, reactive, type InjectionKey, type Ref } from 'vue';
import type { EditorTripState } from '../types';
import { observeMutationDispatch } from '../api/tripEditorApi';
import type { EditorTarget } from '../composables/useEditorSurface';
import type { PlanningAggregate, PlanningAcknowledgement, PlanningEntry, PlanningRequest } from './planningTypes';

const copy = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T;

/** Rejects duplicate decoded members before JSON parsing can turn an ambiguous reply into acknowledgement. */
const readPlanningReply = async <T>(response: Response): Promise<T> => {
  const wire = await response.text();
  const stack: Array<Set<string> | null> = [];
  for (let index = 0; index < wire.length; index++) {
    const character = wire[index];
    if (character === '{') stack.push(new Set());
    else if (character === '[') stack.push(null);
    else if (character === '}' || character === ']') stack.pop();
    else if (character === '"') {
      const start = index++;
      while (index < wire.length && wire[index] !== '"') {
        if (wire[index] === '\\') index++;
        index++;
      }
      let next = index + 1;
      while ([' ', '\t', '\r', '\n'].includes(wire[next] ?? '')) next++;
      const keys = stack.at(-1);
      if (wire[next] === ':' && keys) {
        const name = JSON.parse(wire.slice(start, index + 1)) as string;
        if (keys.has(name)) throw new Error('Ambiguous planning reply.');
        keys.add(name);
      }
    }
  }
  return JSON.parse(wire) as T;
};
/** Typed structural equality ignores object member order while preserving authored entry order and absence. */
export const samePlanning = (left: unknown, right: unknown): boolean => {
  const canonical = (value: unknown): unknown => Array.isArray(value) ? value.map(canonical)
    : value && typeof value === 'object' ? Object.fromEntries(Object.entries(value).sort(([a], [b]) => a.localeCompare(b)).map(([k, v]) => [k, canonical(v)])) : value;
  return JSON.stringify(canonical(left)) === JSON.stringify(canonical(right));
};

/** Workspace-lifetime draft; held attempts survive disclosure/target/dock changes. */
export interface PlanningDraft {
  kind: string; id: string; parentId: string; original: PlanningAggregate | null; entries: PlanningEntry[];
  pending: boolean; uncertain: boolean; error: string | null; confirmed: boolean; deleted?: boolean;
}

/** Creates private planning state inside the existing owner workspace, with no persisted check memory. */
export const createPlanningWorkspace = (state: Ref<EditorTripState | null>, endpoint: string, antiforgery: string) => {
  const drafts = reactive<Record<string, PlanningDraft>>({});
  let publicationPosition = 0;
  const dispatches = new WeakMap<object, number>();
  const planningPositions = new Map<string, number>();
  const deletedTargets = new Set<string>();
  /** The API carries dispatch provenance by object identity, scoped to this workspace and never serialized. */
  const capturePublication = (): ((result: object) => void) => {
    const position = ++publicationPosition;
    return result => { dispatches.set(result, position); };
  };
  const isDeleted = (kind: string, id: string): boolean => deletedTargets.has(`${kind}/${id}`);
  /** Fresh server snapshots advance clean forms; local attempts fence only older or overlapping publication. */
  const publishPlanning = <T extends { id: string; planning?: PlanningAggregate | null; planningDiagnostics?: unknown }>(result: object, kind: string, incoming: T, previous?: T): T => {
    const key = `${kind}/${incoming.id}`;
    const position = dispatches.get(result) ?? ++publicationPosition;
    const draft = drafts[key];
    if (previous && (!Object.hasOwn(incoming, 'planning') || position < (planningPositions.get(key) ?? 0) || draft?.pending || draft?.uncertain))
      return { ...incoming, planning: previous.planning, planningDiagnostics: previous.planningDiagnostics };
    planningPositions.set(key, position);
    if (draft && !dirty(draft)) {
      draft.original = copy(incoming.planning ?? null);
      draft.entries = copy(incoming.planning?.entries ?? []);
      draft.confirmed = false;
    }
    return incoming;
  };
  const current = (target: EditorTarget) => {
    const graph = state.value;
    if (!graph || target.mode === 'add') return null;
    if (target.kind === 'metadata') return { kind: 'trip', entity: graph.metadata, parent: graph.tripId };
    const entity = target.kind === 'region' ? graph.regionsById[target.entityId!]
      : target.kind === 'place' ? graph.placesById[target.entityId!]
      : target.kind === 'area' ? graph.areasById[target.entityId!] : graph.segmentsById[target.entityId!];
    return entity ? { kind: target.kind, entity, parent: 'regionId' in entity ? entity.regionId : graph.tripId } : null;
  };
  const diagnostics = (target: EditorTarget): string[] => (current(target)?.entity as { planningDiagnostics?: { message: string }[] } | undefined)?.planningDiagnostics?.map(d => d.message) ?? [];
  const value = (draft: PlanningDraft): PlanningAggregate | null => draft.entries.length ? { format: 1, entries: copy(draft.entries) } : null;
  const dirty = (draft: PlanningDraft): boolean => draft.deleted === true || draft.pending || draft.uncertain || !samePlanning(value(draft), draft.original);

  /** Clean snapshots can refresh; dirty originals and unknown attempts cannot be exchanged by props. */
  const open = (target: EditorTarget): PlanningDraft | null => {
    const next = current(target);
    if (!next || !Object.hasOwn(next.entity, 'planning')) return null;
    const key = `${next.kind}/${next.entity.id}`;
    const existing = drafts[key];
    if (existing) {
      if (!dirty(existing)) {
        existing.original = copy(next.entity.planning ?? null);
        existing.entries = copy(next.entity.planning?.entries ?? []);
        existing.parentId = next.parent;
      }
      return existing;
    }
    drafts[key] = { kind: next.kind, id: next.entity.id, parentId: next.parent,
      original: copy(next.entity.planning ?? null), entries: copy(next.entity.planning?.entries ?? []),
      pending: false, uncertain: false, error: null, confirmed: false };
    return drafts[key];
  };

  /** Sends one immutable attempt; later typing remains live and a malformed acknowledgement holds further saves. */
  const save = async (draft: PlanningDraft): Promise<void> => {
    if (draft.deleted || draft.pending || draft.uncertain || !dirty(draft)) return;
    const desired = value(draft);
    const request: PlanningRequest = { expected: { parentId: draft.parentId, planning: copy(draft.original) },
      operation: desired ? 'set' : 'clear', ...(desired ? { value: desired } : {}) };
    planningPositions.set(`${draft.kind}/${draft.id}`, ++publicationPosition);
    draft.pending = true;
    draft.error = null;
    draft.confirmed = false;
    try {
      const response = await fetch(`${endpoint}/planning/${draft.kind}/${draft.id}`, {
        method: 'PATCH', credentials: 'same-origin', cache: 'no-store',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json', RequestVerificationToken: antiforgery }, body: JSON.stringify(request)
      });
      if (!response.ok) {
        const problem = await readPlanningReply<Record<string, unknown>>(response);
        const statuses: Record<string, number> = { 'planning-request-invalid': 400, 'planning-value-invalid': 400,
          'mutation-authority-unavailable': 401, 'mutation-scope-stale': 409, 'mutation-base-stale': 409,
          'mutation-scope-unavailable': 404, 'mutation-contention': 409 };
        if (response.redirected || !problem || Array.isArray(problem) || typeof problem.code !== 'string'
          || statuses[problem.code] !== response.status || problem.status !== response.status
          || ['success', 'applied'].some(key => Object.hasOwn(problem, key) && problem[key] !== false)
          || Object.keys(problem).some(key => !['type', 'title', 'status', 'detail', 'instance', 'code', 'errors', 'success', 'applied'].includes(key))
          || ['type', 'title', 'instance'].some(key => Object.hasOwn(problem, key) && typeof problem[key] !== 'string')
          || Object.hasOwn(problem, 'errors') && (!problem.errors || typeof problem.errors !== 'object' || Array.isArray(problem.errors)
            || Object.values(problem.errors).some(errors => !Array.isArray(errors) || errors.some(error => typeof error !== 'string')))
          || Object.hasOwn(problem, 'detail') && typeof problem.detail !== 'string') throw new Error('Planning acknowledgement unavailable.');
        draft.error = typeof problem.detail === 'string' ? problem.detail : 'Planning was not applied. Keep the draft and review current values.';
        return;
      }
      const reply = await readPlanningReply<PlanningAcknowledgement>(response);
      if (response.redirected || reply.contract !== 'planning-1' || reply.success !== true || reply.applied !== !samePlanning(request.expected.planning, desired)
        || reply.targetKind !== draft.kind || reply.targetId !== draft.id || reply.parentId !== draft.parentId
        || !Object.hasOwn(reply, 'planning') || !samePlanning(reply.planning, desired)) throw new Error('Planning acknowledgement incomplete.');
      draft.original = copy(reply.planning);
      draft.confirmed = true;
      planningPositions.set(`${draft.kind}/${draft.id}`, ++publicationPosition);
      // A confirmed comparison advances even when the user typed a newer draft during this attempt.
      const graph = state.value;
      if (graph) {
        const entity = draft.kind === 'trip' ? graph.metadata : draft.kind === 'region' ? graph.regionsById[draft.id]
          : draft.kind === 'place' ? graph.placesById[draft.id] : draft.kind === 'area' ? graph.areasById[draft.id] : graph.segmentsById[draft.id];
        if (entity && !draft.deleted) entity.planning = copy(reply.planning);
      }
    } catch {
      draft.uncertain = true;
      draft.error = 'Planning save not confirmed. The earlier change may still finish. Your complete draft is retained; saving is paused. Reloading loses this draft and does not undo the attempt.';
    } finally { draft.pending = false; }
  };

  /** Explicit comparison review retains the desired aggregate. GET equality never settles an uncertain attempt. */
  const review = async (draft: PlanningDraft): Promise<void> => {
    if (draft.deleted || draft.pending || draft.uncertain) return;
    const reviewPosition = ++publicationPosition;
    planningPositions.set(`${draft.kind}/${draft.id}`, reviewPosition);
    const response = await fetch(endpoint, { credentials: 'same-origin', cache: 'no-store' });
    if (!response.ok || response.redirected) throw new Error('Current owner planning could not be loaded.');
    const graph = await readPlanningReply<EditorTripState>(response);
    if (graph.tripId !== state.value?.tripId) throw new Error('Owner scope changed.');
    const entity = draft.kind === 'trip' ? graph.metadata : draft.kind === 'region' ? graph.regionsById[draft.id]
      : draft.kind === 'place' ? graph.placesById[draft.id] : draft.kind === 'area' ? graph.areasById[draft.id] : graph.segmentsById[draft.id];
    if (draft.deleted || draft.pending || draft.uncertain || planningPositions.get(`${draft.kind}/${draft.id}`) !== reviewPosition) throw new Error('Planning scope changed during review.');
    if (!entity || !Object.hasOwn(entity, 'planning')) throw new Error('Original planning is unavailable.');
    Object.assign(draft, { original: copy(entity.planning ?? null), parentId: 'regionId' in entity ? entity.regionId : graph.tripId,
      error: null, confirmed: false });
  };
  /** Authoritative deletion keeps authored intent reachable; it never settles an in-flight or unknown attempt. */
  const reconcileDeleted = (ids: { regions: string[]; places: string[]; areas: string[]; segments: string[] }): void => {
    // Deleted identities stay absent for this mounted session, even after their recovery draft is discarded.
    for (const [kind, values] of [['region', ids.regions], ['place', ids.places], ['area', ids.areas], ['segment', ids.segments]] as const) {
      for (const id of values) deletedTargets.add(`${kind}/${id}`);
    }
    for (const draft of Object.values(drafts)) {
      const deleted = draft.kind === 'region' ? ids.regions : draft.kind === 'place' ? ids.places
        : draft.kind === 'area' ? ids.areas : draft.kind === 'segment' ? ids.segments : [];
      if (deleted.includes(draft.id)) {
        if (dirty(draft)) draft.deleted = true;
        else delete drafts[`${draft.kind}/${draft.id}`];
      }
    }
  };
  /** Discard is offered only for a known deleted target with no unresolved attempt, after explicit UI consent. */
  const discardDeleted = (draft: PlanningDraft): void => {
    if (draft.deleted && !draft.pending && !draft.uncertain) delete drafts[`${draft.kind}/${draft.id}`];
  };
  /** Separate Planning status participates in shared Saved labels and exit decisions. */
  const status = (): string | null => {
    const items = Object.values(drafts);
    if (items.some(d => d.uncertain)) return 'Planning save not confirmed';
    if (items.some(d => d.pending)) return 'Planning save pending';
    if (items.some(d => d.deleted)) return 'Deleted target Planning retained';
    return items.some(dirty) ? 'Planning unsaved' : null;
  };
  return { open, save, review, dirty, drafts, diagnostics, reconcileDeleted, discardDeleted, status, capturePublication, publishPlanning, isDeleted,
    deletedDrafts: computed(() => Object.values(drafts).filter(d => d.deleted)), hasChanges: computed(() => Object.values(drafts).some(dirty)) };
};

/** Component injection stays scoped to the mounted owner/Trip workspace. */
export const planningWorkspaceKey: InjectionKey<ReturnType<typeof createPlanningWorkspace>> = Symbol('owner planning');

/** Installs the private workspace and its existing browser unload convention at the App boundary. */
export const providePlanningWorkspace = (state: Ref<EditorTripState | null>, endpoint: string, token: string) => {
  const workspace = createPlanningWorkspace(state, endpoint, token);
  provide(planningWorkspaceKey, workspace);
  const stopObserving = observeMutationDispatch(endpoint, workspace.capturePublication);
  const beforeUnload = (event: BeforeUnloadEvent): void => {
    if (workspace.hasChanges.value) { event.preventDefault(); event.returnValue = ''; }
  };
  onMounted(() => window.addEventListener?.('beforeunload', beforeUnload));
  onUnmounted(() => { stopObserving(); window.removeEventListener?.('beforeunload', beforeUnload); });
  return workspace;
};
