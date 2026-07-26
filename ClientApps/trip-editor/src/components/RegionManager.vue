<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, watch } from 'vue';
import { confirm } from '../composables/useConfirmDialog';
import type { EditorSurfaceController, EditorTarget } from '../composables/useEditorSurface';
import PlaceEditorSurface from './PlaceEditorSurface.vue';
import AreaEditorSurface from './AreaEditorSurface.vue';
import RegionEditorSurface from './RegionEditorSurface.vue';
import RegionPlaceList from './RegionPlaceList.vue';
import { areaDraftKey, placeDraftKey, regionDraftKey } from './regionPlaceEditorTargets';
import { buildAreaRequest, buildPlaceRequest, buildRegionRequest, emptyAreaDraft, emptyPlaceDraft, emptyRegionDraft, toAreaDraft, toPlaceDraft, toRegionDraft } from './regionPlaceDrafts';
import { stopAreaPolygonEdit, type AreaPolygonEditor, type AreaPolygonMapWorkState } from './areaPolygonMapWork';
import { useAreaEditorActions } from './areaEditorActions';
import { usePlaceEditorActions } from './placeEditorActions';
import { useRegionEditorActions } from './regionEditorActions';
import { stopPlaceCoordinatePick, type PlaceCoordinateMapWorkState, type PlaceCoordinatePicker } from './placeCoordinateMapWork';
import { mutationFeedbackClass, useEditorMutationFeedback } from './useEditorMutationFeedback';
import type { EditorArea, EditorAreaDraft, EditorAreaSaveRequest, EditorGeocodeSearchResult, EditorMutationResult, EditorPlace, EditorPlaceDraft, EditorPlaceSaveRequest, EditorRegion, EditorRegionSaveRequest, EditorTripState, Guid } from '../types';

type PlaceDraftPreview = {
  coordinate: { latitude: number; longitude: number } | null;
  iconName: string;
  markerColor: string;
  label: string;
  placeId: Guid | null;
};

const props = defineProps<{
  state: EditorTripState;
  editorSurface: EditorSurfaceController;
  editorEndpoint: string;
  antiforgeryToken: string;
  searchActive: boolean;
  selectedPlaceId: Guid | null;
  searchRegions: EditorRegion[];
  searchPlaceIdsByRegionId: Record<Guid, Guid[]>;
  searchAreaIdsByRegionId: Record<Guid, Guid[]>;
  pendingSearchAdd: { result: EditorGeocodeSearchResult; regionId: Guid; requestId: number } | null;
  coordinatePicker: PlaceCoordinatePicker;
  polygonEditor: AreaPolygonEditor;
  selectPlace: (placeId: Guid) => Promise<boolean>;
  clearSelectedPlace: () => Promise<boolean>;
}>();

const emit = defineEmits<{
  mutationApplied: [result: EditorMutationResult<unknown>];
  dirtyStateChanged: [isDirty: boolean];
  placeDraftPreviewChanged: [preview: PlaceDraftPreview | null];
  searchAddOpened: [requestId: number];
}>();

const regionFields = ['name', 'notesHtml', 'coverImage.rawUrl', 'center.latitude', 'center.longitude'];
const placeFields = ['regionId', 'name', 'notesHtml', 'address', 'location.latitude', 'location.longitude', 'iconName', 'markerColor', 'reverseGeocode'];
const areaFields = ['name', 'notesHtml', 'fillHex', 'geometry', 'geometry.coordinates'];
const regionListKey = ref(0);
const regionFormId = 'trip-editor-region-form';
const placeFormId = 'trip-editor-place-form';
const areaFormId = 'trip-editor-area-form';
const draft = reactive(emptyRegionDraft());
const placeDraft = reactive<EditorPlaceDraft>(emptyPlaceDraft());
const areaDraft = reactive<EditorAreaDraft>(emptyAreaDraft());
const isSaving = ref(false);
const isOrdering = ref(false);
const regionCreateBaselineRequest = ref<EditorRegionSaveRequest | null>(null);
const placeCreateBaselineRequest = ref<EditorPlaceSaveRequest | null>(null);
const placeCreateBaselineDraft = ref<EditorPlaceDraft | null>(null);
const placeEditBaselineRequest = ref<EditorPlaceSaveRequest | null>(null);
const areaCreateBaselineRequest = ref<EditorAreaSaveRequest | null>(null);
let unregisterRegionHandler: (() => void) | null = null;
let unregisterPlaceHandler: (() => void) | null = null;
let unregisterAreaHandler: (() => void) | null = null;
const placeCoordinateMapWork = reactive<PlaceCoordinateMapWorkState>({ coordinate: null, stopPick: null });
const areaPolygonMapWork = reactive<AreaPolygonMapWorkState>({ geometry: null, stopEdit: null });

