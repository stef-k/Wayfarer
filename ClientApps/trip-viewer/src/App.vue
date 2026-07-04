<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { TripViewerMountConfig } from './main';
import type { TripViewerState, ViewerSelection } from './types';
import { normalizeViewerState } from './state';
import { buildRegionGroups, buildSegmentSummaries, selectedEntity, tripSelection } from './viewModel';
import MobileDrawer from './components/MobileDrawer.vue';
import type { DrawerState } from './components/MobileDrawer.vue';
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
const drawerState = ref<DrawerState>('peek');
const detailReturnTarget = ref<'peek' | 'hierarchy'>('peek');
const isCompactViewport = ref(false);
const layoutSignal = ref(0);

const isEmbed = computed(() => props.config?.viewerMode === 'embed' || state.value?.viewerMode === 'embed');
const regionGroups = computed(() => state.value ? buildRegionGroups(state.value) : []);
const segments = computed(() => state.value ? buildSegmentSummaries(state.value) : []);
const selected = computed(() => state.value && selection.value ? selectedEntity(state.value, selection.value) : null);
const expandedMobileDrawer = computed(() => isCompactViewport.value && (drawerState.value === 'hierarchy' || drawerState.value === 'detail'));

onMounted(() => {
  updateViewportMode();
  window.addEventListener('resize', updateViewportMode);
  window.addEventListener('orientationchange', updateViewportMode);

  if (!props.config) {
    return;
  }

  void loadViewerState();
});

onBeforeUnmount(() => {
  window.removeEventListener('resize', updateViewportMode);
  window.removeEventListener('orientationchange', updateViewportMode);
  document.body.classList.remove('trip-viewer-body--drawer-open');
});

watch(expandedMobileDrawer, expanded => {
  document.body.classList.toggle('trip-viewer-body--drawer-open', expanded);
  signalLayoutAfterTransition();
});

watch(drawerState, () => {
  signalLayoutAfterTransition();
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
    drawerState.value = 'peek';
    detailReturnTarget.value = 'peek';
    status.value = 'loaded';
    signalLayoutAfterTransition();
  } catch (error) {
    status.value = 'network-error';
    detail.value = error instanceof Error ? error.message : 'The Trip Viewer state request failed.';
  }
}

function selectEntity(nextSelection: ViewerSelection, source: 'map' | 'desktop' | 'drawer' | 'hierarchy' = 'desktop'): void {
  selection.value = nextSelection;
  if (!isCompactViewport.value) {
    return;
  }

  if (nextSelection.type === 'trip') {
    drawerState.value = source === 'hierarchy' ? 'detail' : 'peek';
    detailReturnTarget.value = source === 'hierarchy' ? 'hierarchy' : 'peek';
    return;
  }

  drawerState.value = 'detail';
  detailReturnTarget.value = source === 'hierarchy' ? 'hierarchy' : 'peek';
}

function updateDrawerState(nextState: DrawerState): void {
  drawerState.value = nextState;
  if (nextState === 'hierarchy') {
    detailReturnTarget.value = 'hierarchy';
  }
}

function updateViewportMode(): void {
  const nextCompact = window.matchMedia('(max-width: 1023px)').matches;
  const wasCompact = isCompactViewport.value;
  isCompactViewport.value = nextCompact;

  if (nextCompact && !wasCompact && selection.value) {
    drawerState.value = selection.value.type === 'trip' ? 'peek' : 'detail';
    detailReturnTarget.value = 'peek';
  }

  if (!nextCompact) {
    document.body.classList.remove('trip-viewer-body--drawer-open');
  }

  signalLayoutAfterTransition();
}

function signalLayoutAfterTransition(): void {
  window.setTimeout(() => {
    void nextTick(() => {
      layoutSignal.value += 1;
    });
  }, 170);
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

    <div
      v-else-if="status === 'loaded' && state && selected && selection"
      class="trip-viewer-workspace"
      :class="`trip-viewer-workspace--drawer-${drawerState}`"
    >
      <TripSidebar
        :state="state"
        :groups="regionGroups"
        :segments="segments"
        :selection="selection"
        @select="selection => selectEntity(selection, 'desktop')"
      />
      <TripMap
        :state="state"
        :segments="segments"
        :selection="selection"
        :layout-signal="layoutSignal"
        @select="selection => selectEntity(selection, 'map')"
      />
      <TripDetail
        :state="state"
        :entity="selected"
        @focus="selection => selectEntity(selection, 'desktop')"
      />
      <MobileDrawer
        :state="state"
        :groups="regionGroups"
        :segments="segments"
        :selection="selection"
        :entity="selected"
        :drawer-state="drawerState"
        :return-target="detailReturnTarget"
        @update:drawer-state="updateDrawerState"
        @select="selectEntity"
        @focus="selection => selectEntity(selection, 'drawer')"
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
