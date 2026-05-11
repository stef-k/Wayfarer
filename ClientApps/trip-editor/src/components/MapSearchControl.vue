<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue';
import { EditorValidationError, searchGeocode } from '../api/tripEditorApi';
import type { EditorTarget } from '../composables/useEditorSurface';
import type { EditorGeocodeSearchResult, EditorRegion, EditorTripState, Guid } from '../types';

const props = defineProps<{
  activeTarget: EditorTarget | null;
  editorEndpoint: string;
  state: EditorTripState;
}>();

const emit = defineEmits<{
  addPlace: [request: { result: EditorGeocodeSearchResult; regionId: Guid; requestId: number }];
  clearPreview: [];
  preview: [result: EditorGeocodeSearchResult];
}>();

type SearchStatus = 'idle' | 'loading' | 'no-results' | 'validation' | 'provider-unavailable' | 'rate-limit' | 'error' | 'success';

const query = ref('');
const selectedResultId = ref<string | null>(null);
const selectedRegionId = ref<Guid | ''>('');
const status = ref<SearchStatus>('idle');
const errorText = ref<string | null>(null);
const attribution = ref<string | null>(null);
const results = ref<EditorGeocodeSearchResult[]>([]);
const submittedQuery = ref('');
let controller: AbortController | null = null;
let requestSequence = 0;
let addSequence = 0;

const trimmedQuery = computed(() => query.value.trim());
const minChars = 3;
const limit = computed(() => props.state.options.limits.nominatimSearchLimit);
const canSearch = computed(() => trimmedQuery.value.length >= minChars && status.value !== 'loading');
const eligibleRegions = computed(() => props.state.regionOrder
  .map(id => props.state.regionsById[id])
  .filter((region): region is EditorRegion => Boolean(region) && !region.isShadow && props.state.permissions.canEditPlaces && region.capabilities.canAddChildren));
const selectedResult = computed(() => results.value.find(result => result.id === selectedResultId.value) ?? null);
const canAdd = computed(() => Boolean(selectedResult.value && selectedRegionId.value));
const helperText = computed(() => eligibleRegions.value.length === 0 ? 'No editable region is available for this search result.' : null);
const statusText = computed(() => {
  if (status.value === 'loading') {
    return 'Searching map...';
  }

  if (status.value === 'no-results') {
    return 'No map search results.';
  }

  if (status.value === 'validation') {
    return errorText.value ?? 'Search query must be at least 3 characters.';
  }

  if (status.value === 'provider-unavailable') {
    return 'Map search provider is unavailable.';
  }

  if (status.value === 'rate-limit') {
    return 'Map search is rate limited. Try again shortly.';
  }

  if (status.value === 'error') {
    return 'Map search failed.';
  }

  return null;
});

watch(query, value => {
  if (value.trim().length === 0) {
    clearResults();
  }
});

watch(eligibleRegions, regions => {
  if (regions.length === 1) {
    selectedRegionId.value = regions[0].id;
    return;
  }

  if (regions.length > 1 && props.activeTarget?.kind === 'place' && props.activeTarget.parentRegionId && regions.some(region => region.id === props.activeTarget?.parentRegionId)) {
    selectedRegionId.value = props.activeTarget.parentRegionId;
    return;
  }

  if (!regions.some(region => region.id === selectedRegionId.value)) {
    selectedRegionId.value = '';
  }
}, { immediate: true });

const submitSearch = async (): Promise<void> => {
  const submitted = trimmedQuery.value;
  if (submitted.length < minChars || status.value === 'loading') {
    return;
  }

  controller?.abort();
  controller = new AbortController();
  const sequence = ++requestSequence;
  submittedQuery.value = submitted;
  status.value = 'loading';
  errorText.value = null;
  selectedResultId.value = null;
  emit('clearPreview');

  try {
    const response = await searchGeocode(props.editorEndpoint, submitted, limit.value, controller.signal);
    if (sequence !== requestSequence || response.query !== submittedQuery.value) {
      return;
    }

    results.value = response.results.slice(0, limit.value);
    attribution.value = response.attribution;
    status.value = results.value.length === 0 ? 'no-results' : 'success';
  } catch (error) {
    if (controller.signal.aborted || sequence !== requestSequence) {
      return;
    }

    if (error instanceof EditorValidationError) {
      status.value = 'validation';
      errorText.value = Object.values(error.errors).flat()[0] ?? error.message;
    } else if (error instanceof Error && error.message === 'geocode-rate-limited') {
      status.value = 'rate-limit';
    } else if (error instanceof Error && error.message === 'geocode-provider-unavailable') {
      status.value = 'provider-unavailable';
    } else {
      status.value = 'error';
    }
  }
};

const onSubmit = async (): Promise<void> => {
  await submitSearch();
};

const selectResult = (result: EditorGeocodeSearchResult): void => {
  selectedResultId.value = result.id;
  emit('preview', result);
};

const addAsPlace = (): void => {
  if (!selectedResult.value || !selectedRegionId.value) {
    return;
  }

  emit('addPlace', { result: selectedResult.value, regionId: selectedRegionId.value, requestId: ++addSequence });
};

const clearResults = (): void => {
  controller?.abort();
  controller = null;
  status.value = 'idle';
  errorText.value = null;
  attribution.value = null;
  results.value = [];
  selectedResultId.value = null;
  submittedQuery.value = '';
  emit('clearPreview');
};

const resultMeta = (result: EditorGeocodeSearchResult): string =>
  [result.type, result.category].filter(Boolean).join(' / ');

const roundedCoordinate = (value: number): string => value.toFixed(5);

onUnmounted(() => {
  clearResults();
});
</script>

<template>
  <section class="trip-editor-map-search" aria-label="Map search">
    <form class="trip-editor-map-search__form" @submit.prevent="onSubmit">
      <label class="trip-editor-map-search__label" for="trip-editor-map-search-input">Map search</label>
      <input
        id="trip-editor-map-search-input"
        v-model="query"
        type="search"
        autocomplete="off"
        placeholder="Search address or place"
      />
      <button type="submit" class="btn btn-primary btn-sm" :disabled="!canSearch">Search</button>
    </form>

    <p v-if="statusText" class="trip-editor-map-search__status" :role="status === 'loading' ? 'status' : 'alert'">{{ statusText }}</p>

    <div v-if="results.length > 0" class="trip-editor-map-search__results">
      <button
        v-for="result in results"
        :key="result.id"
        type="button"
        class="trip-editor-map-search__result"
        :class="{ 'trip-editor-map-search__result--selected': selectedResultId === result.id }"
        @click="selectResult(result)"
      >
        <strong>{{ result.name }}</strong>
        <span>{{ result.address || result.displayName }}</span>
        <small>{{ resultMeta(result) || 'geocode result' }} · {{ roundedCoordinate(result.latitude) }}, {{ roundedCoordinate(result.longitude) }}</small>
      </button>
      <small v-if="attribution" class="trip-editor-map-search__attribution">{{ attribution }}</small>
    </div>

    <div v-if="selectedResult" class="trip-editor-map-search__add-panel">
      <label>
        <span>Target region</span>
        <select v-model="selectedRegionId" :disabled="eligibleRegions.length <= 1">
          <option v-if="eligibleRegions.length !== 1" value="">Select region</option>
          <option v-for="region in eligibleRegions" :key="region.id" :value="region.id">{{ region.name }}</option>
        </select>
      </label>
      <p v-if="helperText" class="trip-editor-map-search__helper">{{ helperText }}</p>
      <button type="button" class="btn btn-success btn-sm" :disabled="!canAdd" @click="addAsPlace">Add as place</button>
    </div>
  </section>
</template>
