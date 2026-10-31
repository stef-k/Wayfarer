import { computed, onMounted, onUnmounted, provide, reactive, type InjectionKey, type Ref } from 'vue';
import type { EditorTripState } from '../types';
import type { EditorTarget } from '../composables/useEditorSurface';
import type { PlanningAggregate, PlanningAcknowledgement, PlanningEntry, PlanningRequest } from './planningTypes';

const copy = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T;
/** Typed structural equality ignores object member order while preserving authored entry order and absence. */
export const samePlanning = (left: unknown, right: unknown): boolean => {
  const canonical = (value: unknown): unknown => Array.isArray(value) ? value.map(canonical)
    : value && typeof value === 'object' ? Object.fromEntries(Object.entries(value).sort(([a], [b]) => a.localeCompare(b)).map(([k, v]) => [k, canonical(v)])) : value;
  return JSON.stringify(canonical(left)) === JSON.stringify(canonical(right));
};

/** Workspace-lifetime draft; held attempts survive disclosure/target/dock changes. */
export interface PlanningDraft {
  kind: string; id: string; parentId: string; original: PlanningAggregate | null; entries: PlanningEntry[];
  pending: boolean; uncertain: boolean; error: string | null; confirmed: boolean;
}

/** Creates private planning state inside the existing owner workspace, with no persisted check memory. */
export const createPlanningWorkspace = (state: Ref<EditorTripState | null>, endpoint: string, antiforgery: string) => {
  const drafts = reactive<Record<string, PlanningDraft>>({});
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
  const dirty = (draft: PlanningDraft): boolean => draft.pending || draft.uncertain || !samePlanning(value(draft), draft.original);

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
    if (draft.pending || draft.uncertain || !dirty(draft)) return;
    const desired = value(draft);
    const request: PlanningRequest = { expected: { parentId: draft.parentId, planning: copy(draft.original) },
      operation: desired ? 'set' : 'clear', ...(desired ? { value: desired } : {}) };
    draft.pending = true;
    draft.error = null;
    draft.confirmed = false;
    try {
      const response = await fetch(`${endpoint}/planning/${draft.kind}/${draft.id}`, {
        method: 'PATCH', credentials: 'same-origin', cache: 'no-store',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json', RequestVerificationToken: antiforgery }, body: JSON.stringify(request)
      });
      if (!response.ok) {
        const problem = await response.json() as { code?: string; detail?: string };
        const known = [400, 401, 403, 404, 409].includes(response.status) && typeof problem.code === 'string'
          && ['planning-request-invalid', 'planning-value-invalid', 'mutation-authority-unavailable', 'mutation-scope-stale',
            'mutation-base-stale', 'mutation-scope-unavailable', 'mutation-contention'].includes(problem.code);
        if (!known) throw new Error('Planning acknowledgement unavailable.');
        draft.error = problem.detail ?? 'Planning was not applied. Keep the draft and review current values.';
        return;
      }
      const reply = await response.json() as PlanningAcknowledgement;
      if (response.redirected || reply.contract !== 'planning-1' || reply.success !== true || reply.applied !== !samePlanning(request.expected.planning, desired)
        || reply.targetKind !== draft.kind || reply.targetId !== draft.id || reply.parentId !== draft.parentId
        || !Object.hasOwn(reply, 'planning') || !samePlanning(reply.planning, desired)) throw new Error('Planning acknowledgement incomplete.');
      draft.original = copy(reply.planning);
      draft.confirmed = true;
      // A confirmed comparison advances even when the user typed a newer draft during this attempt.
      const graph = state.value;
      if (graph) {
        const entity = draft.kind === 'trip' ? graph.metadata : draft.kind === 'region' ? graph.regionsById[draft.id]
          : draft.kind === 'place' ? graph.placesById[draft.id] : draft.kind === 'area' ? graph.areasById[draft.id] : graph.segmentsById[draft.id];
        if (entity) entity.planning = copy(reply.planning);
      }
    } catch {
      draft.uncertain = true;
      draft.error = 'Planning save not confirmed. The earlier change may still finish. Your complete draft is retained; saving is paused. Reloading loses this draft and does not undo the attempt.';
    } finally { draft.pending = false; }
  };

  /** Explicit comparison review retains the desired aggregate. GET equality never settles an uncertain attempt. */
  const review = async (draft: PlanningDraft): Promise<void> => {
    if (draft.pending || draft.uncertain) return;
    const response = await fetch(endpoint, { credentials: 'same-origin', cache: 'no-store' });
    if (!response.ok || response.redirected) throw new Error('Current owner planning could not be loaded.');
    const graph = await response.json() as EditorTripState;
    if (graph.tripId !== state.value?.tripId) throw new Error('Owner scope changed.');
    const entity = draft.kind === 'trip' ? graph.metadata : draft.kind === 'region' ? graph.regionsById[draft.id]
      : draft.kind === 'place' ? graph.placesById[draft.id] : draft.kind === 'area' ? graph.areasById[draft.id] : graph.segmentsById[draft.id];
    if (!entity || !Object.hasOwn(entity, 'planning')) throw new Error('Original planning is unavailable.');
    Object.assign(draft, { original: copy(entity.planning ?? null), parentId: 'regionId' in entity ? entity.regionId : graph.tripId,
      error: null, confirmed: false });
  };
  return { open, save, review, dirty, drafts, diagnostics, hasChanges: computed(() => Object.values(drafts).some(dirty)) };
};

/** Component injection stays scoped to the mounted owner/Trip workspace. */
export const planningWorkspaceKey: InjectionKey<ReturnType<typeof createPlanningWorkspace>> = Symbol('owner planning');

/** Installs the private workspace and its existing browser unload convention at the App boundary. */
export const providePlanningWorkspace = (state: Ref<EditorTripState | null>, endpoint: string, token: string): void => {
  const workspace = createPlanningWorkspace(state, endpoint, token);
  provide(planningWorkspaceKey, workspace);
  const beforeUnload = (event: BeforeUnloadEvent): void => {
    if (workspace.hasChanges.value) { event.preventDefault(); event.returnValue = ''; }
  };
  onMounted(() => window.addEventListener?.('beforeunload', beforeUnload));
  onUnmounted(() => window.removeEventListener?.('beforeunload', beforeUnload));
};
