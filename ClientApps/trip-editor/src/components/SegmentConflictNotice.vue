<script setup lang="ts">
import { computed } from 'vue';
import { message, number } from '../../../../wwwroot/js/localization/messages.js';
import type { EditorSegment, EditorTripState } from '../types';

/** Presents the current saved Segment for review without adopting it into the draft. */
const props = defineProps<{ segment: EditorSegment; places: EditorTripState['placesById']; modeLabel: string }>();
defineEmits<{ reload: [] }>();

const journey = computed(() => [props.segment.fromPlaceId, ...props.segment.waypointPlaceIds, props.segment.toPlaceId]
  .map(id => id
    ? props.places[id]?.name ?? message('Editor.SegmentPlaceUnavailable', 'Unavailable place')
    : message('Editor.SegmentPlaceUnset', 'Not selected')).join(' → '));

/** Formats display estimates while leaving their source and values on the wire unchanged. */
const estimates = computed(() => {
  const { estimatedDistanceKm, estimatedDurationMinutes, estimatedDurationSource } = props.segment;
  const distance = estimatedDistanceKm == null
    ? message('Editor.DistanceUnavailable', 'Distance unavailable')
    : message('Editor.EstimateDistance', '{0} km', number(estimatedDistanceKm));
  const duration = estimatedDurationMinutes == null
    ? message('Editor.DurationUnavailable', 'Duration unavailable')
    : message('Editor.EstimateDuration', '{0} min', number(estimatedDurationMinutes));
  const source = estimatedDurationSource === 'Manual'
    ? message('Editor.Manual', 'Manual') : message('Editor.Automatic', 'Automatic');
  return [props.modeLabel, distance, duration, source].join(' · ');
});
</script>

<template>
  <section class="trip-editor-form-warning segment-conflict" aria-labelledby="segment-conflict-heading">
    <h3 id="segment-conflict-heading" tabindex="-1">{{ message('Editor.CurrentSavedSegment', 'Current saved Segment') }}</h3>
    <p>{{ journey }}</p>
    <p>{{ estimates }}</p>
    <button type="button" class="btn btn-outline-light btn-sm" @click="$emit('reload')">
      {{ message('Editor.ReloadCurrentSavedSegment', 'Reload current saved Segment') }}
    </button>
  </section>
</template>
