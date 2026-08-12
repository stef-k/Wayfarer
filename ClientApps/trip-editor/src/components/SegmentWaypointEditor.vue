<script setup lang="ts">
import { computed, nextTick, ref } from 'vue';
import type { EditorPlace, EditorRegion, EditorSegmentDraft, EditorSegmentWaypointDraftRow, EditorTripState, Guid } from '../types';
import { createWaypointRow, syncWaypointArrays } from './regionPlaceDrafts';

const props = defineProps<{
  draft: EditorSegmentDraft;
  fieldErrors: (key: string) => string[];
  isSaving: boolean;
  state: EditorTripState;
}>();
const emit = defineEmits<{ clearError: [key: string] }>();

const placeToAdd = ref('');
const addSelect = ref<HTMLSelectElement | null>(null);
const legend = ref<HTMLElement | null>(null);
const rowControls = new Map<string, HTMLSelectElement>();
const announcement = ref('');
const normalRegions = computed(() => props.state.regionOrder.map(id => props.state.regionsById[id]).filter(region => region && !region.isShadow) as EditorRegion[]);
const aggregateErrors = computed(() => [...new Set([...props.fieldErrors('waypointPlaceIds'), ...props.fieldErrors('waypointRouteVertexIndices')])]);

defineExpose({ focusLegend: () => legend.value?.focus() });

/** Returns deterministic region/place choices while retaining the logical row's current value. */
function choices(row: EditorSegmentWaypointDraftRow | null): EditorPlace[] {
  const used = new Set(props.draft.waypointRows.filter(candidate => candidate.clientId !== row?.clientId).map(candidate => candidate.placeId));
  return normalRegions.value.flatMap(region => orderedPlaceIds(region.id).map(id => props.state.placesById[id]).filter(Boolean))
    .filter(place => place.id === row?.placeId || (!used.has(place.id) && place.id !== props.draft.fromPlaceId && place.id !== props.draft.toPlaceId));
}

function orderedPlaceIds(regionId: Guid): Guid[] {
  return props.state.placeOrderByRegionId[regionId] ?? [];
}

function addWaypoint(): void {
  if (props.isSaving || !placeToAdd.value || !choices(null).some(place => place.id === placeToAdd.value && place.location)) return;
  const row = createWaypointRow(placeToAdd.value);
  props.draft.waypointRows.push(row);
  syncWaypointArrays(props.draft);
  clearAggregateErrors();
  const name = placeName(row.placeId);
  placeToAdd.value = '';
  announcement.value = `${name} added as intermediate place ${props.draft.waypointRows.length}.`;
  void nextTick(() => rowControls.get(row.clientId)?.focus());
}

function substitute(row: EditorSegmentWaypointDraftRow, placeId: string): void {
  row.placeId = placeId;
  syncWaypointArrays(props.draft);
  emit('clearError', `waypoint.${row.clientId}`);
  clearAggregateErrors();
}

function move(row: EditorSegmentWaypointDraftRow, offset: number): void {
  if (props.isSaving) return;
  const index = props.draft.waypointRows.indexOf(row);
  const destination = index + offset;
  if (index < 0 || destination < 0 || destination >= props.draft.waypointRows.length) return;
  props.draft.waypointRows.splice(index, 1);
  props.draft.waypointRows.splice(destination, 0, row);
  syncWaypointArrays(props.draft);
  announcement.value = `${placeName(row.placeId)} moved to position ${destination + 1}.`;
  void nextTick(() => rowControls.get(row.clientId)?.focus());
}

function remove(row: EditorSegmentWaypointDraftRow): void {
  if (props.isSaving) return;
  const index = props.draft.waypointRows.indexOf(row);
  if (index < 0) return;
  const name = placeName(row.placeId);
  props.draft.waypointRows.splice(index, 1);
  syncWaypointArrays(props.draft);
  emit('clearError', `waypoint.${row.clientId}`);
  clearAggregateErrors();
  announcement.value = `${name} removed.`;
  void nextTick(() => {
    const target = props.draft.waypointRows[index] ?? props.draft.waypointRows[index - 1];
    target ? rowControls.get(target.clientId)?.focus() : addSelect.value?.focus();
  });
}

function placeName(id: string | null): string {
  return id ? props.state.placesById[id]?.name ?? `Unavailable place ${id}` : 'Place';
}

/** Returns the indexed errors owned by one stable logical row. */
function rowErrors(row: EditorSegmentWaypointDraftRow): string[] {
  return props.fieldErrors(`waypoint.${row.clientId}`);
}

/** Keeps each selector relationship stable while its logical row moves. */
function rowErrorId(clientId: string): string {
  return `segment-waypoint-${clientId}-errors`;
}

/** Clears only aggregate errors made obsolete by a changed waypoint collection. */
function clearAggregateErrors(): void {
  emit('clearError', 'waypointPlaceIds');
  emit('clearError', 'waypointRouteVertexIndices');
}

