<script setup lang="ts">
import { computed, ref } from 'vue';
import NotesDisplay from './NotesDisplay.vue';
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
const titleElement = ref<HTMLHeadingElement | null>(null);
const showBackToTop = ref(false);
const tags = computed(() => orderedTags(props.state));
// Uses only a server-returned public-safe snapshot action; readable mode never derives image URLs client-side.
const mapSnapshotUrl = computed(() => {
  const action = props.state.actions.copyMapSnapshotUrl;
  return action.allowed && action.url && (action.method == null || action.method.toUpperCase() === 'GET') && !action.requiresAuthentication
    ? action.url
    : null;
});
// Summarizes map context from already-returned DTO fields when no snapshot URL is available.
const mapPreview = computed(() => {
  const mappedPlaces = props.groups.reduce((total, group) => total + group.places.filter(place => place.location).length, 0);
  const regionCenters = props.groups.filter(group => group.region.center).length;
  const mappedAreas = props.groups.reduce((total, group) => total + group.areas.filter(area => area.geometry).length, 0);
  const mappedSegments = props.segments.filter(summary => summary.segment.route || (summary.segment.fallbackStart && summary.segment.fallbackEnd)).length;
  return {
    hasFeatures: mappedPlaces + regionCenters + mappedAreas + mappedSegments > 0,
    mappedPlaces,
    regionCenters,
    mappedAreas,
    mappedSegments,
    center: `${props.state.map.initialView.latitude.toFixed(5)}, ${props.state.map.initialView.longitude.toFixed(5)}`,
    zoom: props.state.map.initialView.zoom
  };
});

const hasMapPreview = computed(() => Boolean(mapSnapshotUrl.value) || mapPreview.value.hasFeatures);
const hasUpdatedDate = computed(() => !Number.isNaN(Date.parse(props.state.trip.updatedAt)));

function onDocumentScroll(): void {
  showBackToTop.value = (documentElement.value?.scrollTop ?? 0) > 300;
}

function backToTop(): void {
  documentElement.value?.scrollTo({ top: 0, behavior: 'smooth' });
  titleElement.value?.focus({ preventScroll: true });
}
</script>