const orderedRegions = computed(() => props.state.regionOrder.map(id => props.state.regionsById[id]).filter(region => region && (!region.isShadow || hasRegionChildren(region))) as EditorRegion[]);
const activeRegion = computed(() => (draft.id ? props.state.regionsById[draft.id] ?? null : null));
const activePlace = computed(() => (placeDraft.id ? props.state.placesById[placeDraft.id] ?? null : null));
const activeArea = computed(() => (areaDraft.id ? props.state.areasById[areaDraft.id] ?? null : null));
const activePlaceEditorId = computed(() => (activePlace.value && isPlaceEditOpen(activePlace.value) ? activePlace.value.id : null));
const isDraftOpen = computed(() => draft.id !== null || Boolean(draft.name || draft.notesHtml || draft.coverImageRawUrl || draft.centerLatitude || draft.centerLongitude));
const isPlaceDraftOpen = computed(() => placeDraft.regionId !== null || Boolean(placeDraft.name || placeDraft.notesHtml || placeDraft.address || placeDraft.latitude || placeDraft.longitude));
const isAreaDraftOpen = computed(() => areaDraft.regionId !== null || Boolean(areaDraft.name || areaDraft.notesHtml || areaDraft.geometry));
const regionDirty = computed(() => JSON.stringify(buildRegionRequest(draft)) !== JSON.stringify(regionBaselineRequest.value));
const placeDirty = computed(() => JSON.stringify(buildPlaceRequest(placeDraft)) !== JSON.stringify(placeBaselineRequest.value));
const areaDirty = computed(() => JSON.stringify(buildAreaRequest(areaDraft)) !== JSON.stringify(areaBaselineRequest.value));
const isDirty = computed(() => regionDirty.value || placeDirty.value || areaDirty.value);
const normalRegions = computed(() => orderedRegions.value.filter(region => !region.isShadow));
const unassignedPlacesRegion = computed(() => props.state.regionOrder.map(id => props.state.regionsById[id]).find(region => region?.isShadow && region.name === 'Unassigned Places') ?? null);
const placeTargetRegions = computed(() => {
  const regions = [...normalRegions.value];
  if (unassignedPlacesRegion.value && !regions.some(region => region.id === unassignedPlacesRegion.value?.id)) {
    regions.push(unassignedPlacesRegion.value);
  }

  const activeCreateRegionId = !placeDraft.id ? placeDraft.regionId : null;
  const activeCreateRegion = activeCreateRegionId ? props.state.regionsById[activeCreateRegionId] : null;
  if (activeCreateRegion?.isShadow && !regions.some(region => region.id === activeCreateRegion.id)) {
    regions.push(activeCreateRegion);
  }

  return regions;
});
const renderedRegions = computed(() => {
  const regions = props.searchActive ? [...props.searchRegions] : [...orderedRegions.value];
  const included = new Set(regions.map(region => region.id));

  for (const region of activeContextRegions()) {
    if (!included.has(region.id)) {
      regions.push(region);
      included.add(region.id);
    }
  }

  return regions;
});
const renderedPlaceIdsByRegionId = computed(() => {
  if (!props.searchActive) {
    return props.state.placeOrderByRegionId;
  }

  const result = cloneIdRecord(props.searchPlaceIdsByRegionId);
  if (activePlace.value) {
    pushUnique(result, activePlace.value.regionId, activePlace.value.id);
  }
  if (props.selectedPlaceId) {
    const selectedPlace = props.state.placesById[props.selectedPlaceId];
    if (selectedPlace) {
      pushUnique(result, selectedPlace.regionId, selectedPlace.id);
    }
  }

  return result;
});
const renderedAreaIdsByRegionId = computed(() => {
  if (!props.searchActive) {
    return props.state.areaOrderByRegionId;
  }

  const result = cloneIdRecord(props.searchAreaIdsByRegionId);
  if (activeArea.value) {
    pushUnique(result, activeArea.value.regionId, activeArea.value.id);
  }

  return result;
});
const placePreview = computed<PlaceDraftPreview | null>(() => {
  if (placeDraft.id && activePlace.value && isPlaceEditOpen(activePlace.value)) {
    return {
      coordinate: parseDraftCoordinate(),
      placeId: placeDraft.id,
      label: placeDraft.name || activePlace.value.name,
      iconName: placeDraft.iconName || activePlace.value.iconName,
      markerColor: placeDraft.markerColor || 'bg-blue'
    };
  }

  if (!placeDraft.id && placeDraft.regionId && props.editorSurface.isTargetActive(activePlaceTarget.value)) {
    return {
      coordinate: parseDraftCoordinate(),
      placeId: null,
      label: placeDraft.name || 'New place',
      iconName: placeDraft.iconName || 'marker',
      markerColor: placeDraft.markerColor || 'bg-blue'
    };
  }

  return null;
});
const persistedPlacePreview = computed<PlaceDraftPreview | null>(() => {
  if (!placePreview.value?.placeId) {
    return null;
  }

  return placePreview.value;
});
const placePreviewById = computed<Record<Guid, Pick<EditorPlace, 'iconName' | 'markerColor'>>>(() => {
  const preview = persistedPlacePreview.value;
  return preview?.placeId ? { [preview.placeId]: { iconName: preview.iconName, markerColor: preview.markerColor } } : {};
});
const forcedExpandedRegionIds = computed(() => {
  const ids = props.searchActive ? renderedRegions.value.map(region => region.id) : activeContextRegions().map(region => region.id);
  return new Set(ids);
});
const regionBaselineRequest = computed(() => draft.id ? buildRegionRequest(toRegionDraft(activeRegion.value)) : regionCreateBaselineRequest.value ?? buildRegionRequest(emptyRegionDraft()));
const placeBaselineRequest = computed(() => placeDraft.id ? placeEditBaselineRequest.value ?? buildPlaceRequest(toPlaceDraft(activePlace.value, placeDraft.regionId)) : placeCreateBaselineRequest.value ?? buildPlaceRequest(emptyPlaceDraft(placeDraft.regionId)));
const areaBaselineRequest = computed(() => areaDraft.id ? buildAreaRequest(toAreaDraft(activeArea.value, areaDraft.regionId, props.state.options.areaDefaults.fillHex)) : areaCreateBaselineRequest.value ?? buildAreaRequest(emptyAreaDraft(areaDraft.regionId, props.state.options.areaDefaults.fillHex)));
const activeRegionTarget = computed<EditorTarget>(() => ({
  key: regionDraftKey,
  identity: draft.id ? `region:edit:${draft.id}` : 'region:add',
  kind: 'region',
  mode: draft.id ? 'edit' : 'add',
  title: draft.id ? `Edit Region - ${activeRegion.value?.name ?? draft.name}` : 'Add Region',
  subtitle: draft.id ? 'Region details' : 'New region',
  entityId: draft.id ?? undefined
}));
const activePlaceTarget = computed<EditorTarget>(() => ({
  key: placeDraftKey,
  identity: placeDraft.id ? `place:edit:${placeDraft.id}` : `place:add:${placeDraft.regionId ?? 'none'}`,
  kind: 'place',
  mode: placeDraft.id ? 'edit' : 'add',
  title: placeDraft.id ? `Edit Place - ${activePlace.value?.name ?? placeDraft.name}` : 'Add Place',
  subtitle: placeDraft.regionId && !props.state.regionsById[placeDraft.regionId]?.isShadow ? props.state.regionsById[placeDraft.regionId]?.name : undefined,
  entityId: placeDraft.id ?? undefined,
  parentRegionId: placeDraft.regionId ?? undefined
}));
const activeAreaTarget = computed<EditorTarget>(() => ({
  key: areaDraftKey,
  identity: areaDraft.id ? `area:edit:${areaDraft.id}` : `area:add:${areaDraft.regionId ?? 'none'}`,
  kind: 'area',
  mode: areaDraft.id ? 'edit' : 'add',
  title: areaDraft.id ? `Edit Area - ${activeArea.value?.name ?? areaDraft.name}` : 'Add Area',
  subtitle: areaDraft.regionId ? props.state.regionsById[areaDraft.regionId]?.name : undefined,
  entityId: areaDraft.id ?? undefined,
  parentRegionId: areaDraft.regionId ?? undefined
}));
const { applyError, fieldErrors, formSummaryErrors, markSaved, resetFeedback, saveError, saveWarning, statusText } = useEditorMutationFeedback({
  isDirty,
  isAreaDraftOpen,
  isOrdering: computed(() => isOrdering.value),
  isPlaceDraftOpen,
  isSaving: computed(() => isSaving.value),
  areaFields,
  placeFields,
  regionFields
});
const { cancelDraft, deleteDraftRegion, openCreate, openEdit, reorderRegions, resetDraft, saveDraft } = useRegionEditorActions({
  activeRegion,
  activeRegionTarget,
  applyError,
  areaCreateBaselineRequest,
  areaDraft,
  clearAllDraftsAndBaselines,
  confirmDiscard,
  draft,
  emit,
  isDirty,
  isOrdering,
  isSaving,
  markSaved,
  placeCreateBaselineRequest,
  placeDraft,
  props,
  regionCreateBaselineRequest,
  resetFeedback,
  restoreRegionOrder
});
const { cancelPlaceDraft, deleteDraftPlace, openPlaceCreate, openPlaceCreateFromSearch, openPlaceEdit, pickPlaceCoordinate, reorderPlaces, resetPlaceDraft, savePlaceDraft } = usePlaceEditorActions({
  activePlace,
  activePlaceTarget,
  applyError,
  areaCreateBaselineRequest,
  areaDraft,
  clearAllDraftsAndBaselines,
  confirmDiscard,
  draft,
  emit,
  isDirty,
  isOrdering,
  isSaving,
  markSaved,
  placeCoordinateMapWork,
  placeCreateBaselineDraft,
  placeCreateBaselineRequest,
  placeEditBaselineRequest,
  placeFormId,
  placeDraft,
  props,
  regionCreateBaselineRequest,
  resetFeedback,
  restoreRegionOrder
});
const { cancelAreaDraft, deleteDraftArea, drawAreaPolygon, openAreaCreate, openAreaEdit, reorderAreas, resetAreaDraft, saveAreaDraft } = useAreaEditorActions({
  activeArea,
  activeAreaTarget,
  applyError,
  areaCreateBaselineRequest,
  areaDraft,
  areaPolygonMapWork,
  clearAllDraftsAndBaselines,
  clearRegionPlaceBaselines,
  clearRegionPlaceDrafts,
  confirmDiscard,
  emit,
  isDirty,
  isOrdering,
  isSaving,
  markSaved,
  props,
  resetFeedback,
  restoreRegionOrder
});

