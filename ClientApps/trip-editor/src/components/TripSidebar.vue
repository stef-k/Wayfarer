<script setup lang="ts">
import type { EditorArea, EditorPlace, EditorRegion, EditorSegment, EditorTripState } from '../types';
import MetadataEditor from './MetadataEditor.vue';

defineProps<{
  state: EditorTripState;
  editorEndpoint: string;
  antiforgeryToken: string;
  tripIndexUrl: string;
}>();

const emit = defineEmits<{
  metadataSaved: [metadata: EditorTripState['metadata']];
}>();

const orderedRegions = (state: EditorTripState): EditorRegion[] =>
  state.regionOrder
    .map(id => state.regionsById[id])
    .filter(region => region && (!region.isShadow || hasRegionChildren(state, region))) as EditorRegion[];

const orderedPlaces = (state: EditorTripState, regionId: string): EditorPlace[] =>
  (state.placeOrderByRegionId[regionId] ?? []).map(id => state.placesById[id]).filter(Boolean) as EditorPlace[];

const orderedAreas = (state: EditorTripState, regionId: string): EditorArea[] =>
  (state.areaOrderByRegionId[regionId] ?? []).map(id => state.areasById[id]).filter(Boolean) as EditorArea[];

const orderedSegments = (state: EditorTripState): EditorSegment[] =>
  state.segmentOrder.map(id => state.segmentsById[id]).filter(Boolean) as EditorSegment[];

const hasRegionChildren = (state: EditorTripState, region: EditorRegion): boolean =>
  (state.placeOrderByRegionId[region.id]?.length ?? 0) > 0 || (state.areaOrderByRegionId[region.id]?.length ?? 0) > 0;

const segmentLabel = (state: EditorTripState, segment: EditorSegment): string => {
  const from = segment.fromPlaceId ? state.placesById[segment.fromPlaceId]?.name : null;
  const to = segment.toPlaceId ? state.placesById[segment.toPlaceId]?.name : null;
  return [from, to].filter(Boolean).join(' to ') || segment.mode || 'Segment';
};
</script>

<template>
  <aside class="trip-editor-sidebar">
    <header class="trip-editor-sidebar__header">
      <div>
        <p class="trip-editor-sidebar__eyebrow">Read-only workspace spike</p>
        <h1>{{ state.metadata.name }}</h1>
      </div>
      <span class="trip-editor-sidebar__status">{{ state.metadata.isPublic ? 'Public' : 'Private' }}</span>
    </header>

    <MetadataEditor
      :metadata="state.metadata"
      :editor-endpoint="editorEndpoint"
      :antiforgery-token="antiforgeryToken"
      :trip-index-url="tripIndexUrl"
      @saved="metadata => emit('metadataSaved', metadata)"
    />

    <section v-if="state.visitProgress.totalPlaces > 0" class="trip-editor-panel">
      <div class="trip-editor-panel__line">
        <span>Visit progress</span>
        <strong>{{ state.visitProgress.percentVisited }}%</strong>
      </div>
      <div class="trip-editor-progress" aria-hidden="true">
        <span :style="{ width: `${state.visitProgress.percentVisited}%` }"></span>
      </div>
      <p>{{ state.visitProgress.visitedPlaces }} / {{ state.visitProgress.totalPlaces }} places visited</p>
    </section>

    <section v-if="state.tagOrder.length > 0" class="trip-editor-panel">
      <h2>Tags</h2>
      <div class="trip-editor-tags">
        <span v-for="slug in state.tagOrder" :key="slug">{{ state.tagsBySlug[slug]?.name }}</span>
      </div>
    </section>

    <section class="trip-editor-panel">
      <h2>Regions & Places</h2>
      <article v-for="region in orderedRegions(state)" :key="region.id" class="trip-editor-region">
        <h3>{{ region.name }}</h3>
        <ul>
          <li v-for="place in orderedPlaces(state, region.id)" :key="place.id">
            <span>{{ place.name }}</span>
            <small v-if="place.visitSummary.isVisited">{{ place.visitSummary.visitCount }} visit(s)</small>
          </li>
          <li v-for="area in orderedAreas(state, region.id)" :key="area.id">
            <span>{{ area.name }}</span>
            <small>Area</small>
          </li>
        </ul>
      </article>
    </section>

    <section v-if="state.segmentOrder.length > 0" class="trip-editor-panel">
      <h2>Segments</h2>
      <ul class="trip-editor-segments">
        <li v-for="segment in orderedSegments(state)" :key="segment.id">
          <span>{{ segmentLabel(state, segment) }}</span>
          <small>{{ segment.mode || 'mode unset' }}</small>
        </li>
      </ul>
    </section>
  </aside>
</template>
