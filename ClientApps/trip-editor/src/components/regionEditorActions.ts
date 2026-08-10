import { createRegion, deleteRegion, EditorLifecycleConfirmationError, orderRegions, updateRegion } from '../api/tripEditorApi';
import type { EditorMutationResult, EditorRegion, EditorRegionSaveRequest, Guid } from '../types';
import { confirm } from '../composables/useConfirmDialog';
import { buildRegionCreateTarget, buildRegionEditTarget } from './regionPlaceEditorTargets';
import { buildRegionRequest, emptyAreaDraft, emptyPlaceDraft, emptyRegionDraft, toRegionDraft } from './regionPlaceDrafts';

/// Coordinates region-specific editor actions while RegionManager owns shared draft state.
export function useRegionEditorActions(context: any) {
  const openCreate = async (): Promise<void> => {
    const target = buildRegionCreateTarget();
    const isAlreadyActive = context.props.editorSurface.isTargetActive(target);
    if (!(await context.props.editorSurface.activateTarget(target)) || isAlreadyActive) {
      return;
    }

    Object.assign(context.draft, emptyRegionDraft());
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
    Object.assign(context.draft, toRegionDraft(region));
    context.regionCreateBaselineRequest.value = null;
    context.placeCreateBaselineRequest.value = null;
    context.areaCreateBaselineRequest.value = null;
    context.resetFeedback();
  };

  const resetDraft = (): void => {
    if (!context.draft.id) {
      Object.assign(context.draft, emptyRegionDraft());
      context.draft.name = 'New Region';
    } else {
      Object.assign(context.draft, toRegionDraft(context.activeRegion.value));
    }
    context.resetFeedback();
  };

  const cancelDraft = async (): Promise<void> => {
    await context.props.editorSurface.closeActiveTarget('Discard unsaved region changes?');
  };

  const saveDraft = async (): Promise<void> => {
    context.isSaving.value = true;
    context.resetFeedback();

    try {
      const request = buildRegionRequest(context.draft);
      const result = context.draft.id
        ? await updateRegion(context.props.editorEndpoint, context.draft.id, context.props.antiforgeryToken, request)
        : await createRegion(context.props.editorEndpoint, context.props.antiforgeryToken, request);
      context.emit('mutationApplied', result as EditorMutationResult<unknown>);
      Object.assign(context.draft, toRegionDraft(result.data));
      context.regionCreateBaselineRequest.value = null;
      context.props.editorSurface.replaceActiveTarget(context.activeRegionTarget.value);
      context.markSaved();
    } catch (error) {
      context.applyError(error, 'Region save failed.');
    } finally {
      context.isSaving.value = false;
    }
  };

  const deleteDraftRegion = async (): Promise<void> => {
    if (!context.activeRegion.value || !context.activeRegion.value.capabilities.canDelete) {
      return;
    }

    if (!(await context.confirmDiscard('Discard unsaved region or place draft changes before deleting?'))) {
      return;
    }

    context.isSaving.value = true;
    context.resetFeedback();
    try {
      const deletedTarget = context.activeRegionTarget.value;
      let confirmationToken: string | undefined;
      let result: Awaited<ReturnType<typeof deleteRegion>> | undefined;
      for (let attempt = 0; attempt < 3 && !result; attempt += 1) {
        try {
          result = await deleteRegion(context.props.editorEndpoint, context.activeRegion.value.id, context.props.antiforgeryToken, confirmationToken);
        } catch (error) {
          if (!(error instanceof EditorLifecycleConfirmationError)) throw error;
          const warning = error.conflict;
          const confirmed = await confirm({
            title: warning.code === 'lifecycle-confirmation-stale' ? 'Dependencies changed' : 'Delete region?',
            message: `This deletes ${warning.deletedPlaces.count} place(s), ${warning.deletedAreas.count} area(s), ${warning.endpointSegments.count} connected segment(s), and updates ${warning.waypointOnlySegments.count} waypoint route(s).`,
            confirmLabel: 'Delete',
            cancelLabel: 'Keep region',
            variant: 'danger'
          });
          if (!confirmed) return;
          confirmationToken = warning.confirmationToken;
        }
      }
      if (!result) throw new Error('Region dependencies changed repeatedly. Please retry.');
      context.emit('mutationApplied', result as EditorMutationResult<unknown>);
      context.clearAllDraftsAndBaselines();
      context.props.editorSurface.clearActiveTarget(deletedTarget);
      context.markSaved();
    } catch (error) {
      context.applyError(error, 'Region delete failed.');
    } finally {
      context.isSaving.value = false;
    }
  };

  const reorderRegions = async (ids: Guid[], previousIds: Guid[]): Promise<void> => {
    if (context.isOrdering.value) {
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
      Object.assign(context.draft, emptyRegionDraft());
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
