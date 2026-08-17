<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { loadEditorState } from './api/tripEditorApi';
import ConfirmDialog from './components/ConfirmDialog.vue';
import MapSearchControl from './components/MapSearchControl.vue';
import MapWorkToolbar from './components/MapWorkToolbar.vue';
import TripSidebar from './components/TripSidebar.vue';
import { disposeConfirmDialogHost, setConfirmDialogFocusFallback } from './composables/useConfirmDialog';
import { useEditorSurface } from './composables/useEditorSurface';
import { canFocusActiveEntity, createTripEditorMap, hasAnyGeometry, hasSavedTripView, type AreaPolygonWorkOptions, type CoordinatePickOptions, type FocusActiveEntityResult, type PlaceDraftMarkerPreview, type SegmentDraftRoutePreview, type SegmentRouteWorkOptions, type TripEditorMapView } from './map/leafletAdapter';
import type { SegmentRouteWorkState } from './components/segmentRouteWorkState';
import type { EditorSegmentDraftPresentation, SegmentPresentationKey } from './segments/editorSegmentPresentation';
import type { BootstrapConfig, EditorCoordinate, EditorGeocodeSearchResult, EditorMutationResult, EditorTripMetadata, EditorTripState, Guid } from './types';

const props = defineProps<{ config: BootstrapConfig }>();

const state = ref<EditorTripState | null>(null);
const error = ref<string | null>(null);
const isLoading = ref(true);
const hasRegionDraftChanges = ref(false);
const workspaceElement = ref<HTMLElement | null>(null);
const mapElement = ref<HTMLElement | null>(null);
const mobileDrawerActive = ref(false);
const navigationStatus = ref<string | null>(null);
const hiddenSegmentIds = ref<Set<string>>(new Set());
const selectedPlaceId = ref<Guid | null>(null);
const activeSegmentKey = ref<SegmentPresentationKey | null>(null);
const activeSegmentDraft = ref<EditorSegmentDraftPresentation | null>(null);
const activePlaceDraftPreview = ref<PlaceDraftMarkerPreview | null>(null);
const pendingSearchAdd = ref<{ result: EditorGeocodeSearchResult; regionId: Guid; requestId: number } | null>(null);
const completedSearchAddRequestId = ref<number | null>(null);
const editorSurface = useEditorSurface();
let mapAdapter: ReturnType<typeof createTripEditorMap> | null = null;
let mobileDrawerQuery: MediaQueryList | null = null;
const coordinatePicker = {
  getMapView: (): TripEditorMapView | null => mapAdapter?.getMapView() ?? null,
  startCoordinatePick: (options: CoordinatePickOptions): (() => void) => mapAdapter?.startCoordinatePick(options) ?? (() => undefined)
};
const polygonEditor = {
  startAreaPolygonWork: (options: AreaPolygonWorkOptions): (() => void) => mapAdapter?.startAreaPolygonWork(options) ?? (() => undefined)
};
const routeEditor = {
  setSegmentRouteWorkState: (workState: SegmentRouteWorkState): void => mapAdapter?.setSegmentRouteWorkState(workState),
  startSegmentRouteWork: (options: SegmentRouteWorkOptions): (() => void) => mapAdapter?.startSegmentRouteWork(options) ?? (() => undefined)
};

