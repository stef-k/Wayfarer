import { message } from '../../../../wwwroot/js/localization/messages.js';
import { onUnmounted, watch } from 'vue';
import { createArea, deleteArea, EditorLifecycleConfirmationError, EditorMutationHttpError, EditorValidationError, orderAreas, updateArea } from '../api/tripEditorApi';
import type { EditorMutationResult, EditorArea, EditorRegion, Guid } from '../types';
import { buildAreaCreateTarget, buildAreaEditTarget } from './regionPlaceEditorTargets';
import { buildAreaRequest, emptyAreaDraft } from './regionPlaceDrafts';
import { beginAreaPolygonMapWork } from './areaPolygonMapWork';
import { saveStructureIntent } from '../recovery/structureMutation';
import { StaleStructureDraft } from '../recovery/structureCustody';
import { areaMaterials, prepareAreaDeletion } from '../recovery/structureOwner';
import { confirm } from '../composables/useConfirmDialog';

/// Coordinates area-specific editor actions while RegionManager owns shared draft state.
export function useAreaEditorActions(context: any) {
  const areaDraft = context.areaDraftState.draft;
  let creationDraftId: string | undefined;
  let creationBase: ReturnType<typeof areaMaterials> | null = null;
  let editorAlive = true;
  let savingSession: number | null = null;
  onUnmounted(() => { editorAlive = false; });
  /** Captures newer authored Polygon, scalar and create intent without starting a submission. */
  watch(() => {
    try {
      if (areaDraft.id) {
        const payload = context.areaDraftState.request();
        return Object.keys(payload.changes).length ? JSON.stringify(payload) : null;
      }
      if (!creationDraftId || !creationBase || !context.areaCreateBaselineRequest.value) return null;
      const value = buildAreaRequest(areaDraft);
      return JSON.stringify(value) === JSON.stringify(context.areaCreateBaselineRequest.value) ? null : JSON.stringify({
        expected: { areas: creationBase.map(({ id, displayOrder }) => ({ id, displayOrder })) }, value
      });
    } catch { return null; }
  }, frozen => {
    if (frozen && areaDraft.regionId) void saveStructureIntent(context.props.editorEndpoint, areaDraft.id ? 'area-fields' : 'area-create',
      { type: areaDraft.id ? 'area' : 'region', id: areaDraft.id ?? areaDraft.regionId },
      JSON.parse(frozen), areaDraft.id ? undefined : creationDraftId).catch(() => undefined);
  });
  // Explicit discard retires only this Area attempt's UI lock; it says nothing about its server outcome.
  watch(() => context.areaDraftState.session(), () => {
    if (savingSession === null) return;
    savingSession = null;
    context.isSaving.value = false;
  }, { flush: 'sync' });
  const openAreaCreate = async (region: EditorRegion): Promise<void> => {
    const target = buildAreaCreateTarget(region);
    const isAlreadyActive = context.props.editorSurface.isTargetActive(target);
    if (region.isShadow || !context.props.state.permissions.canEditAreas || !region.capabilities.canAddChildren || !(await context.props.editorSurface.activateTarget(target)) || isAlreadyActive) {
      return;
    }

    context.clearRegionPlaceDrafts();
    context.areaDraftState.reset(null);
    try { creationBase = areaMaterials(context.props.state, region.id); }
    catch (error) { context.applyError(error, 'Area creation base unavailable.'); return; }
    creationDraftId = crypto.randomUUID();
    Object.assign(areaDraft, emptyAreaDraft(region.id, context.props.state.options.areaDefaults.fillHex));
    areaDraft.name = context.props.state.options.areaDefaults.name || 'Area';
    context.clearRegionPlaceBaselines();
    context.areaCreateBaselineRequest.value = buildAreaRequest(areaDraft);
    context.resetFeedback();
  };

  const openAreaEdit = async (area: EditorArea): Promise<void> => {
    const target = buildAreaEditTarget(area, context.props.state.regionsById[area.regionId]);
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
      // Retire discarded Notes history/dialogs while retaining this creation's parent and defaults.
      const regionId = areaDraft.regionId;
      context.areaDraftState.reset(null);
      Object.assign(areaDraft, emptyAreaDraft(regionId, context.props.state.options.areaDefaults.fillHex));
      areaDraft.name = context.props.state.options.areaDefaults.name || 'Area';
    } else {
      context.areaDraftState.reset(context.activeArea.value);
    }
    context.resetFeedback();
  };

  const cancelAreaDraft = async (): Promise<void> => {
    await context.props.editorSurface.closeActiveTarget(message('Editor.DiscardAreaDraft', 'Discard unsaved area changes?'));
  };

  const saveAreaDraft = async (): Promise<void> => {
    if (context.isSaving.value || !areaDraft.regionId) {
      return;
    }
    if (context.areaDraftState.uncertain.value) {
      context.applyError(new Error('The earlier Area save has an unknown outcome. Keep your draft, review current state and explicitly reset before another attempt.'), 'Area save held.');
      return;
    }

    const areaId = areaDraft.id;
    const regionId = areaDraft.regionId;
    const session = context.areaDraftState.session();
    let dispatched = false;
    savingSession = session;
    context.isSaving.value = true;
    context.resetFeedback();
    try {
      const submitted = areaId ? context.areaDraftState.request() : { expected: {}, changes: buildAreaRequest(areaDraft) };
      if (areaId && Object.keys(submitted.changes).length === 0) return;
      context.areaDraftState.pending.value = Boolean(areaId);
      dispatched = true;
      if (!areaId && !creationBase) throw new Error('Original Area membership is unavailable.');
      const result = areaId
        ? await updateArea(context.props.editorEndpoint, areaId, context.props.antiforgeryToken, submitted)
        : await createArea(context.props.editorEndpoint, regionId, context.props.antiforgeryToken, {
          expected: { areas: creationBase!.map(({ id, displayOrder }) => ({ id, displayOrder })) }, value: submitted.changes
        }, creationDraftId);
      // Retired replies cannot publish older affected slices over a newer editing session.
      if (!editorAlive || session !== context.areaDraftState.session()) return;
      if (!result.success) throw new Error('The Area save was not acknowledged. Keep your draft and review the unknown outcome.');
      // Advance acknowledged originals before the parent's affected slices arrive on the next Vue tick.
      context.areaDraftState.acknowledge(result.data, submitted);
      context.emit('mutationApplied', result as EditorMutationResult<unknown>);
      context.areaCreateBaselineRequest.value = null;
      context.props.editorSurface.replaceActiveTarget(context.activeAreaTarget.value);
      context.markSaved();
    } catch (error) {
      if (!editorAlive || session !== context.areaDraftState.session()) return;
      const rejected = error instanceof StaleStructureDraft || error instanceof EditorValidationError || error instanceof EditorLifecycleConfirmationError
        || error instanceof EditorMutationHttpError && error.status >= 400 && error.status < 500;
      if (areaId && dispatched && !rejected) context.areaDraftState.uncertain.value = true;
      context.applyError(error, 'Area save failed.');
    } finally {
      // An older completion cannot release a newer session's pending/save state.
      if (editorAlive && savingSession === session) {
        savingSession = null;
        context.areaDraftState.pending.value = false;
        context.isSaving.value = false;
      }
    }
  };

  const deleteDraftArea = async (): Promise<void> => {
    if (!context.activeArea.value || !(await context.confirmDiscard(message('Editor.DiscardAreaBeforeDelete', 'Discard unsaved area draft changes before deleting?')))) {
      return;
    }

    const area = context.activeArea.value;
    const deletedTarget = context.activeAreaTarget.value;
    const session = context.areaDraftState.session();
    context.isSaving.value = true;
    context.resetFeedback();
    try {
      const preview = await prepareAreaDeletion(context.props.editorEndpoint, context.props.antiforgeryToken, context.props.state, area.id);
      if (!(await confirm({
        title: message('Editor.DeleteAreaQuestion', 'Delete area?'),
        message: preview.hasPlanning ? message('Editor.DeleteAreaPlanning', 'Delete this area and its private planning?') : preview.name,
        confirmLabel: message('Editor.Delete', 'Delete'),
        cancelLabel: message('Editor.KeepArea', 'Keep area'), variant: 'danger'
      })) || !editorAlive || session !== context.areaDraftState.session()) return;
      const result = await deleteArea(context.props.editorEndpoint, area.id, context.props.antiforgeryToken, preview.confirmationToken);
      if (!editorAlive || session !== context.areaDraftState.session()) return;
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

    let originals: ReturnType<typeof areaMaterials>;
    try { originals = areaMaterials(context.props.state, regionId); }
    catch (error) { context.applyError(error, 'Area order held.'); await context.restoreRegionOrder(previousIds); return; }

    if (context.isDirty.value && !(await context.confirmDiscard(message('Editor.DiscardBeforeAreaReorder', 'Discard unsaved draft changes before reordering areas?')))) {
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
      const result = await orderAreas(context.props.editorEndpoint, regionId, context.props.antiforgeryToken, { expected: { areas: originals }, areaIds: ids });
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
