<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, watch } from 'vue';
import { createPlace, createRegion, deletePlace, deleteRegion, orderPlaces, orderRegions, updatePlace, updateRegion } from '../api/tripEditorApi';
import { confirm } from '../composables/useConfirmDialog';
import type { EditorSurfaceController, EditorTarget } from '../composables/useEditorSurface';
import EditorSurface from './EditorSurface.vue';
import PlaceEditorForm from './PlaceEditorForm.vue';
import RegionEditorForm from './RegionEditorForm.vue';
import RegionPlaceList from './RegionPlaceList.vue';
import { buildPlaceRequest, buildRegionRequest, emptyPlaceDraft, emptyRegionDraft, toPlaceDraft, toRegionDraft, withoutRegionId } from './regionPlaceDrafts';
import { useEditorMutationFeedback } from './useEditorMutationFeedback';
import type { EditorMutationResult, EditorPlace, EditorPlaceDraft, EditorRegion, EditorTripState, Guid } from '../types';

const props = defineProps<{
  state: EditorTripState;
  editorSurface: EditorSurfaceController;
  editorEndpoint: string;
  antiforgeryToken: string;
}>();

const emit = defineEmits<{
  mutationApplied: [result: EditorMutationResult<unknown>];
  dirtyStateChanged: [isDirty: boolean];
}>();

const regionFields = ['name', 'notesHtml', 'coverImage.rawUrl', 'center.latitude', 'center.longitude'];
const placeFields = ['regionId', 'name', 'notesHtml', 'address', 'location.latitude', 'location.longitude', 'iconName', 'markerColor', 'reverseGeocode'];
const regionListKey = ref(0);
const regionFormId = 'trip-editor-region-form';
const placeFormId = 'trip-editor-place-form';
const draft = reactive(emptyRegionDraft());
const placeDraft = reactive<EditorPlaceDraft>(emptyPlaceDraft());
const isSaving = ref(false);
const isOrdering = ref(false);
let unregisterRegionHandler: (() => void) | null = null;
let unregisterPlaceHandler: (() => void) | null = null;

const orderedRegions = computed(() => props.state.regionOrder.map(id => props.state.regionsById[id]).filter(region => region && (!region.isShadow || hasRegionChildren(region))) as EditorRegion[]);
const activeRegion = computed(() => (draft.id ? props.state.regionsById[draft.id] ?? null : null));
const activePlace = computed(() => (placeDraft.id ? props.state.placesById[placeDraft.id] ?? null : null));
const isDraftOpen = computed(() => draft.id !== null || Boolean(draft.name || draft.notesHtml || draft.coverImageRawUrl || draft.centerLatitude || draft.centerLongitude));
const isPlaceDraftOpen = computed(() => placeDraft.regionId !== null || Boolean(placeDraft.name || placeDraft.notesHtml || placeDraft.address || placeDraft.latitude || placeDraft.longitude));
const regionDirty = computed(() => JSON.stringify(buildRegionRequest(draft)) !== JSON.stringify(buildRegionRequest(toRegionDraft(activeRegion.value))));
const placeDirty = computed(() => JSON.stringify(buildPlaceRequest(placeDraft)) !== JSON.stringify(buildPlaceRequest(toPlaceDraft(activePlace.value, placeDraft.regionId))));
const isDirty = computed(() => regionDirty.value || placeDirty.value);
const normalRegions = computed(() => orderedRegions.value.filter(region => !region.isShadow));
const activeRegionTarget = computed<EditorTarget>(() => ({
  key: 'region-draft',
  kind: 'region',
  mode: draft.id ? 'edit' : 'add',
  title: draft.id ? `Edit Region - ${activeRegion.value?.name ?? draft.name}` : 'Add Region',
  subtitle: draft.id ? 'Region details' : 'New region'
}));
const activePlaceTarget = computed<EditorTarget>(() => ({
  key: 'place-draft',
  kind: 'place',
  mode: placeDraft.id ? 'edit' : 'add',
  title: placeDraft.id ? `Edit Place - ${activePlace.value?.name ?? placeDraft.name}` : 'Add Place',
  subtitle: placeDraft.regionId ? props.state.regionsById[placeDraft.regionId]?.name : undefined
}));
const { applyError, fieldErrors, formSummaryErrors, markSaved, resetFeedback, saveError, saveWarning, statusText } = useEditorMutationFeedback({
  isDirty,
  isOrdering: computed(() => isOrdering.value),
  isPlaceDraftOpen,
  isSaving: computed(() => isSaving.value),
  placeFields,
  regionFields
});

watch(
  isDirty,
  value => emit('dirtyStateChanged', value),
  { immediate: true }
);

