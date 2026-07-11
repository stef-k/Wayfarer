<script setup lang="ts">
import { computed } from 'vue';
import type { RegionGroup, SegmentSummary } from '../viewModel';
import { isSameSelection, notesPreview, orderedTags, segmentModeLabel, segmentTitle, validAreaFillHex } from '../viewModel';
import type { TripViewerState, ViewerSelection } from '../types';

const props = defineProps<{
  state: TripViewerState;
  groups: RegionGroup[];
  segments: SegmentSummary[];
  selection: ViewerSelection;
}>();

const emit = defineEmits<{
  select: [selection: ViewerSelection];
}>();

const select = (selection: ViewerSelection): void => emit('select', selection);
const selected = (selection: ViewerSelection): boolean => isSameSelection(props.selection, selection);
const tripTags = computed(() => orderedTags(props.state));
const areaFillHex = (fillHex: string | null): string | null => validAreaFillHex(fillHex);
</script>

<template>
  <aside class="trip-viewer-sidebar" aria-label="Trip contents">
    <button
      type="button"
      class="trip-viewer-sidebar__trip"
      :class="{ 'trip-viewer-list-item--selected': selected({ type: 'trip', id: state.trip.id }) }"
      @click="select({ type: 'trip', id: state.trip.id })"
    >
      <span>Trip</span>
      <strong>{{ state.trip.name }}</strong>
      <small>{{ state.regionOrder.length }} regions · {{ Object.keys(state.placesById).length }} places · {{ state.segmentOrder.length }} segments</small>
      <span v-if="tripTags.length" class="trip-viewer-list-tags">{{ tripTags.map(tag => tag.name).join(', ') }}</span>
    </button>

    <section v-for="group in groups" :key="group.region.id" class="trip-viewer-sidebar__group">
      <button
        type="button"
        class="trip-viewer-list-item trip-viewer-list-item--region"
        :class="{ 'trip-viewer-list-item--selected': selected({ type: 'region', id: group.region.id }) }"
        @click="select({ type: 'region', id: group.region.id })"
      >
        <span>Region</span>
        <strong>{{ group.region.name }}</strong>
        <small>{{ group.places.length }} places · {{ group.areas.length }} areas</small>
      </button>

      <button
        v-for="place in group.places"
        :key="place.id"
        type="button"
        class="trip-viewer-list-item trip-viewer-list-item--child"
        :class="{ 'trip-viewer-list-item--selected': selected({ type: 'place', id: place.id }) }"
        @click="select({ type: 'place', id: place.id })"
      >
        <span class="trip-viewer-marker-swatch" :class="`trip-viewer-marker-swatch--${place.markerColor}`" aria-hidden="true"></span>
        <strong>{{ place.name }}</strong>
        <small>{{ place.address || notesPreview(place.notes) || 'Place' }}</small>
      </button>

      <button
        v-for="area in group.areas"
        :key="area.id"
        type="button"
        class="trip-viewer-list-item trip-viewer-list-item--child"
        :class="{ 'trip-viewer-list-item--selected': selected({ type: 'area', id: area.id }) }"
        @click="select({ type: 'area', id: area.id })"
      >
        <span v-if="areaFillHex(area.fillHex)" class="trip-viewer-area-swatch" :style="{ backgroundColor: areaFillHex(area.fillHex) }" aria-hidden="true"></span>
        <strong>{{ area.name }}</strong>
        <small>{{ notesPreview(area.notes) || 'Area' }}</small>
      </button>
    </section>

    <section v-if="segments.length" class="trip-viewer-sidebar__group">
      <h2>Segments</h2>
      <button
        v-for="summary in segments"
        :key="summary.segment.id"
        type="button"
        class="trip-viewer-list-item trip-viewer-list-item--segment"
        :class="{ 'trip-viewer-list-item--selected': selected({ type: 'segment', id: summary.segment.id }) }"
        @click="select({ type: 'segment', id: summary.segment.id })"
      >
        <span>{{ segmentModeLabel(summary.segment.mode) ?? 'Segment' }}</span>
        <strong>{{ segmentTitle(summary) }}</strong>
        <small>{{ notesPreview(summary.segment.notes) || 'Route segment' }}</small>
      </button>
    </section>
  </aside>
</template>
