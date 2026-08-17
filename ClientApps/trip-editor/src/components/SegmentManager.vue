<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from 'vue';
import { createSegment, deleteSegment, EditorSegmentConflictError, orderSegments, updateSegment } from '../api/tripEditorApi';
import { confirm } from '../composables/useConfirmDialog';
import type { EditorSurfaceController, EditorTarget } from '../composables/useEditorSurface';
import type { SegmentDraftRoutePreview } from '../map/leafletAdapter';
import type { EditorMutationResult, EditorSegment, EditorSegmentConflict, EditorSegmentDraft, EditorSegmentSaveRequest, EditorSegmentWaypointDraftRow, EditorTripState, Guid } from '../types';
import { buildSegmentCreateTarget, buildSegmentEditTarget, segmentDraftKey } from './regionPlaceEditorTargets';
import { buildSegmentRequest, emptySegmentDraft, mapWaypointErrors, toSegmentDraft } from './regionPlaceDrafts';
import SegmentEditorSurface from './SegmentEditorSurface.vue';
import { beginSegmentRouteMapWork, stopSegmentRouteEdit, type SegmentRouteEditor, type SegmentRouteMapWorkLifecycleState } from './segmentRouteMapWork';
import { mutationFeedbackClass } from './useEditorMutationFeedback';
import { invokeSegmentRouteAction } from './segmentRouteWorkPolicy';
import type { EditorSegmentDraftPresentation, SegmentPresentationKey } from '../segments/editorSegmentPresentation';
import { resolveDraftSegmentPresentation, resolvePersistedSegmentPresentation } from '../segments/editorSegmentPresentation';
import { reverseSegmentDraftRoute } from '../segments/segmentPresentationResolver';

declare global {
  interface Window {
    Sortable?: {
      create: (element: HTMLElement, options: Record<string, unknown>) => { destroy: () => void };
    };
  }
}

const props = defineProps<{
  editorEndpoint: string;
  editorSurface: EditorSurfaceController;
  antiforgeryToken: string;
  hiddenSegmentIds: ReadonlySet<Guid>;
  routeEditor: SegmentRouteEditor;
  searchActive: boolean;
  segments: EditorSegment[];
  state: EditorTripState;
  activeSegmentKey: SegmentPresentationKey | null;
  selectSegment: (key: SegmentPresentationKey) => Promise<boolean>;
}>();

const emit = defineEmits<{
  dirtyStateChanged: [isDirty: boolean];
  hiddenSegmentIdsChanged: [ids: Set<Guid>];
  mutationApplied: [result: EditorMutationResult<unknown>];
  routeDraftPreviewChanged: [preview: SegmentDraftRoutePreview | null];
  activeSegmentDraftChanged: [snapshot: EditorSegmentDraftPresentation | null];
  activeSegmentCleared: [key: SegmentPresentationKey];
}>();

const segmentFields = ['fromPlaceId', 'toPlaceId', 'waypointPlaceIds', 'waypointRouteVertexIndices', 'aggregateConcurrencyToken', 'mode', 'estimatedDistanceKm', 'estimatedDurationMinutes', 'estimatedDurationSource', 'notesHtml', 'route', 'route.coordinates'];
const segmentFormId = 'trip-editor-segment-form';
const segmentList = ref<HTMLElement | null>(null);
const segmentListKey = ref(0);
const draft = reactive<EditorSegmentDraft>(emptySegmentDraft());
const createBaselineRequest = ref<EditorSegmentSaveRequest | null>(null);
const persistedBaseline = ref<EditorSegmentDraft>(emptySegmentDraft());
const isSaving = ref(false);
const isOrdering = ref(false);
const validationErrors = ref<Record<string, string[]>>({});
const segmentConflict = ref<EditorSegmentConflict | null>(null);
const saveError = ref<string | null>(null);
const lastSavedAt = ref<string | null>(null);
const routeMapWork = reactive<SegmentRouteMapWorkLifecycleState>({ work: null, stopEdit: null });
const segmentEditorSurface = ref<{ focusNotes: () => void; focusRouteAction: () => boolean } | null>(null);
let unregisterHandler: (() => void) | null = null;
let sortable: { destroy: () => void } | null = null;
let sortableRetry: number | null = null;
let reorderSnapshotIds: Guid[] | null = null;

