<script setup lang="ts">
import { editorOperationText } from './editorFeedbackText';
import { computed } from 'vue';
import { message as localize, number } from '../../../../wwwroot/js/localization/messages.js';
import type { DeepReadonly } from 'vue';
import type { Disposition, RegionDeletionController } from './regionDeletionState';
import { unknownRegionDeletionMessage } from './regionDeletionState';
const props = defineProps<{ controller: DeepReadonly<RegionDeletionController> }>();
const groups = ['places', 'areas'] as const;
const choices: Disposition[] = ['keep', 'delete'];
const destinationName = localize('Shell.UnassignedPlaces', 'Unassigned Places');

/** Explains whether kept children create the canonical destination or join it. */
const destinationText = computed(() => {
  const mode = props.controller.state.preview?.destination.mode;
  if (!mode || mode === 'not-required') return '';
  return mode === 'create'
    ? localize('Editor.RegionDeleteCreatesDestination', 'Creates the “{0}” Region.', destinationName)
    : localize('Editor.RegionDeleteUsesDestination', 'Moves kept items to the existing “{0}” Region.', destinationName);
});

/** Names each radio group and counts the children covered by the current preview. */
function groupLegend(group: typeof groups[number]): string {
  const counts = props.controller.state.preview?.consequences;
  const name = group === 'places' ? localize('Editor.PlaceGroup', 'Places') : localize('Editor.Areas', 'Areas');
  const count = !counts ? '…' : number(group === 'places'
    ? counts.movedPlaces.count + counts.deletedPlaces.count
    : counts.movedAreas.count + counts.deletedAreas.count);
  return `${name} (${count})`;
}
</script>

<template>
  <div class="row g-3">
    <fieldset v-for="group in groups" :key="group" class="col-12 col-sm-6" :disabled="controller.state.submitting || controller.state.held">
      <legend class="h6">{{ groupLegend(group) }}</legend>
      <label v-for="choice in choices" :key="choice" class="d-block border rounded p-3 mb-2" :class="{ 'border-primary': controller.state[group] === choice }">
        <input type="radio" :name="`region-delete-${group}`" :value="choice" :checked="controller.state[group] === choice"
          @change="controller.select(group, choice)" />
        <strong class="ms-2">{{ choice === 'keep' ? localize('Editor.KeepRecommended', 'Keep (recommended)') : localize('Editor.DeletePermanently', 'Delete permanently') }}</strong>
        <span class="d-block small mt-1">{{ choice === 'keep' ? localize('Editor.MoveToRegion', 'Move to “{0}”', destinationName) : localize('Editor.DeleteIrreversible', 'Cannot be undone') }}</span>
      </label>
    </fieldset>
  </div>
  <div role="status" aria-live="polite" aria-atomic="true">
    <p v-if="controller.state.loading">{{ localize("Editor.LoadingCurrentConsequences", "Loading current consequences…") }}</p>
    <div v-if="controller.state.preview">
      <p>{{ localize('Editor.RegionDeleteChildrenSummary', 'Places: {0} kept, {1} deleted. Areas: {2} kept, {3} deleted.',
        number(controller.state.preview.consequences.movedPlaces.count), number(controller.state.preview.consequences.deletedPlaces.count),
        number(controller.state.preview.consequences.movedAreas.count), number(controller.state.preview.consequences.deletedAreas.count)) }}</p>
      <p v-if="destinationText">{{ destinationText }}</p>
      <p>{{ localize("Editor.TheRegionSPrivatePlanningIsRemoved", "The Region’s private planning is removed. Kept children retain their own planning; deleted children and Segments lose theirs.") }}</p>
      <p>{{ localize('Editor.SegmentsDeleted', 'Segments deleted: {0}.', number(controller.state.preview.consequences.deletedSegments.count)) }}</p>
      <p>{{ localize('Editor.RegionDeleteAffectedRoutes', 'Surviving waypoint routes changed: {0}; saved waypoint associations removed: {1}.',
        number(controller.state.preview.consequences.updatedSegments.count), number(controller.state.preview.consequences.detachedWaypoints.count)) }}</p>
      <p v-if="controller.state.places === 'delete'">{{ localize("Editor.DeletionIsPermanentVisitHistoryRetainsSnapshots", "Deletion is permanent. Visit history retains snapshots and is unlinked from deleted Places. Kept Areas do not preserve deleted Places or routes.") }}</p>
    </div>
    <p v-if="controller.state.submitting">{{ localize("Editor.DeletingRegionWaitingForTheServerResult", "Deleting Region. Waiting for the server result…") }}</p>
  </div>
  <div v-if="controller.state.error" role="alert" class="alert alert-warning">{{ editorOperationText(controller.state.error, localize('Editor.RegionPreviewRejected', 'Request rejected. Refresh the preview and confirm again.')) }}</div>
  <div v-if="controller.state.held" role="alert" class="alert alert-warning">
    {{ localize('Editor.RegionDeleteUnknown', unknownRegionDeletionMessage) }} {{ localize("Editor.TheEarlierRequestRemainsUnresolvedAnotherRegion", "The earlier request remains unresolved. Another Region deletion is held until its terminal outcome is explicitly reconciled.") }}
  </div>
  <button v-if="!controller.state.held && !controller.state.submitting" type="button" class="btn btn-outline-secondary btn-sm" :disabled="controller.state.loading" @click="controller.refresh()">{{ localize("Editor.RefreshPreview", "Refresh preview") }}</button>
</template>