watch(
  isDirty,
  value => emit('dirtyStateChanged', value),
  { immediate: true }
);

watch(
  placePreview,
  preview => emit('placeDraftPreviewChanged', preview),
  { immediate: true }
);

watch(
  () => props.pendingSearchAdd?.requestId,
  async requestId => {
    if (!requestId || !props.pendingSearchAdd) {
      return;
    }

    const region = props.state.regionsById[props.pendingSearchAdd.regionId];
    if (!region || !props.state.permissions.canEditPlaces || !region.capabilities.canTargetForSearchAdd) {
      return;
    }

    if (await openPlaceCreateFromSearch(region, props.pendingSearchAdd.result)) {
      emit('searchAddOpened', requestId);
    }
  }
);

onMounted(() => {
  window.addEventListener('beforeunload', confirmUnload);
  unregisterRegionHandler = props.editorSurface.registerTargetHandler(regionDraftKey, {
    isDirty: () => regionDirty.value,
    discard: discardRegionDraft
  });
  unregisterPlaceHandler = props.editorSurface.registerTargetHandler(placeDraftKey, {
    isDirty: () => placeDirty.value,
    discard: discardPlaceDraft
  });
  unregisterAreaHandler = props.editorSurface.registerTargetHandler(areaDraftKey, {
    isDirty: () => areaDirty.value,
    discard: discardAreaDraft
  });
});

