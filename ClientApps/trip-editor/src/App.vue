<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { loadEditorState } from './api/tripEditorApi';
import TripSidebar from './components/TripSidebar.vue';
import { createTripEditorMap } from './map/leafletAdapter';
import type { BootstrapConfig, EditorTripState } from './types';

const props = defineProps<{ config: BootstrapConfig }>();

const state = ref<EditorTripState | null>(null);
const error = ref<string | null>(null);
const isLoading = ref(true);
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
      <TripSidebar :state="state" />
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
