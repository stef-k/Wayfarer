<script setup lang="ts">
import { computed } from 'vue';
import NotesDisplay from './NotesDisplay.vue';
import ActionsBar from './ActionsBar.vue';
import ProgressPanel from './ProgressPanel.vue';
import { coordinateLabel, distanceDisplay, durationDisplay, hasUsableAreaGeometry, orderedTags, segmentModeLabel, validAreaFillHex, visitSummaryForPlace } from '../viewModel';
import type { RegionGroup, SelectedEntity } from '../viewModel';
import type { TripViewerState, ViewerSelection } from '../types';

const props = defineProps<{
  entity: SelectedEntity;
  state: TripViewerState;
  groups: RegionGroup[];
}>();

const emit = defineEmits<{
  focus: [selection: ViewerSelection];
  readable: [];
  print: [];
}>();

const tripTags = computed(() => orderedTags(props.state));
const selectedVisitSummary = computed(() => props.entity.place ? visitSummaryForPlace(props.state, props.entity.place) : null);
const areaFillHex = computed(() => props.entity.area ? validAreaFillHex(props.entity.area.fillHex) : null);
const areaHasUsableGeometry = computed(() => props.entity.area ? hasUsableAreaGeometry(props.entity.area.geometry) : false);
</script>

<template>
  <aside class="trip-viewer-detail" aria-label="Selection details">
    <header class="trip-viewer-detail__header">
      <span>{{ entity.eyebrow }}</span>
      <h2>{{ entity.title }}</h2>
      <small v-if="entity.region && entity.type !== 'region'">In {{ entity.region.name }}</small>
    </header>

    <ActionsBar
      v-if="entity.type === 'trip'"
      :actions="state.actions"
      :embed="state.viewerMode === 'embed'"
      @readable="emit('readable')"
      @print="emit('print')"
    />

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
        <!-- The returned fill is decorative only and must not become an area fact. -->
        <span v-if="areaFillHex" class="trip-viewer-area-swatch" :style="{ backgroundColor: areaFillHex }" aria-hidden="true"></span>
        <div>
          <dt>Map boundary</dt>
          <dd>{{ areaHasUsableGeometry ? 'Available on the map.' : 'No map boundary is available.' }}</dd>
        </div>
      </template>

      <template v-if="entity.segment">
        <div>
          <dt>Mode</dt>
          <dd>{{ segmentModeLabel(entity.segment.segment.mode) ?? 'Mode not provided.' }}</dd>
        </div>
        <div>
          <dt>Distance</dt>
          <dd>{{ distanceDisplay(entity.segment.segment.estimatedDistanceKm).detail }}</dd>
        </div>
        <div>
          <dt>Duration</dt>
          <dd>{{ durationDisplay(entity.segment.segment.estimatedDurationMinutes).detail }}</dd>
        </div>
      </template>
    </dl>

    <ProgressPanel v-if="entity.type === 'trip'" :state="state" :groups="groups" />

    <button v-if="entity.type !== 'trip' && (entity.type !== 'area' || areaHasUsableGeometry)" type="button" class="trip-viewer-focus-button" @click="emit('focus', { type: entity.type, id: entity.id })">
      Focus on map
    </button>

    <section v-if="entity.notes.hasRenderableContent" class="trip-viewer-detail__notes">
      <h3>Notes</h3>
      <NotesDisplay :notes="entity.notes" />
    </section>
  </aside>
</template>
