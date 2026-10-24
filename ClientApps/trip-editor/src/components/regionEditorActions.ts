import { onUnmounted, watch } from 'vue';
import { validateRegionCreation, type RegionCreationRequest } from '../api/creationTypes';
import { createRegion, EditorLifecycleConfirmationError, EditorMutationHttpError, EditorValidationError, orderRegions, updateRegion } from '../api/tripEditorApi';
import type { EditorMutationResult, EditorRegion, Guid } from '../types';
import { createRegionDeletionController, heldRegionDeletionKey } from './regionDeletionState';
import { confirm } from '../composables/useConfirmDialog';
import { buildRegionCreateTarget, buildRegionEditTarget } from './regionPlaceEditorTargets';
import { buildRegionRequest, emptyAreaDraft, emptyPlaceDraft } from './regionPlaceDrafts';

/// Coordinates region-specific editor actions while RegionManager owns shared draft state.
export function useRegionEditorActions(context: any) {
  let editorAlive = true;
  const owner = context.regionDraftState;
  let savingSession: number | null = null;
  watch(() => owner.session(), () => {
    if (savingSession === null) return;
    savingSession = null;
    context.isSaving.value = false;
  }, { flush: 'sync' });
  onUnmounted(() => { editorAlive = false; });
  /// Unknown earlier Region effects fence conflicting metadata/reorder/deletion actions in this Trip.
  const held = (): boolean => {
    const mount = document.getElementById('trip-editor-app');
    if (!mount?.dataset.accountId || !mount.dataset.tripId) return true;
    return Boolean(localStorage.getItem(heldRegionDeletionKey(location.origin, mount.dataset.accountId, mount.dataset.tripId)));
  };
  const openCreate = async (): Promise<void> => {
    const target = buildRegionCreateTarget();
    const isAlreadyActive = context.props.editorSurface.isTargetActive(target);
    if (!(await context.props.editorSurface.activateTarget(target)) || isAlreadyActive) {
      return;
    }

    context.regionDraftState.reset(null);
    owner.beginCreate(context.props.state);
    Object.assign(context.placeDraft, emptyPlaceDraft());
    Object.assign(context.areaDraft, emptyAreaDraft());
    context.draft.name = 'New Region';
    context.regionCreateBaselineRequest.value = buildRegionRequest(context.draft);
    context.placeCreateBaselineRequest.value = null;
    context.areaCreateBaselineRequest.value = null;
    context.resetFeedback();
  };

  const openEdit = async (region: EditorRegion): Promise<void> => {
    const target = buildRegionEditTarget(region);
    const isAlreadyActive = context.props.editorSurface.isTargetActive(target);
    if (!region.capabilities.canEdit || !(await context.props.editorSurface.activateTarget(target)) || isAlreadyActive) {
      return;
    }

    Object.assign(context.placeDraft, emptyPlaceDraft());
    Object.assign(context.areaDraft, emptyAreaDraft());
    context.regionDraftState.reset(region);
    context.regionCreateBaselineRequest.value = null;
    context.placeCreateBaselineRequest.value = null;
    context.areaCreateBaselineRequest.value = null;
    context.resetFeedback();
  };

  const resetDraft = (): void => {
    if (!context.draft.id) {
      context.regionDraftState.reset(null);
      owner.beginCreate(context.props.state);
      context.draft.name = 'New Region';
      context.regionCreateBaselineRequest.value = buildRegionRequest(context.draft);
    } else {
      context.regionDraftState.reset(context.activeRegion.value);
    }
    context.resetFeedback();
  };

  const cancelDraft = async (): Promise<void> => {
    await context.props.editorSurface.closeActiveTarget('Discard unsaved region changes?');
  };

  const saveDraft = async (): Promise<void> => {
    if (context.isSaving.value) return;
    if (owner.uncertain.value || !context.draft.id && owner.creationUncertain.value) {
      context.applyError(new Error('The earlier Region save has an unknown outcome. Keep your draft, review current state and an ordinary refresh cannot establish whether creation committed. Do not replay an uncertain creation.'), 'Region save held.');
      return;
    }
    if (held()) { context.applyError(new Error('An earlier Region deletion has an unresolved outcome. Reconcile it before changing Regions.'), 'Region operation held.'); return; }
    const regionId = context.draft.id;
    const session = owner.session();
    const currentAttempt = (): boolean => editorAlive && owner.session() === session;
    let dispatched = false;
    let creation: RegionCreationRequest | null = null;
    savingSession = session;
    context.isSaving.value = true;
    context.resetFeedback();

    try {
      const submitted = regionId ? context.regionDraftState.request() : { expected: {}, changes: buildRegionRequest(context.draft) };
      if (regionId && Object.keys(submitted.changes).length === 0) return;
      if (!regionId) {
        if (!owner.creationOriginals.value) throw new Error('Original Region membership is unavailable. Explicitly review a new starting view.');
        creation = { expected: { regions: owner.creationOriginals.value }, value: submitted.changes } as RegionCreationRequest;
      }
      owner.pending.value = true;
      dispatched = true;
      if (creation) owner.creationUncertain.value = true;
      const result = regionId
        ? await updateRegion(context.props.editorEndpoint, regionId, context.props.antiforgeryToken, submitted)
        : await createRegion(context.props.editorEndpoint, context.props.antiforgeryToken, creation!);
      if (!currentAttempt()) return;
      if (creation) {
        validateRegionCreation(result, context.props.state.tripId, creation);
        owner.creationUncertain.value = false;
      }
      // Acknowledge before emitting: Vue schedules the parent's authoritative props for a later tick.
      context.regionDraftState.acknowledge(result.data, submitted);
      context.emit('mutationApplied', result as EditorMutationResult<unknown>);
      context.regionCreateBaselineRequest.value = null;
      context.props.editorSurface.replaceActiveTarget(context.activeRegionTarget.value);
      if (Object.keys(owner.request().changes).length === 0) context.markSaved();
    } catch (error) {
      if (!currentAttempt()) return;
      // A transport/server failure is not evidence of no effect. Preserve even a newer reversal to the old base.
      const rejected = error instanceof EditorValidationError || error instanceof EditorLifecycleConfirmationError
        || error instanceof EditorMutationHttpError && [401, 403, 404, 405, 409, 429].includes(error.status);
      if (creation && rejected) owner.creationUncertain.value = false;
      if (dispatched && !rejected) {
        owner.uncertain.value = true;
        if (!regionId) owner.creationUncertain.value = true;
      }
      context.applyError(error, 'Region save failed.');
    } finally {
      if (currentAttempt() && savingSession === session) {
        savingSession = null;
        owner.pending.value = false;
        context.isSaving.value = false;
      }
    }
  };

  const deleteDraftRegion = async (): Promise<void> => {
    if (!context.activeRegion.value || !context.activeRegion.value.capabilities.canDelete) {
      return;
    }

    const region = context.activeRegion.value;
    const deletedTarget = context.activeRegionTarget.value;
    const mount = document.getElementById('trip-editor-app');
    const account = mount?.dataset.accountId;
    if (!account) { context.applyError(new Error('Reload the editor to verify your account.'), 'Account unavailable.'); return; }
    const scopeKey = heldRegionDeletionKey(location.origin, account, region.tripId);
    const controller = createRegionDeletionController({
      sourceId: region.id, tripId: region.tripId, endpoint: context.props.editorEndpoint,
      antiforgeryToken: context.props.antiforgeryToken, storage: localStorage, scopeKey, fetch: window.fetch.bind(window),
      isCurrent: () => editorAlive && document.getElementById('trip-editor-app') === mount && mount?.dataset.accountId === account && mount?.dataset.tripId === region.tripId,
      onSuccess: result => {
        context.emit('mutationApplied', result as EditorMutationResult<unknown>);
        // Only the deleted Region's own draft is cleared, after authoritative slices have been applied.
        context.regionDraftState.reset(null);
        context.regionCreateBaselineRequest.value = null;
        context.props.editorSurface.clearActiveTarget(deletedTarget);
        context.markSaved();
      }
    });
    const settled = confirm({ title: `Delete Region ‘${region.name}’?`,
      message: 'The Region and its name, notes, cover and map center will be lost. Choose independently what happens to Places and Areas.',
      cancelLabel: 'Cancel', variant: 'danger', regionDeletion: controller });
    void controller.refresh();
    await settled;
  };

  const reorderRegions = async (ids: Guid[], previousIds: Guid[]): Promise<void> => {
    if (context.isOrdering.value || held()) {
      await context.restoreRegionOrder(previousIds);
      return;
    }

    if (context.isDirty.value && !(await context.confirmDiscard('Discard unsaved draft changes before reordering?'))) {
      await context.restoreRegionOrder(previousIds);
      return;
    }

    if (context.isDirty.value) {
      context.clearAllDraftsAndBaselines();
    }

    if (context.isOrdering.value) {
      await context.restoreRegionOrder(previousIds);
      return;
    }

    context.isOrdering.value = true;
    context.resetFeedback();
    try {
      const result = await orderRegions(context.props.editorEndpoint, context.props.antiforgeryToken, { regionIds: ids });
      context.emit('mutationApplied', result as EditorMutationResult<unknown>);
      context.regionDraftState.reset(null);
      context.markSaved();
    } catch (error) {
      context.applyError(error, 'Region reorder failed.');
      await context.restoreRegionOrder(previousIds);
    } finally {
      context.isOrdering.value = false;
    }
  };

  return { cancelDraft, deleteDraftRegion, openCreate, openEdit, reorderRegions, resetDraft, saveDraft };
}