onUnmounted(() => {
  unregisterRegionHandler?.();
  unregisterPlaceHandler?.();
  unregisterAreaHandler?.();
  stopPlaceCoordinatePick(placeCoordinateMapWork);
  stopAreaPolygonEdit(areaPolygonMapWork);
  emit('dirtyStateChanged', false);
  emit('placeDraftPreviewChanged', null);
  window.removeEventListener('beforeunload', confirmUnload);
});

/// Rebuilds the Sortable-mutated list from persisted Vue state after canceled or failed reorder.
async function restoreRegionOrder(_previousIds: string[]): Promise<void> {
  regionListKey.value += 1;
}

function hasRegionChildren(region: EditorRegion): boolean {
  return (props.state.placeOrderByRegionId[region.id]?.length ?? 0) > 0 || (props.state.areaOrderByRegionId[region.id]?.length ?? 0) > 0;
}

function activeContextRegions(): EditorRegion[] {
  const regions: EditorRegion[] = [];
  if (activeRegion.value) {
    regions.push(activeRegion.value);
  }

  const activePlaceRegionId = activePlace.value?.regionId ?? placeDraft.regionId;
  if (activePlaceRegionId) {
    const region = props.state.regionsById[activePlaceRegionId];
    if (region) {
      regions.push(region);
    }
  }

  const activeAreaRegionId = activeArea.value?.regionId ?? areaDraft.regionId;
  if (activeAreaRegionId) {
    const region = props.state.regionsById[activeAreaRegionId];
    if (region) {
      regions.push(region);
    }
  }
  if (props.selectedPlaceId) {
    const selectedPlaceRegionId = props.state.placesById[props.selectedPlaceId]?.regionId;
    const region = selectedPlaceRegionId ? props.state.regionsById[selectedPlaceRegionId] : null;
    if (region) {
      regions.push(region);
    }
  }

  return regions;
}

