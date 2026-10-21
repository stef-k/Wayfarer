import { onUnmounted } from 'vue';
import { createArea, deleteArea, EditorLifecycleConfirmationError, EditorMutationHttpError, EditorValidationError, orderAreas, updateArea } from '../api/tripEditorApi';
import type { EditorMutationResult, EditorArea, EditorRegion, Guid } from '../types';
import { buildAreaCreateTarget, buildAreaEditTarget } from './regionPlaceEditorTargets';
import { buildAreaRequest, emptyAreaDraft } from './regionPlaceDrafts';
import { beginAreaPolygonMapWork } from './areaPolygonMapWork';
import { confirm } from '../composables/useConfirmDialog';

/// Coordinates area-specific editor actions while RegionManager owns shared draft state.
export function useAreaEditorActions(context: any) {
  const areaDraft = context.areaDraftState.draft;
  let editorAlive = true;
  onUnmounted(() => { editorAlive = false; });
  const openAreaCreate = async (region: EditorRegion): Promise<void> => {
    const target = buildAreaCreateTarget(region);
    const isAlreadyActive = context.props.editorSurface.isTargetActive(target);
    if (region.isShadow || !context.props.state.permissions.canEditAreas || !region.capabilities.canAddChildren || !(await context.props.editorSurface.activateTarget(target)) || isAlreadyActive) {
      return;
    }

    context.clearRegionPlaceDrafts();
    context.areaDraftState.reset(null);
    Object.assign(areaDraft, emptyAreaDraft(region.id, context.props.state.options.areaDefaults.fillHex));
    areaDraft.name = context.props.state.options.areaDefaults.name || 'Area';
    context.clearRegionPlaceBaselines();
    context.areaCreateBaselineRequest.value = buildAreaRequest(areaDraft);
    context.resetFeedback();
  };

  const openAreaEdit = async (area: EditorArea): Promise<void> => {
    const target = buildAreaEditTarget(area, context.props.state.regionsById[area.regionId]?.name);
    const isAlreadyActive = context.props.editorSurface.isTargetActive(target);
    if (!area.capabilities.canEdit || !(await context.props.editorSurface.activateTarget(target)) || isAlreadyActive) {
      return;
    }

    context.clearRegionPlaceDrafts();
    context.areaDraftState.reset(area);
    context.clearRegionPlaceBaselines();
    context.areaCreateBaselineRequest.value = null;
    context.resetFeedback();
  };

  const resetAreaDraft = (): void => {
    if (context.isSaving.value) return;
    if (!areaDraft.id) {
      Object.assign(areaDraft, emptyAreaDraft(areaDraft.regionId, context.props.state.options.areaDefaults.fillHex));
      areaDraft.name = context.props.state.options.areaDefaults.name || 'Area';
    } else {
      context.areaDraftState.reset(context.activeArea.value);
    }
    context.resetFeedback();
  };

  const cancelAreaDraft = async (): Promise<void> => {
    await context.props.editorSurface.closeActiveTarget('Discard unsaved area changes?');
  };

  const saveAreaDraft = async (): Promise<void> => {
    if (context.isSaving.value || !areaDraft.regionId) {
      return;
    }
    if (context.areaDraftState.uncertain.value) {
      context.applyError(new Error('The earlier Area save has an unknown outcome. Keep your draft, review current state and explicitly reset before another attempt.'), 'Area save held.');
      return;
    }

    context.isSaving.value = true;
    context.resetFeedback();
    try {
      const areaId = areaDraft.id;
      const session = context.areaDraftState.session();
      const submitted = areaId ? context.areaDraftState.request() : { expected: {}, changes: buildAreaRequest(areaDraft) };
      if (areaId && Object.keys(submitted.changes).length === 0) return;
      context.areaDraftState.pending.value = Boolean(areaId);
      const result = areaId
        ? await updateArea(context.props.editorEndpoint, areaId, context.props.antiforgeryToken, submitted)
        : await createArea(context.props.editorEndpoint, areaDraft.regionId, context.props.antiforgeryToken, submitted.changes);
      if (!editorAlive) return;
      if (!result.success) throw new Error('The Area save was not acknowledged. Keep your draft and review the unknown outcome.');
      if (session !== context.areaDraftState.session()) {
        context.emit('mutationApplied', result as EditorMutationResult<unknown>);
        return;
      }
      // Advance acknowledged originals before the parent's affected slices arrive on the next Vue tick.
      context.areaDraftState.acknowledge(result.data, submitted);
      context.emit('mutationApplied', result as EditorMutationResult<unknown>);
      context.areaCreateBaselineRequest.value = null;
      context.props.editorSurface.replaceActiveTarget(context.activeAreaTarget.value);
      context.markSaved();
    } catch (error) {
      const rejected = error instanceof EditorValidationError || error instanceof EditorLifecycleConfirmationError
        || error instanceof EditorMutationHttpError && error.status >= 400 && error.status < 500;
      if (context.areaDraftState.pending.value && !rejected) context.areaDraftState.uncertain.value = true;
      context.applyError(error, 'Area save failed.');
    } finally {
      context.areaDraftState.pending.value = false;
      context.isSaving.value = false;
    }
  };

  const deleteDraftArea = async (): Promise<void> => {
    if (!context.activeArea.value || !(await context.confirmDiscard('Discard unsaved area draft changes before deleting?'))) {
      return;
    }

    if (!(await confirm({
      title: 'Delete area?',
      message: 'Delete this area?',
      confirmLabel: 'Delete',
      cancelLabel: 'Keep area',
      variant: 'danger'
    }))) {
      return;
    }

    context.isSaving.value = true;
    context.resetFeedback();
    try {
      const deletedTarget = context.activeAreaTarget.value;
      const result = await deleteArea(context.props.editorEndpoint, context.activeArea.value.id, context.props.antiforgeryToken);
      context.emit('mutationApplied', result as EditorMutationResult<unknown>);
      context.areaDraftState.reset(null);
      context.areaCreateBaselineRequest.value = null;
      context.props.editorSurface.clearActiveTarget(deletedTarget);
      context.markSaved();
    } catch (error) {
      context.applyError(error, 'Area delete failed.');
    } finally {
      context.isSaving.value = false;
    }
  };

  const reorderAreas = async (regionId: Guid, ids: Guid[], previousIds: Guid[]): Promise<void> => {
    if (context.isOrdering.value) {
      await context.restoreRegionOrder(previousIds);
      return;
    }

    if (context.isDirty.value && !(await context.confirmDiscard('Discard unsaved draft changes before reordering areas?'))) {
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
      const result = await orderAreas(context.props.editorEndpoint, regionId, context.props.antiforgeryToken, { areaIds: ids });
      context.emit('mutationApplied', result as EditorMutationResult<unknown>);
      context.markSaved();
    } catch (error) {
      context.applyError(error, 'Area reorder failed.');
      await context.restoreRegionOrder(previousIds);
    } finally {
      context.isOrdering.value = false;
    }
  };

  const drawAreaPolygon = (): void => beginAreaPolygonMapWork(areaDraft, context.props.editorSurface, context.props.polygonEditor, context.areaPolygonMapWork);

  return { cancelAreaDraft, deleteDraftArea, drawAreaPolygon, openAreaCreate, openAreaEdit, reorderAreas, resetAreaDraft, saveAreaDraft };
}
