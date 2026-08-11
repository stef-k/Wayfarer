<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from 'vue';
import { createSegment, deleteSegment, EditorSegmentConflictError, orderSegments, updateSegment } from '../api/tripEditorApi';
import { confirm } from '../composables/useConfirmDialog';
import type { EditorSurfaceController, EditorTarget } from '../composables/useEditorSurface';
import type { SegmentDraftRoutePreview } from '../map/leafletAdapter';
import type { EditorMutationResult, EditorSegment, EditorSegmentDraft, EditorSegmentSaveRequest, EditorTripState, Guid } from '../types';
import { buildSegmentCreateTarget, buildSegmentEditTarget, segmentDraftKey } from './regionPlaceEditorTargets';
import { buildSegmentRequest, emptySegmentDraft, toSegmentDraft } from './regionPlaceDrafts';
import SegmentEditorSurface from './SegmentEditorSurface.vue';
import { beginSegmentRouteMapWork, stopSegmentRouteEdit, type SegmentRouteEditor, type SegmentRouteMapWorkState } from './segmentRouteMapWork';
import { mutationFeedbackClass } from './useEditorMutationFeedback';
import { invokeSegmentRouteAction } from './segmentRouteWorkPolicy';

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
}>();

const emit = defineEmits<{
  dirtyStateChanged: [isDirty: boolean];
  hiddenSegmentIdsChanged: [ids: Set<Guid>];
  mutationApplied: [result: EditorMutationResult<unknown>];
  routeDraftPreviewChanged: [preview: SegmentDraftRoutePreview | null];
}>();

const segmentFields = ['fromPlaceId', 'toPlaceId', 'waypointPlaceIds', 'waypointRouteVertexIndices', 'aggregateConcurrencyToken', 'mode', 'estimatedDistanceKm', 'estimatedDurationMinutes', 'estimatedDurationSource', 'notesHtml', 'route', 'route.coordinates'];
const segmentFormId = 'trip-editor-segment-form';
const segmentList = ref<HTMLElement | null>(null);
const segmentListKey = ref(0);
const draft = reactive<EditorSegmentDraft>(emptySegmentDraft());
const createBaselineRequest = ref<EditorSegmentSaveRequest | null>(null);
const isSaving = ref(false);
const isOrdering = ref(false);
const validationErrors = ref<Record<string, string[]>>({});
const saveError = ref<string | null>(null);
const lastSavedAt = ref<string | null>(null);
const routeMapWork = reactive<SegmentRouteMapWorkState>({ route: null, stopEdit: null });
let unregisterHandler: (() => void) | null = null;
let sortable: { destroy: () => void } | null = null;
let sortableRetry: number | null = null;
let reorderSnapshotIds: Guid[] | null = null;

const activeSegment = computed(() => (draft.id ? props.state.segmentsById[draft.id] ?? null : null));
const isDraftOpen = computed(() => props.editorSurface.isTargetActive(activeSegmentTarget.value) || draft.id !== null || Boolean(draft.fromPlaceId || draft.toPlaceId || draft.mode || draft.estimatedDistanceKm || draft.estimatedDurationMinutes || draft.notesHtml || draft.route));
const baselineRequest = computed(() => draft.id ? buildSegmentRequest(toSegmentDraft(activeSegment.value)) : createBaselineRequest.value ?? buildSegmentRequest(emptySegmentDraft()));
const isDirty = computed(() => JSON.stringify(buildSegmentRequest(draft)) !== JSON.stringify(baselineRequest.value));
const statusText = computed(() => isSaving.value ? 'Saving...' : isOrdering.value ? 'Saving order...' : saveError.value ? 'Save failed' : isDirty.value ? 'Unsaved changes' : lastSavedAt.value ? `Saved ${lastSavedAt.value}` : 'Saved');
const formSummaryErrors = computed(() => Object.entries(validationErrors.value).filter(([key]) => !segmentFields.includes(key)).flatMap(([, messages]) => messages));
const activeSegmentTarget = computed<EditorTarget>(() => draft.id && activeSegment.value
  ? buildSegmentEditTarget(activeSegment.value, segmentLabel(activeSegment.value))
  : buildSegmentCreateTarget());

