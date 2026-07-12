<script setup lang="ts">
import { computed, nextTick, ref } from 'vue';
import type { RegionGroup } from '../viewModel';
import type { TripViewerState } from '../types';

const props = defineProps<{
  state: TripViewerState;
  groups: RegionGroup[];
}>();

const disclosure = ref<HTMLDialogElement | null>(null);
const disclosureTrigger = ref<HTMLButtonElement | null>(null);

const canShowCounts = computed(() =>
  props.state.visitProgress.canDisplayProgress
    && props.state.visitProgress.canDisplayCounts
    && props.state.permissions.canReadVisitCounts);

const canShowHistory = computed(() =>
  props.state.viewerMode === 'private'
    && props.state.permissions.isOwner
    && props.state.visitProgress.canDisplayHistory
    && props.state.permissions.canReadVisitHistory);

const ownerVisitLabel = computed(() =>
  !props.state.permissions.isOwner && props.state.trip.ownerDisplayName
    ? `${props.state.trip.ownerDisplayName} visited`
    : null);

const regionBreakdown = computed(() => props.groups.map(group => {
  const places = group.places.map(place => {
    const summary = props.state.visitProgress.placeSummariesByPlaceId[place.id] ?? place.visitSummary;
    return { place, summary };
  });

  const visited = places.filter(({ summary }) => summary.isVisited).length;

  return { region: group.region, total: places.length, visited, places };
}).filter(group => group.total > 0));

function openDisclosure(): void {
  disclosureTrigger.value = document.activeElement instanceof HTMLButtonElement ? document.activeElement : null;
  disclosure.value?.showModal();
}

function closeDisclosure(): void {
  disclosure.value?.close();
}

function restoreDisclosureFocus(): void {
  void nextTick(() => disclosureTrigger.value?.focus());
}

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
    <!-- This compact summary deliberately uses only server-returned display-safe fields. -->
    <header class="trip-viewer-progress__summary">
      <small v-if="ownerVisitLabel">{{ ownerVisitLabel }}</small>
      <strong>{{ state.visitProgress.visitedPlaces }} / {{ state.visitProgress.totalPlaces }} places</strong>
      <div>
        <button ref="disclosureTrigger" type="button" class="trip-viewer-progress__disclosure" @click="openDisclosure">
          Details
        </button>
        <span class="trip-viewer-progress__percentage">{{ state.visitProgress.percentVisited }}%</span>
      </div>
    </header>

    <div class="trip-viewer-progress__bar" role="progressbar" :aria-valuenow="state.visitProgress.percentVisited" aria-valuemin="0" aria-valuemax="100">
      <span :style="{ width: `${Math.max(0, Math.min(100, state.visitProgress.percentVisited))}%` }"></span>
    </div>

    <!-- Native modal behavior keeps the map and sidebar out of the active interaction layer while details are open. -->
    <dialog
      ref="disclosure"
      class="trip-viewer-progress__dialog"
      :aria-label="canShowHistory ? 'Visit history' : 'Visit progress details'"
      @close="restoreDisclosureFocus"
    >
      <header>
        <h2>{{ canShowHistory ? 'Visit history' : 'Visit progress details' }}</h2>
        <button type="button" aria-label="Close visit progress" @click="closeDisclosure">Close</button>
      </header>
      <!-- Public disclosures intentionally contain aggregate counts only. -->
      <section v-if="!canShowHistory" class="trip-viewer-progress__counts" aria-label="Visit progress counts">
        <strong>{{ state.visitProgress.visitedPlaces }} / {{ state.visitProgress.totalPlaces }} places visited</strong>
        <small>{{ state.visitProgress.percentVisited }}% complete</small>
      </section>
      <template v-else>
        <section v-for="group in regionBreakdown" :key="group.region.id" class="trip-viewer-progress__region">
          <h3>{{ group.region.name }}</h3>
          <small>{{ group.visited }} / {{ group.total }} visited</small>
          <ul>
            <li v-for="entry in group.places" :key="entry.place.id">
              <span>{{ entry.place.name }}</span>
              <small>{{ entry.summary.isVisited ? `${entry.summary.visitCount} visit(s)` : 'Not visited' }}</small>
            </li>
          </ul>
        </section>
        <section v-if="state.visitProgress.historyRows.length" class="trip-viewer-progress__history" aria-label="Visit history entries">
          <h3>Visit history</h3>
          <ul>
            <li v-for="row in state.visitProgress.historyRows" :key="row.visitId">
              <span>{{ state.placesById[row.placeId]?.name ?? 'Unknown place' }}</span>
              <small>{{ formatDate(row.startedAt) }} · {{ formatDuration(row.durationMinutes) }}</small>
            </li>
          </ul>
        </section>
      </template>
    </dialog>
  </section>
</template>