const activeSegment = computed(() => (draft.id ? props.state.segmentsById[draft.id] ?? null : null));
const isDraftOpen = computed(() => props.editorSurface.isTargetActive(activeSegmentTarget.value) || draft.id !== null || Boolean(draft.fromPlaceId || draft.toPlaceId || draft.mode || draft.estimatedDistanceKm || draft.estimatedDurationMinutes || draft.notesHtml || draft.route));
const baselineRequest = computed(() => draft.id ? buildSegmentRequest(persistedBaseline.value) : createBaselineRequest.value ?? buildSegmentRequest(emptySegmentDraft()));
const isDirty = computed(() => JSON.stringify(buildSegmentRequest(draft)) !== JSON.stringify(baselineRequest.value));
const statusText = computed(() => isSaving.value ? 'Saving...' : isOrdering.value ? 'Saving order...' : saveError.value ? 'Save failed' : isDirty.value ? 'Unsaved changes' : lastSavedAt.value ? `Saved ${lastSavedAt.value}` : 'Saved');
const formSummaryErrors = computed(() => Object.entries(validationErrors.value)
  .filter(([key]) => !segmentFields.includes(key) && !key.startsWith('waypoint.'))
  .flatMap(([, messages]) => messages));
const activeSegmentTarget = computed<EditorTarget>(() => draft.id && activeSegment.value
  ? buildSegmentEditTarget(activeSegment.value, segmentLabel(activeSegment.value))
  : buildSegmentCreateTarget());
const draftOrientation = computed(() => {
  if (!draft.route) return null;
  try {
    return resolveDraftSegmentPresentation({ key: draft.id ? persistedPresentationKey(draft.id) : createPresentationKey(), draft, work: null }, props.state).orientation;
  } catch {
    return 'ambiguous';
  }
});

watch(isDirty, value => emit('dirtyStateChanged', value), { immediate: true });
watch(
  () => [draft.id, draft.fromPlaceId, draft.toPlaceId, JSON.stringify(draft.route), JSON.stringify(draft.waypointRows)],
  () => { syncRouteDraftPreview(); publishPresentation(); },
  { flush: 'sync' }
);
watch(() => [props.segments.length, props.searchActive, segmentListKey.value], () => nextTick(attachSortable), { immediate: true });
watch(
  () => routeMapWork.work?.nodes.some(node => {
    if (node.kind !== 'anchor') return false;
    const location = props.state.placesById[node.placeId]?.location;
    return !location || location.longitude !== node.coordinate[0] || location.latitude !== node.coordinate[1];
  }) ?? false,
  stale => { if (stale) void invalidateStaleRouteWork(); }
);
watch(() => JSON.stringify(routeMapWork.work), publishPresentation, { flush: 'sync' });

onMounted(() => {
  unregisterHandler = props.editorSurface.registerTargetHandler(segmentDraftKey, {
    isDirty: () => isDirty.value,
    discard: discardDraft
  });
});

onUnmounted(() => {
  unregisterHandler?.();
  destroySortable();
  stopSegmentRouteEdit(routeMapWork);
  emit('routeDraftPreviewChanged', null);
  emit('activeSegmentDraftChanged', null);
  emit('dirtyStateChanged', false);
});

async function openCreate(): Promise<void> {
  const target = buildSegmentCreateTarget();
  const isAlreadyActive = props.editorSurface.isTargetActive(target);
  if (!props.state.permissions.canEditSegments || !(await props.editorSurface.activateTarget(target)) || isAlreadyActive) {
    return;
  }

  Object.assign(draft, emptySegmentDraft());
  persistedBaseline.value = emptySegmentDraft();
  createBaselineRequest.value = buildSegmentRequest(draft);
  resetFeedback();
  syncRouteDraftPreview();
  await props.selectSegment(createPresentationKey());
  publishPresentation();
}

