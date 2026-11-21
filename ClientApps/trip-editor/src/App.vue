<script setup lang="ts">
import { date, message as localize } from '../../../wwwroot/js/localization/messages.js';
import { registerLanguageDraft } from '../../../wwwroot/js/localization/languageNavigation.js';
import { describeMapWork } from './map/mapWorkDescription';
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { providePlanningWorkspace } from './planning/planningState';
import { EditorMutationHttpError, loadEditorState } from './api/tripEditorApi';
import { editorModeText } from './components/editorFeedbackText';
import { discardBeforeSelectingPlace, discardBeforeSwitchingTabs, focusStatusText } from './components/editorNavigationText';
import { regionDisplayName } from './regionDisplayName.js';
import ConfirmDialog from './components/ConfirmDialog.vue';
import MapSearchControl from './components/MapSearchControl.vue';
import MapWorkToolbar from './components/MapWorkToolbar.vue';
import TripSidebar from './components/TripSidebar.vue';
import TripSidebarLayout from './components/TripSidebarLayout.vue';
import { disposeConfirmDialogHost, setConfirmDialogFocusFallback } from './composables/useConfirmDialog';
import { useEditorSurface } from './composables/useEditorSurface';
import { canFocusActiveEntity, createTripEditorMap, hasAnyGeometry, hasSavedTripView, type AreaPolygonWorkOptions, type CoordinatePickOptions, type PlaceDraftMarkerPreview, type SegmentDraftRoutePreview, type SegmentRouteWorkOptions, type TripEditorMapView } from './map/leafletAdapter';
import type { SegmentRouteWorkState } from './components/segmentRouteWorkState';
import type { EditorSegmentDraftPresentation, SegmentPresentationKey } from './segments/editorSegmentPresentation';
import type { BootstrapConfig, EditorCoordinate, EditorGeocodeSearchResult, EditorMutationResult, EditorTripState, Guid } from './types';

const props = defineProps<{ config: BootstrapConfig }>();

const state = ref<EditorTripState | null>(null);
const planningWorkspace = providePlanningWorkspace(state, props.config.editorEndpoint, props.config.antiforgeryToken);
const error = ref<string | null>(null);
const isLoading = ref(true);
const workspaceElement = ref<HTMLElement | null>(null);
const mapElement = ref<HTMLElement | null>(null);
const mobileDrawerActive = ref(false);
// Plain viewport notifications cross Vue props; Leaflet instances stay adapter-owned.
const capturedMapView = ref<TripEditorMapView | null>(null);
const navigationStatus = ref<string | null>(null);
const hiddenSegmentIds = ref<Set<string>>(new Set());
const selectedPlaceId = ref<Guid | null>(null);
const activeSegmentKey = ref<SegmentPresentationKey | null>(null);
const activeSegmentDraft = ref<EditorSegmentDraftPresentation | null>(null);
const activePlaceDraftPreview = ref<PlaceDraftMarkerPreview | null>(null);
const pendingSearchAdd = ref<{ result: EditorGeocodeSearchResult; regionId: Guid; requestId: number } | null>(null);
const completedSearchAddRequestId = ref<number | null>(null);
const editorSurface = useEditorSurface();
// Query the map owner before any close workflow can restore or clear its snapshot.
const unregisterLanguageDraft = registerLanguageDraft(() =>
  editorSurface.isMapWorkActive.value || editorSurface.isActiveTargetDirty() || pendingSearchAdd.value !== null);
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
  state.value ? date(new Date(state.value.metadata.updatedAt), { dateStyle: 'medium', timeStyle: 'short' }) : ''
);
const isMapWorkActive = computed(() => editorSurface.isMapWorkActive.value);
const mapAccessibleDescription = computed(() => describeMapWork(editorSurface.mapWork.value?.target.kind));
const selectedPlace = computed(() => selectedPlaceId.value && state.value ? state.value.placesById[selectedPlaceId.value] ?? null : null);
const toolbarEyebrow = computed(() => {
  if (editorSurface.mapWork.value) {
    return localize('Editor.MapWork', 'Map work');
  }

  if (selectedPlace.value) {
    return localize('Editor.SelectedPlace', 'Selected place');
  }

  if (editorSurface.activeTarget.value) {
    return editorModeText(editorSurface.activeTarget.value.kind, editorSurface.activeTarget.value.mode);
  }

  return localize('Editor.MapStatus', 'Map status');
});
const toolbarTitle = computed(() => {
  if (editorSurface.mapWork.value) {
    return editorSurface.mapWork.value.modeName;
  }

  return selectedPlace.value?.name ?? editorSurface.activeTarget.value?.title ?? localize('Editor.TripMap', 'Trip map');
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
    const regionName = regionDisplayName(state.value?.regionsById[selectedPlace.value.regionId]);
    return regionName ? localize('Editor.InsideRegion', 'In {0}', regionName) : localize('Editor.SelectedOnMap', 'Selected on the map and sidebar');
  }

  if (editorSurface.activeTarget.value?.subtitle) {
    return editorSurface.activeTarget.value.subtitle;
  }

  return localize('Editor.UpdatedAt', 'Updated {0}', updatedLabel.value);
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
      onSegmentSelected: key => selectSegment(key),
      canCaptureTripView: () => !editorSurface.isMapWorkActive.value,
      onMapViewCaptured: view => { capturedMapView.value = view; }
    });
    mapAdapter.render(loadedState, hiddenSegmentIds.value, selectedPlaceId.value);
  } catch (loadError) {
    error.value = loadError instanceof EditorMutationHttpError && [401, 403].includes(loadError.status)
      ? localize('Editor.LoadUnauthorized', 'Your session ended or you no longer have access. Sign in again or return to My Trips.')
      : localize('Editor.LoadError', 'Trip Editor failed to load.');
    isLoading.value = false;
  }
});

