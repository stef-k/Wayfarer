<script setup lang="ts">
import type { EditorSurfaceController, EditorTarget } from '../composables/useEditorSurface';
import type { EditorPlace, EditorPlaceDraft, EditorRegion, EditorTripState } from '../types';
import EditorSurface from './EditorSurface.vue';
import PlaceEditorForm from './PlaceEditorForm.vue';

defineProps<{
  activePlace: EditorPlace | null;
  controller: EditorSurfaceController;
  draft: EditorPlaceDraft;
  fieldErrors: (key: string) => string[];
  formId: string;
  formSummaryErrors: string[];
  isDirty: boolean;
  isSaving: boolean;
  normalRegions: EditorRegion[];
  state: EditorTripState;
  statusText: string;
  target: EditorTarget;
}>();

defineEmits<{
  cancel: [];
  delete: [];
  pickCoordinate: [];
  reset: [];
  save: [];
}>();
</script>

<template>
  <EditorSurface :controller="controller" :target="target" :status-text="statusText">
    <template #body>
      <PlaceEditorForm
        :active-place="activePlace"
        :draft="draft"
        :field-errors="fieldErrors"
        :form-id="formId"
        :form-summary-errors="formSummaryErrors"
        :is-saving="isSaving"
        :normal-regions="normalRegions"
        :state="state"
        @save="$emit('save')"
      />
    </template>

    <template #footer>
      <button v-if="activePlace?.capabilities.canDelete" type="button" class="btn btn-outline-danger btn-sm me-auto" :disabled="isSaving" @click="$emit('delete')">
        Delete
      </button>
      <button
        type="button"
        class="btn btn-outline-light btn-sm"
        :disabled="isSaving"
        title="Pick this place's latitude and longitude on the map"
        :aria-describedby="`${formId}-pick-help`"
        @click="$emit('pickCoordinate')"
      >
        Pick on map
        <span :id="`${formId}-pick-help`" class="visually-hidden">Use the map to choose this place's latitude and longitude.</span>
      </button>
      <button type="button" class="btn btn-outline-light btn-sm" :disabled="isSaving" @click="$emit('cancel')">Cancel</button>
      <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="isSaving || !isDirty" @click="$emit('reset')">Reset</button>
      <button type="submit" :form="formId" class="btn btn-primary btn-sm" :disabled="isSaving">Save Place</button>
    </template>
  </EditorSurface>
</template>
