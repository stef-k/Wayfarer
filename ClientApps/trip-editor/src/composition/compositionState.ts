import { reactive } from 'vue';
import { copy } from './compositionModel';
import type { Action, ReviewDraft, Selection, Workspace } from './compositionTypes';

/** Account-scoped local view/draft only. Source snapshots, auth/consent tokens and request outcomes are never retained. */
export const draftKey = (server: string, account: string, composition: string): string => `wayfarer:composition:v1:${server}:${account}:${composition}`;
export interface RetainedView { selection: Selection | null; drafts: Record<string, ReviewDraft>; expanded: string[]; drawer: number; collapsed: boolean; tab: string;
  block: ReviewDraft | null; blockMode: boolean }
export const viewKey = (selection: Selection): string => `${selection.side}/${selection.kind}/${selection.id}`;
export const createCompositionState = (account: string, composition: string) => {
  let active = true;
  let readGeneration = 0;
  const state = reactive({ workspace: null as Workspace | null, pending: false, loading: false, error: '', status: '',
    needsInspection: false, view: { selection: null, drafts: {}, expanded: [], drawer: 45, collapsed: false, tab: 'a', block: null, blockMode: false } as RetainedView });
  const current = (): boolean => active && Boolean(account && composition);
  return {
    state,
    restore: (value: string | null): void => {
      try { const v = JSON.parse(value ?? 'null') as RetainedView | null;
        if (v && v.drafts && Array.isArray(v.expanded) && typeof v.drawer === 'number') state.view = v;
      } catch { /* A damaged local view never authorizes a write. */ }
    },
    retain: (): string => JSON.stringify(state.view),
    read: async (load: () => Promise<Workspace>): Promise<boolean> => {
      const generation = ++readGeneration; state.loading = true;
      try {
        const w = await load();
        if (!current() || generation !== readGeneration || w.compositionId !== composition) return false;
        if (state.workspace && w.tripId !== state.workspace.tripId) throw new Error('The saved destination does not match this comparison.');
        state.workspace = w; state.needsInspection = false; state.error = ''; return true;
      } catch (error) { if (current() && generation === readGeneration) state.error = error instanceof Error ? error.message : 'Refresh unavailable.'; return false; }
      finally { if (current() && generation === readGeneration) state.loading = false; }
    },
    /** Captures submitted choices before previews/network; later input remains owned by its current draft. */
    submit: async (draft: ReviewDraft, action: Action, save: (frozen: Action) => Promise<Pick<Workspace, 'compositionId' | 'tripId' | 'current' | 'mappings'>>,
      accepted: () => void): Promise<void> => {
      if (!current() || state.pending || state.needsInspection || !state.workspace) return;
      const submitted = JSON.stringify(draft); const workspace = state.workspace; const selected = state.view.selection && viewKey(state.view.selection);
      const frozen = copy(action); state.pending = true; state.error = ''; state.status = 'Saving the reviewed action…';
      ++readGeneration;
      try {
        const result = await save(frozen);
        if (!current() || result.compositionId !== composition || result.tripId !== workspace.tripId) return;
        state.workspace = { ...workspace, current: result.current, mappings: result.mappings };
        const sameDraft = selected === (state.view.selection && viewKey(state.view.selection)) && submitted === JSON.stringify(draft);
        if (sameDraft) { accepted(); state.status = 'Saved to C.'; }
        else state.status = 'Earlier action saved to C. Newer choices remain unsaved; review current C before applying.';
      } catch (error) {
        if (!current()) return;
        state.error = error instanceof Error ? error.message : 'The save could not be confirmed. Inspect or refresh C.';
        // Ordinary refresh is required after any refusal or uncertain save; it never replays the action.
        state.needsInspection = true; state.status = 'Choices retained. Refresh and review current C.';
      } finally { if (current()) state.pending = false; }
    },
    dispose: (): void => { active = false; ++readGeneration; }
  };
};