function clearRegionPlaceDrafts(): void {
  Object.assign(draft, emptyRegionDraft());
  Object.assign(placeDraft, emptyPlaceDraft());
}

function clearRegionPlaceBaselines(): void {
  regionCreateBaselineRequest.value = null;
  placeCreateBaselineRequest.value = null;
  placeCreateBaselineDraft.value = null;
  placeEditBaselineRequest.value = null;
}

function clearAllDraftsAndBaselines(): void {
  clearRegionPlaceDrafts();
  Object.assign(areaDraft, emptyAreaDraft());
  clearRegionPlaceBaselines();
  areaCreateBaselineRequest.value = null;
}

function cloneIdRecord(record: Record<Guid, Guid[]>): Record<Guid, Guid[]> {
  return Object.fromEntries(Object.entries(record).map(([regionId, ids]) => [regionId, [...ids]]));
}

function pushUnique(record: Record<Guid, Guid[]>, regionId: Guid, id: Guid): void {
  record[regionId] = record[regionId] ?? [];
  if (!record[regionId].includes(id)) {
    record[regionId].push(id);
  }
}

function parseDraftCoordinate(): { latitude: number; longitude: number } | null {
  const latitudeText = String(placeDraft.latitude ?? '').trim();
  const longitudeText = String(placeDraft.longitude ?? '').trim();
  if (!latitudeText || !longitudeText) {
    return null;
  }

  const latitude = Number(latitudeText);
  const longitude = Number(longitudeText);
  return Number.isFinite(latitude) && latitude >= -90 && latitude <= 90 &&
    Number.isFinite(longitude) && longitude >= -180 && longitude <= 180
    ? { latitude, longitude }
    : null;
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
  regionCreateBaselineRequest.value = null;
  resetFeedback();
}