async function openEdit(segment: EditorSegment): Promise<boolean> {
  const target = buildSegmentEditTarget(segment, segmentLabel(segment));
  const isAlreadyActive = props.editorSurface.isTargetActive(target);
  if (!segment.capabilities.canEdit) {
    return false;
  }

  if (isAlreadyActive) {
    return true;
  }

  if (!(await props.editorSurface.activateTarget(target))) {
    return false;
  }

  persistedBaseline.value = toSegmentDraft(segment);
  Object.assign(draft, toSegmentDraft(segment));
  createBaselineRequest.value = null;
  resetFeedback();
  syncRouteDraftPreview();
  await props.selectSegment(persistedPresentationKey(segment.id));
  publishPresentation();
  return true;
}

function resetDraft(): void {
  const focusDestination = resetFocusDestination(draft, draft.id ? persistedBaseline.value : emptySegmentDraft());
  Object.assign(draft, draft.id ? cloneDraft(persistedBaseline.value) : emptySegmentDraft());
  resetFeedback();
  syncRouteDraftPreview();
  publishPresentation();
  void nextTick(async () => {
    await nextTick();
    if (focusDestination === 'notes') {
      segmentEditorSurface.value?.focusNotes();
      return;
    }
    if (focusDestination === 'route' && segmentEditorSurface.value?.focusRouteAction()) return;
    const requested = focusDestination ? document.querySelector<HTMLElement>(focusDestination) : null;
    const target = requested && isFocusable(requested) ? requested : document.querySelector<HTMLElement>('[data-segment-waypoint-group]');
    target?.focus();
  });
}

async function cancelDraft(): Promise<void> {
  await props.editorSurface.closeActiveTarget('Discard unsaved segment changes?');
}

async function saveDraft(): Promise<void> {
  isSaving.value = true;
  resetFeedback();
  const submittedWaypointRows = draft.waypointRows.map(row => ({ ...row }));
  try {
    const request = buildSegmentRequest(draft);
    const wasCreate = draft.id === null;
    const result = draft.id
      ? await updateSegment(props.editorEndpoint, draft.id, props.antiforgeryToken, request)
      : await createSegment(props.editorEndpoint, props.antiforgeryToken, request);
    const savedDraft = toSegmentDraft(result.data);
    if (wasCreate) {
      emit('activeSegmentDraftChanged', { key: persistedPresentationKey(result.data.id), draft: cloneDraft(savedDraft), work: null });
    }
    if (wasCreate) {
      emit('routeDraftPreviewChanged', buildRouteDraftPreview(result.data.id));
    }
    emit('mutationApplied', result as EditorMutationResult<unknown>);
    persistedBaseline.value = cloneDraft(savedDraft);
    Object.assign(draft, savedDraft);
    createBaselineRequest.value = null;
    props.editorSurface.replaceActiveTarget(buildSegmentEditTarget(result.data, segmentLabel(result.data)));
    emit('routeDraftPreviewChanged', null);
    publishPresentation();
    markSaved();
  } catch (error) {
    if (error instanceof EditorSegmentConflictError && draft.id) {
      if (error.confirmationToken && error.conflict.code === 'segment-route-clear-confirmation-required') {
        const accepted = await confirm({ title: 'Clear custom route?', message: error.conflict.warning,
          confirmLabel: 'Clear route and save', cancelLabel: 'Keep editing', variant: 'warning' });
        if (!accepted) {
          void nextTick(() => document.querySelector<HTMLElement>(`button[form="${segmentFormId}"]`)?.focus());
          return;
        }
        try {
          const confirmed = await updateSegment(props.editorEndpoint, draft.id, props.antiforgeryToken, buildSegmentRequest(draft), error.confirmationToken);
          emit('mutationApplied', confirmed as EditorMutationResult<unknown>);
          persistedBaseline.value = toSegmentDraft(confirmed.data);
          Object.assign(draft, toSegmentDraft(confirmed.data));
          emit('routeDraftPreviewChanged', null);
          publishPresentation();
          markSaved();
          return;
        } catch (retryError) {
          if (retryError instanceof EditorSegmentConflictError) showConflict(retryError.conflict);
          applyError(retryError, 'Segment save failed.', submittedWaypointRows);
          return;
        }
      }
      showConflict(error.conflict);
      // Keep the user's complete visible and hidden proposal retryable after canonical contention.
    }
    applyError(error, 'Segment save failed.', submittedWaypointRows);
  } finally {
    isSaving.value = false;
  }
}

