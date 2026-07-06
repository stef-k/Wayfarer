<script setup lang="ts">
import { computed, ref } from 'vue';
import NotesDisplay from './NotesDisplay.vue';
import ProgressPanel from './ProgressPanel.vue';
import type { RegionGroup, SegmentSummary } from '../viewModel';
import { distanceLabel, durationLabel, orderedTags, segmentTitle } from '../viewModel';
import type { TripViewerState } from '../types';

const props = defineProps<{
  state: TripViewerState;
  groups: RegionGroup[];
  segments: SegmentSummary[];
}>();

const emit = defineEmits<{
  close: [];
  print: [];
}>();

const documentElement = ref<HTMLElement | null>(null);
const tags = computed(() => orderedTags(props.state));

function backToTop(): void {
  documentElement.value?.scrollTo({ top: 0, behavior: 'smooth' });
}
</script>

<template>
  <section class="trip-viewer-readable" role="dialog" aria-modal="true" aria-label="Readable trip itinerary">
    <header class="trip-viewer-readable__toolbar">
      <button type="button" @click="emit('close')">Close</button>
      <button v-if="state.actions.print.allowed && state.permissions.canPrint" type="button" @click="emit('print')">Print</button>
    </header>

    <article ref="documentElement" class="trip-viewer-readable__document">
      <header class="trip-viewer-readable__header">
        <span>{{ state.viewerMode }} viewer</span>
        <h1>{{ state.trip.name }}</h1>
        <p v-if="state.trip.ownerDisplayName">By {{ state.trip.ownerDisplayName }}</p>
        <p>Updated {{ new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(new Date(state.trip.updatedAt)) }}</p>
        <ul v-if="tags.length" class="trip-viewer-tags">
          <li v-for="tag in tags" :key="tag.slug">{{ tag.name }}</li>
        </ul>
      </header>

      <img v-if="state.trip.coverImage?.displayUrl" class="trip-viewer-readable__cover" :src="state.trip.coverImage.displayUrl" alt="" loading="lazy">

      <section>
        <h2>Trip notes</h2>
        <NotesDisplay :notes="state.trip.notes" />
      </section>

      <ProgressPanel :state="state" :groups="groups" />

      <section v-for="group in groups" :key="group.region.id" class="trip-viewer-readable__region">
        <header>
          <h2>{{ group.region.name }}</h2>
          <small>{{ group.places.length }} places · {{ group.areas.length }} areas</small>
        </header>
        <img v-if="group.region.coverImage?.displayUrl" class="trip-viewer-readable__cover" :src="group.region.coverImage.displayUrl" alt="" loading="lazy">
        <NotesDisplay :notes="group.region.notes" />

        <section v-for="place in group.places" :key="place.id" class="trip-viewer-readable__item">
          <h3>{{ place.name }}</h3>
          <p v-if="place.address">{{ place.address }}</p>
          <NotesDisplay :notes="place.notes" />
        </section>

        <section v-for="area in group.areas" :key="area.id" class="trip-viewer-readable__item">
          <h3>{{ area.name }}</h3>
          <NotesDisplay :notes="area.notes" />
        </section>
      </section>

      <section v-if="segments.length" class="trip-viewer-readable__region">
        <h2>Segments</h2>
        <section v-for="summary in segments" :key="summary.segment.id" class="trip-viewer-readable__item">
          <h3>{{ segmentTitle(summary) }}</h3>
          <p>{{ summary.segment.mode || 'Segment' }} · {{ distanceLabel(summary.segment.estimatedDistanceKm) }} · {{ durationLabel(summary.segment.estimatedDurationMinutes) }}</p>
          <NotesDisplay :notes="summary.segment.notes" />
        </section>
      </section>

      <button type="button" class="trip-viewer-readable__top" @click="backToTop">Back to top</button>
    </article>
  </section>
</template>
