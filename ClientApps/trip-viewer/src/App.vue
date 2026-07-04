<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import type { TripViewerMountConfig } from './main';

type LoadStatus = 'loading' | 'loaded' | 'auth' | 'not-found' | 'server-error' | 'network-error' | 'invalid-config';

type ViewerState = {
  viewerMode?: string;
  ViewerMode?: string;
  trip?: ViewerTrip;
  Trip?: ViewerTrip;
  regionOrder?: unknown[];
  RegionOrder?: unknown[];
  placesById?: Record<string, unknown>;
  PlacesById?: Record<string, unknown>;
  segmentsById?: Record<string, unknown>;
  SegmentsById?: Record<string, unknown>;
};

type ViewerTrip = {
  name?: string;
  Name?: string;
  isPublic?: boolean;
  IsPublic?: boolean;
  updatedAt?: string;
  UpdatedAt?: string;
};

const props = defineProps<{
  config: TripViewerMountConfig | null;
  configError: string | null;
}>();

const status = ref<LoadStatus>(props.configError ? 'invalid-config' : 'loading');
const state = ref<ViewerState | null>(null);
const detail = ref(props.configError ?? '');

const isEmbed = computed(() => props.config?.viewerMode === 'embed');
const trip = computed(() => state.value ? readObject<ViewerTrip>(state.value, 'trip') : null);
const stateViewerMode = computed(() => state.value ? readString(state.value, 'viewerMode') : null);
const tripName = computed(() => trip.value ? readString(trip.value, 'name') : props.config?.tripName ?? 'Trip');
const regionCount = computed(() => state.value ? readArray(state.value, 'regionOrder').length : 0);
const placeCount = computed(() => state.value ? Object.keys(readRecord(state.value, 'placesById')).length : 0);
const segmentCount = computed(() => state.value ? Object.keys(readRecord(state.value, 'segmentsById')).length : 0);

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

    state.value = await response.json() as ViewerState;
    status.value = 'loaded';
  } catch (error) {
    status.value = 'network-error';
    detail.value = error instanceof Error ? error.message : 'The Trip Viewer state request failed.';
  }
}

// Reads DTO fields from either PascalCase or camelCase JSON without changing the backend contract.
function readValue<T>(source: Record<string, unknown>, key: string): T | null {
  const pascalKey = `${key.charAt(0).toUpperCase()}${key.slice(1)}`;
  return (source[key] ?? source[pascalKey] ?? null) as T | null;
}

function readObject<T extends Record<string, unknown>>(source: Record<string, unknown>, key: string): T | null {
  const value = readValue<unknown>(source, key);
  return value && typeof value === 'object' && !Array.isArray(value) ? value as T : null;
}

function readString(source: Record<string, unknown>, key: string): string | null {
  const value = readValue<unknown>(source, key);
  return typeof value === 'string' ? value : null;
}

function readArray(source: Record<string, unknown>, key: string): unknown[] {
  const value = readValue<unknown>(source, key);
  return Array.isArray(value) ? value : [];
}

function readRecord(source: Record<string, unknown>, key: string): Record<string, unknown> {
  return readObject<Record<string, unknown>>(source, key) ?? {};
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

    <div v-else-if="status === 'loaded'" class="trip-viewer-state trip-viewer-state--loaded">
      <div>
        <strong>{{ tripName }}</strong>
        <span>Trip Viewer DTO loaded for {{ stateViewerMode ?? props.config?.viewerMode }} mode.</span>
      </div>
      <dl class="trip-viewer-summary">
        <div>
          <dt>Regions</dt>
          <dd>{{ regionCount }}</dd>
        </div>
        <div>
          <dt>Places</dt>
          <dd>{{ placeCount }}</dd>
        </div>
        <div>
          <dt>Segments</dt>
          <dd>{{ segmentCount }}</dd>
        </div>
      </dl>
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