watch(isDirty, value => emit('dirtyStateChanged', value), { immediate: true });
watch(
  () => [draft.id, draft.fromPlaceId, draft.toPlaceId, JSON.stringify(draft.route), props.hiddenSegmentIds.has(draft.id ?? '')],
  syncRouteDraftPreview,
  { flush: 'sync' }
);
watch(() => [props.segments.length, props.searchActive, segmentListKey.value], () => nextTick(attachSortable), { immediate: true });

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
  emit('dirtyStateChanged', false);
});

async function openCreate(): Promise<void> {
  const target = buildSegmentCreateTarget();
  const isAlreadyActive = props.editorSurface.isTargetActive(target);
  if (!props.state.permissions.canEditSegments || !(await props.editorSurface.activateTarget(target)) || isAlreadyActive) {
    return;
  }

  Object.assign(draft, emptySegmentDraft());
  createBaselineRequest.value = buildSegmentRequest(draft);
  resetFeedback();
  syncRouteDraftPreview();
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

  Object.assign(draft, toSegmentDraft(segment));
  createBaselineRequest.value = null;
  resetFeedback();
  syncRouteDraftPreview();
  return true;
}

function resetDraft(): void {
  Object.assign(draft, draft.id ? toSegmentDraft(activeSegment.value) : emptySegmentDraft());
  resetFeedback();
  syncRouteDraftPreview();
}

async function cancelDraft(): Promise<void> {
  await props.editorSurface.closeActiveTarget('Discard unsaved segment changes?');
}

