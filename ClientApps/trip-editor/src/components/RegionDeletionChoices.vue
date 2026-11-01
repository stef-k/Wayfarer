<script setup lang="ts">
import type { DeepReadonly } from 'vue';
import type { Disposition, RegionDeletionController } from './regionDeletionState';
import { unknownRegionDeletionMessage } from './regionDeletionState';
defineProps<{ controller: DeepReadonly<RegionDeletionController> }>();
const groups = ['places', 'areas'] as const;
const choices: Disposition[] = ['keep', 'delete'];
</script>

<template>
  <div class="row g-3">
    <fieldset v-for="group in groups" :key="group" class="col-12 col-sm-6" :disabled="controller.state.submitting || controller.state.held">
      <legend class="h6 text-capitalize">{{ group }} ({{ controller.state.preview ? controller.state.preview.consequences[group === 'places' ? 'movedPlaces' : 'movedAreas'].count + controller.state.preview.consequences[group === 'places' ? 'deletedPlaces' : 'deletedAreas'].count : '…' }})</legend>
      <label v-for="choice in choices" :key="choice" class="d-block border rounded p-3 mb-2" :class="{ 'border-primary': controller.state[group] === choice }">
        <input type="radio" :name="`region-delete-${group}`" :value="choice" :checked="controller.state[group] === choice"
          @change="controller.select(group, choice)" />
        <strong class="ms-2">{{ choice === 'keep' ? 'Keep (recommended)' : 'Delete permanently' }}</strong>
        <span class="d-block small mt-1">{{ choice === 'keep' ? 'Move to Unassigned Places' : 'Cannot be undone' }}</span>
      </label>
    </fieldset>
  </div>
  <div role="status" aria-live="polite" aria-atomic="true">
    <p v-if="controller.state.loading">Loading current consequences…</p>
    <div v-if="controller.state.preview">
      <p>Places: {{ controller.state.preview.consequences.movedPlaces.count }} kept, {{ controller.state.preview.consequences.deletedPlaces.count }} deleted.
        Areas: {{ controller.state.preview.consequences.movedAreas.count }} kept, {{ controller.state.preview.consequences.deletedAreas.count }} deleted.</p>
      <p v-if="controller.state.preview.destination.mode !== 'not-required'">{{ controller.state.preview.destination.mode === 'create' ? 'Creates' : 'Appends to existing' }} Unassigned Places.</p>
      <p>The Region’s private planning is removed. Kept children retain their own planning; deleted children and Segments lose theirs.</p>
      <p>Segments deleted: {{ controller.state.preview.consequences.deletedSegments.count }}.</p>
      <p>Surviving waypoint routes changed: {{ controller.state.preview.consequences.updatedSegments.count }};
        saved waypoint associations removed: {{ controller.state.preview.consequences.detachedWaypoints.count }}.</p>
      <p v-if="controller.state.places === 'delete'">Deletion is permanent. Visit history retains snapshots and is unlinked from deleted Places. Kept Areas do not preserve deleted Places or routes.</p>
    </div>
    <p v-if="controller.state.submitting">Deleting Region. Waiting for the server result…</p>
  </div>
  <div v-if="controller.state.error" role="alert" class="alert alert-warning">{{ controller.state.error }}</div>
  <div v-if="controller.state.held" role="alert" class="alert alert-warning">
    {{ unknownRegionDeletionMessage }} The earlier request remains unresolved. Another Region deletion is held until its terminal outcome is explicitly reconciled.
  </div>
  <button v-if="!controller.state.held && !controller.state.submitting" type="button" class="btn btn-outline-secondary btn-sm" :disabled="controller.state.loading" @click="controller.refresh()">Refresh preview</button>
</template>