function discardPlaceDraft(): void {
  Object.assign(placeDraft, emptyPlaceDraft());
  placeCreateBaselineRequest.value = null;
  placeCreateBaselineDraft.value = null;
  placeEditBaselineRequest.value = null;
  resetFeedback();
}

function discardAreaDraft(): void {
  Object.assign(areaDraft, emptyAreaDraft());
  areaCreateBaselineRequest.value = null;
  resetFeedback();
}

function isRegionEditOpen(region: EditorRegion): boolean {
  return Boolean(draft.id && activeRegion.value?.id === region.id && props.editorSurface.isTargetActive(activeRegionTarget.value));
}

function isPlaceEditOpen(place: EditorPlace): boolean {
  return Boolean(placeDraft.id && activePlace.value?.id === place.id && props.editorSurface.isTargetActive(activePlaceTarget.value));
}

function isPlaceCreateOpen(region: EditorRegion): boolean {
  return Boolean(!placeDraft.id && placeDraft.regionId === region.id && props.editorSurface.isTargetActive(activePlaceTarget.value));
}

function isAreaEditOpen(area: EditorArea): boolean {
  return Boolean(areaDraft.id && activeArea.value?.id === area.id && props.editorSurface.isTargetActive(activeAreaTarget.value));
}

function isAreaCreateOpen(region: EditorRegion): boolean {
  return Boolean(!areaDraft.id && areaDraft.regionId === region.id && props.editorSurface.isTargetActive(activeAreaTarget.value));
}

/// Selects a place from the sidebar without opening an editor or touching persisted state.
async function selectPlace(place: EditorPlace): Promise<void> {
  await props.selectPlace(place.id);
}

/// Opens place editing only after the shared dirty-target guard approves the switch.
async function selectAndOpenPlaceEdit(place: EditorPlace): Promise<void> {
  if (await openPlaceEdit(place)) {
    await props.selectPlace(place.id);
  }
}
</script>

