<script setup lang="ts">
import { computed } from 'vue';
import NotesDisplay from './NotesDisplay.vue';
import ActionsBar from './ActionsBar.vue';
import { coordinateLabel, distanceLabel, durationLabel, orderedTags, visitSummaryForPlace } from '../viewModel';
import type { SelectedEntity } from '../viewModel';
import type { TripViewerState, ViewerSelection } from '../types';

const props = defineProps<{
  entity: SelectedEntity;
  state: TripViewerState;
}>();

const emit = defineEmits<{
  focus: [selection: ViewerSelection];
}>();

const tripTags = computed(() => orderedTags(props.state));
const selectedVisitSummary = computed(() => props.entity.place ? visitSummaryForPlace(props.state, props.entity.place) : null);
</script>

<template>
  <aside class="trip-viewer-detail" aria-label="Selection details">
    <header class="trip-viewer-detail__header">
      <span>{{ entity.eyebrow }}</span>
      <h2>{{ entity.title }}</h2>
      <small v-if="entity.region && entity.type !== 'region'">In {{ entity.region.name }}</small>
    </header>

    <ActionsBar v-if="entity.type === 'trip'" :actions="state.actions" :embed="state.viewerMode === 'embed'" />

    <ul v-if="entity.type === 'trip' && tripTags.length" class="trip-viewer-tags" aria-label="Trip tags">
      <li v-for="tag in tripTags" :key="tag.slug">{{ tag.name }}</li>
    </ul>

    <img v-if="entity.type === 'trip' && state.trip.coverImage?.displayUrl" class="trip-viewer-cover" :src="state.trip.coverImage.displayUrl" alt="" loading="lazy">
    <img v-if="entity.region?.coverImage?.displayUrl && entity.type === 'region'" class="trip-viewer-cover" :src="entity.region.coverImage.displayUrl" alt="" loading="lazy">

    <dl class="trip-viewer-facts">
      <template v-if="entity.type === 'trip'">
        <div>
          <dt>Mode</dt>
          <dd>{{ state.viewerMode }}</dd>
        </div>
        <div v-if="state.trip.ownerDisplayName">
          <dt>Owner</dt>
          <dd>{{ state.trip.ownerDisplayName }}</dd>
        </div>
        <div v-if="state.visitProgress.canDisplayProgress && state.visitProgress.canDisplayCounts && state.permissions.canReadVisitCounts">
          <dt>Progress</dt>
          <dd>{{ state.visitProgress.visitedPlaces }} / {{ state.visitProgress.totalPlaces }} places · {{ state.visitProgress.percentVisited }}%</dd>
        </div>
      </template>

      <template v-if="entity.region">
        <div v-if="entity.type === 'region'">
          <dt>Center</dt>
          <dd>{{ coordinateLabel(entity.region.center) }}</dd>
        </div>
      </template>

      <template v-if="entity.place">
        <div>
          <dt>Address</dt>
          <dd>{{ entity.place.address || 'Not set' }}</dd>
        </div>
        <div>
          <dt>Coordinates</dt>
          <dd>{{ coordinateLabel(entity.place.location) }}</dd>
        </div>
        <div v-if="selectedVisitSummary">
          <dt>Visits</dt>
          <dd>{{ selectedVisitSummary.isVisited ? selectedVisitSummary.visitCount : 0 }}</dd>
        </div>
      </template>

      <template v-if="entity.area">
        <div>
          <dt>Fill</dt>
          <dd><span class="trip-viewer-area-swatch" :style="{ backgroundColor: entity.area.fillHex }"></span>{{ entity.area.fillHex }}</dd>
        </div>
        <div>
          <dt>Geometry</dt>
          <dd>{{ entity.area.geometry ? 'Polygon available' : 'Not set' }}</dd>
        </div>
      </template>

      <template v-if="entity.segment">
        <div>
          <dt>Mode</dt>
          <dd>{{ entity.segment.segment.mode || 'Not set' }}</dd>
        </div>
        <div>
          <dt>Distance</dt>
          <dd>{{ distanceLabel(entity.segment.segment.estimatedDistanceKm) }}</dd>
        </div>
        <div>
          <dt>Duration</dt>
          <dd>{{ durationLabel(entity.segment.segment.estimatedDurationMinutes) }}</dd>
        </div>
      </template>
    </dl>

    <button v-if="entity.type !== 'trip'" type="button" class="trip-viewer-focus-button" @click="emit('focus', { type: entity.type, id: entity.id })">
      Focus on map
    </button>

    <section class="trip-viewer-detail__notes">
      <h3>Notes</h3>
      <NotesDisplay :notes="entity.notes" />
    </section>
  </aside>
</template>
