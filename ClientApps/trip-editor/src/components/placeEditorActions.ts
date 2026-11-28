import { saveStructureIntent } from '../recovery/structureMutation';
import { structureContract } from '../recovery/structureRoutes';
import { message, plural } from '../../../../wwwroot/js/localization/messages.js';
import { editorWarningText } from './editorFeedbackText';
import { validatePlaceCreation, type PlaceCreationRequest } from '../api/creationTypes';
import { nextTick, onUnmounted, watch } from 'vue';
import { createPlace, previewPlaceDeletion, deletePlace, EditorLifecycleConfirmationError, EditorMutationHttpError, EditorValidationError, orderPlaces, preparePlaceStructure, updatePlace, updatePlaceConditional } from '../api/tripEditorApi';
import type { EditorGeocodeSearchResult, EditorMutationResult, EditorPlace, EditorRegion, Guid } from '../types';
import { confirm } from '../composables/useConfirmDialog';
import { buildPlaceCreateTarget, buildPlaceEditTarget } from './regionPlaceEditorTargets';
import { buildPlaceRequest, defaultPlaceIconName, defaultPlaceMarkerColor, emptyAreaDraft, emptyPlaceDraft, emptyRegionDraft, withoutRegionId } from './regionPlaceDrafts';
import { validatePlaceAcknowledgement } from './placeConditionalDraft';
import { validatePlaceDeletionPreview, validatePlaceDeletionAcknowledgement, placeDeletionMessage } from './placeDeletion';
import { beginPlaceCoordinateMapWork } from './placeCoordinateMapWork';
import { placeMaterialOriginal, validatePlaceStructurePreview, validatePlaceOrderAcknowledgement, type PlaceStructureRequest } from './placeStructuralEditing';