const updatedLabel = computed(() =>
  state.value ? new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(state.value.metadata.updatedAt)) : ''
);
const isMapWorkActive = computed(() => editorSurface.isMapWorkActive.value);
// Projects the authoritative map-work owner into the map landmark's exact instruction.
const mapAccessibleDescription = computed(() => {
  switch (editorSurface.mapWork.value?.target.kind) {
    case 'place':
      return 'Select the Place location. Click the map or drag the marker; Done updates the draft.';
    case 'area':
      return 'Edit the Area geometry. Click the map to place polygon vertices; Done updates the draft.';
    case 'segment':
      return 'Edit the Segment route. Saved Place anchors are fixed; add, move, or remove other route points; Done updates the draft.';
    default:
      return 'View and navigate trip geography.';
  }
});
const selectedPlace = computed(() => selectedPlaceId.value && state.value ? state.value.placesById[selectedPlaceId.value] ?? null : null);
const selectedPlaceRegionName = computed(() => selectedPlace.value && state.value ? state.value.regionsById[selectedPlace.value.regionId]?.name ?? null : null);
const toolbarEyebrow = computed(() => {
  if (editorSurface.mapWork.value) {
    return 'Map work';
  }

  if (selectedPlace.value) {
    return 'Selected place';
  }

  if (editorSurface.activeTarget.value) {
    return `${editorSurface.activeTarget.value.mode === 'add' ? 'New' : 'Editing'} ${editorSurface.activeTarget.value.kind}`;
  }

  return 'Map status';
});
const toolbarTitle = computed(() => {
  if (editorSurface.mapWork.value) {
    return editorSurface.mapWork.value.modeName;
  }

  return selectedPlace.value?.name ?? editorSurface.activeTarget.value?.title ?? 'Trip map';
});
const toolbarDetail = computed(() => {
  if (editorSurface.mapWork.value) {
    const status = editorSurface.mapWork.value.statusText;
    return typeof status === 'function' ? status() : status;
  }

  if (navigationStatus.value) {
    return navigationStatus.value;
  }

  if (selectedPlace.value) {
    return selectedPlaceRegionName.value ? `In ${selectedPlaceRegionName.value}` : 'Selected on the map and sidebar';
  }

  if (editorSurface.activeTarget.value?.subtitle) {
    return editorSurface.activeTarget.value.subtitle;
  }

  return `Updated ${updatedLabel.value}`;
});
const canFitAllGeometry = computed(() => Boolean(state.value && hasAnyGeometry(state.value)));
const canRecenterSavedView = computed(() => Boolean(state.value && hasSavedTripView(state.value.metadata)));
const canFocusTarget = computed(() => Boolean(state.value && canFocusActiveEntity(state.value, editorSurface.activeTarget.value)));
const activeEditorTarget = computed(() => editorSurface.activeTarget.value);

watch(
  () => editorSurface.activeTarget.value?.identity,
  () => {
    mapAdapter?.clearSearchPreview();
    const target = editorSurface.activeTarget.value;
    if (target?.kind === 'place' && target.mode === 'edit' && target.entityId && state.value?.placesById[target.entityId]) {
      void selectPlace(target.entityId, { focusMap: false });
    }
  }
);

onMounted(async () => {
  setConfirmDialogFocusFallback(workspaceElement.value);
  mobileDrawerQuery = window.matchMedia('(max-width: 640px)');
  mobileDrawerActive.value = mobileDrawerQuery.matches;
  mobileDrawerQuery.addEventListener('change', updateMobileDrawerState);

  try {
    const loadedState = await loadEditorState(props.config.editorEndpoint);
    state.value = loadedState;
    isLoading.value = false;
    await nextTick();
    if (!mapElement.value) {
      throw new Error('Trip Editor map element was unavailable after the workspace rendered.');
    }

    mapAdapter = createTripEditorMap(mapElement.value, props.config.tilesUrl, {
      onPlaceSelected: placeId => selectPlace(placeId, { focusMap: false, openPopup: true }),
      onSegmentSelected: key => selectSegment(key)
    });
    mapAdapter.render(loadedState, hiddenSegmentIds.value, selectedPlaceId.value);
  } catch (loadError) {
    error.value = loadError instanceof Error ? loadError.message : 'Trip Editor failed to load.';
    isLoading.value = false;
  }
});

onUnmounted(() => {
  disposeConfirmDialogHost();
  setConfirmDialogFocusFallback(null);
  mobileDrawerQuery?.removeEventListener('change', updateMobileDrawerState);
  mapAdapter?.dispose();
});

/// Keeps the release-scoped phone drawer below tablet/intermediate widths.
function updateMobileDrawerState(event: MediaQueryListEvent): void {
  mobileDrawerActive.value = event.matches;
}

const applyMetadata = (metadata: EditorTripMetadata): void => {
  if (!state.value) {
    return;
  }

  state.value = { ...state.value, metadata };
  mapAdapter?.render(state.value, hiddenSegmentIds.value, selectedPlaceId.value);
};

