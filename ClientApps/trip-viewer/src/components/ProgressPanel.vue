<script setup lang="ts">
import { computed, ref } from 'vue';
import type { RegionGroup } from '../viewModel';
import type { TripViewerState } from '../types';

const props = defineProps<{
  state: TripViewerState;
  groups: RegionGroup[];
}>();

const filter = ref<'all' | 'visited' | 'not-visited'>('all');

const canShowCounts = computed(() =>
  props.state.visitProgress.canDisplayProgress
    && props.state.visitProgress.canDisplayCounts
    && props.state.permissions.canReadVisitCounts);

const canShowHistory = computed(() =>
  props.state.visitProgress.canDisplayHistory
    && props.state.permissions.canReadVisitHistory);

const regionBreakdown = computed(() => props.groups.map(group => {
  const places = group.places.filter(place => {
    const summary = props.state.visitProgress.placeSummariesByPlaceId[place.id] ?? place.visitSummary;
    if (filter.value === 'visited') return summary?.isVisited === true;
    if (filter.value === 'not-visited') return summary?.isVisited !== true;
    return true;
  });

  const visited = group.places.filter(place => {
    const summary = props.state.visitProgress.placeSummariesByPlaceId[place.id] ?? place.visitSummary;
    return summary?.isVisited === true;
  }).length;

  return { region: group.region, total: group.places.length, visited, places };
}).filter(group => group.places.length > 0));

function formatDate(value: string | null): string {
  if (!value) return 'Not returned';
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value));
}

function formatDuration(minutes: number | null): string {
  if (minutes == null) return 'Duration not returned';
  if (minutes < 60) return `${Math.round(minutes)} min`;
  const hours = Math.floor(minutes / 60);
  const remainder = Math.round(minutes % 60);
  return remainder > 0 ? `${hours} hr ${remainder} min` : `${hours} hr`;
}
</script>

<template>
  <section v-if="canShowCounts" class="trip-viewer-progress" aria-label="Visit progress">
    <header>
      <span>Visit progress</span>
      <strong>{{ state.visitProgress.visitedPlaces }} / {{ state.visitProgress.totalPlaces }} places</strong>
      <small>{{ state.visitProgress.percentVisited }}% visited</small>
    </header>

    <div class="trip-viewer-progress__bar" role="progressbar" :aria-valuenow="state.visitProgress.percentVisited" aria-valuemin="0" aria-valuemax="100">
      <span :style="{ width: `${Math.max(0, Math.min(100, state.visitProgress.percentVisited))}%` }"></span>
    </div>

    <div class="trip-viewer-progress__filters" aria-label="Visit history filter">
      <button type="button" :class="{ active: filter === 'all' }" @click="filter = 'all'">All</button>
      <button type="button" :class="{ active: filter === 'visited' }" @click="filter = 'visited'">Visited</button>
      <button type="button" :class="{ active: filter === 'not-visited' }" @click="filter = 'not-visited'">Not visited</button>
    </div>

    <section v-for="group in regionBreakdown" :key="group.region.id" class="trip-viewer-progress__region">
      <h3>{{ group.region.name }}</h3>
      <small>{{ group.visited }} / {{ group.total }} visited</small>
      <ul>
        <li v-for="place in group.places" :key="place.id">
          <span>{{ place.name }}</span>
          <small v-if="(state.visitProgress.placeSummariesByPlaceId[place.id] ?? place.visitSummary).isVisited">
            {{ (state.visitProgress.placeSummariesByPlaceId[place.id] ?? place.visitSummary).visitCount }} visit(s)
          </small>
          <small v-else>Not visited</small>
        </li>
      </ul>
    </section>

    <section v-if="canShowHistory && state.visitProgress.historyRows.length" class="trip-viewer-progress__history" aria-label="Visit history">
      <h3>Visit history</h3>
      <ul>
        <li v-for="row in state.visitProgress.historyRows" :key="row.visitId">
          <span>{{ state.placesById[row.placeId]?.name ?? 'Unknown place' }}</span>
          <small>{{ formatDate(row.startedAt) }} · {{ formatDuration(row.durationMinutes) }}</small>
        </li>
      </ul>
    </section>
  </section>
</template>
