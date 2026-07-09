<script setup lang="ts">
import { computed, ref } from 'vue';
import type { SearchResult } from '../search';
import { hasSearchQuery, searchViewerState } from '../search';
import type { TripViewerState, ViewerSelection } from '../types';

const props = defineProps<{
  state: TripViewerState;
}>();

const emit = defineEmits<{
  select: [selection: ViewerSelection];
  clear: [];
}>();

const query = ref('');
const hasQuery = computed(() => hasSearchQuery(query.value));
const results = computed(() => hasQuery.value ? searchViewerState(props.state, query.value) : []);

function selectResult(result: SearchResult): void {
  emit('select', result.selection);
}

function clearSearch(): void {
  query.value = '';
  emit('clear');
}
</script>

<template>
  <section class="trip-viewer-search" aria-label="Search viewer content">
    <label>
      <span>Search</span>
      <input v-model="query" type="search" placeholder="Search places, notes, tags" autocomplete="off">
    </label>

    <button v-if="hasQuery" type="button" class="trip-viewer-search__clear" @click="clearSearch">Clear</button>

    <div v-if="hasQuery" class="trip-viewer-search__results" aria-live="polite">
      <p v-if="results.length === 0" class="trip-viewer-empty">No matching trip content.</p>
      <button
        v-for="result in results"
        v-else
        :key="result.key"
        type="button"
        class="trip-viewer-search__result"
        @click="selectResult(result)"
      >
        <span>{{ result.type }}</span>
        <strong>{{ result.label }}</strong>
        <small>{{ result.context }}</small>
      </button>
    </div>
  </section>
</template>