/** Describes a persisted current value that is outside the normal eligible catalogue. */
function unavailableCurrentLabel(row: EditorSegmentWaypointDraftRow): string {
  const place = props.state.placesById[row.placeId];
  if (!place) return 'Place no longer available';
  const region = props.state.regionsById[place.regionId];
  if (!region || !props.state.regionOrder.includes(place.regionId) || region.isShadow) return `${place.name} — region unavailable`;
  return `${place.name} — unavailable in this Region`;
}

const journeyOrder = computed(() => [props.draft.fromPlaceId ? placeName(props.draft.fromPlaceId) : 'From not selected',
  ...props.draft.waypointRows.map(row => placeName(row.placeId)), props.draft.toPlaceId ? placeName(props.draft.toPlaceId) : 'To not selected'].join(' → '));
</script>

<template>
  <fieldset class="segment-waypoints" data-segment-waypoint-group tabindex="-1" :disabled="isSaving" :aria-invalid="aggregateErrors.length > 0 ? 'true' : undefined" :aria-errormessage="aggregateErrors.length > 0 ? 'segment-waypoint-errors' : undefined">
    <legend ref="legend" tabindex="-1">Intermediate places</legend>
    <p v-if="draft.waypointRows.length === 0" class="trip-editor-empty-state">No intermediate saved Place selected.</p>
    <div class="segment-waypoints__add">
      <label class="trip-editor-field">
        <span>Place to add</span>
        <select ref="addSelect" v-model="placeToAdd">
          <option value="">Select a saved Place</option>
          <option v-for="place in choices(null)" :key="place.id" :value="place.id" :disabled="!place.location">{{ place.name }}{{ place.location ? '' : ' — location required' }}</option>
        </select>
      </label>
      <button type="button" class="btn btn-outline-light btn-sm" :disabled="!placeToAdd || isSaving" @click="addWaypoint">Add intermediate place</button>
    </div>
    <div v-for="(row, index) in draft.waypointRows" :key="row.clientId" class="segment-waypoints__row">
      <label class="trip-editor-field segment-waypoints__select">
        <span>{{ `Intermediate place ${index + 1}: ${placeName(row.placeId)}` }}</span>
        <select :ref="element => { if (element) rowControls.set(row.clientId, element as HTMLSelectElement); }" :data-waypoint-client-id="row.clientId" :value="row.placeId" :aria-invalid="rowErrors(row).length > 0 ? 'true' : undefined" :aria-errormessage="rowErrors(row).length > 0 ? rowErrorId(row.clientId) : undefined" @change="substitute(row, ($event.target as HTMLSelectElement).value)">
          <option v-if="!choices(row).some(place => place.id === row.placeId)" :value="row.placeId" disabled>{{ unavailableCurrentLabel(row) }}</option>
          <option v-for="place in choices(row)" :key="place.id" :value="place.id" :disabled="!place.location">{{ place.name }}{{ place.location ? '' : ' — location required' }}</option>
        </select>
        <div v-if="rowErrors(row).length > 0" :id="rowErrorId(row.clientId)">
          <small v-for="message in rowErrors(row)" :key="message">{{ message }}</small>
        </div>
      </label>
      <div class="segment-waypoints__actions">
        <button type="button" class="btn btn-outline-light btn-sm" :disabled="index === 0 || isSaving" :aria-label="`Move ${placeName(row.placeId)} up`" @click="move(row, -1)">Move up</button>
        <button type="button" class="btn btn-outline-light btn-sm" :disabled="index === draft.waypointRows.length - 1 || isSaving" :aria-label="`Move ${placeName(row.placeId)} down`" @click="move(row, 1)">Move down</button>
        <button type="button" class="btn btn-outline-danger btn-sm" :disabled="isSaving" :aria-label="`Remove ${placeName(row.placeId)}`" @click="remove(row)">Remove</button>
      </div>
    </div>
    <div v-if="aggregateErrors.length > 0" id="segment-waypoint-errors">
      <small v-for="message in aggregateErrors" :key="message">{{ message }}</small>
    </div>
    <p><strong>Journey order:</strong> {{ journeyOrder }}</p>
    <span class="visually-hidden" aria-live="polite">{{ announcement }}</span>
  </fieldset>
</template>

<style scoped>
/* Keeps the cohesive waypoint workflow contained at desktop, zoom, and phone widths. */
.segment-waypoints {
  min-width: 0;
  border: 1px solid var(--bs-border-color);
  border-radius: .375rem;
  padding: .75rem;
}

.segment-waypoints__add,
.segment-waypoints__row,
.segment-waypoints__actions {
  display: flex;
  min-width: 0;
  gap: .5rem;
  align-items: end;
  flex-wrap: wrap;
}

.segment-waypoints__row {
  padding-block: .5rem;
  border-block-start: 1px solid var(--bs-border-color);
}

.segment-waypoints__select,
.segment-waypoints__add .trip-editor-field {
  flex: 1 1 14rem;
  min-width: 0;
}

select {
  width: 100%;
  min-width: 0;
}

@media (max-width: 430px) {
  .segment-waypoints__actions,
  .segment-waypoints__actions .btn {
    width: 100%;
  }

  .segment-waypoints__actions .btn {
    min-height: 2.75rem;
    flex: 1 1 8rem;
  }
}
</style>