async function saveDraft(): Promise<void> {
  isSaving.value = true;
  resetFeedback();
  try {
    const request = buildSegmentRequest(draft);
    const wasCreate = draft.id === null;
    const result = draft.id
      ? await updateSegment(props.editorEndpoint, draft.id, props.antiforgeryToken, request)
      : await createSegment(props.editorEndpoint, props.antiforgeryToken, request);
    if (wasCreate) {
      emit('routeDraftPreviewChanged', buildRouteDraftPreview(result.data.id));
    }
    emit('mutationApplied', result as EditorMutationResult<unknown>);
    Object.assign(draft, toSegmentDraft(result.data));
    createBaselineRequest.value = null;
    props.editorSurface.replaceActiveTarget(activeSegmentTarget.value);
    emit('routeDraftPreviewChanged', null);
    markSaved();
  } catch (error) {
    if (error instanceof EditorSegmentConflictError && draft.id) {
      if (error.confirmationToken && await confirm({
        title: 'Clear custom route?', message: error.conflict.warning,
        confirmLabel: 'Clear route and save', cancelLabel: 'Keep editing', variant: 'warning'
      })) {
        try {
          const confirmed = await updateSegment(props.editorEndpoint, draft.id, props.antiforgeryToken, buildSegmentRequest(draft), error.confirmationToken);
          emit('mutationApplied', confirmed as EditorMutationResult<unknown>);
          Object.assign(draft, toSegmentDraft(confirmed.data));
          markSaved();
          return;
        } catch (retryError) {
          applyError(retryError, 'Segment save failed.');
          return;
        }
      }
      // Keep the user's complete visible and hidden proposal retryable after canonical contention.
    }
    applyError(error, 'Segment save failed.');
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
    emit('routeDraftPreviewChanged', null);
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
  invokeSegmentRouteAction(draft, () =>
    beginSegmentRouteMapWork(routePreviewIdentity(), draft, props.editorSurface, props.routeEditor, routeMapWork, props.state));
}

function clearRoute(): void {
  invokeSegmentRouteAction(draft, () => { draft.route = null; });
}

function toggleVisibility(segment: EditorSegment): void {
  const hidden = new Set(props.hiddenSegmentIds);
  if (hidden.has(segment.id)) {
    hidden.delete(segment.id);
  } else {
    hidden.add(segment.id);
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
  stopSegmentRouteEdit(routeMapWork);
  Object.assign(draft, emptySegmentDraft());
  createBaselineRequest.value = null;
  resetFeedback();
  emit('routeDraftPreviewChanged', null);
}

/// Publishes the active segment form route without exposing form state to Leaflet internals.
function syncRouteDraftPreview(): void {
  if (!props.editorSurface.isTargetActive(activeSegmentTarget.value)) {
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

function applyError(error: unknown, fallback: string): void {
  if (error instanceof Error && 'errors' in error) {
    validationErrors.value = (error as Error & { errors: Record<string, string[]> }).errors;
  }

  saveError.value = error instanceof Error ? error.message : fallback;
}

function resetFeedback(): void {
  validationErrors.value = {};
  saveError.value = null;
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
    <div v-if="saveError" class="trip-editor-form-error" role="alert">{{ saveError }}</div>
    <button type="button" class="btn btn-primary btn-sm trip-editor-add-button" :disabled="isSaving || isOrdering" @click="openCreate">Add Segment</button>

    <SegmentEditorSurface v-if="isDraftOpen && !draft.id" :active-segment="activeSegment" :controller="editorSurface" :draft="draft" :field-errors="fieldErrors" :form-id="segmentFormId" :form-summary-errors="formSummaryErrors" :is-dirty="isDirty" :is-saving="isSaving" :state="state" :status-text="statusText" :target="activeSegmentTarget" @cancel="cancelDraft" @clear-route="clearRoute" @delete="deleteDraft" @draw-route="drawRoute" @reset="resetDraft" @save="saveDraft" />

    <ul v-if="segments.length > 0" :key="segmentListKey" ref="segmentList" class="trip-editor-segments">
      <li v-for="segment in segments" :key="segment.id" class="trip-editor-segment-row" :data-segment-id="segment.id">
        <button type="button" class="trip-editor-icon-button trip-editor-segment-drag-handle" title="Drag to reorder segment" aria-label="Drag to reorder segment" @keydown.arrow-up.prevent="moveSegmentByKeyboard(segment.id, -1)" @keydown.arrow-down.prevent="moveSegmentByKeyboard(segment.id, 1)">↕</button>
        <button type="button" class="trip-editor-icon-button" :title="hiddenSegmentIds.has(segment.id) ? 'Show segment' : 'Hide segment'" :aria-label="hiddenSegmentIds.has(segment.id) ? 'Show segment' : 'Hide segment'" @click="toggleVisibility(segment)">{{ hiddenSegmentIds.has(segment.id) ? '○' : '●' }}</button>
        <button type="button" class="trip-editor-list-button" @click="openEdit(segment)">
          <span>{{ segmentLabel(segment) }}</span>
          <small>{{ modeText(segment) }}</small>
        </button>
        <button type="button" class="trip-editor-icon-button" title="Delete segment" aria-label="Delete segment" @click="deleteSegmentFromRow(segment)">×</button>

        <SegmentEditorSurface v-if="draft.id === segment.id && editorSurface.isTargetActive(activeSegmentTarget)" :active-segment="activeSegment" :controller="editorSurface" :draft="draft" :field-errors="fieldErrors" :form-id="segmentFormId" :form-summary-errors="formSummaryErrors" :is-dirty="isDirty" :is-saving="isSaving" :state="state" :status-text="statusText" :target="activeSegmentTarget" @cancel="cancelDraft" @clear-route="clearRoute" @delete="deleteDraft" @draw-route="drawRoute" @reset="resetDraft" @save="saveDraft" />
      </li>
    </ul>
    <p v-else class="trip-editor-empty-state">No travel segments added yet.</p>
  </section>
</template>
