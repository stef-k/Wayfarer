<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from 'vue';
import { createRegion, deleteRegion, orderRegions, updateRegion } from '../api/tripEditorApi';
import { EditorValidationError } from '../api/tripEditorApi';
import type { EditorArea, EditorMutationResult, EditorPlace, EditorRegion, EditorRegionSaveRequest, EditorTripState } from '../types';

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
}>();

type RegionDraft = {
  id: string | null;
  name: string;
  notesHtml: string;
  coverImageRawUrl: string;
  centerLatitude: string;
  centerLongitude: string;
};

const fields = ['name', 'notesHtml', 'coverImage.rawUrl', 'center.latitude', 'center.longitude'];
const regionList = ref<HTMLElement | null>(null);
const draft = reactive<RegionDraft>(emptyDraft());
const isSaving = ref(false);
const isOrdering = ref(false);
const saveError = ref<string | null>(null);
const validationErrors = ref<Record<string, string[]>>({});
const lastSavedAt = ref<string | null>(null);
let sortable: { destroy: () => void } | null = null;

const orderedRegions = computed(() =>
  props.state.regionOrder
    .map(id => props.state.regionsById[id])
    .filter(region => region && (!region.isShadow || hasRegionChildren(region))) as EditorRegion[]
);
const normalRegionIds = computed(() => orderedRegions.value.filter(region => !region.isShadow).map(region => region.id));
const activeRegion = computed(() => (draft.id ? props.state.regionsById[draft.id] ?? null : null));
const isDraftOpen = computed(() => draft.id !== null || Boolean(draft.name || draft.notesHtml || draft.coverImageRawUrl || draft.centerLatitude || draft.centerLongitude));
const isDirty = computed(() => JSON.stringify(buildRequest(draft)) !== JSON.stringify(buildRequest(toDraft(activeRegion.value))));
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
    .filter(([key]) => !fields.includes(key))
    .flatMap(([, messages]) => messages)
);

watch(
  () => props.state.regionOrder.join('|'),
  async () => {
    await nextTick();
    attachSortable();
  }
);

onMounted(() => {
  attachSortable();
  window.addEventListener('beforeunload', confirmUnload);
});

onUnmounted(() => {
  sortable?.destroy();
  window.removeEventListener('beforeunload', confirmUnload);
});

const openCreate = (): void => {
  if (!confirmDiscard()) {
    return;
  }

  Object.assign(draft, emptyDraft());
  draft.name = 'New Region';
  resetFeedback();
};

const openEdit = (region: EditorRegion): void => {
  if (!region.capabilities.canEdit || !confirmDiscard()) {
    return;
  }

  Object.assign(draft, toDraft(region));
  resetFeedback();
};

const resetDraft = (): void => {
  Object.assign(draft, toDraft(activeRegion.value));
  resetFeedback();
};

const cancelDraft = (): void => {
  if (!confirmDiscard()) {
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

  if (!window.confirm('Delete this region, its child places and areas, and any segments connected to deleted places?')) {
    return;
  }

  isSaving.value = true;
  resetFeedback();
  try {
    const result = await deleteRegion(props.editorEndpoint, activeRegion.value.id, props.antiforgeryToken);
    emit('mutationApplied', result as EditorMutationResult<unknown>);
    Object.assign(draft, emptyDraft());
    lastSavedAt.value = new Intl.DateTimeFormat(undefined, { timeStyle: 'short' }).format(new Date());
  } catch (error) {
    applyError(error, 'Region delete failed.');
  } finally {
    isSaving.value = false;
  }
};

const onSortEnd = async (): Promise<void> => {
  if (!regionList.value) {
    return;
  }

  const ids = Array.from(regionList.value.querySelectorAll<HTMLElement>('[data-region-id][data-reorderable="true"]')).map(element => element.dataset.regionId!);
  if (ids.join('|') === normalRegionIds.value.join('|')) {
    return;
  }

  if (isDirty.value && !window.confirm('Discard unsaved region draft changes before reordering?')) {
    await nextTick();
    attachSortable();
    return;
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
    onEnd: onSortEnd
  });
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
  const latitude = value.centerLatitude.trim();
  const longitude = value.centerLongitude.trim();
  const coverImageRawUrl = value.coverImageRawUrl.trim();
  const hasPartialCenter = Boolean(latitude || longitude);

  return {
    name: value.name,
    notesHtml: value.notesHtml,
    coverImage: coverImageRawUrl ? { rawUrl: coverImageRawUrl } : null,
    center: hasPartialCenter ? { latitude: latitude ? Number(latitude) : Number.NaN, longitude: longitude ? Number(longitude) : Number.NaN } : null
  };
}

function confirmDiscard(): boolean {
  return !isDirty.value || window.confirm('Discard unsaved region changes?');
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

    <div ref="regionList" class="trip-editor-region-list">
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

        <ul>
          <li v-for="place in orderedPlaces(region.id)" :key="place.id">
            <span>{{ place.name }}</span>
            <small v-if="place.visitSummary.isVisited">{{ place.visitSummary.visitCount }} visit(s)</small>
          </li>
          <li v-for="area in orderedAreas(region.id)" :key="area.id">
            <span>{{ area.name }}</span>
            <small>Area</small>
          </li>
        </ul>
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
  </section>
</template>
