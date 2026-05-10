<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue';
import { loadEditorState } from './api/tripEditorApi';
import ConfirmDialog from './components/ConfirmDialog.vue';
import MapWorkToolbar from './components/MapWorkToolbar.vue';
import TripSidebar from './components/TripSidebar.vue';
import { disposeConfirmDialogHost, setConfirmDialogFocusFallback } from './composables/useConfirmDialog';
import { useEditorSurface } from './composables/useEditorSurface';
import { canFocusActiveEntity, createTripEditorMap, hasAnyGeometry, hasSavedTripView, type FocusActiveEntityResult } from './map/leafletAdapter';
import type { BootstrapConfig, EditorMutationResult, EditorTripMetadata, EditorTripState } from './types';

const props = defineProps<{ config: BootstrapConfig }>();

const state = ref<EditorTripState | null>(null);
const error = ref<string | null>(null);
const isLoading = ref(true);
const hasRegionDraftChanges = ref(false);
const workspaceElement = ref<HTMLElement | null>(null);
const mapElement = ref<HTMLElement | null>(null);
const navigationStatus = ref<string | null>(null);
const editorSurface = useEditorSurface();
let mapAdapter: ReturnType<typeof createTripEditorMap> | null = null;

const updatedLabel = computed(() =>
  state.value ? new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(state.value.metadata.updatedAt)) : ''
);
const isMapWorkActive = computed(() => editorSurface.isMapWorkActive.value);
const toolbarContext = computed(() => {
  if (editorSurface.mapWork.value) {
    return `${editorSurface.mapWork.value.modeName}: ${editorSurface.mapWork.value.statusText}`;
  }

  return editorSurface.activeTarget.value?.title ?? 'Trip map';
});
const canFitAllGeometry = computed(() => Boolean(state.value && hasAnyGeometry(state.value)));
const canRecenterSavedView = computed(() => Boolean(state.value && hasSavedTripView(state.value.metadata)));
const canFocusTarget = computed(() => Boolean(state.value && canFocusActiveEntity(state.value, editorSurface.activeTarget.value)));

onMounted(async () => {
  setConfirmDialogFocusFallback(workspaceElement.value);

  try {
    const loadedState = await loadEditorState(props.config.editorEndpoint);
    state.value = loadedState;
    isLoading.value = false;
    await nextTick();
    if (!mapElement.value) {
      throw new Error('Trip Editor map element was unavailable after the workspace rendered.');
    }

    mapAdapter = createTripEditorMap(mapElement.value, props.config.tilesUrl);
    mapAdapter.render(loadedState);
  } catch (loadError) {
    error.value = loadError instanceof Error ? loadError.message : 'Trip Editor failed to load.';
    isLoading.value = false;
  }
});

onUnmounted(() => {
  disposeConfirmDialogHost();
  setConfirmDialogFocusFallback(null);
  mapAdapter?.dispose();
});

const applyMetadata = (metadata: EditorTripMetadata): void => {
  if (!state.value) {
    return;
  }

  state.value = { ...state.value, metadata };
  mapAdapter?.render(state.value);
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
      <span>Loading trip editor workspace...</span>
    </div>

    <div v-else-if="error" class="trip-editor-state trip-editor-state--error">
      <strong>Workspace unavailable</strong>
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
        @metadata-saved="applyMetadata"
        @mutation-applied="applyMutation"
        @region-draft-dirty-changed="setRegionDraftChanges"
      />
      <main class="trip-editor-map-shell">
        <header class="trip-editor-toolbar">
          <div class="trip-editor-toolbar__context">
            <span>{{ state.metadata.isPublic ? 'Public trip' : 'Private trip' }}</span>
            <strong>Updated {{ updatedLabel }}</strong>
            <small>{{ toolbarContext }}</small>
            <small v-if="navigationStatus" class="trip-editor-toolbar__status" role="status">{{ navigationStatus }}</small>
          </div>
          <div class="trip-editor-toolbar__actions">
            <template v-if="!isMapWorkActive">
              <button type="button" class="btn btn-outline-light btn-sm" :disabled="!canFitAllGeometry" @click="fitAllGeometry">Fit All</button>
              <button type="button" class="btn btn-outline-light btn-sm" :disabled="!canRecenterSavedView" @click="recenterSavedTripView">
                Recenter Saved Trip View
              </button>
              <button type="button" class="btn btn-outline-light btn-sm" :disabled="!canFocusTarget" @click="focusActiveEntity">Focus Active Entity</button>
            </template>
            <a class="btn btn-outline-light btn-sm" :href="`/User/Trip/Edit/${state.tripId}`">Legacy editor</a>
          </div>
        </header>
        <MapWorkToolbar :controller="editorSurface" />
        <div ref="mapElement" class="trip-editor-map" aria-label="Read-only trip map"></div>
      </main>
    </template>

    <ConfirmDialog />
  </div>
</template>
