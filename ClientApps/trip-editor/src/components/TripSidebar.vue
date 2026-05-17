<script setup lang="ts">
import { computed, ref } from 'vue';
import type { EditorArea, EditorGeocodeSearchResult, EditorMutationResult, EditorPlace, EditorRegion, EditorSegment, EditorTripState, Guid } from '../types';
import type { EditorSurfaceController } from '../composables/useEditorSurface';
import MetadataEditor from './MetadataEditor.vue';
import RegionManager from './RegionManager.vue';
import SegmentManager from './SegmentManager.vue';
import VisitProgressSurface from './VisitProgressSurface.vue';
import type { AreaPolygonWorkOptions, CoordinatePickOptions, SegmentRouteWorkOptions } from '../map/leafletAdapter';

type SidebarSearchResult = {
  hasMatches: boolean;
  regions: EditorRegion[];
  placesByRegionId: Record<Guid, Guid[]>;
  areasByRegionId: Record<Guid, Guid[]>;
};

type PlaceDraftPreview = {
  iconName: string;
  markerColor: string;
  placeId: Guid;
};

const props = defineProps<{
  state: EditorTripState;
  editorSurface: EditorSurfaceController;
  editorEndpoint: string;
  antiforgeryToken: string;
  tripIndexUrl: string;
  hasRegionDraftChanges: boolean;
  hiddenSegmentIds: ReadonlySet<Guid>;
  selectedPlaceId: Guid | null;
  pendingSearchAdd: { result: EditorGeocodeSearchResult; regionId: Guid; requestId: number } | null;
  coordinatePicker: { startCoordinatePick: (options: CoordinatePickOptions) => () => void };
  polygonEditor: { startAreaPolygonWork: (options: AreaPolygonWorkOptions) => () => void };
  routeEditor: {
    setSegmentRouteWorkRoute: (route: EditorSegment['route']) => void;
    startSegmentRouteWork: (options: SegmentRouteWorkOptions) => () => void;
  };
  selectPlace: (placeId: Guid) => Promise<boolean>;
  clearSelectedPlace: () => Promise<boolean>;
}>();

const emit = defineEmits<{
  metadataSaved: [metadata: EditorTripState['metadata']];
  mutationApplied: [result: EditorMutationResult<unknown>];
  regionDraftDirtyChanged: [isDirty: boolean];
  hiddenSegmentIdsChanged: [ids: Set<Guid>];
  placeDraftPreviewChanged: [preview: PlaceDraftPreview | null];
  searchAddOpened: [requestId: number];
}>();

const searchQuery = ref('');
const segmentDraftDirty = ref(false);
const isVisitProgressOpen = ref(false);
const normalizedSearchQuery = computed(() => normalize(searchQuery.value));
const searchMinimumCharacters = computed(() => props.state.options.limits?.sidebarSearchMinCharacters ?? 1);
const isSearchActive = computed(() => normalizedSearchQuery.value.length >= searchMinimumCharacters.value);
const orderedSegments = computed(() => props.state.segmentOrder.map(id => props.state.segmentsById[id]).filter(Boolean) as EditorSegment[]);
const filteredSegments = computed(() => (isSearchActive.value ? orderedSegments.value.filter(segment => matchesSegment(props.state, segment, normalizedSearchQuery.value)) : orderedSegments.value));
const sidebarSearch = computed<SidebarSearchResult>(() => {
  if (!isSearchActive.value) {
    return {
      hasMatches: true,
      regions: orderedVisibleRegions(props.state),
      placesByRegionId: props.state.placeOrderByRegionId,
      areasByRegionId: props.state.areaOrderByRegionId
    };
  }

  const result: SidebarSearchResult = {
    hasMatches: false,
    regions: [],
    placesByRegionId: {},
    areasByRegionId: {}
  };

  for (const region of orderedVisibleRegions(props.state)) {
    const regionMatches = includesSearch(region.name, normalizedSearchQuery.value);
    const places = orderedPlaces(props.state, region.id);
    const areas = orderedAreas(props.state, region.id);
    const matchingPlaces = regionMatches ? places : places.filter(place => matchesPlace(place, normalizedSearchQuery.value));
    const matchingAreas = regionMatches ? areas : areas.filter(area => includesSearch(area.name, normalizedSearchQuery.value));

    if (!regionMatches && matchingPlaces.length === 0 && matchingAreas.length === 0) {
      continue;
    }

    result.hasMatches = true;
    result.regions.push(region);
    result.placesByRegionId[region.id] = matchingPlaces.map(place => place.id);
    result.areasByRegionId[region.id] = matchingAreas.map(area => area.id);
  }

  return result;
});
const hasSidebarSearchMatches = computed(() => sidebarSearch.value.hasMatches || filteredSegments.value.length > 0);
const hasAnyDraftChanges = computed(() => props.hasRegionDraftChanges || segmentDraftDirty.value);