/// Updates UI-only place selection shared by the sidebar, toolbar, and map marker halo after guarded editor cleanup.
const selectPlace = async (placeId: Guid, options: { focusMap?: boolean; openPopup?: boolean } = {}): Promise<boolean> => {
  if (!state.value) {
    selectedPlaceId.value = null;
    return false;
  }

  if (!state.value.placesById[placeId]) {
    selectedPlaceId.value = null;
    mapAdapter?.selectPlace(state.value, null);
    return false;
  }

  if (selectedPlaceId.value === placeId) {
    const target = editorSurface.activeTarget.value;
    if (target?.kind === 'place' && target.mode === 'edit' && target.entityId === placeId) {
      return true;
    }

    return await clearSelectedPlace();
  }

  if (!(await closeActiveEditorBeforeSelection(placeId))) {
    return false;
  }

  selectedPlaceId.value = placeId;
  activeSegmentKey.value = null;
  activeSegmentDraft.value = null;
  mapAdapter?.setSegmentPresentation(state.value, null, null);
  mapAdapter?.selectPlace(state.value, placeId, { focus: options.focusMap, openPopup: options.openPopup });
  navigationStatus.value = `Selected place: ${state.value.placesById[placeId].name}`;
  return true;
};

/// Runs the shared dirty-discard flow before selection hides a different active editor.
async function closeActiveEditorBeforeSelection(placeId: Guid): Promise<boolean> {
  const target = editorSurface.activeTarget.value;
  if (!target) {
    return true;
  }

  if (target.kind === 'place' && target.mode === 'edit' && target.entityId === placeId) {
    return true;
  }

  if (mobileDrawerActive.value && tabForTargetKind(target.kind) !== 'regions') {
    return await editorSurface.closeActiveTarget(`Discard unsaved ${targetKindLabel(target.kind)} changes before switching tabs?`);
  }

  if (target.kind !== 'place') {
    return await editorSurface.closeActiveTarget(`Discard unsaved ${targetKindLabel(target.kind)} changes before selecting a Place?`);
  }

  if (target.mode === 'add') {
    return await editorSurface.closeActiveTarget('Discard unsaved place changes before selecting another place?');
  }

  return await editorSurface.closeActiveTarget('Discard unsaved place changes before selecting another place?');
}

/** Applies one guarded transient Segment selection without opening an editor or mutating Segment data. */
async function selectSegment(key: SegmentPresentationKey): Promise<boolean> {
  if (!state.value || key.kind === 'persisted' && !state.value.segmentsById[key.id]) return false;
  const target = editorSurface.activeTarget.value;
  const selectedId = key.kind === 'persisted' ? key.id : null;
  const ownsActiveEditor = target?.kind === 'segment'
    && (key.kind === 'create-draft' ? target.mode === 'add' : target.entityId === selectedId);
  if (target && !ownsActiveEditor && !(await editorSurface.closeActiveTarget('Discard unsaved changes before selecting another Segment?'))) {
    return false;
  }
  selectedPlaceId.value = null;
  mapAdapter?.selectPlace(state.value, null);
  activeSegmentKey.value = key;
  navigationStatus.value = key.kind === 'persisted'
    ? `Selected segment: ${state.value.segmentsById[key.id]?.mode || 'Segment'}`
    : 'Selected new Segment draft';
  const retainedDraft = activeSegmentDraft.value;
  const retainedKey = retainedDraft?.key;
  const ownsRetainedDraft = retainedKey?.kind === key.kind
    && (key.kind === 'persisted' ? retainedKey.id === key.id : retainedKey.token === key.token);
  mapAdapter?.setSegmentPresentation(state.value, key, ownsRetainedDraft ? retainedDraft : null);
  return true;
}

/** Receives the current D/W snapshot while SegmentManager retains mutation authority. */
function applyActiveSegmentDraft(snapshot: EditorSegmentDraftPresentation | null): void {
  activeSegmentDraft.value = snapshot;
  if (snapshot) activeSegmentKey.value = snapshot.key;
  if (state.value) mapAdapter?.setSegmentPresentation(state.value, activeSegmentKey.value, snapshot);
}

/** Clears active presentation only when the supplied owner still owns it. */
function clearActiveSegment(key: SegmentPresentationKey): void {
  const current = activeSegmentKey.value;
  if (!current || current.kind !== key.kind || (current.kind === 'persisted' ? current.id !== (key as { id: Guid }).id : false)) return;
  activeSegmentKey.value = null;
  activeSegmentDraft.value = null;
  if (state.value) mapAdapter?.setSegmentPresentation(state.value, null, null);
}

/// Maps active editor ownership to the phone drawer tab that can keep it visible.
function tabForTargetKind(kind: string): 'trip' | 'regions' | 'segments' {
  if (kind === 'segment') {
    return 'segments';
  }

  if (kind === 'region' || kind === 'place' || kind === 'area') {
    return 'regions';
  }

  return 'trip';
}