<template>
  <section class="trip-viewer-readable" role="dialog" aria-modal="true" aria-label="Readable trip itinerary">
    <header class="trip-viewer-readable__toolbar">
      <button type="button" @click="emit('close')">Close</button>
      <!-- Readable mode intentionally retains only Close, browser Print, and Back to top. -->
      <button v-if="state.actions.print.allowed" type="button" @click="emit('print')">Print</button>
    </header>

    <article ref="documentElement" class="trip-viewer-readable__document" @scroll="onDocumentScroll">
      <header class="trip-viewer-readable__header">
        <h1 ref="titleElement" tabindex="-1">{{ state.trip.name }}</h1>
        <p v-if="state.trip.ownerDisplayName">By {{ state.trip.ownerDisplayName }}</p>
        <p v-if="hasUpdatedDate">Updated {{ new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(new Date(state.trip.updatedAt)) }}</p>
      </header>

      <img v-if="state.trip.coverImage?.displayUrl" class="trip-viewer-readable__cover" :src="state.trip.coverImage.displayUrl" :alt="`Cover for ${state.trip.name}`" loading="lazy">

      <section v-if="hasMapPreview" class="trip-viewer-readable__map" aria-label="Readable map preview">
        <h2>Map</h2>
        <img
          v-if="mapSnapshotUrl"
          class="trip-viewer-readable__snapshot"
          :src="mapSnapshotUrl"
          alt="Trip map snapshot"
          loading="lazy"
        >
        <div v-else class="trip-viewer-readable__map-fallback">
          <strong>{{ mapPreview.hasFeatures ? 'Map preview unavailable' : 'No mapped trip features available' }}</strong>
          <p>Showing read-only map context from returned trip state.</p>
          <dl>
            <div>
              <dt>Places</dt>
              <dd>{{ mapPreview.mappedPlaces }}</dd>
            </div>
            <div>
              <dt>Regions</dt>
              <dd>{{ mapPreview.regionCenters }}</dd>
            </div>
            <div>
              <dt>Areas</dt>
              <dd>{{ mapPreview.mappedAreas }}</dd>
            </div>
            <div>
              <dt>Segments</dt>
              <dd>{{ mapPreview.mappedSegments }}</dd>
            </div>
            <div>
              <dt>Initial center</dt>
              <dd>{{ mapPreview.center }}</dd>
            </div>
            <div>
              <dt>Zoom</dt>
              <dd>{{ mapPreview.zoom }}</dd>
            </div>
          </dl>
        </div>
      </section>

      <section v-if="tags.length">
        <h2>Tags</h2>
        <ul class="trip-viewer-tags">
          <li v-for="tag in tags" :key="tag.slug">{{ tag.name }}</li>
        </ul>
      </section>

      <section v-if="state.trip.notes.hasRenderableContent">
        <h2>Trip notes</h2>
        <NotesDisplay :notes="state.trip.notes" />
      </section>

      <section v-if="groups.length" class="trip-viewer-readable__regions">
        <h2>Regions</h2>
        <section v-for="group in groups" :key="group.region.id" class="trip-viewer-readable__region">
          <h3>{{ group.region.name }}</h3>
        <img v-if="group.region.coverImage?.displayUrl" class="trip-viewer-readable__cover" :src="group.region.coverImage.displayUrl" alt="" loading="lazy">
          <NotesDisplay v-if="group.region.notes.hasRenderableContent" :notes="group.region.notes" />

          <section v-if="group.places.length" class="trip-viewer-readable__children">
            <h4>Places</h4>
            <section v-for="place in group.places" :key="place.id" class="trip-viewer-readable__item">
              <h4>{{ place.name }}</h4>
              <p v-if="place.address">{{ place.address }}</p>
              <NotesDisplay v-if="place.notes.hasRenderableContent" :notes="place.notes" />
            </section>
          </section>

          <section v-if="group.areas.length" class="trip-viewer-readable__children">
            <h4>Areas</h4>
            <section v-for="area in group.areas" :key="area.id" class="trip-viewer-readable__item">
              <h4>{{ area.name }}</h4>
              <NotesDisplay v-if="area.notes.hasRenderableContent" :notes="area.notes" />
            </section>
          </section>
        </section>
      </section>

      <section v-if="segments.length" class="trip-viewer-readable__segments">
        <h2>Segments</h2>
        <section v-for="summary in segments" :key="summary.segment.id" class="trip-viewer-readable__item">
          <h3>{{ segmentTitle(summary) }}</h3>
          <p v-if="summary.segment.mode || summary.segment.estimatedDistanceKm != null || summary.segment.estimatedDurationMinutes != null">
            <span v-if="summary.segment.mode">{{ summary.segment.mode }}</span>
            <span v-if="summary.segment.mode && (summary.segment.estimatedDistanceKm != null || summary.segment.estimatedDurationMinutes != null)"> · </span>
            <span v-if="summary.segment.estimatedDistanceKm != null">{{ distanceLabel(summary.segment.estimatedDistanceKm) }}</span>
            <span v-if="summary.segment.estimatedDistanceKm != null && summary.segment.estimatedDurationMinutes != null"> · </span>
            <span v-if="summary.segment.estimatedDurationMinutes != null">{{ durationLabel(summary.segment.estimatedDurationMinutes) }}</span>
          </p>
          <NotesDisplay v-if="summary.segment.notes.hasRenderableContent" :notes="summary.segment.notes" />
        </section>
      </section>
    </article>
    <button v-show="showBackToTop" type="button" class="trip-viewer-readable__top" @click="backToTop">Back to top</button>
  </section>
</template>