onUnmounted(() => {
  unregisterLanguageDraft();
  disposeConfirmDialogHost();
  setConfirmDialogFocusFallback(null);
  mobileDrawerQuery?.removeEventListener('change', updateMobileDrawerState);
  mapAdapter?.dispose();
});

/// Keeps the release-scoped phone drawer below tablet/intermediate widths.
function updateMobileDrawerState(event: MediaQueryListEvent): void {
  mobileDrawerActive.value = event.matches;
}

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
  navigationStatus.value = localize('Editor.SelectedPlaceName', 'Selected place: {0}', state.value.placesById[placeId].name);
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
    return await editorSurface.closeActiveTarget(discardBeforeSwitchingTabs(target.kind));
  }

  return await editorSurface.closeActiveTarget(discardBeforeSelectingPlace(target.kind));
}

/** Applies one guarded transient Segment selection without opening an editor or mutating Segment data. */
async function selectSegment(key: SegmentPresentationKey): Promise<boolean> {
  if (!state.value || key.kind === 'persisted' && !state.value.segmentsById[key.id]) return false;
  const target = editorSurface.activeTarget.value;
  const selectedId = key.kind === 'persisted' ? key.id : null;
  const ownsActiveEditor = target?.kind === 'segment'
    && (key.kind === 'create-draft' ? target.mode === 'add' : target.entityId === selectedId);
  if (target && !ownsActiveEditor && !(await editorSurface.closeActiveTarget(
    localize('Editor.DiscardBeforeSelectingSegment', 'Discard unsaved changes before selecting another Segment?')
  ))) {
    return false;
  }
  selectedPlaceId.value = null;
  mapAdapter?.selectPlace(state.value, null);
  activeSegmentKey.value = key;
  navigationStatus.value = segmentSelectionStatus(state.value, key);
  const retainedDraft = activeSegmentDraft.value;
  const retainedKey = retainedDraft?.key;
  const ownsRetainedDraft = key.kind === 'persisted'
    ? retainedKey?.kind === 'persisted' && retainedKey.id === key.id
    : retainedKey?.kind === 'create-draft' && retainedKey.token === key.token;
  mapAdapter?.setSegmentPresentation(state.value, key, ownsRetainedDraft ? retainedDraft : null);
  return true;
}

