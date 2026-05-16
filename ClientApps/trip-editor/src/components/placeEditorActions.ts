import { nextTick } from 'vue';
import { createPlace, deletePlace, orderPlaces, updatePlace } from '../api/tripEditorApi';
import type { EditorGeocodeSearchResult, EditorMutationResult, EditorPlace, EditorRegion, Guid } from '../types';
import { confirm } from '../composables/useConfirmDialog';
import { buildPlaceCreateTarget, buildPlaceEditTarget } from './regionPlaceEditorTargets';
import { buildPlaceRequest, emptyAreaDraft, emptyPlaceDraft, emptyRegionDraft, toPlaceDraft, withoutRegionId } from './regionPlaceDrafts';
import { beginPlaceCoordinateMapWork } from './placeCoordinateMapWork';

/// Coordinates place-specific editor actions while RegionManager owns shared draft state.
export function usePlaceEditorActions(context: any) {
  const openPlaceCreate = async (region: EditorRegion): Promise<void> => {
    await openPlaceCreateDraft(region, null);
  };

  const openPlaceCreateFromSearch = async (region: EditorRegion, result: EditorGeocodeSearchResult): Promise<boolean> => {
    return await openPlaceCreateDraft(region, result);
  };

  const openPlaceCreateDraft = async (region: EditorRegion, result: EditorGeocodeSearchResult | null): Promise<boolean> => {
    const target = buildPlaceCreateTarget(region);
    const isAlreadyActive = context.props.editorSurface.isTargetActive(target);
    if (region.isShadow || !(await context.props.editorSurface.activateTarget(target)) || (isAlreadyActive && !result)) {
      return false;
    }

    if (isAlreadyActive && result && context.isDirty.value && !(await context.confirmDiscard('Discard unsaved place changes before adding this search result?'))) {
      return false;
    }

    Object.assign(context.draft, emptyRegionDraft());
    Object.assign(context.placeDraft, emptyPlaceDraft(region.id));
    Object.assign(context.areaDraft, emptyAreaDraft());
    context.placeDraft.name = result ? searchResultName(result) : 'New Place';
    context.placeDraft.address = result?.address || result?.displayName || '';
    context.placeDraft.latitude = result?.latitude ?? '';
    context.placeDraft.longitude = result?.longitude ?? '';
    context.placeDraft.iconName = context.props.state.options.iconNames[0] ?? 'marker';
    context.placeDraft.markerColor = context.props.state.options.markerColorClasses[0] ?? 'bg-blue';
    context.placeDraft.reverseGeocode = false;
    context.regionCreateBaselineRequest.value = null;
    context.placeCreateBaselineRequest.value = buildPlaceRequest(context.placeDraft);
    context.placeEditBaselineRequest.value = null;
    context.areaCreateBaselineRequest.value = null;
    context.resetFeedback();
    return true;
  };

  const openPlaceEdit = async (place: EditorPlace): Promise<boolean> => {
    const target = buildPlaceEditTarget(place, context.props.state.regionsById[place.regionId]?.name);
    const isAlreadyActive = context.props.editorSurface.isTargetActive(target);
    if (!place.capabilities.canEdit || !(await context.props.editorSurface.activateTarget(target)) || isAlreadyActive) {
      return false;
    }

    Object.assign(context.draft, emptyRegionDraft());
    Object.assign(context.placeDraft, toPlaceDraft(place, place.regionId));
    Object.assign(context.areaDraft, emptyAreaDraft());
    context.regionCreateBaselineRequest.value = null;
    context.placeCreateBaselineRequest.value = null;
    context.placeEditBaselineRequest.value = buildPlaceRequest(context.placeDraft);
    context.areaCreateBaselineRequest.value = null;
    // Quill can normalize legacy persisted notes after render; keep that hydration out of dirty-state prompts.
    void nextTick(() => {
      if (context.placeDraft.id === place.id && context.props.editorSurface.isTargetActive(target)) {
        context.placeEditBaselineRequest.value = buildPlaceRequest(context.placeDraft);
      }
    });
    context.resetFeedback();
    return true;
  };

  const resetPlaceDraft = (): void => {
    if (!context.placeDraft.id) {
      const regionId = context.placeDraft.regionId;
      Object.assign(context.placeDraft, emptyPlaceDraft(regionId));
      context.placeDraft.name = 'New Place';
      context.placeDraft.iconName = context.props.state.options.iconNames[0] ?? 'marker';
      context.placeDraft.markerColor = context.props.state.options.markerColorClasses[0] ?? 'bg-blue';
    } else {
      Object.assign(context.placeDraft, toPlaceDraft(context.activePlace.value, context.placeDraft.regionId));
      context.placeEditBaselineRequest.value = buildPlaceRequest(context.placeDraft);
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
    if (!context.placeDraft.regionId) {
      return;
    }

    context.isSaving.value = true;
    context.resetFeedback();
    try {
      const request = buildPlaceRequest(context.placeDraft);
      const result = context.placeDraft.id
        ? await updatePlace(context.props.editorEndpoint, context.placeDraft.id, context.props.antiforgeryToken, request)
        : await createPlace(context.props.editorEndpoint, context.placeDraft.regionId, context.props.antiforgeryToken, withoutRegionId(request));
      context.emit('mutationApplied', result as EditorMutationResult<unknown>);
      Object.assign(context.placeDraft, toPlaceDraft(result.data, result.data.regionId));
      context.placeCreateBaselineRequest.value = null;
      context.placeEditBaselineRequest.value = buildPlaceRequest(context.placeDraft);
      context.props.editorSurface.replaceActiveTarget(context.activePlaceTarget.value);
      context.markSaved(result.warnings.map((warning: { message: string }) => warning.message));
    } catch (error) {
      context.applyError(error, 'Place save failed.');
    } finally {
      context.isSaving.value = false;
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

    if (!(await confirm({
      title: 'Delete place?',
      message: 'Delete this place and any segments connected to it?',
      confirmLabel: 'Delete',
      cancelLabel: 'Keep place',
      variant: 'danger'
    }))) {
      return;
    }

    context.isSaving.value = true;
    context.resetFeedback();
    try {
      const deletedTarget = context.activePlaceTarget.value;
      const result = await deletePlace(context.props.editorEndpoint, context.activePlace.value.id, context.props.antiforgeryToken);
      context.emit('mutationApplied', result as EditorMutationResult<unknown>);
      Object.assign(context.placeDraft, emptyPlaceDraft());
      Object.assign(context.areaDraft, emptyAreaDraft());
      context.placeCreateBaselineRequest.value = null;
      context.placeEditBaselineRequest.value = null;
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
    if (context.isDirty.value && !(await context.confirmDiscard('Discard unsaved draft changes before reordering places?'))) {
      await context.restoreRegionOrder(previousIds);
      return;
    }

    if (context.isDirty.value) {
      context.clearAllDraftsAndBaselines();
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