/// Keeps dirty-discard copy aligned with the manual phone tab switch guard.
function targetKindLabel(kind: string): string {
  if (kind === 'metadata') {
    return 'trip';
  }

  return kind;
}

/// Clears selected-place context, closing the selected place editor first when that editor owns the selection.
const clearSelectedPlace = async (): Promise<boolean> => {
  if (!state.value || !selectedPlaceId.value) {
    return true;
  }

  const placeId = selectedPlaceId.value;
  const target = editorSurface.activeTarget.value;
  if (target?.kind === 'place' && target.mode === 'edit' && target.entityId === placeId) {
    if (!(await editorSurface.closeActiveTarget('Discard unsaved place changes?'))) {
      return false;
    }
  }

  selectedPlaceId.value = null;
  navigationStatus.value = null;
  mapAdapter?.selectPlace(state.value, null);
  return true;
};

/// Runs a navigation-only adapter command without mutating editor drafts or metadata.
const fitAllGeometry = (): void => {
  if (!state.value || !mapAdapter) {
    return;
  }

  const result = mapAdapter.fitAllGeometry(state.value);
  navigationStatus.value = result === 'moved' ? 'Fit all geometry' : 'No geometry to fit';
};

/// Recenters to the persisted trip view; it never saves the current map viewport.
const recenterSavedTripView = (): void => {
  if (!state.value || !mapAdapter) {
    return;
  }

  const result = mapAdapter.focusSavedTripView(state.value.metadata);
  navigationStatus.value = result === 'moved' ? 'Recentered saved trip view' : 'Saved trip view unavailable';
};

/// Focuses the active target through the map adapter and reports only local toolbar status.
const focusActiveEntity = (): void => {
  if (!state.value || !mapAdapter) {
    return;
  }

  const target = editorSurface.activeTarget.value;
  const result = mapAdapter.focusActiveEntity(state.value, target);
  navigationStatus.value = focusStatusText(result, target);
};

/// Shows a temporary provider-result marker without entering coordinate-pick map-work.
const previewSearchResult = (result: EditorGeocodeSearchResult): void => {
  mapAdapter?.showSearchPreview({ latitude: result.latitude, longitude: result.longitude }, result.name);
};

/// Clears the temporary map-search preview marker.
const clearSearchPreview = (): void => {
  mapAdapter?.clearSearchPreview();
};

/// Requests that the sidebar open the existing Add Place draft for a provider result.
const requestSearchAddPlace = (request: { result: EditorGeocodeSearchResult; regionId: Guid; requestId: number }): void => {
  completedSearchAddRequestId.value = null;
  pendingSearchAdd.value = request;
};

/// Clears the preview marker only after the existing Add Place draft actually opens.
const handleSearchAddOpened = (requestId: number): void => {
  if (pendingSearchAdd.value?.requestId === requestId) {
    clearSearchPreview();
    completedSearchAddRequestId.value = requestId;
    pendingSearchAdd.value = null;
  }
};

/// Tracks region draft changes that live inside the sidebar child component.
const setRegionDraftChanges = (isDirty: boolean): void => {
  hasRegionDraftChanges.value = isDirty;
};

/// Applies active place draft icon/color to the selected marker preview without saving it.
const applyPlaceDraftPreview = (preview: PlaceDraftMarkerPreview | null): void => {
  if (!state.value) {
    activePlaceDraftPreview.value = preview;
    return;
  }

  activePlaceDraftPreview.value = preview;
  mapAdapter?.setPlaceDraftPreview(state.value, preview);
};

/// Applies segment form ownership through the segment-specific map preview contract.
const applySegmentRouteDraftPreview = (preview: SegmentDraftRoutePreview | null): void => {
  if (state.value) {
    mapAdapter?.setSegmentDraftPreview(state.value, preview);
  }
};