/** Localizes the selection announcement around the catalog's authored transport label or the stored mode. */
function segmentSelectionStatus(state: EditorTripState, key: SegmentPresentationKey): string {
  if (key.kind === 'create-draft') {
    return localize('Editor.SelectedSegmentDraft', 'Selected new Segment draft');
  }

  const mode = state.segmentsById[key.id]?.mode;
  const label = state.options.transportModes.find(option => option.value === mode)?.label ?? mode;
  return label ? localize('Editor.SelectedSegmentMode', 'Selected segment: {0}', label)
    : localize('Editor.SelectedSegment', 'Selected segment');
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

/// Clears selected-place context, closing the selected place editor first when that editor owns the selection.
const clearSelectedPlace = async (): Promise<boolean> => {
  if (!state.value || !selectedPlaceId.value) {
    return true;
  }

  const placeId = selectedPlaceId.value;
  const target = editorSurface.activeTarget.value;
  if (target?.kind === 'place' && target.mode === 'edit' && target.entityId === placeId) {
    if (!(await editorSurface.closeActiveTarget(localize('Editor.DiscardPlaceDraft', 'Discard unsaved place changes?')))) {
      return false;
    }
  }

  selectedPlaceId.value = null;
  navigationStatus.value = null;
  mapAdapter?.selectPlace(state.value, null);
  return true;
};

/// Runs a navigation-only adapter command without mutating editor drafts or metadata.
const fitAllGeometry = async (): Promise<void> => {
  if (!state.value || !mapAdapter) {
    return;
  }

  const result = await mapAdapter.fitAllGeometry(state.value);
  if (!result) return;
  navigationStatus.value = result === 'moved'
    ? localize('Editor.FitAllMoved', 'Fit all geometry')
    : localize('Editor.FitAllNoGeometry', 'No geometry to fit');
};

/// Recenters to the persisted trip view; it never saves the current map viewport.
const recenterSavedTripView = async (): Promise<void> => {
  if (!state.value || !mapAdapter) {
    return;
  }

  const result = await mapAdapter.focusSavedTripView(state.value.metadata);
  if (!result) return;
  navigationStatus.value = result === 'moved'
    ? localize('Editor.RecenterMoved', 'Recentered saved trip view')
    : localize('Editor.RecenterUnavailable', 'Saved trip view unavailable');
};

/// Focuses the active target through the map adapter and reports only local toolbar status.
const focusActiveEntity = async (): Promise<void> => {
  if (!state.value || !mapAdapter) {
    return;
  }

  const target = editorSurface.activeTarget.value;
  const result = await mapAdapter.focusActiveEntity(state.value, target);
  if (!result || target?.identity !== editorSurface.activeTarget.value?.identity) return;
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

  planningWorkspace.reconcileDeleted(result.deletedIds);
  const next: EditorTripState = {
    ...state.value,
    metadata: result.affected.metadata ? planningWorkspace.publishPlanning(result, 'trip', result.affected.metadata, state.value.metadata) : state.value.metadata,
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
    if (planningWorkspace.isDeleted('region', region.id)) return;
    next.regionsById[region.id] = planningWorkspace.publishPlanning(result, 'region', region, next.regionsById[region.id]);
  });
  result.affected.places.forEach(place => {
    if (planningWorkspace.isDeleted('place', place.id)) return;
    next.placesById[place.id] = planningWorkspace.publishPlanning(result, 'place', place, next.placesById[place.id]);
  });
  result.affected.areas.forEach(area => {
    if (planningWorkspace.isDeleted('area', area.id)) return;
    next.areasById[area.id] = planningWorkspace.publishPlanning(result, 'area', area, next.areasById[area.id]);
  });
  result.affected.segments.forEach(segment => {
    if (planningWorkspace.isDeleted('segment', segment.id)) return;
    next.segmentsById[segment.id] = planningWorkspace.publishPlanning(result, 'segment', segment.externalRouting
      ? segment
      : { ...segment, externalRouting: next.segmentsById[segment.id]?.externalRouting ?? null }, next.segmentsById[segment.id]);
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
</script>

<template>
  <div ref="workspaceElement" class="trip-editor-workspace" tabindex="-1">
    <div v-if="isLoading" class="trip-editor-state trip-editor-state--loading">
      <div class="spinner-border" role="status" :aria-label="localize('Editor.Loading', 'Loading')"></div>
      <span>{{ localize("Editor.LoadingEditor", "Loading trip editor...") }}</span>
    </div>

    <div v-else-if="error" class="trip-editor-state trip-editor-state--error">
      <strong>{{ localize("Editor.TripEditorUnavailable", "Trip Editor unavailable") }}</strong>
      <span>{{ error }}</span>
    </div>

    <TripSidebarLayout v-else-if="state" :surface-mode="editorSurface.surfaceMode.value">
      <TripSidebar
        :state="state"
        :captured-map-view="capturedMapView"
        :editor-surface="editorSurface"
        :editor-endpoint="props.config.editorEndpoint"
        :antiforgery-token="props.config.antiforgeryToken"
        :trip-index-url="props.config.tripIndexUrl"
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
        @mutation-applied="applyMutation"
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
      <template #map>
      <main class="trip-editor-map-shell">
        <header class="trip-editor-toolbar">
          <div class="trip-editor-toolbar__context">
            <span>{{ toolbarEyebrow }}</span>
            <strong>{{ toolbarTitle }}</strong>
            <small class="trip-editor-toolbar__status" role="status">{{ toolbarDetail }}</small>
          </div>
          <div class="trip-editor-toolbar__actions">
            <template v-if="!isMapWorkActive">
              <button type="button" class="btn btn-outline-light btn-sm" :disabled="!canFitAllGeometry" @click="fitAllGeometry">{{ localize("Editor.FitAll", "Fit All") }}</button>
              <button type="button" class="btn btn-outline-light btn-sm" :disabled="!canRecenterSavedView" @click="recenterSavedTripView">
                {{ localize("Editor.RecenterSavedTripView", "Recenter Saved Trip View") }}
              </button>
              <button type="button" class="btn btn-outline-light btn-sm" :disabled="!canFocusTarget" @click="focusActiveEntity">{{ localize("Editor.FocusActiveEntity", "Focus Active Entity") }}</button>
              <button v-if="selectedPlace" type="button" class="btn btn-outline-light btn-sm" @click="clearSelectedPlace">{{ localize("Editor.ClearSelection", "Clear Selection") }}</button>
            </template>
          </div>
        </header>
        <MapWorkToolbar :controller="editorSurface" />
        <MapSearchControl
          v-if="!isMapWorkActive && !mobileDrawerActive"
          :active-target="activeEditorTarget"
          :antiforgery-token="props.config.antiforgeryToken"
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
          :aria-label="localize('Editor.TripMap', 'Trip map')"
          aria-describedby="trip-editor-map-description"
        ></div>
        <p id="trip-editor-map-description" class="visually-hidden">{{ mapAccessibleDescription }}</p>
      </main>
      </template>
    </TripSidebarLayout>

    <ConfirmDialog />
  </div>
</template>
