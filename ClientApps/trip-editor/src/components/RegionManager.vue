<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from 'vue';
import { createPlace, createRegion, deletePlace, deleteRegion, orderPlaces, orderRegions, updatePlace, updateRegion } from '../api/tripEditorApi';
import { EditorValidationError } from '../api/tripEditorApi';
import { confirm } from '../composables/useConfirmDialog';
import type { EditorArea, EditorMutationResult, EditorPlace, EditorPlaceSaveRequest, EditorRegion, EditorRegionSaveRequest, EditorTripState } from '../types';

declare global {
  interface Window {
    Sortable?: {
      create: (element: HTMLElement, options: Record<string, unknown>) => { destroy: () => void };
    };
  }
}

const props = defineProps<{
  state: EditorTripState;
  editorEndpoint: string;
  antiforgeryToken: string;
}>();

const emit = defineEmits<{
  mutationApplied: [result: EditorMutationResult<unknown>];
  dirtyStateChanged: [isDirty: boolean];
}>();

type RegionDraft = {
  id: string | null;
  name: string;
  notesHtml: string;
  coverImageRawUrl: string;
  centerLatitude: string | number;
  centerLongitude: string | number;
};

type PlaceDraft = {
  id: string | null;
  regionId: string | null;
  name: string;
  notesHtml: string;
  address: string;
  latitude: string | number;
  longitude: string | number;
  iconName: string;
  markerColor: string;
  reverseGeocode: boolean;
};

const regionFields = ['name', 'notesHtml', 'coverImage.rawUrl', 'center.latitude', 'center.longitude'];
const placeFields = ['regionId', 'name', 'notesHtml', 'address', 'location.latitude', 'location.longitude', 'iconName', 'markerColor', 'reverseGeocode'];
const regionList = ref<HTMLElement | null>(null);
const regionListKey = ref(0);
const draft = reactive<RegionDraft>(emptyDraft());
const placeDraft = reactive<PlaceDraft>(emptyPlaceDraft());
const isSaving = ref(false);
const isOrdering = ref(false);
const saveError = ref<string | null>(null);
const validationErrors = ref<Record<string, string[]>>({});
const lastSavedAt = ref<string | null>(null);
let sortable: { destroy: () => void } | null = null;
const placeSortables = new Map<string, { destroy: () => void }>();
let reorderSnapshotIds: string[] | null = null;
let placeReorderSnapshot: { regionId: string; ids: string[] } | null = null;

const orderedRegions = computed(() =>
  props.state.regionOrder
    .map(id => props.state.regionsById[id])
    .filter(region => region && (!region.isShadow || hasRegionChildren(region))) as EditorRegion[]
);
const normalRegionIds = computed(() => orderedRegions.value.filter(region => !region.isShadow).map(region => region.id));
const activeRegion = computed(() => (draft.id ? props.state.regionsById[draft.id] ?? null : null));
const activePlace = computed(() => (placeDraft.id ? props.state.placesById[placeDraft.id] ?? null : null));
const isDraftOpen = computed(() => draft.id !== null || Boolean(draft.name || draft.notesHtml || draft.coverImageRawUrl || draft.centerLatitude || draft.centerLongitude));
const isPlaceDraftOpen = computed(() => placeDraft.regionId !== null || Boolean(placeDraft.name || placeDraft.notesHtml || placeDraft.address || placeDraft.latitude || placeDraft.longitude));
const regionDirty = computed(() => JSON.stringify(buildRequest(draft)) !== JSON.stringify(buildRequest(toDraft(activeRegion.value))));
const placeDirty = computed(() => JSON.stringify(buildPlaceRequest(placeDraft)) !== JSON.stringify(buildPlaceRequest(toPlaceDraft(activePlace.value, placeDraft.regionId))));
const isDirty = computed(() => regionDirty.value || placeDirty.value);
const statusText = computed(() => {
  if (isSaving.value) {
    return 'Saving...';
  }

  if (isOrdering.value) {
    return 'Saving order...';
  }

  if (saveError.value) {
    return 'Save failed';
  }

  if (isDirty.value) {
    return 'Unsaved changes';
  }

  return lastSavedAt.value ? `Saved ${lastSavedAt.value}` : 'Saved';
});
const formSummaryErrors = computed(() =>
  Object.entries(validationErrors.value)
    .filter(([key]) => !(isPlaceDraftOpen.value ? placeFields : regionFields).includes(key))
    .flatMap(([, messages]) => messages)
);
const normalRegions = computed(() => orderedRegions.value.filter(region => !region.isShadow));