const segmentLabel = (state: EditorTripState, segment: EditorSegment): string => {
  const from = segment.fromPlaceId ? state.placesById[segment.fromPlaceId]?.name : null;
  const to = segment.toPlaceId ? state.placesById[segment.toPlaceId]?.name : null;
  return [from, to].filter(Boolean).join(' to ') || segment.mode || 'Segment';
};

const transportModeLabel = (state: EditorTripState, mode: string): string | null =>
  state.options.transportModes.find(option => option.value === mode)?.label ?? null;

const segmentModeText = (state: EditorTripState, segment: EditorSegment): string =>
  (transportModeLabel(state, segment.mode) ?? segment.mode) || 'mode unset';

function orderedVisibleRegions(state: EditorTripState): EditorRegion[] {
  return state.regionOrder.map(id => state.regionsById[id]).filter(region => region && (!region.isShadow || hasRegionChildren(state, region))) as EditorRegion[];
}

function orderedPlaces(state: EditorTripState, regionId: Guid): EditorPlace[] {
  return (state.placeOrderByRegionId[regionId] ?? []).map(id => state.placesById[id]).filter(Boolean) as EditorPlace[];
}

function orderedAreas(state: EditorTripState, regionId: Guid): EditorArea[] {
  return (state.areaOrderByRegionId[regionId] ?? []).map(id => state.areasById[id]).filter(Boolean) as EditorArea[];
}

function hasRegionChildren(state: EditorTripState, region: EditorRegion): boolean {
  return orderedPlaces(state, region.id).length > 0 || orderedAreas(state, region.id).length > 0;
}

function matchesPlace(place: EditorPlace, query: string): boolean {
  return includesSearch(place.name, query) || includesSearch(place.address, query);
}

function matchesSegment(state: EditorTripState, segment: EditorSegment, query: string): boolean {
  const from = segment.fromPlaceId ? state.placesById[segment.fromPlaceId]?.name : null;
  const to = segment.toPlaceId ? state.placesById[segment.toPlaceId]?.name : null;
  return [segmentLabel(state, segment), segment.mode, transportModeLabel(state, segment.mode), from, to].some(value => includesSearch(value, query));
}

function includesSearch(value: string | null | undefined, query: string): boolean {
  return normalize(value ?? '').includes(query);
}

function normalize(value: string): string {
  return value.trim().toLocaleLowerCase();
}
</script>