async function deleteDraft(): Promise<void> {
  const segment = activeSegment.value;
  if (!segment) {
    return;
  }

  await deleteSegmentWithConfirmation(segment, activeSegmentTarget.value);
}

async function deleteSegmentFromRow(segment: EditorSegment): Promise<void> {
  if (!segment.capabilities.canDelete) {
    return;
  }

  const target = buildSegmentEditTarget(segment, segmentLabel(segment));
  const isAlreadyActive = props.editorSurface.isTargetActive(target);
  if (!isAlreadyActive) {
    const activated = await props.editorSurface.activateTarget(target);
    if (!activated) {
      return;
    }

    Object.assign(draft, toSegmentDraft(segment));
    createBaselineRequest.value = null;
    resetFeedback();
  }

  if (!props.editorSurface.isTargetActive(target) || draft.id !== segment.id) {
    return;
  }

  await deleteSegmentWithConfirmation(segment, target);
}

async function deleteSegmentWithConfirmation(segment: EditorSegment, deletedTarget: EditorTarget): Promise<void> {
  if (!(await confirmDiscard('Discard unsaved segment draft changes before deleting?'))) {
    return;
  }

  if (!(await confirm({
    title: 'Delete segment?',
    message: 'Delete this segment?',
    confirmLabel: 'Delete',
    cancelLabel: 'Keep segment',
    variant: 'danger'
  }))) {
    return;
  }

  isSaving.value = true;
  resetFeedback();
  try {
    const result = await deleteSegment(props.editorEndpoint, segment.id, props.antiforgeryToken);
    emit('mutationApplied', result as EditorMutationResult<unknown>);
    const hidden = new Set(props.hiddenSegmentIds);
    hidden.delete(segment.id);
    emit('hiddenSegmentIdsChanged', hidden);
    Object.assign(draft, emptySegmentDraft());
    persistedBaseline.value = emptySegmentDraft();
    emit('routeDraftPreviewChanged', null);
    emit('activeSegmentCleared', persistedPresentationKey(segment.id));
    createBaselineRequest.value = null;
    props.editorSurface.clearActiveTarget(deletedTarget);
    markSaved();
  } catch (error) {
    applyError(error, 'Segment delete failed.');
  } finally {
    isSaving.value = false;
  }
}

function drawRoute(): void {
  invokeSegmentRouteAction(draft, () => {
    const error = beginSegmentRouteMapWork(routePreviewIdentity(), draft, props.editorSurface, props.routeEditor, routeMapWork, props.state, focusRouteAction);
    if (!error) return;
    saveError.value = error;
    void nextTick(() => document.getElementById('segment-route-work-error')?.focus());
  });
}

function clearRoute(): void {
  invokeSegmentRouteAction(draft, () => {
    draft.route = null;
    draft.waypointRouteVertexIndices = draft.waypointRows.map(() => null);
    draft.waypointRows.forEach(row => { row.routeVertexIndex = null; });
  });
}

/** Returns focus to the route action after map-work teardown completes. */
function focusRouteAction(): void {
  void nextTick(() => document.querySelector<HTMLButtonElement>('[data-segment-route-action]:not([disabled])')?.focus());
}

/** Rejects an observed authoritative anchor change instead of merging it into active W. */
async function invalidateStaleRouteWork(): Promise<void> {
  await props.editorSurface.invalidateMapWork();
  saveError.value = 'Saved Place coordinates changed. Reload the current saved Segment before reopening route work.';
  await nextTick();
  document.getElementById('segment-route-work-error')?.focus();
}