<template>
  <section class="trip-editor-panel trip-editor-regions">
    <div class="trip-editor-panel__line">
      <h2>Regions &amp; Places</h2>
      <span class="trip-editor-save-state" :class="mutationFeedbackClass(statusText)" role="status">{{ statusText }}</span>
    </div>

    <div v-if="saveError" class="trip-editor-form-error" role="alert">{{ saveError }}</div>
    <div v-if="saveWarning" class="trip-editor-form-warning" role="status">{{ saveWarning }}</div>

    <button type="button" class="btn btn-primary btn-sm trip-editor-add-button" :disabled="isSaving || isOrdering" @click="openCreate">Add Region</button>

    <RegionEditorSurface v-if="isDraftOpen && !draft.id" :active-region="activeRegion" :controller="editorSurface" :draft="draft" :field-errors="fieldErrors" :form-id="regionFormId" :form-summary-errors="formSummaryErrors" :is-dirty="regionDirty" :is-saving="isSaving" :status-text="statusText" :target="activeRegionTarget" @cancel="cancelDraft" @delete="deleteDraftRegion" @reset="resetDraft" @save="saveDraft" />

    <RegionPlaceList :key="regionListKey" :active-area-id="activeArea?.id ?? null" :active-place-editor-id="activePlaceEditorId" :active-place-id="selectedPlaceId" :active-region-id="activeRegion?.id ?? null" :force-expanded-region-ids="forcedExpandedRegionIds" :is-ordering="isOrdering" :is-saving="isSaving" :place-ids-by-region-id="renderedPlaceIdsByRegionId" :place-preview-by-id="placePreviewById" :area-ids-by-region-id="renderedAreaIdsByRegionId" :regions="renderedRegions" :search-active="searchActive" :state="state" @add-area="openAreaCreate" @add-place="openPlaceCreate" @area-reorder="reorderAreas" @edit-area="openAreaEdit" @edit-place="selectAndOpenPlaceEdit" @edit-region="openEdit" @place-reorder="reorderPlaces" @region-reorder="reorderRegions" @select-place="selectPlace">
      <template #region-editor="{ region }">
        <RegionEditorSurface v-if="isRegionEditOpen(region)" :active-region="activeRegion" :controller="editorSurface" :draft="draft" :field-errors="fieldErrors" :form-id="regionFormId" :form-summary-errors="formSummaryErrors" :is-dirty="regionDirty" :is-saving="isSaving" :status-text="statusText" :target="activeRegionTarget" @cancel="cancelDraft" @delete="deleteDraftRegion" @reset="resetDraft" @save="saveDraft" />
      </template>

      <template #place-editor="{ place }">
        <PlaceEditorSurface v-if="isPlaceEditOpen(place)" :active-place="activePlace" :controller="editorSurface" :draft="placeDraft" :field-errors="fieldErrors" :form-id="placeFormId" :form-summary-errors="formSummaryErrors" :is-dirty="placeDirty" :is-saving="isSaving" :normal-regions="placeTargetRegions" :state="state" :status-text="statusText" :target="activePlaceTarget" @cancel="cancelPlaceDraft" @delete="deleteDraftPlace" @pick-coordinate="pickPlaceCoordinate" @reset="resetPlaceDraft" @save="savePlaceDraft" />
      </template>

      <template #area-editor="{ area }">
        <AreaEditorSurface v-if="isAreaEditOpen(area)" :active-area="activeArea" :controller="editorSurface" :draft="areaDraft" :field-errors="fieldErrors" :form-id="areaFormId" :form-summary-errors="formSummaryErrors" :is-dirty="areaDirty" :is-saving="isSaving" :status-text="statusText" :target="activeAreaTarget" @cancel="cancelAreaDraft" @delete="deleteDraftArea" @draw-area="drawAreaPolygon" @reset="resetAreaDraft" @save="saveAreaDraft" />
      </template>

      <template #add-place-editor="{ region }">
        <PlaceEditorSurface v-if="isPlaceCreateOpen(region)" :active-place="activePlace" :controller="editorSurface" :draft="placeDraft" :field-errors="fieldErrors" :form-id="placeFormId" :form-summary-errors="formSummaryErrors" :is-dirty="placeDirty" :is-saving="isSaving" :normal-regions="placeTargetRegions" :state="state" :status-text="statusText" :target="activePlaceTarget" @cancel="cancelPlaceDraft" @delete="deleteDraftPlace" @pick-coordinate="pickPlaceCoordinate" @reset="resetPlaceDraft" @save="savePlaceDraft" />
      </template>

      <template #add-area-editor="{ region }">
        <AreaEditorSurface v-if="isAreaCreateOpen(region)" :active-area="activeArea" :controller="editorSurface" :draft="areaDraft" :field-errors="fieldErrors" :form-id="areaFormId" :form-summary-errors="formSummaryErrors" :is-dirty="areaDirty" :is-saving="isSaving" :status-text="statusText" :target="activeAreaTarget" @cancel="cancelAreaDraft" @delete="deleteDraftArea" @draw-area="drawAreaPolygon" @reset="resetAreaDraft" @save="saveAreaDraft" />
      </template>
    </RegionPlaceList>
  </section>
</template>
