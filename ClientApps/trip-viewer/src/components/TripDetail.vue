<script setup lang="ts">
import { computed } from 'vue';
import NotesDisplay from './NotesDisplay.vue';
import { coordinateLabel, distanceDisplay, durationDisplay, hasUsableAreaGeometry, segmentModeLabel, validAreaFillHex } from '../viewModel';
import type { SelectedEntity } from '../viewModel';
import type { TripViewerState, ViewerSelection } from '../types';

const props = defineProps<{
  entity: SelectedEntity;
  state: TripViewerState;
}>();

const emit = defineEmits<{
  focus: [selection: ViewerSelection];
}>();

const areaFillHex = computed(() => props.entity.area ? validAreaFillHex(props.entity.area.fillHex) : null);
const areaHasUsableGeometry = computed(() => props.entity.area ? hasUsableAreaGeometry(props.entity.area.geometry) : false);
// Place location remains display-safe: invalid or absent coordinates are omitted rather than presented as a placeholder.
const placeLocation = computed(() => {
  const place = props.entity.place;
  if (!place) return null;

  const address = place.address.trim();
  const coordinate = place.location;
  const coordinates = coordinate && Number.isFinite(coordinate.latitude) && Number.isFinite(coordinate.longitude)
    ? coordinateLabel(coordinate)
    : null;
  return address || coordinates ? { address: address || null, coordinates } : null;
});
</script>

<template>
  <aside class="trip-viewer-detail" aria-label="Selection details">
    <header class="trip-viewer-detail__header">
      <span>{{ entity.eyebrow }}</span>
      <h2>{{ entity.title }}</h2>
      <small v-if="entity.region && entity.type !== 'region'">In {{ entity.region.name }}</small>
    </header>

    <dl class="trip-viewer-facts">
      <template v-if="entity.region">
        <div v-if="entity.type === 'region'">
          <dt>Center</dt>
          <dd>{{ coordinateLabel(entity.region.center) }}</dd>
        </div>
      </template>

      <template v-if="entity.place">
        <!-- Location is intentionally one compact presentation block, not a second facts hierarchy. -->
        <div v-if="placeLocation" class="trip-viewer-detail__location">
          <span v-if="placeLocation.address">{{ placeLocation.address }}</span>
          <small v-if="placeLocation.coordinates">{{ placeLocation.coordinates }}</small>
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

    <button v-if="entity.type !== 'trip' && (entity.type !== 'area' || areaHasUsableGeometry)" type="button" class="trip-viewer-focus-button" @click="emit('focus', { type: entity.type, id: entity.id })">
      Focus on map
    </button>

    <section v-if="entity.notes.hasRenderableContent" class="trip-viewer-detail__notes">
      <h3>Notes</h3>
      <NotesDisplay :notes="entity.notes" />
    </section>
  </aside>
</template>