function toggleVisibility(segment: EditorSegment): void {
  const hidden = new Set(props.hiddenSegmentIds);
  if (hidden.has(segment.id)) {
    hidden.delete(segment.id);
  } else {
    hidden.add(segment.id);
    emit('activeSegmentCleared', persistedPresentationKey(segment.id));
  }

  emit('hiddenSegmentIdsChanged', hidden);
}

async function reorder(ids: Guid[], previousIds: Guid[]): Promise<void> {
  if (isDirty.value && !(await confirmDiscard('Discard unsaved segment changes before reordering?'))) {
    restoreSegmentOrder(previousIds);
    return;
  }

  if (isDirty.value) {
    discardDraft();
  }

  isOrdering.value = true;
  resetFeedback();
  try {
    const result = await orderSegments(props.editorEndpoint, props.antiforgeryToken, { segmentIds: ids });
    emit('mutationApplied', result as EditorMutationResult<unknown>);
    markSaved();
  } catch (error) {
    applyError(error, 'Segment reorder failed.');
    restoreSegmentOrder(previousIds);
  } finally {
    isOrdering.value = false;
  }
}

async function moveSegmentByKeyboard(segmentId: Guid, offset: number): Promise<void> {
  if (props.searchActive || isOrdering.value) {
    return;
  }

  const ids = props.segments.map(segment => segment.id);
  const index = ids.indexOf(segmentId);
  const nextIndex = index + offset;
  if (index < 0 || nextIndex < 0 || nextIndex >= ids.length) {
    return;
  }

  const previousIds = props.state.segmentOrder.filter(id => props.state.segmentsById[id]);
  const [id] = ids.splice(index, 1);
  ids.splice(nextIndex, 0, id);
  await reorder(ids, previousIds);
}

function attachSortable(): void {
  destroySortable();
  if (props.searchActive || !segmentList.value) {
    return;
  }

  if (!window.Sortable) {
    sortableRetry = window.setTimeout(() => {
      sortableRetry = null;
      attachSortable();
    }, 50);
    return;
  }

  sortable = window.Sortable.create(segmentList.value, {
    animation: 150,
    draggable: '.trip-editor-segment-row',
    handle: '.trip-editor-segment-drag-handle',
    onStart: () => {
      reorderSnapshotIds = props.state.segmentOrder.filter(id => props.state.segmentsById[id]);
    },
    onEnd: () => {
      if (!segmentList.value) {
        return;
      }

      const previousIds = reorderSnapshotIds ?? props.state.segmentOrder;
      reorderSnapshotIds = null;
      const ids = Array.from(segmentList.value.querySelectorAll<HTMLElement>('[data-segment-id]')).map(element => element.dataset.segmentId!);
      void reorder(ids, previousIds);
    }
  });
}

function destroySortable(): void {
  if (sortableRetry !== null) {
    window.clearTimeout(sortableRetry);
    sortableRetry = null;
  }
  sortable?.destroy();
  sortable = null;
}

function restoreSegmentOrder(_previousIds: Guid[]): void {
  segmentListKey.value += 1;
}

function discardDraft(): void {
  const discardedKey = draft.id ? persistedPresentationKey(draft.id) : createPresentationKey();
  stopSegmentRouteEdit(routeMapWork);
  Object.assign(draft, emptySegmentDraft());
  persistedBaseline.value = emptySegmentDraft();
  createBaselineRequest.value = null;
  resetFeedback();
  emit('routeDraftPreviewChanged', null);
  emit('activeSegmentDraftChanged', null);
  emit('activeSegmentCleared', discardedKey);
}

/** Reverses only D; Save remains the sole persistence/reconciliation boundary. */
function reverseRoute(): void {
  if (!draft.route || routeMapWork.work || draftOrientation.value !== 'reversed') return;
  reverseSegmentDraftRoute(draft);
  syncRouteDraftPreview();
  publishPresentation();
}

