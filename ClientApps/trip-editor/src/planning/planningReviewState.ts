import { computed, ref, watch } from 'vue';
import type { EditorTripState } from '../types';
import type { EditorTarget } from '../composables/useEditorSurface';
import type { createPlanningWorkspace } from './planningState';
import { emptyCheckSetup, type CheckResult, type CheckSetup } from './planningCheckTypes';

const storageKey = 'wayfarer.latest-owner-planning-check';
const copy = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T;

/** Saved entry inventory, also used before the first request for an explicitly narrower entity shortcut. */
const savedKeys = (graph: EditorTripState): string[] => [
  { kind: 'trip', entity: graph.metadata },
  ...Object.values(graph.regionsById).map(entity => ({ kind: 'region', entity })),
  ...Object.values(graph.placesById).map(entity => ({ kind: 'place', entity })),
  ...Object.values(graph.areasById).map(entity => ({ kind: 'area', entity })),
  ...Object.values(graph.segmentsById).map(entity => ({ kind: 'segment', entity }))
].flatMap(({ kind, entity }) => (entity.planning?.entries ?? []).map(entry => `${kind}/${entity.id}/${entry.id}`));

/** Mounted review owner: saved facts, mutable check inputs and displayed results have separate lifetimes. */
export const createPlanningReview = (options: {
  state: () => EditorTripState; endpoint: string; antiforgery: string;
  workspace: ReturnType<typeof createPlanningWorkspace> | null;
  ordinaryDirty: () => boolean; saveOrdinary: () => Promise<void>;
  storage?: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;
}) => {
  const setup = ref<CheckSetup>(emptyCheckSetup());
  const result = ref<CheckResult | null>(null);
  const busy = ref(false);
  const stale = ref(false);
  const choices = ref(false);
  const opened = ref(false);
  const error = ref<string | null>(null);
  const storageNotice = ref<string | null>(null);
  const dismissed = ref<string[]>([]);
  const showDismissed = ref(false);
  let position = 0;
  let partition = '';
  let disposed = false;
  let assigning = false;
  const storage = options.storage ?? globalThis.localStorage;
  const identity = (): string => `${options.state().ownerId ?? ''}/${options.state().tripId}`;
  const dirty = (): boolean => options.ordinaryDirty() || Boolean(options.workspace?.hasChanges.value);
  const held = computed(() => Object.values(options.workspace?.drafts ?? {}).some(d => d.pending || d.uncertain || d.deleted));
  const clearStorage = (): void => {
    try { storage?.removeItem(storageKey); } catch { storageNotice.value = 'Device storage is unavailable; inputs last only in this open editor.'; }
  };
  /** Owner/Trip changes fence old replies and remove all private presentation before reading a new partition. */
  const reset = (): void => {
    position++;
    busy.value = false;
    result.value = null;
    setup.value = emptyCheckSetup();
    dismissed.value = [];
    choices.value = false;
    stale.value = false;
    error.value = null;
  };
  const stopIdentity = watch(identity, next => {
    reset();
    partition = next;
    const graph = options.state();
    try {
      const wire = storage?.getItem(storageKey);
      const saved = wire && wire.length <= 262144 ? JSON.parse(wire) : null;
      if (graph.ownerId && saved?.ownerId === graph.ownerId && saved.tripId === graph.tripId && saved.setup?.entries && Array.isArray(saved.setup.transfers))
        setup.value = copy(saved.setup);
      else clearStorage();
    } catch { clearStorage(); }
  }, { immediate: true, flush: 'sync' });
  /** A later edit or newer request makes outstanding work ineligible to publish. No snapshot is used to rebase saves. */
  const invalidate = (): void => { position++; busy.value = false; if (result.value) stale.value = true; };
  const stopFacts = watch(() => JSON.stringify(options.state()), invalidate, { flush: 'sync' });
  const stopDrafts = watch(() => JSON.stringify(options.workspace?.drafts ?? {}), invalidate, { flush: 'sync' });
  const stopInputs = watch(setup, () => { if (!assigning) invalidate(); }, { deep: true, flush: 'sync' });

  /** Saved-version checks send only check-local setup; draft values never cross this endpoint. */
  const run = async (): Promise<void> => {
    choices.value = false;
    opened.value = true;
    const request = ++position;
    const scope = identity();
    const ownerId = options.state().ownerId;
    if (!ownerId) { error.value = 'Refresh the owner editor before checking.'; return; }
    busy.value = true;
    error.value = null;
    try {
      const response = await fetch(`${options.endpoint}/planning/check`, { method: 'POST', credentials: 'same-origin', cache: 'no-store',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json', RequestVerificationToken: options.antiforgery }, body: JSON.stringify(copy(setup.value)) });
      if (disposed || request !== position || scope !== identity() || scope !== partition) return;
      if (response.status === 401 || response.status === 403) {
        reset(); clearStorage(); error.value = 'Owner access changed. Private check setup cleared; reload the editor.'; return;
      }
      if (!response.ok || response.redirected) throw new Error('Saved owner planning could not be checked. Inputs are retained; return to editing or try a fresh review.');
      const reply = await response.json() as CheckResult;
      if (disposed || request !== position || scope !== identity()) return;
      if (reply.ownerId !== ownerId) { reset(); clearStorage(); error.value = 'Owner changed. Private check setup cleared; reload the editor.'; return; }
      if (reply.contract !== 'planning-check-1' || reply.tripId !== options.state().tripId || !Array.isArray(reply.inventory) || !Array.isArray(reply.findings)
        || !reply.setup || !Array.isArray(reply.reused) || !Array.isArray(reply.invalidated)) throw new Error('Check result is incomplete. Saved planning is unchanged.');
      assigning = true;
      setup.value = copy(reply.setup);
      assigning = false;
      result.value = copy(reply);
      dismissed.value = [];
      showDismissed.value = false;
      stale.value = false;
      try {
        const wire = JSON.stringify({ ownerId, tripId: reply.tripId, setup: reply.setup });
        if (wire.length > 262144) { clearStorage(); storageNotice.value = 'This setup exceeds device retention; it lasts only in this editor.'; }
        else storage?.setItem(storageKey, wire);
      } catch { storageNotice.value = 'Device storage is unavailable; inputs last only in this open editor.'; }
    } catch (failure) {
      if (!disposed && scope === identity() && request === position) error.value = failure instanceof Error ? failure.message : 'Review unavailable.';
    } finally { if (request === position) busy.value = false; }
  };

  /** Dirty planning and ordinary drafts require an explicit saved-version decision before any assessment request. */
  const request = (target?: EditorTarget | null): void => {
    if (target) {
      const kind = target.kind === 'metadata' ? 'trip' : target.kind;
      const id = kind === 'trip' ? options.state().tripId : target.entityId;
      setup.value.selection = savedKeys(options.state()).filter(key => key.startsWith(`${kind}/${id}/`));
    }
    opened.value = true;
    if (dirty()) choices.value = true;
    else void run();
  };
  /** The user explicitly requests existing protected saves; failed/held attempts and newer typing prevent checking. */
  const saveAndCheck = async (): Promise<void> => {
    if (held.value) return;
    const scope = identity();
    try { await options.saveOrdinary(); }
    catch {
      if (scope === identity() && !disposed) error.value = 'The ordinary save did not complete. Drafts remain separate; use their save controls or check the saved version.';
      return;
    }
    if (scope !== identity() || disposed) return;
    for (const draft of Object.values(options.workspace?.drafts ?? {})) {
      if (options.workspace?.dirty(draft)) await options.workspace.save(draft);
      if (scope !== identity() || disposed) return;
      if (draft.error || draft.uncertain || draft.pending) break;
    }
    if (dirty()) { error.value = 'Changes remain unsaved or unconfirmed. Use their existing save controls, check the saved version, or return to editing.'; return; }
    await run();
  };
  const clear = (): void => { reset(); clearStorage(); };
  const dispose = (): void => { disposed = true; position++; stopIdentity(); stopFacts(); stopDrafts(); stopInputs(); };
  const summary = computed(() => {
    const findings = result.value?.findings ?? [];
    return {
      attention: findings.filter(f => ['mismatch', 'possible', 'contradiction', 'risk', 'inconsistent'].includes(f.result.status)).length,
      completed: findings.filter(f => f.result.status === 'completed').length,
      unknown: findings.filter(f => f.result.status === 'unknown').length,
      excluded: result.value?.inventory.filter(i => !i.selected).length ?? 0
    };
  });
  return { setup, result, busy, stale, choices, opened, error, storageNotice, dismissed, showDismissed, held, summary,
    request, run, saveAndCheck, clear, dispose, hasDrafts: computed(dirty) };
};
