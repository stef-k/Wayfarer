<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { RegionGroup, SegmentSummary } from '../viewModel';
import { isSameSelection, notesPreview, orderedTags, segmentModeLabel, segmentTitle, validAreaFillHex } from '../viewModel';
import type { TripViewerState, ViewerSelection } from '../types';
import NotesDisplay from './NotesDisplay.vue';

const props = defineProps<{
  state: TripViewerState;
  groups: RegionGroup[];
  segments: SegmentSummary[];
  selection: ViewerSelection;
  notesBackEnabled: boolean;
}>();

const emit = defineEmits<{
  select: [selection: ViewerSelection];
}>();

const select = (selection: ViewerSelection): void => emit('select', selection);
const selected = (selection: ViewerSelection): boolean => isSameSelection(props.selection, selection);
const areaFillHex = (fillHex: string | null): string | null => validAreaFillHex(fillHex);
const tags = computed(() => orderedTags(props.state));
const notesViewport = ref<HTMLElement | null>(null);
const tripNotesHeading = ref<HTMLElement | null>(null);
const notesOverflowing = ref(false);
const showBackToNotes = ref(false);
let notesResizeObserver: ResizeObserver | null = null;

function updateNotesOverflow(): void {
  const viewport = notesViewport.value;
  if (!viewport) {
    notesOverflowing.value = false;
    showBackToNotes.value = false;
    return;
  }

  notesOverflowing.value = viewport.scrollHeight > viewport.clientHeight + 1;
  showBackToNotes.value = notesOverflowing.value && viewport.scrollTop > 160;
}

function handleNotesScroll(): void {
  updateNotesOverflow();
}

function returnToTripNotes(): void {
  notesViewport.value?.scrollTo({ top: 0, behavior: 'auto' });
  updateNotesOverflow();
  tripNotesHeading.value?.focus();
}

function scheduleNotesOverflowUpdate(): void {
  void nextTick(updateNotesOverflow);
}

onMounted(() => {
  scheduleNotesOverflowUpdate();
  if ('ResizeObserver' in window && notesViewport.value) {
    notesResizeObserver = new ResizeObserver(scheduleNotesOverflowUpdate);
    notesResizeObserver.observe(notesViewport.value);
  }
});

onBeforeUnmount(() => notesResizeObserver?.disconnect());

watch(() => props.state.trip.notes, scheduleNotesOverflowUpdate, { deep: true });
</script>

<template>
  <aside class="trip-viewer-sidebar" aria-label="Trip contents">
    <!-- This is display-safe trip information, not a second identity or hierarchy surface. -->
    <section v-if="tags.length || state.trip.notes.hasRenderableContent" class="trip-viewer-sidebar__overview">
      <ul v-if="tags.length" class="trip-viewer-tags" aria-label="Trip tags">
        <li v-for="tag in tags" :key="tag.id">{{ tag.name }}</li>
      </ul>
      <section v-if="state.trip.notes.hasRenderableContent" class="trip-viewer-sidebar__notes">
        <h2 id="trip-viewer-trip-notes-heading" ref="tripNotesHeading" tabindex="-1">Trip notes</h2>
        <div
          ref="notesViewport"
          class="trip-viewer-sidebar__notes-viewport"
          :class="{ 'trip-viewer-sidebar__notes-viewport--overflowing': notesOverflowing }"
          @scroll="handleNotesScroll"
          @load.capture="scheduleNotesOverflowUpdate"
        >
          <NotesDisplay :notes="state.trip.notes" />
        </div>
        <button
          v-if="notesBackEnabled && showBackToNotes"
          type="button"
          class="trip-viewer-sidebar__notes-back"
          aria-label="Back to trip notes"
          title="Back to trip notes"
          @click="returnToTripNotes"
        >
          <span aria-hidden="true">↑</span>
        </button>
      </section>
    </section>

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
