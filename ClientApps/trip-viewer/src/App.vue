<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import type { TripViewerMountConfig } from './main';
import type { TripViewerState, ViewerSelection } from './types';
import { normalizeViewerState } from './state';
import { buildRegionGroups, buildSegmentSummaries, selectedEntity, tripSelection } from './viewModel';
import TripMap from './components/TripMap.vue';
import TripSidebar from './components/TripSidebar.vue';
import TripDetail from './components/TripDetail.vue';

type LoadStatus = 'loading' | 'loaded' | 'auth' | 'not-found' | 'server-error' | 'network-error' | 'invalid-config';

const props = defineProps<{
  config: TripViewerMountConfig | null;
  configError: string | null;
}>();

const status = ref<LoadStatus>(props.configError ? 'invalid-config' : 'loading');
const state = ref<TripViewerState | null>(null);
const selection = ref<ViewerSelection | null>(null);
const detail = ref(props.configError ?? '');

const isEmbed = computed(() => props.config?.viewerMode === 'embed' || state.value?.viewerMode === 'embed');
const regionGroups = computed(() => state.value ? buildRegionGroups(state.value) : []);
const segments = computed(() => state.value ? buildSegmentSummaries(state.value) : []);
const selected = computed(() => state.value && selection.value ? selectedEntity(state.value, selection.value) : null);

onMounted(() => {
  if (!props.config) {
    return;
  }

  void loadViewerState();
});

// Fetches the server-emitted #335 state endpoint without deriving privileged URLs client-side.
async function loadViewerState(): Promise<void> {
  if (!props.config) {
    return;
  }

  status.value = 'loading';
  detail.value = '';

  try {
    const response = await fetch(props.config.viewerStateEndpoint, {
      credentials: 'same-origin',
      headers: { Accept: 'application/json' }
    });

    if (response.status === 401 || response.status === 403) {
      status.value = 'auth';
      detail.value = 'Authentication is required or this trip is not available to this account.';
      return;
    }

    if (response.status === 404) {
      status.value = 'not-found';
      detail.value = 'The trip was not found or is not public.';
      return;
    }

    if (!response.ok) {
      status.value = 'server-error';
      detail.value = `Trip Viewer state failed with HTTP ${response.status}.`;
      return;
    }

    const loadedState = normalizeViewerState(await response.json());
    state.value = loadedState;
    selection.value = tripSelection(loadedState);
    status.value = 'loaded';
  } catch (error) {
    status.value = 'network-error';
    detail.value = error instanceof Error ? error.message : 'The Trip Viewer state request failed.';
  }
}

function selectEntity(nextSelection: ViewerSelection): void {
  selection.value = nextSelection;
}
</script>

<template>
  <section class="trip-viewer-preview" :class="{ 'trip-viewer-preview--embed': isEmbed }" aria-live="polite">
    <div v-if="status === 'loading'" class="trip-viewer-state">
      <span class="trip-viewer-state__spinner" aria-hidden="true"></span>
      <div>
        <strong>Loading trip viewer</strong>
        <span>{{ props.config?.tripName ?? 'Fetching trip state' }}</span>
      </div>
    </div>

    <div v-else-if="status === 'loaded' && state && selected && selection" class="trip-viewer-workspace">
      <TripSidebar
        :state="state"
        :groups="regionGroups"
        :segments="segments"
        :selection="selection"
        @select="selectEntity"
      />
      <TripMap
        :state="state"
        :segments="segments"
        :selection="selection"
        @select="selectEntity"
      />
      <TripDetail
        :state="state"
        :entity="selected"
        @focus="selectEntity"
      />
    </div>

    <div v-else class="trip-viewer-state trip-viewer-state--error" role="alert">
      <strong v-if="status === 'auth'">Trip unavailable</strong>
      <strong v-else-if="status === 'not-found'">Trip not found</strong>
      <strong v-else-if="status === 'invalid-config'">Trip Viewer configuration error</strong>
      <strong v-else>Trip Viewer failed to load</strong>
      <span>{{ detail }}</span>
      <button v-if="status === 'network-error' || status === 'server-error'" type="button" @click="loadViewerState">
        Retry
      </button>
    </div>
  </section>
</template>