watch(
  () => `${props.state.regionOrder.join('|')}|${Object.entries(props.state.placeOrderByRegionId).map(([regionId, ids]) => `${regionId}:${ids.join(',')}`).join('|')}`,
  async () => {
    await nextTick();
    attachSortable();
    attachPlaceSortables();
  }
);

watch(
  isDirty,
  value => emit('dirtyStateChanged', value),
  { immediate: true }
);

onMounted(() => {
  attachSortable();
  attachPlaceSortables();
  window.addEventListener('beforeunload', confirmUnload);
});

onUnmounted(() => {
  sortable?.destroy();
  destroyPlaceSortables();
  emit('dirtyStateChanged', false);
  window.removeEventListener('beforeunload', confirmUnload);
});

const openCreate = async (): Promise<void> => {
  if (!(await confirmDiscard())) {
    return;
  }

  Object.assign(draft, emptyDraft());
  Object.assign(placeDraft, emptyPlaceDraft());
  draft.name = 'New Region';
  resetFeedback();
};

const openEdit = async (region: EditorRegion): Promise<void> => {
  if (!region.capabilities.canEdit || !(await confirmDiscard())) {
    return;
  }

  Object.assign(placeDraft, emptyPlaceDraft());
  Object.assign(draft, toDraft(region));
  resetFeedback();
};

const resetDraft = (): void => {
  Object.assign(draft, toDraft(activeRegion.value));
  resetFeedback();
};

const cancelDraft = async (): Promise<void> => {
  if (!(await confirmDiscard())) {
    return;
  }

  Object.assign(draft, emptyDraft());
  resetFeedback();
};

const saveDraft = async (): Promise<void> => {
  isSaving.value = true;
  resetFeedback();

  try {
    const request = buildRequest(draft);
    const result = draft.id
      ? await updateRegion(props.editorEndpoint, draft.id, props.antiforgeryToken, request)
      : await createRegion(props.editorEndpoint, props.antiforgeryToken, request);
    emit('mutationApplied', result as EditorMutationResult<unknown>);
    Object.assign(draft, toDraft(result.data));
    lastSavedAt.value = new Intl.DateTimeFormat(undefined, { timeStyle: 'short' }).format(new Date());
  } catch (error) {
    applyError(error, 'Region save failed.');
  } finally {
    isSaving.value = false;
  }
};

const deleteDraftRegion = async (): Promise<void> => {
  if (!activeRegion.value || !activeRegion.value.capabilities.canDelete) {
    return;
  }

  if (!(await confirmDiscard('Discard unsaved region or place draft changes before deleting?'))) {
    return;
  }

  if (!(await confirm({
    title: 'Delete region?',
    message: 'Delete this region, its child places and areas, and any segments connected to deleted places?',
    confirmLabel: 'Delete',
    cancelLabel: 'Keep region',
    variant: 'danger'
  }))) {
    return;
  }

  isSaving.value = true;
  resetFeedback();
  try {
    const result = await deleteRegion(props.editorEndpoint, activeRegion.value.id, props.antiforgeryToken);
    emit('mutationApplied', result as EditorMutationResult<unknown>);
    Object.assign(draft, emptyDraft());
    Object.assign(placeDraft, emptyPlaceDraft());
    lastSavedAt.value = new Intl.DateTimeFormat(undefined, { timeStyle: 'short' }).format(new Date());
  } catch (error) {
    applyError(error, 'Region delete failed.');
  } finally {
    isSaving.value = false;
  }
};

const onSortStart = (): void => {
  reorderSnapshotIds = [...normalRegionIds.value];
};