onMounted(() => {
  window.addEventListener('beforeunload', confirmUnload);
  unregisterRegionHandler = props.editorSurface.registerTargetHandler('region-draft', {
    isDirty: () => regionDirty.value,
    discard: discardRegionDraft
  });
  unregisterPlaceHandler = props.editorSurface.registerTargetHandler('place-draft', {
    isDirty: () => placeDirty.value,
    discard: discardPlaceDraft
  });
});

onUnmounted(() => {
  unregisterRegionHandler?.();
  unregisterPlaceHandler?.();
  emit('dirtyStateChanged', false);
  window.removeEventListener('beforeunload', confirmUnload);
});

const openCreate = async (): Promise<void> => {
  if (!(await props.editorSurface.activateTarget({ key: 'region-draft', kind: 'region', mode: 'add', title: 'Add Region', subtitle: 'New region' }))) {
    return;
  }

  Object.assign(draft, emptyRegionDraft());
  Object.assign(placeDraft, emptyPlaceDraft());
  draft.name = 'New Region';
  resetFeedback();
};

const openEdit = async (region: EditorRegion): Promise<void> => {
  if (!region.capabilities.canEdit || !(await props.editorSurface.activateTarget({ key: 'region-draft', kind: 'region', mode: 'edit', title: `Edit Region - ${region.name}`, subtitle: 'Region details' }))) {
    return;
  }

  Object.assign(placeDraft, emptyPlaceDraft());
  Object.assign(draft, toRegionDraft(region));
  resetFeedback();
};

const resetDraft = (): void => {
  Object.assign(draft, toRegionDraft(activeRegion.value));
  resetFeedback();
};

const cancelDraft = async (): Promise<void> => {
  await props.editorSurface.closeActiveTarget('Discard unsaved region changes?');
};

const saveDraft = async (): Promise<void> => {
  isSaving.value = true;
  resetFeedback();

  try {
    const request = buildRegionRequest(draft);
    const result = draft.id
      ? await updateRegion(props.editorEndpoint, draft.id, props.antiforgeryToken, request)
      : await createRegion(props.editorEndpoint, props.antiforgeryToken, request);
    emit('mutationApplied', result as EditorMutationResult<unknown>);
    Object.assign(draft, toRegionDraft(result.data));
    await props.editorSurface.activateTarget(activeRegionTarget.value);
    markSaved();
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
    Object.assign(draft, emptyRegionDraft());
    Object.assign(placeDraft, emptyPlaceDraft());
    markSaved();
  } catch (error) {
    applyError(error, 'Region delete failed.');
  } finally {
    isSaving.value = false;
  }
};

const reorderRegions = async (ids: Guid[], previousIds: Guid[]): Promise<void> => {
  if (isDirty.value && !(await confirmDiscard('Discard unsaved draft changes before reordering?'))) {
    await restoreRegionOrder(previousIds);
    return;
  }

  if (isDirty.value) {
    Object.assign(draft, emptyRegionDraft());
    Object.assign(placeDraft, emptyPlaceDraft());
  }

  isOrdering.value = true;
  resetFeedback();
  try {
    const result = await orderRegions(props.editorEndpoint, props.antiforgeryToken, { regionIds: ids });
    emit('mutationApplied', result as EditorMutationResult<unknown>);
    Object.assign(draft, emptyRegionDraft());
    markSaved();
  } catch (error) {
    applyError(error, 'Region reorder failed.');
    await restoreRegionOrder(previousIds);
  } finally {
    isOrdering.value = false;
  }
};

const openPlaceCreate = async (region: EditorRegion): Promise<void> => {
  if (region.isShadow || !(await props.editorSurface.activateTarget({ key: 'place-draft', kind: 'place', mode: 'add', title: 'Add Place', subtitle: region.name }))) {
    return;
  }

  Object.assign(draft, emptyRegionDraft());
  Object.assign(placeDraft, emptyPlaceDraft(region.id));
  placeDraft.name = 'New Place';
  placeDraft.iconName = props.state.options.iconNames[0] ?? 'marker';
  placeDraft.markerColor = props.state.options.markerColorClasses[0] ?? 'bg-blue';
  resetFeedback();
};

const openPlaceEdit = async (place: EditorPlace): Promise<void> => {
  if (!place.capabilities.canEdit || !(await props.editorSurface.activateTarget({ key: 'place-draft', kind: 'place', mode: 'edit', title: `Edit Place - ${place.name}`, subtitle: props.state.regionsById[place.regionId]?.name }))) {
    return;
  }

  Object.assign(draft, emptyRegionDraft());
  Object.assign(placeDraft, toPlaceDraft(place, place.regionId));
  resetFeedback();
};

const resetPlaceDraft = (): void => {
  Object.assign(placeDraft, toPlaceDraft(activePlace.value, placeDraft.regionId));
  resetFeedback();
};

const cancelPlaceDraft = async (): Promise<void> => {
  await props.editorSurface.closeActiveTarget('Discard unsaved place changes?');
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
    await props.editorSurface.activateTarget(activePlaceTarget.value);
    markSaved(result.warnings.map(warning => warning.message));
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
    markSaved();
  } catch (error) {
    applyError(error, 'Place delete failed.');
  } finally {
    isSaving.value = false;
  }
};