/** Publishes a cloned D/W presentation while this component retains all mutation authority. */
function publishPresentation(): void {
  if (!props.editorSurface.isTargetActive(activeSegmentTarget.value)) return;
  emit('activeSegmentDraftChanged', {
    key: draft.id ? persistedPresentationKey(draft.id) : createPresentationKey(),
    draft: cloneDraft(draft),
    work: routeMapWork.work ? JSON.parse(JSON.stringify(routeMapWork.work)) : null
  });
}

const persistedPresentationKey = (id: Guid): SegmentPresentationKey => ({ kind: 'persisted', id });
const createPresentationKey = (): SegmentPresentationKey => ({ kind: 'create-draft', token: 'segment-create-draft' });

/** Derives current compact and accessible text from persisted ordered anchors. */
function segmentJourney(segment: EditorSegment): { compact: string; accessible: string } {
  const presentation = draft.id === segment.id && props.editorSurface.isTargetActive(activeSegmentTarget.value)
    ? resolveDraftSegmentPresentation({ key: persistedPresentationKey(segment.id), draft, work: routeMapWork.work }, props.state)
    : resolvePersistedSegmentPresentation(segment, props.state);
  return { compact: presentation.anchors.compactTrail, accessible: presentation.anchors.accessibleName };
}

function isActiveSegment(segment: EditorSegment): boolean {
  return props.activeSegmentKey?.kind === 'persisted' && props.activeSegmentKey.id === segment.id;
}

/// Publishes the active segment form route without exposing form state to Leaflet internals.
function syncRouteDraftPreview(): void {
  if (!props.editorSurface.isTargetActive(activeSegmentTarget.value)) {
    return;
  }

  if (draft.waypointPlaceIds.length > 0) {
    emit('routeDraftPreviewChanged', null);
    return;
  }
  emit('routeDraftPreviewChanged', buildRouteDraftPreview());
}

function buildRouteDraftPreview(segmentId: Guid | null = draft.id): SegmentDraftRoutePreview {
  return {
    fromPlaceId: draft.fromPlaceId,
    identity: routePreviewIdentity(),
    route: draft.route ? JSON.parse(JSON.stringify(draft.route)) : null,
    segmentId,
    toPlaceId: draft.toPlaceId
  };
}

function routePreviewIdentity(): string {
  return draft.id ?? activeSegmentTarget.value.identity;
}

function confirmDiscard(message: string): Promise<boolean> {
  if (!isDirty.value) {
    return Promise.resolve(true);
  }

  return confirm({
    title: 'Discard changes?',
    message,
    confirmLabel: 'Discard',
    cancelLabel: 'Keep editing',
    variant: 'warning'
  });
}

function fieldErrors(key: string): string[] {
  return validationErrors.value[key] ?? [];
}

function showConflict(conflict: EditorSegmentConflict): void {
  segmentConflict.value = conflict;
  void nextTick(() => document.getElementById('segment-conflict-heading')?.focus());
}

function clearFieldError(key: string): void {
  if (!(key in validationErrors.value)) return;
  const next = { ...validationErrors.value };
  delete next[key];
  validationErrors.value = next;
}

function applyError(error: unknown, fallback: string, submittedWaypointRows: EditorSegmentWaypointDraftRow[] = []): void {
  if (error instanceof Error && 'errors' in error) {
    validationErrors.value = mapWaypointErrors((error as Error & { errors: Record<string, string[]> }).errors, submittedWaypointRows);
    void nextTick(() => {
      const firstKey = Object.keys(validationErrors.value)[0];
      const rowId = firstKey?.startsWith('waypoint.') ? firstKey.slice('waypoint.'.length) : null;
      const target = rowId ? document.querySelector<HTMLElement>(`[data-waypoint-client-id="${CSS.escape(rowId)}"]`)
        : firstKey?.startsWith('waypoint') ? document.querySelector<HTMLElement>('[data-segment-waypoint-group]')
          : firstKey ? document.querySelector<HTMLElement>(`[data-segment-field="${CSS.escape(firstKey)}"]`) : null;
      target?.focus();
    });
  }

  saveError.value = error instanceof Error ? error.message : fallback;
}

