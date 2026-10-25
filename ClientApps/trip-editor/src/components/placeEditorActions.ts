import { validatePlaceCreation, type PlaceCreationRequest } from '../api/creationTypes';
import { nextTick, onUnmounted, watch } from 'vue';
import { createPlace, deletePlace, EditorLifecycleConfirmationError, EditorMutationHttpError, EditorValidationError, orderPlaces, preparePlaceStructure, updatePlace, updatePlaceConditional } from '../api/tripEditorApi';
import type { EditorGeocodeSearchResult, EditorMutationResult, EditorPlace, EditorRegion, Guid } from '../types';
import { confirm } from '../composables/useConfirmDialog';
import { buildPlaceCreateTarget, buildPlaceEditTarget } from './regionPlaceEditorTargets';
import { buildPlaceRequest, defaultPlaceIconName, defaultPlaceMarkerColor, emptyAreaDraft, emptyPlaceDraft, emptyRegionDraft, withoutRegionId } from './regionPlaceDrafts';
import { validatePlaceAcknowledgement } from './placeConditionalDraft';
import { beginPlaceCoordinateMapWork } from './placeCoordinateMapWork';
import { placeMaterialOriginal, validatePlaceStructurePreview, validatePlaceOrderAcknowledgement, type PlaceStructureRequest } from './placeStructuralEditing';

/// Coordinates place-specific editor actions while RegionManager owns shared draft state.
export function usePlaceEditorActions(context: any) {
  const owner = context.placeDraftState;
  let alive = true;
  let savingSession: number | null = null;
  onUnmounted(() => { alive = false; });
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

    if (isAlreadyActive && result && context.isDirty.value && !(await context.confirmDiscard('Discard unsaved place changes before adding this search result?'))) {
      return false;
    }

    Object.assign(context.draft, emptyRegionDraft());
    owner.reset(null);
    owner.beginCreate(context.props.state);
    Object.assign(context.placeDraft, emptyPlaceDraft(region.id));
    Object.assign(context.areaDraft, emptyAreaDraft());
    context.placeDraft.name = result ? searchResultName(result) : 'New Place';
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
    const target = buildPlaceEditTarget(place, context.props.state.regionsById[place.regionId]?.name);
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

  const resetPlaceDraft = (): void => {
    if (context.isSaving.value) return;
    if (!context.placeDraft.id) {
      owner.reset(null);
      owner.beginCreate(context.props.state);
      Object.assign(context.placeDraft, context.placeCreateBaselineDraft.value ?? emptyPlaceDraft(context.placeDraft.regionId));
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

    await context.props.editorSurface.closeActiveTarget('Discard unsaved place changes?');
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
        const approved = await confirm({ title: 'Review Place save',
          message: `Review the current dependency base for ${preview.place!.name}. This save affects ${preview.affectedSegmentIds.length} route(s). Changed coordinates clear retained enrichment; Address changes only when explicitly edited. Your authored values remain as submitted.`,
          confirmLabel: 'Apply reviewed save', cancelLabel: 'Keep draft', variant: 'default' });
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
          : await createPlace(context.props.editorEndpoint, complete.regionId!, context.props.antiforgeryToken, creation!);
      if (structure) {
        validatePlaceAcknowledgement(result, context.props.state.tripId, placeId, complete.regionId!, scalar, structure, segmentIds);
        releaseAttempt();
      }
      if (!currentAttempt()) return;
      if (creation) {
        validatePlaceCreation(result, context.props.state.tripId, creation);
        owner.creationUncertain.value = false;
      }
      else if (!structure) validatePlaceAcknowledgement(result, context.props.state.tripId, placeId, complete.regionId!, scalar);
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
      if (!owner.creationUncertain.value && !owner.structural() && Object.keys(owner.request().changes).length === 0) context.markSaved(result.warnings.map((warning: { message: string }) => warning.message), 'Place saved');
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
    if (!context.activePlace.value) {
      return;
    }

    if (!(await context.confirmDiscard('Discard unsaved place draft changes before deleting?'))) {
      return;
    }

    context.isSaving.value = true;
    context.resetFeedback();
    try {
      const deletedTarget = context.activePlaceTarget.value;
      let confirmationToken: string | undefined;
      let result: Awaited<ReturnType<typeof deletePlace>> | undefined;
      for (let attempt = 0; attempt < 3 && !result; attempt += 1) {
        try {
          result = await deletePlace(context.props.editorEndpoint, context.activePlace.value.id, context.props.antiforgeryToken, confirmationToken);
        } catch (error) {
          if (!(error instanceof EditorLifecycleConfirmationError)) throw error;
          const warning = error.conflict;
          // The dependency request is complete while the user decides, so its trigger can receive restored focus.
          context.isSaving.value = false;
          const confirmed = await confirm({
            title: warning.code === 'lifecycle-confirmation-stale' ? 'Dependencies changed' : 'Delete place?',
            message: `This deletes ${warning.endpointSegments.count} connected segment(s) and updates ${warning.waypointOnlySegments.count} waypoint route(s).`,
            confirmLabel: 'Delete',
            cancelLabel: 'Keep place',
            variant: 'danger'
          });
          if (!confirmed) return;
          context.isSaving.value = true;
          confirmationToken = warning.confirmationToken;
        }
      }
      if (!result) throw new Error('Place dependencies changed repeatedly. Please retry.');
      context.emit('mutationApplied', result as EditorMutationResult<unknown>);
      owner.reset(null);
      Object.assign(context.areaDraft, emptyAreaDraft());
      context.placeCreateBaselineRequest.value = null;
      context.placeCreateBaselineDraft.value = null;
      context.areaCreateBaselineRequest.value = null;
      context.props.editorSurface.clearActiveTarget(deletedTarget);
      context.markSaved();
    } catch (error) {
      context.applyError(error, 'Place delete failed.');
    } finally {
      context.isSaving.value = false;
    }
  };

  const reorderPlaces = async (regionId: Guid, ids: Guid[], previousIds: Guid[]): Promise<void> => {
    if (context.isOrdering.value) {
      await context.restoreRegionOrder(previousIds);
      return;
    }

    if (context.isDirty.value && !(await context.confirmDiscard('Discard unsaved draft changes before reordering places?'))) {
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
      if (!(await confirm({ title: 'Review Place order', message: `Apply this order to all ${ids.length} Places using their current reviewed content?`,
        confirmLabel: 'Apply reviewed order', cancelLabel: 'Keep current order', variant: 'default' }))) {
        if (currentAttempt()) await context.restoreRegionOrder(previousIds);
        return;
      }
      if (!currentAttempt()) return;
      request = { ...request, conditionToken: preview.conditionToken, reviewed: true };
      attemptIntent = JSON.stringify(request);
      owner.structuralHolds.value.push({ targetId: regionId, regionIds: [regionId], intent: attemptIntent });
      dispatched = true;
      const result = await orderPlaces(context.props.editorEndpoint, regionId, context.props.antiforgeryToken, request);
      validatePlaceOrderAcknowledgement(result, context.props.state.tripId, regionId, ids, originals);
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

function searchResultName(result: EditorGeocodeSearchResult): string {
  return result.name || result.displayName.split(',').map(part => part.trim()).find(Boolean) || 'New Place';
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
