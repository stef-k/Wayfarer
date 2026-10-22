import { nextTick, onUnmounted, watch } from 'vue';
import { createPlace, deletePlace, EditorLifecycleConfirmationError, EditorMutationHttpError, EditorValidationError, orderPlaces, updatePlace, updatePlaceConditional } from '../api/tripEditorApi';
import type { EditorGeocodeSearchResult, EditorMutationResult, EditorPlace, EditorRegion, Guid } from '../types';
import { confirm } from '../composables/useConfirmDialog';
import { buildPlaceCreateTarget, buildPlaceEditTarget } from './regionPlaceEditorTargets';
import { buildPlaceRequest, defaultPlaceIconName, defaultPlaceMarkerColor, emptyAreaDraft, emptyPlaceDraft, emptyRegionDraft, withoutRegionId } from './regionPlaceDrafts';
import { validatePlaceAcknowledgement } from './placeConditionalDraft';
import { beginPlaceCoordinateMapWork } from './placeCoordinateMapWork';

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
      Object.assign(context.placeDraft, context.placeCreateBaselineDraft.value ?? emptyPlaceDraft(context.placeDraft.regionId));
    } else {
      owner.reset(context.activePlace.value);
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
    if (owner.uncertain.value) {
      context.applyError(new Error('The earlier Place save has an unknown outcome. Keep your draft, review current state and explicitly reset before another attempt.'), 'Place save held.');
      return;
    }
    const placeId = context.placeDraft.id;
    const session = owner.session();
    const currentAttempt = (): boolean => alive && session === owner.session();
    let dispatched = false;
    savingSession = session;
    context.isSaving.value = true;
    context.resetFeedback();
    try {
      const complete = buildPlaceRequest(context.placeDraft);
      const scalar = Boolean(placeId) && !owner.structural();
      const conditional = scalar ? owner.request() : null;
      if (conditional && Object.keys(conditional.changes).length === 0) return;
      owner.pending.value = true;
      dispatched = true;
      // Combined edits stay one lifecycle PUT. A failed protected PATCH never falls back to that writer.
      const result = conditional
        ? await updatePlaceConditional(context.props.editorEndpoint, placeId, context.props.antiforgeryToken, conditional)
        : placeId
          ? await updatePlace(context.props.editorEndpoint, placeId, context.props.antiforgeryToken, complete)
          : await createPlace(context.props.editorEndpoint, context.placeDraft.regionId, context.props.antiforgeryToken, withoutRegionId(complete));
      if (!currentAttempt()) return;
      validatePlaceAcknowledgement(result, context.props.state.tripId, placeId, complete.regionId!, scalar);
      owner.acknowledge(result.data, conditional?.changes ?? complete);
      context.emit('mutationApplied', result as EditorMutationResult<unknown>);
      context.placeCreateBaselineRequest.value = null;
      context.placeCreateBaselineDraft.value = null;
      context.props.editorSurface.replaceActiveTarget(context.activePlaceTarget.value);
      context.markSaved(result.warnings.map((warning: { message: string }) => warning.message), 'Place saved');
    } catch (error) {
      if (!currentAttempt()) return;
      const rejected = error instanceof EditorValidationError || error instanceof EditorLifecycleConfirmationError
        || error instanceof EditorMutationHttpError && [401, 403, 404, 405, 409, 429].includes(error.status);
      if (dispatched && !rejected) owner.uncertain.value = true;
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

    context.isOrdering.value = true;
    context.resetFeedback();
    try {
      const result = await orderPlaces(context.props.editorEndpoint, regionId, context.props.antiforgeryToken, { placeIds: ids });
      context.emit('mutationApplied', result as EditorMutationResult<unknown>);
      context.markSaved();
    } catch (error) {
      context.applyError(error, 'Place reorder failed.');
      await context.restoreRegionOrder(previousIds);
    } finally {
      context.isOrdering.value = false;
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