function resetFeedback(): void {
  validationErrors.value = {};
  saveError.value = null;
  segmentConflict.value = null;
}

/** Resolves Reset focus from the first changed visible Segment control in document order. */
function resetFocusDestination(current: EditorSegmentDraft, baseline: EditorSegmentDraft): string | null {
  const changed = (key: keyof EditorSegmentDraft): boolean => JSON.stringify(current[key]) !== JSON.stringify(baseline[key]);
  const ordered: Array<[() => boolean, string]> = [
    [() => changed('fromPlaceId'), '[data-segment-field="fromPlaceId"]'],
    [() => changed('waypointPlaceIds') || changed('waypointRouteVertexIndices'), '[data-segment-waypoint-group]'],
    [() => changed('toPlaceId'), '[data-segment-field="toPlaceId"]'],
    [() => changed('mode'), '[data-segment-field="mode"]'],
    [() => changed('estimatedDurationSource'), `[data-segment-field="estimatedDurationSource"][value="${baseline.estimatedDurationSource}"]`],
    [() => changed('estimatedDurationMinutes'), '[data-segment-field="estimatedDurationMinutes"]'],
    [() => changed('notesHtml'), 'notes'],
    [() => changed('route'), 'route']
  ];
  return ordered.find(([isChanged]) => isChanged())?.[1] ?? null;
}

/** Rejects hidden or disabled Reset destinations after responsive rerendering. */
function isFocusable(element: HTMLElement): boolean {
  return !element.hasAttribute('disabled') && element.getClientRects().length > 0;
}

/** Replaces the complete draft only after explicit confirmation against current server state. */
async function reloadCurrentSegment(): Promise<void> {
  if (!segmentConflict.value || !(await confirm({ title: 'Reload current saved Segment?', message: 'Discard this complete unsaved Segment draft and load the current saved version?', confirmLabel: 'Reload saved Segment', cancelLabel: 'Keep editing', variant: 'warning' }))) return;
  persistedBaseline.value = toSegmentDraft(segmentConflict.value.currentSegment);
  Object.assign(draft, toSegmentDraft(segmentConflict.value.currentSegment));
  resetFeedback();
  syncRouteDraftPreview();
}

function cloneDraft(value: EditorSegmentDraft): EditorSegmentDraft {
  return JSON.parse(JSON.stringify(value)) as EditorSegmentDraft;
}

function conflictJourney(segment: EditorSegment): string {
  return [segment.fromPlaceId, ...segment.waypointPlaceIds, segment.toPlaceId]
    .map(id => id ? props.state.placesById[id]?.name ?? 'Unavailable place' : 'Not selected').join(' → ');
}

function markSaved(): void {
  lastSavedAt.value = new Intl.DateTimeFormat(undefined, { timeStyle: 'short' }).format(new Date());
  saveError.value = null;
}

function segmentLabel(segment: EditorSegment): string {
  const from = segment.fromPlaceId ? props.state.placesById[segment.fromPlaceId]?.name : null;
  const to = segment.toPlaceId ? props.state.placesById[segment.toPlaceId]?.name : null;
  return [from, to].filter(Boolean).join(' to ') || segment.mode || 'Segment';
}

function modeText(segment: EditorSegment): string {
  return props.state.options.transportModes.find(mode => mode.value === segment.mode)?.label ?? (segment.mode || 'mode unset');
}
</script>