<template>
  <aside class="trip-editor-sidebar">
    <header class="trip-editor-sidebar__header">
      <div>
        <p class="trip-editor-sidebar__eyebrow">Trip Editor</p>
        <h1>{{ state.metadata.name }}</h1>
      </div>
      <span class="trip-editor-sidebar__status">{{ state.metadata.isPublic ? 'Public' : 'Private' }}</span>
    </header>

    <MetadataEditor
      :metadata="state.metadata"
      :tags-by-slug="state.tagsBySlug"
      :tag-order="state.tagOrder"
      :tag-options="state.options.tag"
      :editor-surface="editorSurface"
      :editor-endpoint="editorEndpoint"
      :antiforgery-token="antiforgeryToken"
      :trip-index-url="tripIndexUrl"
      :has-region-draft-changes="hasAnyDraftChanges"
      @saved="metadata => emit('metadataSaved', metadata)"
      @mutation-applied="result => emit('mutationApplied', result)"
    />

    <section v-if="state.permissions.canReadVisitProgress" class="trip-editor-panel">
      <div class="trip-editor-panel__line">
        <span>Visit progress</span>
        <strong>{{ state.visitProgress.percentVisited }}%</strong>
      </div>
      <div class="trip-editor-progress" aria-hidden="true">
        <span :style="{ width: `${state.visitProgress.percentVisited}%` }"></span>
      </div>
      <div class="trip-editor-panel__line trip-editor-visit-progress-entry">
        <p>{{ state.visitProgress.visitedPlaces }} / {{ state.visitProgress.totalPlaces }} places visited</p>
        <button type="button" class="btn btn-outline-light btn-sm" @click="isVisitProgressOpen = true">Visits</button>
      </div>
    </section>

    <VisitProgressSurface
      v-if="state.permissions.canReadVisitProgress"
      :is-open="isVisitProgressOpen"
      :state="state"
      :editor-surface="editorSurface"
      @close="isVisitProgressOpen = false"
    />

    <section v-if="state.tagOrder.length > 0" class="trip-editor-panel">
      <h2>Tags</h2>
      <div class="trip-editor-tags">
        <span v-for="slug in state.tagOrder" :key="slug">{{ state.tagsBySlug[slug]?.name }}</span>
      </div>
    </section>

    <section class="trip-editor-panel trip-editor-sidebar-search">
      <label class="trip-editor-field">
        <span>Sidebar search</span>
        <input v-model="searchQuery" type="search" autocomplete="off" :placeholder="`Search regions, places, areas, segments`" />
      </label>
      <p v-if="isSearchActive && !hasSidebarSearchMatches" class="trip-editor-empty-state">No matching regions, places, areas, or segments.</p>
    </section>

    <RegionManager
      :state="state"
      :editor-surface="editorSurface"
      :editor-endpoint="editorEndpoint"
      :antiforgery-token="antiforgeryToken"
      :coordinate-picker="coordinatePicker"
      :polygon-editor="polygonEditor"
      :pending-search-add="pendingSearchAdd"
      :selected-place-id="selectedPlaceId"
      :search-active="isSearchActive"
      :search-regions="sidebarSearch.regions"
      :search-place-ids-by-region-id="sidebarSearch.placesByRegionId"
      :search-area-ids-by-region-id="sidebarSearch.areasByRegionId"
      :select-place="selectPlace"
      :clear-selected-place="clearSelectedPlace"
      @mutation-applied="result => emit('mutationApplied', result)"
      @dirty-state-changed="isDirty => emit('regionDraftDirtyChanged', isDirty)"
      @place-draft-preview-changed="preview => emit('placeDraftPreviewChanged', preview)"
      @search-add-opened="requestId => emit('searchAddOpened', requestId)"
    />

    <SegmentManager
      :state="state"
      :editor-surface="editorSurface"
      :editor-endpoint="editorEndpoint"
      :antiforgery-token="antiforgeryToken"
      :hidden-segment-ids="hiddenSegmentIds"
      :route-editor="routeEditor"
      :search-active="isSearchActive"
      :segments="filteredSegments"
      @dirty-state-changed="isDirty => { segmentDraftDirty = isDirty; emit('regionDraftDirtyChanged', hasAnyDraftChanges); }"
      @hidden-segment-ids-changed="ids => emit('hiddenSegmentIdsChanged', ids)"
      @mutation-applied="result => emit('mutationApplied', result)"
    />
  </aside>
</template>
