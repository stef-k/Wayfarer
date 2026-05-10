import { createArea, deleteArea, orderAreas, updateArea } from '../api/tripEditorApi';
import type { EditorMutationResult, EditorArea, EditorRegion, Guid } from '../types';
import { buildAreaCreateTarget, buildAreaEditTarget } from './regionPlaceEditorTargets';
import { buildAreaRequest, emptyAreaDraft, toAreaDraft } from './regionPlaceDrafts';
import { beginAreaPolygonMapWork } from './areaPolygonMapWork';
import { confirm } from '../composables/useConfirmDialog';

/// Coordinates area-specific editor actions while RegionManager owns shared draft state.
export function useAreaEditorActions(context: any) {
  const openAreaCreate = async (region: EditorRegion): Promise<void> => {
    const target = buildAreaCreateTarget(region);
    const isAlreadyActive = context.props.editorSurface.isTargetActive(target);
    if (region.isShadow || !context.props.state.permissions.canEditAreas || !region.capabilities.canAddChildren || !(await context.props.editorSurface.activateTarget(target)) || isAlreadyActive) {
      return;
    }

    context.clearRegionPlaceDrafts();
    Object.assign(context.areaDraft, emptyAreaDraft(region.id, context.props.state.options.areaDefaults.fillHex));
    context.areaDraft.name = context.props.state.options.areaDefaults.name || 'Area';
    context.clearRegionPlaceBaselines();
    context.areaCreateBaselineRequest.value = buildAreaRequest(context.areaDraft);
    context.resetFeedback();
  };

  const openAreaEdit = async (area: EditorArea): Promise<void> => {
    const target = buildAreaEditTarget(area, context.props.state.regionsById[area.regionId]?.name);
    const isAlreadyActive = context.props.editorSurface.isTargetActive(target);
    if (!area.capabilities.canEdit || !(await context.props.editorSurface.activateTarget(target)) || isAlreadyActive) {
      return;
    }

    context.clearRegionPlaceDrafts();
    Object.assign(context.areaDraft, toAreaDraft(area, area.regionId, context.props.state.options.areaDefaults.fillHex));
    context.clearRegionPlaceBaselines();
    context.areaCreateBaselineRequest.value = null;
    context.resetFeedback();
  };

  const resetAreaDraft = (): void => {
    if (!context.areaDraft.id) {
      Object.assign(context.areaDraft, emptyAreaDraft(context.areaDraft.regionId, context.props.state.options.areaDefaults.fillHex));
      context.areaDraft.name = context.props.state.options.areaDefaults.name || 'Area';
    } else {
      Object.assign(context.areaDraft, toAreaDraft(context.activeArea.value, context.areaDraft.regionId, context.props.state.options.areaDefaults.fillHex));
    }
    context.resetFeedback();
  };

  const cancelAreaDraft = async (): Promise<void> => {
    await context.props.editorSurface.closeActiveTarget('Discard unsaved area changes?');
  };

  const saveAreaDraft = async (): Promise<void> => {
    if (!context.areaDraft.regionId) {
      return;
    }

    context.isSaving.value = true;
    context.resetFeedback();
    try {
      const request = buildAreaRequest(context.areaDraft);
      const result = context.areaDraft.id
        ? await updateArea(context.props.editorEndpoint, context.areaDraft.id, context.props.antiforgeryToken, request)
        : await createArea(context.props.editorEndpoint, context.areaDraft.regionId, context.props.antiforgeryToken, request);
      context.emit('mutationApplied', result as EditorMutationResult<unknown>);
      Object.assign(context.areaDraft, toAreaDraft(result.data, result.data.regionId, context.props.state.options.areaDefaults.fillHex));
      context.areaCreateBaselineRequest.value = null;
      context.props.editorSurface.replaceActiveTarget(context.activeAreaTarget.value);
      context.markSaved();
    } catch (error) {
      context.applyError(error, 'Area save failed.');
    } finally {
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
      Object.assign(context.areaDraft, emptyAreaDraft());
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
    if (context.isDirty.value && !(await context.confirmDiscard('Discard unsaved draft changes before reordering areas?'))) {
      await context.restoreRegionOrder(previousIds);
      return;
    }

    if (context.isDirty.value) {
      context.clearAllDraftsAndBaselines();
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

  const drawAreaPolygon = (): void => beginAreaPolygonMapWork(context.areaDraft, context.props.editorSurface, context.props.polygonEditor, context.areaPolygonMapWork);

  return { cancelAreaDraft, deleteDraftArea, drawAreaPolygon, openAreaCreate, openAreaEdit, reorderAreas, resetAreaDraft, saveAreaDraft };
}