/// Coordinates place-specific editor actions while RegionManager owns shared draft state.
export function usePlaceEditorActions(context: any) {
  const owner = context.placeDraftState;
  let alive = true;
  let savingSession: number | null = null;
  let creationDraftId: string | undefined;
  onUnmounted(() => { alive = false; });
  /** Captures newer scalar, structural and map/search creation intent with fixed comparison originals. */
  watch(() => {
    try {
      if (context.placeDraft.id) {
        const structural = owner.structural();
        const payload = structural ? owner.structuralRequest() : owner.request();
        return Object.keys(payload.changes).length ? JSON.stringify({ kind: structural ? 'place-save' : 'place-fields', payload }) : null;
      }
      const value = buildPlaceRequest(context.placeDraft);
      const originals = value.regionId && owner.creationOriginals.value?.[value.regionId];
      if (!creationDraftId || !originals || !context.placeCreateBaselineRequest.value ||
          JSON.stringify(value) === JSON.stringify(context.placeCreateBaselineRequest.value)) return null;
      return JSON.stringify({ kind: 'place-create', payload: { expected: { regionId: value.regionId, places: originals }, value: withoutRegionId(value) } });
    } catch { return null; }
  }, frozen => {
    if (!frozen) return;
    const { kind, payload } = JSON.parse(frozen);
    const id = context.placeDraft.id;
    const target = id ? { type: 'place' as const, id } : { type: 'region' as const, id: context.placeDraft.regionId };
    void saveStructureIntent(context.props.editorEndpoint, kind, target, payload, id ? undefined : creationDraftId).catch(() => undefined);
  });
  // Discard retires this attempt's UI gate without declaring that the server did not commit.
  watch(() => owner.session(), () => {
    if (savingSession === null) return;
    savingSession = null;
    context.isSaving.value = false;
  }, { flush: 'sync' });
  const openPlaceCreate = async (region: EditorRegion): Promise<void> => {
    await openPlaceCreateDraft(region, null);
  };

  const openPlaceCreateFromSearch = async (region: EditorRegion, result: EditorGeocodeSearchResult): Promise<boolean> => {
    return await openPlaceCreateDraft(region, result);
  };

  const openPlaceCreateDraft = async (region: EditorRegion, result: EditorGeocodeSearchResult | null): Promise<boolean> => {
    // Manual creation owns the map center that exists at invocation time, before target activation can render or wait.
    const manualCenter = result ? null : context.props.coordinatePicker.getMapView()?.center ?? null;
    const target = buildPlaceCreateTarget(region);
    const isAlreadyActive = context.props.editorSurface.isTargetActive(target);
    if (!(await context.props.editorSurface.activateTarget(target)) || (isAlreadyActive && !result)) {
      return false;
    }

    if (isAlreadyActive && result && context.isDirty.value && !(await context.confirmDiscard(message('Editor.DiscardPlaceBeforeSearchAdd', 'Discard unsaved place changes before adding this search result?')))) {
      return false;
    }

    Object.assign(context.draft, emptyRegionDraft());
    owner.reset(null);
    creationDraftId = crypto.randomUUID();
    owner.beginCreate(context.props.state);
    Object.assign(context.placeDraft, emptyPlaceDraft(region.id));
    Object.assign(context.areaDraft, emptyAreaDraft());
    context.placeDraft.name = suggestPlaceDraftName(result);
    context.placeDraft.address = result?.address || result?.displayName || '';
    context.placeDraft.latitude = result?.latitude ?? manualCenter?.latitude ?? '';
    context.placeDraft.longitude = result?.longitude ?? manualCenter?.longitude ?? '';
    context.placeDraft.iconName = defaultPlaceIconName;
    context.placeDraft.markerColor = defaultPlaceMarkerColor;
    context.placeDraft.reverseGeocode = false;
    context.regionCreateBaselineRequest.value = null;
    context.placeCreateBaselineRequest.value = buildPlaceRequest(context.placeDraft);
    context.placeCreateBaselineDraft.value = { ...context.placeDraft };
    context.areaCreateBaselineRequest.value = null;
    context.resetFeedback();
    await nextTick();
    focusPlaceDraftForm(context.placeFormId);
    return true;
  };

  const openPlaceEdit = async (place: EditorPlace): Promise<boolean> => {
    const target = buildPlaceEditTarget(place, context.props.state.regionsById[place.regionId]);
    const isAlreadyActive = context.props.editorSurface.isTargetActive(target);
    if (!place.capabilities.canEdit || !(await context.props.editorSurface.activateTarget(target)) || isAlreadyActive) {
      return false;
    }

    Object.assign(context.draft, emptyRegionDraft());
    owner.reset(place);
    owner.beginStructure(context.props.state);
    Object.assign(context.areaDraft, emptyAreaDraft());
    context.regionCreateBaselineRequest.value = null;
    context.placeCreateBaselineRequest.value = null;
    context.placeCreateBaselineDraft.value = null;
    context.areaCreateBaselineRequest.value = null;
    context.resetFeedback();
    return true;
  };

  /** Restores the new draft's starting values or reads the existing Place from the active snapshot. */
  const resetPlaceDraft = (): void => {
    if (context.isSaving.value) return;
    if (!context.placeDraft.id) {
      const initialDraft = context.placeCreateBaselineDraft.value ?? {
        ...emptyPlaceDraft(context.placeDraft.regionId), name: suggestPlaceDraftName()
      };
      owner.reset(null);
      owner.beginCreate(context.props.state);
      Object.assign(context.placeDraft, initialDraft);
    } else {
      owner.reset(context.activePlace.value);
      owner.beginStructure(context.props.state);
    }
    context.resetFeedback();
  };

  const cancelPlaceDraft = async (): Promise<void> => {
    if (context.placeDraft.id && context.props.selectedPlaceId === context.placeDraft.id) {
      await context.props.clearSelectedPlace();
      return;
    }

    await context.props.editorSurface.closeActiveTarget(message('Editor.DiscardPlaceDraft', 'Discard unsaved place changes?'));
  };

  const savePlaceDraft = async (): Promise<void> => {
    if (context.isSaving.value || !context.placeDraft.regionId) return;
    if (owner.uncertain.value || owner.held(context.placeDraft.id, context.placeDraft.regionId) || !context.placeDraft.id && owner.creationUncertain.value) {
      const message = !context.placeDraft.id && owner.creationUncertain.value
        ? 'The earlier Place creation has an unknown outcome and may have committed. Keep your draft. A refresh cannot prove noncommit; do not replay this creation.'
        : 'The earlier Place save has an unknown outcome. Keep your draft, review current state and explicitly reset before another attempt.';
      context.applyError(new Error(message), 'Place save held.');
      return;
    }
    const placeId = context.placeDraft.id;
    const session = owner.session();
    const currentAttempt = (): boolean => alive && session === owner.session();
    let dispatched = false;
    let creation: PlaceCreationRequest | null = null;
    let structure: PlaceStructureRequest | null = null;
    let segmentIds: string[] = [];
    let attemptIntent: string | null = null;
    const releaseAttempt = (): void => {
      if (attemptIntent) owner.structuralHolds.value = owner.structuralHolds.value.filter((h: { intent: string }) => h.intent !== attemptIntent);
    };
    savingSession = session;
    context.isSaving.value = true;
    context.resetFeedback();
    try {
      const complete = buildPlaceRequest(context.placeDraft);
      const scalar = Boolean(placeId) && !owner.structural();
      const conditional = scalar ? owner.request() : null;
      if (conditional && Object.keys(conditional.changes).length === 0) return;
      if (!placeId) {
        const originals = owner.creationOriginals.value?.[complete.regionId!];
        if (!originals) throw new Error('Original creation destination is unavailable. Keep the draft and explicitly review a new starting view.');
        creation = { expected: { regionId: complete.regionId!, places: originals }, value: withoutRegionId(complete) };
      }
      owner.pending.value = true;
      if (placeId && !scalar) {
        const preparedRequest = owner.structuralRequest() as PlaceStructureRequest;
        structure = preparedRequest;
        const preview = await preparePlaceStructure(context.props.editorEndpoint, `places/${placeId}`, context.props.antiforgeryToken, preparedRequest);
        if (!currentAttempt()) return;
        validatePlaceStructurePreview(preview, context.props.state.tripId, placeId, 'web-update');
        const approved = await confirm({ title: message('Editor.ReviewPlaceSave', 'Review Place save'),
          message: message('Editor.ReviewPlaceSaveDetail', 'Review the current dependency base for {0}. {1} Changed coordinates clear retained enrichment; Address changes only when explicitly edited. Your authored values remain as submitted.', preview.place!.name,
            plural('Editor.ReviewPlaceAffectedRoutes', preview.affectedSegmentIds.length, 'This save affects {0} route.', 'This save affects {0} routes.')),
          confirmLabel: message('Editor.ApplyReviewedPlaceSave', 'Apply reviewed save'), cancelLabel: message('Shell.KeepEditing', 'Keep editing'), variant: 'default' });
        if (!currentAttempt() || !approved) return;
        structure = { ...preparedRequest, conditionToken: preview.conditionToken, reviewed: true };
        const original = structure.expected.place as { location: unknown } | undefined;
        if (Object.hasOwn(structure.changes, 'location') && JSON.stringify(structure.changes.location) !== JSON.stringify(original?.location ?? structure.expected.location))
          segmentIds = preview.affectedSegmentIds;
      }
      if (structure) {
        attemptIntent = JSON.stringify(structure);
        owner.structuralHolds.value.push({ targetId: placeId!, regionIds: Object.keys(structure.placeIdsByRegion ?? { [completeRegion(structure)]: [] }), intent: attemptIntent });
      }
      dispatched = true;
      if (creation) owner.creationUncertain.value = true;
      // Prepared combined edits commit once. Scalar requests never fall back to another writer.
      const result = conditional
        ? await updatePlaceConditional(context.props.editorEndpoint, placeId, context.props.antiforgeryToken, conditional)
        : placeId
          ? await updatePlace(context.props.editorEndpoint, placeId, context.props.antiforgeryToken, structure!)
          : await createPlace(context.props.editorEndpoint, complete.regionId!, context.props.antiforgeryToken, creation!, creationDraftId);
      if (structure) {
        if (result.contract !== structureContract) validatePlaceAcknowledgement(result, context.props.state.tripId, placeId, complete.regionId!, scalar, structure, segmentIds);
        releaseAttempt();
      }
      if (!currentAttempt()) return;
      if (creation) {
        if (result.contract !== structureContract) validatePlaceCreation(result, context.props.state.tripId, creation);
        owner.creationUncertain.value = false;
      }
      else if (!structure && result.contract !== structureContract) validatePlaceAcknowledgement(result, context.props.state.tripId, placeId, complete.regionId!, scalar);
      owner.acknowledge(result.data, conditional?.changes ?? complete);
      for (const [regionId, ids] of Object.entries(result.affected.placeOrdersByRegionId) as [string, string[]][]) {
        if (owner.structuralOrders.value) owner.structuralOrders.value[regionId] = ids.map(id => {
          const place = result.affected.places.find((p: EditorPlace) => p.id === id)!;
          return { id, displayOrder: place.originalValues!.displayOrder! };
        });
      }
      context.emit('mutationApplied', result as EditorMutationResult<unknown>);
      context.placeCreateBaselineRequest.value = null;
      context.placeCreateBaselineDraft.value = null;
      context.props.editorSurface.replaceActiveTarget(context.activePlaceTarget.value);
      if (!owner.creationUncertain.value && !owner.structural() && Object.keys(owner.request().changes).length === 0) context.markSaved(result.warnings.map(warning => editorWarningText(warning.code)), 'Place saved');
    } catch (error) {
      const rejected = error instanceof EditorValidationError || error instanceof EditorLifecycleConfirmationError
        || error instanceof EditorMutationHttpError && [401, 403, 404, 405, 409, 429].includes(error.status);
      if (rejected) releaseAttempt();
      if (!currentAttempt()) return;
      if (creation && rejected) owner.creationUncertain.value = false;
      if (dispatched && !rejected) {
        owner.uncertain.value = true;
        if (!placeId) owner.creationUncertain.value = true;
      }
      context.applyError(error, 'Place save failed.');
    } finally {
      if (currentAttempt() && savingSession === session) {
        savingSession = null;
        owner.pending.value = false;
        context.isSaving.value = false;
      }
    }
  };

  const pickPlaceCoordinate = (): void => beginPlaceCoordinateMapWork(
    context.placeDraft,
    context.props.editorSurface,
    context.props.coordinatePicker,
    context.placeCoordinateMapWork);

  const deleteDraftPlace = async (): Promise<void> => {
    const place = context.activePlace.value as EditorPlace | null;
    if (!place?.capabilities.canDelete || context.isSaving.value || owner.pending.value) return;
    if (owner.held(place.id, place.regionId)) {
      context.applyError(new Error('The earlier Place operation has an unknown outcome. Keep it held without retry. Abandoning this page does not undo a request.'), 'Place deletion held.');
      return;
    }
    const session = owner.session();
    const intent = JSON.stringify(context.placeDraft);
    const deletedTarget = context.activePlaceTarget.value;
    const current = (): boolean => alive && owner.session() === session;
    const unchanged = (): boolean => current() && JSON.stringify(context.placeDraft) === intent;
    const mount = document.getElementById('trip-editor-app');
    const account = mount?.dataset.accountId;
    const scopeCurrent = (): boolean => current() && document.getElementById('trip-editor-app') === mount
      && mount?.dataset.accountId === account;
    const attemptId = `delete:${place.id}:${crypto.randomUUID()}`;
    const release = (): void => { owner.structuralHolds.value = owner.structuralHolds.value.filter((h: { intent: string }) => h.intent !== attemptId); };
    let dispatched = false;
    savingSession = session;
    context.isSaving.value = true;
    owner.pending.value = true;
    context.resetFeedback();
    try {
      const preview = await previewPlaceDeletion(context.props.editorEndpoint, place.id, context.props.antiforgeryToken);
      if (!scopeCurrent() || !unchanged()) return;
      validatePlaceDeletionPreview(preview, context.props.state.tripId, place.id, place.regionId);
      // Opening/cancelling consent preserves every draft. Only this approved target may be cleared after success.
      const approved = await confirm({ title: message('Editor.DeletePlaceQuestion', 'Delete Place?'), message: placeDeletionMessage(preview),
        confirmLabel: message('Editor.DeletePlacePermanently', 'Permanently delete Place'), cancelLabel: message('Editor.KeepPlaceAndDrafts', 'Keep Place and drafts'), variant: 'danger' });
      if (!approved || !scopeCurrent() || !unchanged()) return;
      validatePlaceDeletionPreview(preview, context.props.state.tripId, place.id, place.regionId);
      owner.structuralHolds.value.push({ targetId: place.id, regionIds: [place.regionId], intent: attemptId });
      dispatched = true;
      const result = await deletePlace(context.props.editorEndpoint, place.id, context.props.antiforgeryToken, preview.confirmation.token);
      if (result.contract !== structureContract) validatePlaceDeletionAcknowledgement(result, preview);
      if (!scopeCurrent()) return;
      const clearTarget = unchanged();
      context.emit('mutationApplied', result as EditorMutationResult<unknown>);
      release();
      if (clearTarget) {
        owner.reset(null);
        context.props.editorSurface.clearActiveTarget(deletedTarget);
        context.markSaved();
      }
    } catch (error) {
      const rejected = error instanceof EditorValidationError || error instanceof EditorMutationHttpError && [401, 403, 404, 405, 409, 429].includes(error.status);
      if (!dispatched || rejected) release();
      if (!current()) return;
      if (dispatched && !rejected) {
        owner.uncertain.value = true;
        context.applyError(new Error('Place deletion outcome unknown. Refresh to inspect current facts; keep this attempt held without retry. Leaving does not undo the request.'), 'Place deletion held.');
      } else context.applyError(error, 'Place deletion rejected. Review a fresh preview and confirm again.');
    } finally {
      if (current()) { savingSession = null; context.isSaving.value = false; owner.pending.value = false; }
    }
  };

  const reorderPlaces = async (regionId: Guid, ids: Guid[], previousIds: Guid[]): Promise<void> => {
    if (context.isOrdering.value) {
      await context.restoreRegionOrder(previousIds);
      return;
    }

    if (context.isDirty.value && !(await context.confirmDiscard(message('Editor.DiscardBeforePlaceReorder', 'Discard unsaved draft changes before reordering places?')))) {
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

    if (owner.held('', regionId)) {
      context.applyError(new Error('The earlier Place operation has an unknown outcome. Do not replay this order.'), 'Place reorder held.');
      await context.restoreRegionOrder(previousIds);
      return;
    }
    const session = owner.session();
    const currentAttempt = (): boolean => alive && owner.session() === session;
    let dispatched = false;
    let request: import('../types').EditorPlaceOrderRequest | null = null;
    let attemptIntent: string | null = null;
    const releaseAttempt = (): void => {
      if (attemptIntent) owner.structuralHolds.value = owner.structuralHolds.value.filter((h: { intent: string }) => h.intent !== attemptIntent);
    };
    context.isOrdering.value = true;
    owner.orderingPending.value = true;
    context.resetFeedback();
    try {
      const originals = previousIds.map(id => placeMaterialOriginal(context.props.state.placesById[id]));
      request = { expected: { places: originals }, placeIds: ids };
      const preview = await preparePlaceStructure(context.props.editorEndpoint, `regions/${regionId}/places/order`, context.props.antiforgeryToken, request);
      if (!currentAttempt()) return;
      validatePlaceStructurePreview(preview, context.props.state.tripId, regionId, 'order');
      if (!(await confirm({ title: message('Editor.ReviewPlaceOrder', 'Review Place order'),
        message: plural('Editor.ReviewPlaceOrderDetail', ids.length, 'Apply this order to {0} Place using its current reviewed content?', 'Apply this order to all {0} Places using their current reviewed content?'),
        confirmLabel: message('Editor.ApplyReviewedOrder', 'Apply reviewed order'), cancelLabel: message('Editor.KeepCurrentOrder', 'Keep current order'), variant: 'default' }))) {
        if (currentAttempt()) await context.restoreRegionOrder(previousIds);
        return;
      }
      if (!currentAttempt()) return;
      request = { ...request, conditionToken: preview.conditionToken, reviewed: true };
      attemptIntent = JSON.stringify(request);
      owner.structuralHolds.value.push({ targetId: regionId, regionIds: [regionId], intent: attemptIntent });
      dispatched = true;
      const result = await orderPlaces(context.props.editorEndpoint, regionId, context.props.antiforgeryToken, request);
      if (result.contract !== structureContract) validatePlaceOrderAcknowledgement(result, context.props.state.tripId, regionId, ids, originals);
      releaseAttempt();
      if (!currentAttempt()) return;
      context.emit('mutationApplied', result as EditorMutationResult<unknown>);
      context.markSaved();
    } catch (error) {
      const rejected = error instanceof EditorValidationError || error instanceof EditorMutationHttpError && [401, 403, 404, 405, 409, 429].includes(error.status);
      if (rejected || !dispatched) releaseAttempt();
      if (!currentAttempt()) return;
      context.applyError(error, 'Place reorder failed.');
      await context.restoreRegionOrder(previousIds);
    } finally {
      if (alive) { context.isOrdering.value = false; owner.orderingPending.value = false; }
    }
  };

  return { cancelPlaceDraft, deleteDraftPlace, openPlaceCreate, openPlaceCreateFromSearch, openPlaceEdit, pickPlaceCoordinate, reorderPlaces, resetPlaceDraft, savePlaceDraft };
}

/** Keeps provider names ahead of the localized suggestion for an unnamed new Place. */
function suggestPlaceDraftName(result?: EditorGeocodeSearchResult | null): string {
  return result?.name || result?.displayName.split(',').map(part => part.trim()).find(Boolean)
    || message('Editor.NewPlaceDraftName', 'New Place');
}

function focusPlaceDraftForm(formId: string): void {
  const form = document.getElementById(formId);
  const sidebar = form?.closest<HTMLElement>('.trip-editor-sidebar');
  if (form && sidebar) {
    const formTop = form.getBoundingClientRect().top;
    const sidebarTop = sidebar.getBoundingClientRect().top;
    sidebar.scrollTo({ top: sidebar.scrollTop + formTop - sidebarTop - 16, behavior: 'auto' });
  }

  form?.scrollIntoView({ behavior: 'auto', block: 'start', inline: 'nearest' });
  form?.querySelector<HTMLElement>('input, select, textarea, button')?.focus({ preventScroll: false });
}

/** Identifies the original coordinate/address scope for a retained unknown attempt. */
const completeRegion = (request: PlaceStructureRequest): string => String(request.expected.regionId ?? (request.expected.place as { regionId?: string })?.regionId ?? '');
