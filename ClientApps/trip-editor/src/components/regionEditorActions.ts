import { saveStructureIntent } from '../recovery/structureMutation';
import { StaleStructureDraft } from '../recovery/structureCustody';
import { structureContract } from '../recovery/structureRoutes';
import { type RegionOrderOriginals, validateRegionOrderAcknowledgement, validateRegionOrderPreview } from './regionOrdering';
import { computed, onUnmounted, watch } from 'vue';
import { message, plural } from '../../../../wwwroot/js/localization/messages.js';
import { validateRegionCreation, type RegionCreationRequest } from '../api/creationTypes';
import { createRegion, EditorLifecycleConfirmationError, EditorMutationHttpError, EditorValidationError, orderRegions, prepareRegionOrder, updateRegion } from '../api/tripEditorApi';
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
  let creationDraftId: string | undefined;
  watch(() => owner.session(), () => {
    if (savingSession === null) return;
    savingSession = null;
    context.isSaving.value = false;
  }, { flush: 'sync' });
  onUnmounted(() => { editorAlive = false; });
  // A reopened or reset form cannot inherit another form's staged generation.
  const stageSession = computed(() => Symbol(owner.session()));
  /** Persists typed Region intent even while an earlier response or permit is in flight. */
  watch([stageSession, () => {
    try {
      if (context.draft.id && context.activeRegion.value) {
        const payload = owner.request();
        return Object.keys(payload.changes).length ? JSON.stringify(payload) : null;
      }
      if (!creationDraftId || !owner.creationOriginals.value || !context.regionCreateBaselineRequest.value) return null;
      const value = buildRegionRequest(context.draft);
      return JSON.stringify(value) === JSON.stringify(context.regionCreateBaselineRequest.value) ? null
        : JSON.stringify({ expected: { regions: owner.creationOriginals.value }, value });
    } catch { return null; }
  }], ([session, frozen]) => {
    if (!frozen) return;
    const id = context.draft.id;
    const target = id ? { type: 'region' as const, id } : { type: 'trip' as const, id: context.props.state.tripId };
    void saveStructureIntent(context.props.editorEndpoint, id ? 'region-fields' : 'region-create', target,
      JSON.parse(frozen), id ? undefined : creationDraftId, session).catch(error => {
      if (editorAlive && session === stageSession.value && error instanceof StaleStructureDraft)
        context.applyError(error, 'Region save failed.');
    });
  });
  /// Unknown earlier Region effects fence conflicting metadata/reorder/deletion actions in this Trip.
  const held = (): boolean => {
    const mount = document.getElementById('trip-editor-app');
    if (!mount?.dataset.accountId || !mount.dataset.tripId) return true;
    return Boolean(localStorage.getItem(heldRegionDeletionKey(location.origin, mount.dataset.accountId, mount.dataset.tripId)));
  };

  /** Restores a clean creation draft with the document's suggested Region name. */
  const resetRegionCreation = (): void => {
    creationDraftId = crypto.randomUUID();
    owner.reset(null);
    owner.beginCreate(context.props.state);
    context.draft.name = message('Editor.NewRegionDraftName', 'New Region');
    context.regionCreateBaselineRequest.value = buildRegionRequest(context.draft);
  };

  const openCreate = async (): Promise<void> => {
    const target = buildRegionCreateTarget();
    const isAlreadyActive = context.props.editorSurface.isTargetActive(target);
    if (!(await context.props.editorSurface.activateTarget(target)) || isAlreadyActive) {
      return;
    }

    resetRegionCreation();
    Object.assign(context.placeDraft, emptyPlaceDraft());
    Object.assign(context.areaDraft, emptyAreaDraft());
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

  /** Resets creation to its suggestion and editing to the active saved values. */
  const resetDraft = (): void => {
    if (!context.draft.id) {
      resetRegionCreation();
    } else {
      context.regionDraftState.reset(context.activeRegion.value);
    }
    context.resetFeedback();
  };

  const cancelDraft = async (): Promise<void> => {
    await context.props.editorSurface.closeActiveTarget(message('Editor.DiscardRegionDraft', 'Discard unsaved region changes?'));
  };

  const saveDraft = async (): Promise<void> => {
    if (context.isSaving.value) return;
    if (owner.orderUncertain.value) { context.applyError(new Error('The earlier Region order has an unknown outcome. Keep your draft; do not replay it.'), 'Region operation held.'); return; }
    if (owner.uncertain.value || !context.draft.id && owner.creationUncertain.value) {
      const message = !context.draft.id && owner.creationUncertain.value
        ? 'The earlier Region creation has an unknown outcome and may have committed. Keep your draft. A refresh cannot prove noncommit; do not replay this creation.'
        : 'The earlier Region save has an unknown outcome. Keep your draft, review current state and explicitly reset before another attempt.';
      context.applyError(new Error(message), 'Region save held.');
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
        : await createRegion(context.props.editorEndpoint, context.props.antiforgeryToken, creation!, creationDraftId);
      if (!currentAttempt()) return;
      if (creation) {
        if (result.contract !== structureContract) validateRegionCreation(result, context.props.state.tripId, creation);
        owner.creationUncertain.value = false;
      }
      // Acknowledge before emitting: Vue schedules the parent's authoritative props for a later tick.
      context.regionDraftState.acknowledge(result.data, submitted);
      context.emit('mutationApplied', result as EditorMutationResult<unknown>);
      context.regionCreateBaselineRequest.value = null;
      context.props.editorSurface.replaceActiveTarget(context.activeRegionTarget.value);
      if (!owner.creationUncertain.value && Object.keys(owner.request().changes).length === 0) context.markSaved();
    } catch (error) {
      if (!currentAttempt()) return;
      // A transport/server failure is not evidence of no effect. Preserve even a newer reversal to the old base.
      const rejected = error instanceof StaleStructureDraft || error instanceof EditorValidationError || error instanceof EditorLifecycleConfirmationError
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
    if (owner.orderUncertain.value || owner.orderingPending.value || !context.activeRegion.value || !context.activeRegion.value.capabilities.canDelete) {
      return;
    }

    const region = context.activeRegion.value;
    const deletedTarget = context.activeRegionTarget.value;
    const draftSession = owner.session();
    const draftIntent = JSON.stringify(context.draft);
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
        if (owner.session() === draftSession && JSON.stringify(context.draft) === draftIntent) {
          context.regionDraftState.reset(null);
          context.regionCreateBaselineRequest.value = null;
          context.props.editorSurface.clearActiveTarget(deletedTarget);
          context.markSaved();
        }
      }
    });
    const settled = confirm({ title: message('Editor.DeleteRegionTitle', 'Delete Region ‘{0}’?', region.name),
      message: message('Editor.DeleteRegionDescription', 'The Region and its name, notes, cover and map center will be lost. Choose independently what happens to Places and Areas.'),
      cancelLabel: message('Shell.Cancel', 'Cancel'), variant: 'danger', regionDeletion: controller });
    void controller.refresh();
    await settled;
  };

  const reorderRegions = async (ids: Guid[], previousIds: Guid[], originals: RegionOrderOriginals | null): Promise<void> => {
    if (context.isOrdering.value || held() || owner.orderUncertain.value) {
      if (owner.orderUncertain.value) context.applyError(new Error('The earlier Region order may have committed. Keep your order; refresh/reset cannot prove noncommit. Do not replay it. Explicitly leave without saving if you choose to abandon this page.'), 'Region order held.');
      await context.restoreRegionOrder(previousIds);
      return;
    }
    // Pin the full operation base before any await can replace props or authored intent.
    let request: import('../types').EditorRegionOrderRequest;
    try {
      if (!originals || originals.regions.length !== previousIds.length || new Set(previousIds).size !== previousIds.length
        || new Set(ids).size !== previousIds.length || ids.length !== previousIds.length
        || originals.regions.some(r => !previousIds.includes(r.id) || !ids.includes(r.id)))
        throw new Error('The original Region order is unavailable. Keep the order and explicitly review a fresh starting view.');
      request = { expected: JSON.parse(JSON.stringify(originals)), regionIds: [...ids] };
    }
    catch (error) { context.applyError(error, 'Region order held.'); await context.restoreRegionOrder(previousIds); return; }
    if (context.isDirty.value && !(await context.confirmDiscard(message('Editor.DiscardBeforeRegionReorder', 'Discard unsaved draft changes before reordering?')))) {
      await context.restoreRegionOrder(previousIds);
      return;
    }
    if (context.isDirty.value) context.clearAllDraftsAndBaselines();
    if (context.isOrdering.value) { await context.restoreRegionOrder(previousIds); return; }
    const tripId = context.props.state.tripId;
    const session = owner.session();
    const currentAttempt = (): boolean => editorAlive && owner.session() === session;
    let dispatched = false;
    context.isOrdering.value = true;
    owner.orderingPending.value = true;
    context.resetFeedback();
    try {
      await saveStructureIntent(context.props.editorEndpoint, 'region-order', { type: 'trip', id: tripId }, request);
      const preview = await prepareRegionOrder(context.props.editorEndpoint, context.props.antiforgeryToken, request);
      if (!currentAttempt()) return;
      validateRegionOrderPreview(preview, tripId, request.expected);
      if (!(await confirm({ title: message('Editor.ReviewRegionOrder', 'Review Region order'),
        message: plural('Editor.ApplyReviewedRegionCount', ids.length, 'Apply this order to {0} Region using its reviewed content?', 'Apply this complete order to {0} Regions using their reviewed content?'),
        confirmLabel: message('Editor.ApplyReviewedOrder', 'Apply reviewed order'),
        cancelLabel: message('Editor.KeepCurrentOrder', 'Keep current order'), variant: 'default' }))) {
        if (currentAttempt()) await context.restoreRegionOrder(previousIds);
        return;
      }
      if (!currentAttempt()) return;
      request = { ...request, conditionToken: preview.conditionToken, reviewed: true };
      owner.orderIntent.value = request;
      owner.orderUncertain.value = true;
      dispatched = true;
      const result = await orderRegions(context.props.editorEndpoint, context.props.antiforgeryToken, request);
      if (result.contract !== structureContract) validateRegionOrderAcknowledgement(result, tripId, request);
      owner.orderUncertain.value = false;
      owner.orderIntent.value = null;
      owner.orderingPending.value = false;
      if (!currentAttempt()) return;
      context.emit('mutationApplied', result as EditorMutationResult<unknown>);
      // Reordering does not acknowledge or reset a newer scalar draft.
      if (Object.keys(owner.request().changes).length === 0 && !context.isDirty.value) context.markSaved();
    } catch (error) {
      const rejected = error instanceof StaleStructureDraft || error instanceof EditorValidationError
        || error instanceof EditorMutationHttpError && [401, 403, 404, 405, 409, 429].includes(error.status);
      if (rejected || !dispatched) { owner.orderUncertain.value = false; owner.orderIntent.value = null; }
      if (!currentAttempt()) return;
      const heldError = dispatched && !rejected ? new Error('The Region order could not be confirmed and may have committed. Your intent is retained. Do not replay it; refresh/reset cannot prove noncommit. Explicitly leave without saving to abandon this page.') : error;
      context.applyError(heldError, 'Region reorder failed.');
      await context.restoreRegionOrder(previousIds);
    } finally {
      owner.orderingPending.value = false;
      if (editorAlive) context.isOrdering.value = false;
    }
  };

  return { cancelDraft, deleteDraftRegion, openCreate, openEdit, reorderRegions, resetDraft, saveDraft };
}