const onSortEnd = async (): Promise<void> => {
  if (!regionList.value) {
    return;
  }

  const previousIds = reorderSnapshotIds ?? [...normalRegionIds.value];
  reorderSnapshotIds = null;
  const ids = Array.from(regionList.value.querySelectorAll<HTMLElement>('[data-region-id][data-reorderable="true"]')).map(element => element.dataset.regionId!);
  if (ids.join('|') === previousIds.join('|')) {
    return;
  }

  if (isDirty.value && !(await confirmDiscard('Discard unsaved draft changes before reordering?'))) {
    await restoreRegionOrder(previousIds);
    return;
  }

  if (isDirty.value) {
    Object.assign(draft, emptyDraft());
    Object.assign(placeDraft, emptyPlaceDraft());
  }

  isOrdering.value = true;
  resetFeedback();
  try {
    const result = await orderRegions(props.editorEndpoint, props.antiforgeryToken, { regionIds: ids });
    emit('mutationApplied', result as EditorMutationResult<unknown>);
    Object.assign(draft, emptyDraft());
    lastSavedAt.value = new Intl.DateTimeFormat(undefined, { timeStyle: 'short' }).format(new Date());
  } catch (error) {
    applyError(error, 'Region reorder failed.');
    await restoreRegionOrder(previousIds);
  } finally {
    isOrdering.value = false;
  }
};

function attachSortable(): void {
  sortable?.destroy();
  sortable = null;
  if (!regionList.value || !window.Sortable) {
    if (!window.Sortable) {
      saveError.value = 'Region reorder is unavailable because SortableJS is not loaded.';
    }
    return;
  }

  sortable = window.Sortable.create(regionList.value, {
    animation: 150,
    draggable: '.trip-editor-region-card--normal',
    handle: '.trip-editor-drag-handle',
    onStart: onSortStart,
    onEnd: onSortEnd
  });
}

const openPlaceCreate = async (region: EditorRegion): Promise<void> => {
  if (region.isShadow || !(await confirmDiscard())) {
    return;
  }

  Object.assign(draft, emptyDraft());
  Object.assign(placeDraft, emptyPlaceDraft(region.id));
  placeDraft.name = 'New Place';
  placeDraft.iconName = props.state.options.iconNames[0] ?? 'marker';
  placeDraft.markerColor = props.state.options.markerColorClasses[0] ?? 'bg-blue';
  resetFeedback();
};

const openPlaceEdit = async (place: EditorPlace): Promise<void> => {
  if (!place.capabilities.canEdit || !(await confirmDiscard())) {
    return;
  }

  Object.assign(draft, emptyDraft());
  Object.assign(placeDraft, toPlaceDraft(place, place.regionId));
  resetFeedback();
};

const resetPlaceDraft = (): void => {
  Object.assign(placeDraft, toPlaceDraft(activePlace.value, placeDraft.regionId));
  resetFeedback();
};

const cancelPlaceDraft = async (): Promise<void> => {
  if (!(await confirmDiscard('Discard unsaved place changes?'))) {
    return;
  }

  Object.assign(placeDraft, emptyPlaceDraft());
  resetFeedback();
};

const savePlaceDraft = async (): Promise<void> => {
  if (!placeDraft.regionId) {
    return;
  }

  isSaving.value = true;
  resetFeedback();
  try {
    const request = buildPlaceRequest(placeDraft);
    const result = placeDraft.id
      ? await updatePlace(props.editorEndpoint, placeDraft.id, props.antiforgeryToken, request)
      : await createPlace(props.editorEndpoint, placeDraft.regionId, props.antiforgeryToken, withoutRegionId(request));
    emit('mutationApplied', result as EditorMutationResult<unknown>);
    Object.assign(placeDraft, toPlaceDraft(result.data, result.data.regionId));
    if (result.warnings.length > 0) {
      saveError.value = result.warnings.map(warning => warning.message).join(' ');
    }
    lastSavedAt.value = new Intl.DateTimeFormat(undefined, { timeStyle: 'short' }).format(new Date());
  } catch (error) {
    applyError(error, 'Place save failed.');
  } finally {
    isSaving.value = false;
  }
};