async function reorderPlaces(regionId: Guid, ids: Guid[], previousIds: Guid[]): Promise<void> {
  if (isDirty.value && !(await confirmDiscard('Discard unsaved draft changes before reordering places?'))) {
    await restoreRegionOrder(previousIds);
    return;
  }

  if (isDirty.value) {
    Object.assign(draft, emptyRegionDraft());
    Object.assign(placeDraft, emptyPlaceDraft());
  }

  isOrdering.value = true;
  resetFeedback();
  try {
    const result = await orderPlaces(props.editorEndpoint, regionId, props.antiforgeryToken, { placeIds: ids });
    emit('mutationApplied', result as EditorMutationResult<unknown>);
    markSaved();
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
}

function hasRegionChildren(region: EditorRegion): boolean {
  return (props.state.placeOrderByRegionId[region.id]?.length ?? 0) > 0 || (props.state.areaOrderByRegionId[region.id]?.length ?? 0) > 0;
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

function confirmUnload(event: BeforeUnloadEvent): void {
  if (!isDirty.value) {
    return;
  }

  event.preventDefault();
  event.returnValue = '';
}

function discardRegionDraft(): void {
  Object.assign(draft, emptyRegionDraft());
  resetFeedback();
}

function discardPlaceDraft(): void {
  Object.assign(placeDraft, emptyPlaceDraft());
  resetFeedback();
}
</script>

<template>
  <section class="trip-editor-panel trip-editor-regions">
    <div class="trip-editor-panel__line">
      <h2>Regions &amp; Places</h2>
      <span class="trip-editor-save-state">{{ statusText }}</span>
    </div>

    <div v-if="saveError" class="trip-editor-form-error" role="alert">{{ saveError }}</div>
    <div v-if="saveWarning" class="trip-editor-form-warning" role="status">{{ saveWarning }}</div>

    <RegionPlaceList
      :key="regionListKey"
      :active-place-id="activePlace?.id ?? null"
      :active-region-id="activeRegion?.id ?? null"
      :is-ordering="isOrdering"
      :is-saving="isSaving"
      :regions="orderedRegions"
      :state="state"
      @add-place="openPlaceCreate"
      @edit-place="openPlaceEdit"
      @edit-region="openEdit"
      @place-reorder="reorderPlaces"
      @region-reorder="reorderRegions"
    />

    <button type="button" class="btn btn-primary btn-sm trip-editor-add-button" :disabled="isSaving || isOrdering" @click="openCreate">Add Region</button>

    <EditorSurface v-if="isDraftOpen" :controller="editorSurface" :target="activeRegionTarget" :status-text="statusText">
      <template #body>
        <RegionEditorForm
          :active-region="activeRegion"
          :draft="draft"
          :field-errors="fieldErrors"
          :form-id="regionFormId"
          :form-summary-errors="formSummaryErrors"
          :is-saving="isSaving"
          @save="saveDraft"
        />
      </template>

      <template #footer>
        <button v-if="activeRegion?.capabilities.canDelete" type="button" class="btn btn-outline-danger btn-sm me-auto" :disabled="isSaving" @click="deleteDraftRegion">
          Delete
        </button>
        <button type="button" class="btn btn-outline-light btn-sm" :disabled="isSaving" @click="cancelDraft">Cancel</button>
        <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="isSaving || !regionDirty" @click="resetDraft">Reset</button>
        <button type="submit" :form="regionFormId" class="btn btn-primary btn-sm" :disabled="isSaving">Save Region</button>
      </template>
    </EditorSurface>

    <EditorSurface v-if="isPlaceDraftOpen" :controller="editorSurface" :target="activePlaceTarget" :status-text="statusText">
      <template #body>
        <PlaceEditorForm
          :active-place="activePlace"
          :draft="placeDraft"
          :field-errors="fieldErrors"
          :form-id="placeFormId"
          :form-summary-errors="formSummaryErrors"
          :is-saving="isSaving"
          :normal-regions="normalRegions"
          :state="state"
          @save="savePlaceDraft"
        />
      </template>

      <template #footer>
        <button v-if="activePlace?.capabilities.canDelete" type="button" class="btn btn-outline-danger btn-sm me-auto" :disabled="isSaving" @click="deleteDraftPlace">
          Delete
        </button>
        <button type="button" class="btn btn-outline-light btn-sm" :disabled="isSaving" @click="cancelPlaceDraft">Cancel</button>
        <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="isSaving || !placeDirty" @click="resetPlaceDraft">Reset</button>
        <button type="submit" :form="placeFormId" class="btn btn-primary btn-sm" :disabled="isSaving">Save Place</button>
      </template>
    </EditorSurface>
  </section>
</template>