/// Applies mutation affected slices and authoritative deleted IDs to normalized editor state.
const applyMutation = (result: EditorMutationResult<unknown>): void => {
  if (!state.value) {
    return;
  }

  const next: EditorTripState = {
    ...state.value,
    metadata: result.affected.metadata ?? state.value.metadata,
    regionsById: { ...state.value.regionsById },
    regionOrder: result.affected.regionOrder ?? [...state.value.regionOrder],
    placesById: { ...state.value.placesById },
    placeOrderByRegionId: { ...state.value.placeOrderByRegionId },
    areasById: { ...state.value.areasById },
    areaOrderByRegionId: { ...state.value.areaOrderByRegionId },
    segmentsById: { ...state.value.segmentsById },
    segmentOrder: result.affected.segmentOrder ?? [...state.value.segmentOrder],
    tagsBySlug: { ...state.value.tagsBySlug },
    tagOrder: result.affected.tagOrder ?? [...state.value.tagOrder],
    visitProgress: result.affected.visitProgress ?? state.value.visitProgress,
    options: result.affected.options ?? state.value.options
  };

  result.deletedIds.regions.forEach(id => {
    delete next.regionsById[id];
    delete next.placeOrderByRegionId[id];
    delete next.areaOrderByRegionId[id];
  });
  result.deletedIds.places.forEach(id => {
    delete next.placesById[id];
  });
  result.deletedIds.areas.forEach(id => {
    delete next.areasById[id];
  });
  result.deletedIds.segments.forEach(id => {
    delete next.segmentsById[id];
    hiddenSegmentIds.value.delete(id);
    if (activeSegmentKey.value?.kind === 'persisted' && activeSegmentKey.value.id === id) {
      activeSegmentKey.value = null;
      activeSegmentDraft.value = null;
    }
  });
  result.deletedIds.tags.forEach(slug => {
    delete next.tagsBySlug[slug];
  });

  result.affected.regions.forEach(region => {
    next.regionsById[region.id] = region;
  });
  result.affected.places.forEach(place => {
    next.placesById[place.id] = place;
  });
  result.affected.areas.forEach(area => {
    next.areasById[area.id] = area;
  });
  result.affected.segments.forEach(segment => {
    next.segmentsById[segment.id] = segment;
  });
  result.affected.tags.forEach(tag => {
    next.tagsBySlug[tag.slug] = tag;
  });
  Object.entries(result.affected.placeOrdersByRegionId).forEach(([regionId, order]) => {
    next.placeOrderByRegionId[regionId] = order;
  });
  Object.entries(result.affected.areaOrdersByRegionId).forEach(([regionId, order]) => {
    next.areaOrderByRegionId[regionId] = order;
  });

  next.regionOrder = next.regionOrder.filter(id => next.regionsById[id]);
  next.segmentOrder = next.segmentOrder.filter(id => next.segmentsById[id]);
  next.tagOrder = next.tagOrder.filter(slug => next.tagsBySlug[slug]);
  Object.keys(next.placeOrderByRegionId).forEach(regionId => {
    next.placeOrderByRegionId[regionId] = next.placeOrderByRegionId[regionId].filter(id => next.placesById[id]);
  });
  Object.keys(next.areaOrderByRegionId).forEach(regionId => {
    next.areaOrderByRegionId[regionId] = next.areaOrderByRegionId[regionId].filter(id => next.areasById[id]);
  });

  if (selectedPlaceId.value && !next.placesById[selectedPlaceId.value]) {
    selectedPlaceId.value = null;
    navigationStatus.value = null;
  }
  if (activePlaceDraftPreview.value?.placeId && !next.placesById[activePlaceDraftPreview.value.placeId]) {
    activePlaceDraftPreview.value = null;
  }

  state.value = next;
  mapAdapter?.render(next, hiddenSegmentIds.value, selectedPlaceId.value);
};

/// Updates client-session-only segment visibility without touching the API contract.
const updateHiddenSegmentIds = (ids: Set<string>): void => {
  if (activeSegmentKey.value?.kind === 'persisted' && ids.has(activeSegmentKey.value.id)) {
    activeSegmentKey.value = null;
    activeSegmentDraft.value = null;
  }
  hiddenSegmentIds.value = ids;
  if (state.value) {
    mapAdapter?.render(state.value, hiddenSegmentIds.value, selectedPlaceId.value);
  }
};

function focusStatusText(result: FocusActiveEntityResult, target: { kind: string; mode: string } | null): string {
  if (result === 'moved') {
    if (target?.kind === 'place' && target.mode === 'add') {
      return 'Focused parent region';
    }

    const kind = target?.kind ?? null;
    if (kind === 'metadata') {
      return 'Focused trip map';
    }

    if (kind === 'region') {
      return 'Focused region';
    }

    if (kind === 'place') {
      return 'Focused place';
    }

    if (kind === 'area') {
      return 'Focused area';
    }

    if (kind === 'segment') {
      return 'Focused segment';
    }

    return 'Focused active entity';
  }

  if (result === 'missing-target') {
    return 'No active target to focus';
  }

  if (result === 'unsupported-target') {
    return 'Active target cannot be focused';
  }

  return 'No geometry to focus';
}
</script>

