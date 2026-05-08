<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { loadEditorState } from './api/tripEditorApi';
import TripSidebar from './components/TripSidebar.vue';
import { createTripEditorMap } from './map/leafletAdapter';
import type { BootstrapConfig, EditorMutationResult, EditorTripMetadata, EditorTripState } from './types';

const props = defineProps<{ config: BootstrapConfig }>();

const state = ref<EditorTripState | null>(null);
const error = ref<string | null>(null);
const isLoading = ref(true);
const hasRegionDraftChanges = ref(false);
const mapElement = ref<HTMLElement | null>(null);
let mapAdapter: ReturnType<typeof createTripEditorMap> | null = null;

const updatedLabel = computed(() =>
  state.value ? new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(state.value.metadata.updatedAt)) : ''
);

onMounted(async () => {
  try {
    state.value = await loadEditorState(props.config.editorEndpoint);
    if (mapElement.value) {
      mapAdapter = createTripEditorMap(mapElement.value, props.config.tilesUrl);
      mapAdapter.render(state.value);
    }
  } catch (loadError) {
    error.value = loadError instanceof Error ? loadError.message : 'Trip Editor failed to load.';
  } finally {
    isLoading.value = false;
  }
});

onUnmounted(() => {
  mapAdapter?.dispose();
});

const applyMetadata = (metadata: EditorTripMetadata): void => {
  if (!state.value) {
    return;
  }

  state.value = { ...state.value, metadata };
  mapAdapter?.render(state.value);
};

/// Tracks region draft changes that live inside the sidebar child component.
const setRegionDraftChanges = (isDirty: boolean): void => {
  hasRegionDraftChanges.value = isDirty;
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

  state.value = next;
  mapAdapter?.render(next);
};
</script>

<template>
  <div class="trip-editor-workspace">
    <div v-if="isLoading" class="trip-editor-state trip-editor-state--loading">
      <div class="spinner-border" role="status" aria-label="Loading"></div>
      <span>Loading trip editor workspace...</span>
    </div>

    <div v-else-if="error" class="trip-editor-state trip-editor-state--error">
      <strong>Workspace unavailable</strong>
      <span>{{ error }}</span>
    </div>

    <template v-else-if="state">
      <TripSidebar
        :state="state"
        :editor-endpoint="props.config.editorEndpoint"
        :antiforgery-token="props.config.antiforgeryToken"
        :trip-index-url="props.config.tripIndexUrl"
        :has-region-draft-changes="hasRegionDraftChanges"
        @metadata-saved="applyMetadata"
        @mutation-applied="applyMutation"
        @region-draft-dirty-changed="setRegionDraftChanges"
      />
      <main class="trip-editor-map-shell">
        <header class="trip-editor-toolbar">
          <div>
            <span>{{ state.metadata.isPublic ? 'Public trip' : 'Private trip' }}</span>
            <strong>Updated {{ updatedLabel }}</strong>
          </div>
          <a class="btn btn-outline-light btn-sm" :href="`/User/Trip/Edit/${state.tripId}`">Legacy editor</a>
        </header>
        <div ref="mapElement" class="trip-editor-map" aria-label="Read-only trip map"></div>
      </main>
    </template>
  </div>
</template>
