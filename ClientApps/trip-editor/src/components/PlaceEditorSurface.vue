<script setup lang="ts">
import { computed } from 'vue';
import type { EditorSurfaceController, EditorTarget } from '../composables/useEditorSurface';
import type { EditorPlace, EditorPlaceDraft, EditorRegion, EditorTripState } from '../types';
import EditorSurface from './EditorSurface.vue';
import PlaceEditorForm from './PlaceEditorForm.vue';

const props = defineProps<{
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

const isMapWorkActive = computed(() => props.controller.isMapWorkActive.value);

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
        :coordinate-read-only="isMapWorkActive"
        :normal-regions="normalRegions"
        :state="state"
        @save="$emit('save')"
      />
    </template>

    <template #footer>
      <button v-if="activePlace?.capabilities.canDelete" type="button" class="btn btn-outline-danger btn-sm me-auto" :disabled="isSaving || isMapWorkActive" @click="$emit('delete')">
        Delete
      </button>
      <button
        type="button"
        class="btn btn-outline-light btn-sm"
        :disabled="isSaving || isMapWorkActive"
        title="Pick this place's latitude and longitude on the map"
        :aria-describedby="`${formId}-pick-help`"
        @click="$emit('pickCoordinate')"
      >
        Pick on map
        <span :id="`${formId}-pick-help`" class="visually-hidden">Click the map or drag the marker. Done updates the draft; Save Place persists it.</span>
      </button>
      <button type="button" class="btn btn-outline-light btn-sm" :disabled="isSaving || isMapWorkActive" @click="$emit('cancel')">Cancel</button>
      <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="isSaving || isMapWorkActive || !isDirty" @click="$emit('reset')">Reset</button>
      <button type="submit" :form="formId" class="btn btn-primary btn-sm" :disabled="isSaving || isMapWorkActive">Save Place</button>
    </template>
  </EditorSurface>
</template>