<template>
  <div ref="workspaceElement" class="trip-editor-workspace" tabindex="-1">
    <div v-if="isLoading" class="trip-editor-state trip-editor-state--loading">
      <div class="spinner-border" role="status" aria-label="Loading"></div>
      <span>Loading trip editor...</span>
    </div>

    <div v-else-if="error" class="trip-editor-state trip-editor-state--error">
      <strong>Trip Editor unavailable</strong>
      <span>{{ error }}</span>
    </div>

    <template v-else-if="state">
      <TripSidebar
        :state="state"
        :editor-surface="editorSurface"
        :editor-endpoint="props.config.editorEndpoint"
        :antiforgery-token="props.config.antiforgeryToken"
        :trip-index-url="props.config.tripIndexUrl"
        :has-region-draft-changes="hasRegionDraftChanges"
        :hidden-segment-ids="hiddenSegmentIds"
        :selected-place-id="selectedPlaceId"
        :active-segment-key="activeSegmentKey"
        :pending-search-add="pendingSearchAdd"
        :mobile-drawer-active="mobileDrawerActive"
        :is-map-work-active="isMapWorkActive"
        :completed-search-add-request-id="completedSearchAddRequestId"
        :coordinate-picker="coordinatePicker"
        :polygon-editor="polygonEditor"
        :route-editor="routeEditor"
        @metadata-saved="applyMetadata"
        @mutation-applied="applyMutation"
        @region-draft-dirty-changed="setRegionDraftChanges"
        @place-draft-preview-changed="applyPlaceDraftPreview"
        @segment-route-draft-preview-changed="applySegmentRouteDraftPreview"
        @active-segment-draft-changed="applyActiveSegmentDraft"
        @active-segment-cleared="clearActiveSegment"
        @hidden-segment-ids-changed="updateHiddenSegmentIds"
        :select-place="placeId => selectPlace(placeId, { focusMap: true })"
        :select-segment="selectSegment"
        :clear-selected-place="clearSelectedPlace"
        @search-add-opened="handleSearchAddOpened"
        @search-add-place="requestSearchAddPlace"
        @search-clear-preview="clearSearchPreview"
        @search-preview="previewSearchResult"
      />
      <main class="trip-editor-map-shell">
        <header class="trip-editor-toolbar">
          <div class="trip-editor-toolbar__context">
            <span>{{ toolbarEyebrow }}</span>
            <strong>{{ toolbarTitle }}</strong>
            <small class="trip-editor-toolbar__status" role="status">{{ toolbarDetail }}</small>
          </div>
          <div class="trip-editor-toolbar__actions">
            <template v-if="!isMapWorkActive">
              <button type="button" class="btn btn-outline-light btn-sm" :disabled="!canFitAllGeometry" @click="fitAllGeometry">Fit All</button>
              <button type="button" class="btn btn-outline-light btn-sm" :disabled="!canRecenterSavedView" @click="recenterSavedTripView">
                Recenter Saved Trip View
              </button>
              <button type="button" class="btn btn-outline-light btn-sm" :disabled="!canFocusTarget" @click="focusActiveEntity">Focus Active Entity</button>
              <button v-if="selectedPlace" type="button" class="btn btn-outline-light btn-sm" @click="clearSelectedPlace">Clear Selection</button>
            </template>
          </div>
        </header>
        <MapWorkToolbar :controller="editorSurface" />
        <MapSearchControl
          v-if="!isMapWorkActive && !mobileDrawerActive"
          :active-target="activeEditorTarget"
          :completed-add-request-id="completedSearchAddRequestId"
          :editor-endpoint="props.config.editorEndpoint"
          :state="state"
          @add-place="requestSearchAddPlace"
          @clear-preview="clearSearchPreview"
          @preview="previewSearchResult"
        />
        <div
          ref="mapElement"
          class="trip-editor-map"
          role="region"
          aria-label="Trip map"
          aria-describedby="trip-editor-map-description"
        ></div>
        <p id="trip-editor-map-description" class="visually-hidden">{{ mapAccessibleDescription }}</p>
      </main>
    </template>

    <ConfirmDialog />
  </div>
</template>