<template>
  <section class="trip-editor-panel">
    <div class="trip-editor-panel__line">
      <h2>Segments</h2>
      <span class="trip-editor-save-state" :class="mutationFeedbackClass(statusText)" role="status">{{ statusText }}</span>
    </div>
    <div v-if="saveError" id="segment-route-work-error" class="trip-editor-form-error" role="alert" tabindex="-1">{{ saveError }}</div>
    <section v-if="segmentConflict" class="trip-editor-form-warning segment-conflict" aria-labelledby="segment-conflict-heading">
      <h3 id="segment-conflict-heading" tabindex="-1">Current saved Segment</h3>
      <p>{{ conflictJourney(segmentConflict.currentSegment) }}</p>
      <p>{{ modeText(segmentConflict.currentSegment) }} · {{ segmentConflict.currentSegment.estimatedDistanceKm ?? 'Distance unavailable' }} km · {{ segmentConflict.currentSegment.estimatedDurationMinutes ?? 'Duration unavailable' }} minutes · {{ segmentConflict.currentSegment.estimatedDurationSource }}</p>
      <button type="button" class="btn btn-outline-light btn-sm" @click="reloadCurrentSegment">Reload current saved Segment</button>
    </section>
    <button type="button" class="btn btn-primary btn-sm trip-editor-add-button" :disabled="isSaving || isOrdering" @click="openCreate">Add Segment</button>

    <SegmentEditorSurface v-if="isDraftOpen && !draft.id" ref="segmentEditorSurface" :active-segment="activeSegment" :controller="editorSurface" :draft="draft" :field-errors="fieldErrors" :form-id="segmentFormId" :form-summary-errors="formSummaryErrors" :is-dirty="isDirty" :is-saving="isSaving" :route-orientation="draftOrientation" :route-map-work-active="Boolean(routeMapWork.work)" :state="state" :status-text="statusText" :target="activeSegmentTarget" @cancel="cancelDraft" @clear-error="clearFieldError" @clear-route="clearRoute" @delete="deleteDraft" @draw-route="drawRoute" @reset="resetDraft" @reverse-route="reverseRoute" @save="saveDraft" />

    <ul v-if="segments.length > 0" :key="segmentListKey" ref="segmentList" class="trip-editor-segments">
      <li v-for="segment in segments" :key="segment.id" class="trip-editor-segment-row" :data-segment-id="segment.id">
        <button type="button" class="trip-editor-icon-button trip-editor-segment-drag-handle" title="Drag to reorder segment" aria-label="Drag to reorder segment" @keydown.arrow-up.prevent="moveSegmentByKeyboard(segment.id, -1)" @keydown.arrow-down.prevent="moveSegmentByKeyboard(segment.id, 1)">↕</button>
        <button type="button" class="trip-editor-icon-button" :title="hiddenSegmentIds.has(segment.id) ? 'Show segment' : 'Hide segment'" :aria-label="hiddenSegmentIds.has(segment.id) ? 'Show segment' : 'Hide segment'" @click="toggleVisibility(segment)">{{ hiddenSegmentIds.has(segment.id) ? '○' : '●' }}</button>
        <button type="button" class="trip-editor-list-button" :aria-current="isActiveSegment(segment) ? 'true' : undefined" :aria-label="segmentJourney(segment).accessible" @click="openEdit(segment)">
          <span>{{ segmentJourney(segment).compact }}</span>
          <small>{{ modeText(segment) }}</small>
        </button>
        <button type="button" class="trip-editor-icon-button" title="Delete segment" aria-label="Delete segment" @click="deleteSegmentFromRow(segment)">×</button>

        <SegmentEditorSurface v-if="draft.id === segment.id && editorSurface.isTargetActive(activeSegmentTarget)" ref="segmentEditorSurface" :active-segment="activeSegment" :controller="editorSurface" :draft="draft" :field-errors="fieldErrors" :form-id="segmentFormId" :form-summary-errors="formSummaryErrors" :is-dirty="isDirty" :is-saving="isSaving" :route-orientation="draftOrientation" :route-map-work-active="Boolean(routeMapWork.work)" :state="state" :status-text="statusText" :target="activeSegmentTarget" @cancel="cancelDraft" @clear-error="clearFieldError" @clear-route="clearRoute" @delete="deleteDraft" @draw-route="drawRoute" @reset="resetDraft" @reverse-route="reverseRoute" @save="saveDraft" />
      </li>
    </ul>
    <p v-else class="trip-editor-empty-state">No travel segments added yet.</p>
  </section>
</template>