const deleteDraftPlace = async (): Promise<void> => {
  if (!activePlace.value) {
    return;
  }

  if (!(await confirmDiscard('Discard unsaved place draft changes before deleting?'))) {
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

  isSaving.value = true;
  resetFeedback();
  try {
    const result = await deletePlace(props.editorEndpoint, activePlace.value.id, props.antiforgeryToken);
    emit('mutationApplied', result as EditorMutationResult<unknown>);
    Object.assign(placeDraft, emptyPlaceDraft());
    lastSavedAt.value = new Intl.DateTimeFormat(undefined, { timeStyle: 'short' }).format(new Date());
  } catch (error) {
    applyError(error, 'Place delete failed.');
  } finally {
    isSaving.value = false;
  }
};

function attachPlaceSortables(): void {
  destroyPlaceSortables();
  if (!window.Sortable) {
    return;
  }

  document.querySelectorAll<HTMLElement>('[data-place-list-region-id]').forEach(element => {
    const regionId = element.dataset.placeListRegionId!;
    placeSortables.set(regionId, window.Sortable!.create(element, {
      animation: 150,
      draggable: '.trip-editor-place-row',
      handle: '.trip-editor-place-drag-handle',
      onStart: () => {
        placeReorderSnapshot = { regionId, ids: [...(props.state.placeOrderByRegionId[regionId] ?? [])] };
      },
      onEnd: () => orderPlaceList(regionId, element)
    }));
  });
}

function destroyPlaceSortables(): void {
  placeSortables.forEach(instance => instance.destroy());
  placeSortables.clear();
}

async function orderPlaceList(regionId: string, element: HTMLElement): Promise<void> {
  const previousIds = placeReorderSnapshot?.ids ?? [...(props.state.placeOrderByRegionId[regionId] ?? [])];
  placeReorderSnapshot = null;
  const ids = Array.from(element.querySelectorAll<HTMLElement>('[data-place-id]')).map(row => row.dataset.placeId!);
  if (ids.join('|') === previousIds.join('|')) {
    return;
  }

  if (isDirty.value && !(await confirmDiscard('Discard unsaved draft changes before reordering places?'))) {
    await restoreRegionOrder(previousIds);
    return;
  }

  if (isDirty.value) {
    Object.assign(draft, emptyDraft());
    Object.assign(placeDraft, emptyPlaceDraft());
  }

  isOrdering.value = true;
  resetFeedback();
  try {
    const result = await orderPlaces(props.editorEndpoint, regionId, props.antiforgeryToken, { placeIds: ids });
    emit('mutationApplied', result as EditorMutationResult<unknown>);
    lastSavedAt.value = new Intl.DateTimeFormat(undefined, { timeStyle: 'short' }).format(new Date());
  } catch (error) {
    applyError(error, 'Place reorder failed.');
    await restoreRegionOrder(previousIds);
  } finally {
    isOrdering.value = false;
  }
}

/// Rebuilds the Sortable-mutated list from persisted Vue state after canceled or failed reorder.
async function restoreRegionOrder(_previousIds: string[]): Promise<void> {
  regionListKey.value += 1;
  await nextTick();
  attachSortable();
}

function orderedPlaces(regionId: string): EditorPlace[] {
  return (props.state.placeOrderByRegionId[regionId] ?? []).map(id => props.state.placesById[id]).filter(Boolean) as EditorPlace[];
}

function orderedAreas(regionId: string): EditorArea[] {
  return (props.state.areaOrderByRegionId[regionId] ?? []).map(id => props.state.areasById[id]).filter(Boolean) as EditorArea[];
}

function hasRegionChildren(region: EditorRegion): boolean {
  return (props.state.placeOrderByRegionId[region.id]?.length ?? 0) > 0 || (props.state.areaOrderByRegionId[region.id]?.length ?? 0) > 0;
}

function emptyPlaceDraft(regionId: string | null = null): PlaceDraft {
  return { id: null, regionId, name: '', notesHtml: '', address: '', latitude: '', longitude: '', iconName: '', markerColor: '', reverseGeocode: false };
}

function toPlaceDraft(place: EditorPlace | null, fallbackRegionId: string | null): PlaceDraft {
  if (!place) {
    return emptyPlaceDraft(fallbackRegionId);
  }

  return {
    id: place.id,
    regionId: place.regionId,
    name: place.name,
    notesHtml: place.notesHtml,
    address: place.address,
    latitude: place.location ? String(place.location.latitude) : '',
    longitude: place.location ? String(place.location.longitude) : '',
    iconName: place.iconName,
    markerColor: place.markerColor,
    reverseGeocode: false
  };
}

function emptyDraft(): RegionDraft {
  return { id: null, name: '', notesHtml: '', coverImageRawUrl: '', centerLatitude: '', centerLongitude: '' };
}

function toDraft(region: EditorRegion | null): RegionDraft {
  if (!region) {
    return emptyDraft();
  }

  return {
    id: region.id,
    name: region.name,
    notesHtml: region.notesHtml,
    coverImageRawUrl: region.coverImage?.rawUrl ?? '',
    centerLatitude: region.center ? String(region.center.latitude) : '',
    centerLongitude: region.center ? String(region.center.longitude) : ''
  };
}

function buildRequest(value: RegionDraft): EditorRegionSaveRequest {
  const latitude = draftText(value.centerLatitude);
  const longitude = draftText(value.centerLongitude);
  const coverImageRawUrl = draftText(value.coverImageRawUrl);
  const hasPartialCenter = Boolean(latitude || longitude);

  return {
    name: value.name,
    notesHtml: value.notesHtml,
    coverImage: coverImageRawUrl ? { rawUrl: coverImageRawUrl } : null,
    center: hasPartialCenter ? { latitude: latitude ? Number(latitude) : Number.NaN, longitude: longitude ? Number(longitude) : Number.NaN } : null
  };
}

function buildPlaceRequest(value: PlaceDraft): EditorPlaceSaveRequest {
  const latitude = draftText(value.latitude);
  const longitude = draftText(value.longitude);
  const hasLocation = Boolean(latitude || longitude);
  return {
    regionId: value.regionId ?? undefined,
    name: value.name,
    notesHtml: value.notesHtml,
    address: value.address || null,
    location: hasLocation ? { latitude: latitude ? Number(latitude) : Number.NaN, longitude: longitude ? Number(longitude) : Number.NaN } : null,
    iconName: value.iconName,
    markerColor: value.markerColor,
    reverseGeocode: value.reverseGeocode
  };
}

function withoutRegionId(request: EditorPlaceSaveRequest): EditorPlaceSaveRequest {
  const { regionId: _regionId, ...createRequest } = request;
  return createRequest;
}

/// Normalizes Vue number-input values before validation and API serialization.
function draftText(value: string | number): string {
  return String(value ?? '').trim();
}

function confirmDiscard(message = 'Discard unsaved changes?'): Promise<boolean> {
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

function resetFeedback(): void {
  saveError.value = null;
  validationErrors.value = {};
}

function applyError(error: unknown, fallback: string): void {
  if (error instanceof EditorValidationError) {
    validationErrors.value = error.errors;
    saveError.value = error.message;
    return;
  }

  saveError.value = error instanceof Error ? error.message : fallback;
}

function confirmUnload(event: BeforeUnloadEvent): void {
  if (!isDirty.value) {
    return;
  }

  event.preventDefault();
  event.returnValue = '';
}

const fieldErrors = (key: string): string[] => validationErrors.value[key] ?? [];
</script>

<template>
  <section class="trip-editor-panel trip-editor-regions">
    <div class="trip-editor-panel__line">
      <h2>Regions &amp; Places</h2>
      <span class="trip-editor-save-state">{{ statusText }}</span>
    </div>

    <div v-if="saveError" class="trip-editor-form-error" role="alert">{{ saveError }}</div>

    <div :key="regionListKey" ref="regionList" class="trip-editor-region-list">
      <article
        v-for="region in orderedRegions"
        :key="region.id"
        class="trip-editor-region-card"
        :class="{ 'trip-editor-region-card--normal': !region.isShadow, 'trip-editor-region-card--shadow': region.isShadow }"
        :data-region-id="region.id"
        :data-reorderable="!region.isShadow"
      >
        <header class="trip-editor-region-card__header">
          <button
            v-if="!region.isShadow"
            type="button"
            class="trip-editor-icon-button trip-editor-drag-handle"
            title="Drag to reorder region"
            aria-label="Drag to reorder region"
          >
            <span aria-hidden="true">::</span>
          </button>
          <div>
            <h3>{{ region.name }}</h3>
            <small v-if="region.isShadow">Shadow region</small>
          </div>
          <button v-if="!region.isShadow" type="button" class="btn btn-outline-light btn-sm" :disabled="isSaving" @click="openEdit(region)">Edit</button>
        </header>

        <ul :data-place-list-region-id="region.id">
          <li v-for="place in orderedPlaces(region.id)" :key="place.id" class="trip-editor-place-row" :data-place-id="place.id">
            <button
              v-if="!region.isShadow"
              type="button"
              class="trip-editor-icon-button trip-editor-place-drag-handle"
              title="Drag to reorder place"
              aria-label="Drag to reorder place"
            >
              <span aria-hidden="true">::</span>
            </button>
            <span>{{ place.name }}</span>
            <small v-if="place.visitSummary.isVisited">{{ place.visitSummary.visitCount }} visit(s)</small>
            <button type="button" class="btn btn-outline-light btn-sm" :disabled="isSaving || isOrdering" @click="openPlaceEdit(place)">Edit</button>
          </li>
          <li v-for="area in orderedAreas(region.id)" :key="area.id">
            <span>{{ area.name }}</span>
            <small>Area</small>
          </li>
        </ul>
        <button v-if="!region.isShadow" type="button" class="btn btn-outline-light btn-sm" :disabled="isSaving || isOrdering" @click="openPlaceCreate(region)">
          Add Place
        </button>
      </article>
    </div>

    <button type="button" class="btn btn-primary btn-sm trip-editor-add-button" :disabled="isSaving || isOrdering" @click="openCreate">Add Region</button>

    <form v-if="isDraftOpen" class="trip-editor-region-form" @submit.prevent="saveDraft">
      <div class="trip-editor-panel__line">
        <h3>{{ draft.id ? 'Edit Region' : 'Add Region' }}</h3>
        <span class="trip-editor-save-state">{{ statusText }}</span>
      </div>

      <div v-if="formSummaryErrors.length > 0" class="trip-editor-form-error" role="alert">
        <p v-for="message in formSummaryErrors" :key="message">{{ message }}</p>
      </div>

      <label class="trip-editor-field">
        <span>Name</span>
        <input v-model="draft.name" type="text" autocomplete="off" />
        <small v-for="message in fieldErrors('name')" :key="message">{{ message }}</small>
      </label>

      <label class="trip-editor-field">
        <span>Notes HTML</span>
        <textarea v-model="draft.notesHtml" rows="6"></textarea>
        <small v-for="message in fieldErrors('notesHtml')" :key="message">{{ message }}</small>
      </label>

      <label class="trip-editor-field">
        <span>Cover Image URL</span>
        <input v-model="draft.coverImageRawUrl" type="url" autocomplete="off" />
        <small v-for="message in fieldErrors('coverImage.rawUrl')" :key="message">{{ message }}</small>
      </label>

      <div class="trip-editor-grid">
        <label class="trip-editor-field">
          <span>Center Latitude</span>
          <input v-model="draft.centerLatitude" type="number" step="any" />
          <small v-for="message in fieldErrors('center.latitude')" :key="message">{{ message }}</small>
        </label>
        <label class="trip-editor-field">
          <span>Center Longitude</span>
          <input v-model="draft.centerLongitude" type="number" step="any" />
          <small v-for="message in fieldErrors('center.longitude')" :key="message">{{ message }}</small>
        </label>
      </div>

      <div class="trip-editor-actions">
        <button type="submit" class="btn btn-primary btn-sm" :disabled="isSaving">Save Region</button>
        <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="isSaving || !isDirty" @click="resetDraft">Reset</button>
        <button type="button" class="btn btn-outline-light btn-sm" :disabled="isSaving" @click="cancelDraft">Cancel</button>
        <button v-if="activeRegion?.capabilities.canDelete" type="button" class="btn btn-outline-danger btn-sm" :disabled="isSaving" @click="deleteDraftRegion">
          Delete
        </button>
      </div>
    </form>

    <form v-if="isPlaceDraftOpen" class="trip-editor-region-form" @submit.prevent="savePlaceDraft">
      <div class="trip-editor-panel__line">
        <h3>{{ placeDraft.id ? 'Edit Place' : 'Add Place' }}</h3>
        <span class="trip-editor-save-state">{{ statusText }}</span>
      </div>

      <div v-if="formSummaryErrors.length > 0" class="trip-editor-form-error" role="alert">
        <p v-for="message in formSummaryErrors" :key="message">{{ message }}</p>
      </div>

      <label class="trip-editor-field">
        <span>Region</span>
        <select v-model="placeDraft.regionId">
          <option v-for="region in normalRegions" :key="region.id" :value="region.id">{{ region.name }}</option>
        </select>
        <small v-for="message in fieldErrors('regionId')" :key="message">{{ message }}</small>
      </label>

      <label class="trip-editor-field">
        <span>Name</span>
        <input v-model="placeDraft.name" type="text" autocomplete="off" />
        <small v-for="message in fieldErrors('name')" :key="message">{{ message }}</small>
      </label>

      <label class="trip-editor-field">
        <span>Notes HTML</span>
        <textarea v-model="placeDraft.notesHtml" rows="6"></textarea>
        <small v-for="message in fieldErrors('notesHtml')" :key="message">{{ message }}</small>
      </label>

      <label class="trip-editor-field">
        <span>Address</span>
        <input v-model="placeDraft.address" type="text" autocomplete="off" />
        <small v-for="message in fieldErrors('address')" :key="message">{{ message }}</small>
      </label>

      <div class="trip-editor-grid">
        <label class="trip-editor-field">
          <span>Latitude</span>
          <input v-model="placeDraft.latitude" type="number" step="any" />
          <small v-for="message in fieldErrors('location.latitude')" :key="message">{{ message }}</small>
        </label>
        <label class="trip-editor-field">
          <span>Longitude</span>
          <input v-model="placeDraft.longitude" type="number" step="any" />
          <small v-for="message in fieldErrors('location.longitude')" :key="message">{{ message }}</small>
        </label>
      </div>

      <div class="trip-editor-grid">
        <label class="trip-editor-field">
          <span>Icon</span>
          <select v-model="placeDraft.iconName">
            <option v-for="icon in state.options.iconNames" :key="icon" :value="icon">{{ icon }}</option>
          </select>
          <small v-for="message in fieldErrors('iconName')" :key="message">{{ message }}</small>
        </label>
        <label class="trip-editor-field">
          <span>Marker Color</span>
          <select v-model="placeDraft.markerColor">
            <option v-for="color in state.options.markerColorClasses" :key="color" :value="color">{{ color }}</option>
          </select>
          <small v-for="message in fieldErrors('markerColor')" :key="message">{{ message }}</small>
        </label>
      </div>

      <label class="trip-editor-check">
        <input v-model="placeDraft.reverseGeocode" type="checkbox" />
        <span>Reverse geocode this location on save</span>
      </label>
      <small v-for="message in fieldErrors('reverseGeocode')" :key="message">{{ message }}</small>

      <div class="trip-editor-actions">
        <button type="submit" class="btn btn-primary btn-sm" :disabled="isSaving">Save Place</button>
        <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="isSaving || !placeDirty" @click="resetPlaceDraft">Reset</button>
        <button type="button" class="btn btn-outline-light btn-sm" :disabled="isSaving" @click="cancelPlaceDraft">Cancel</button>
        <button v-if="activePlace?.capabilities.canDelete" type="button" class="btn btn-outline-danger btn-sm" :disabled="isSaving" @click="deleteDraftPlace">
          Delete
        </button>
      </div>
    </form>
  </section>
</template>
