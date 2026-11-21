<script setup lang="ts">
import { message as localize } from '../../../../wwwroot/js/localization/messages.js';
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
  /** Owner-selected visible Notes replacement; local echoes do not advance it. */
  notesAdoption: number;
  /** An explicit reset/session change retires the mounted Notes instance. */
  notesSession?: number;
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
        :notes-session="notesSession"
        :notes-adoption="notesAdoption"
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
        {{ localize("Editor.Delete", "Delete") }}
      </button>
      <button
        type="button"
        class="btn btn-outline-light btn-sm"
        :disabled="isSaving || isMapWorkActive"
        :title="localize('Editor.PickThisPlaceSLatitudeAndLongitude', 'Pick this place\'s latitude and longitude on the map')"
        :aria-describedby="`${formId}-pick-help`"
        @click="$emit('pickCoordinate')"
      >
        {{ localize("Editor.PickOnMap", "Pick on map") }}
        <span :id="`${formId}-pick-help`" class="visually-hidden">{{ localize("Editor.ClickTheMapOrDragTheMarker", "Click the map or drag the marker. Done updates the draft; Save Place persists it.") }}</span>
      </button>
      <button type="button" class="btn btn-outline-light btn-sm" :disabled="isSaving || isMapWorkActive" @click="$emit('cancel')">{{ localize("Shell.Cancel", "Cancel") }}</button>
      <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="isSaving || isMapWorkActive || !isDirty" @click="$emit('reset')">{{ localize("Editor.Reset", "Reset") }}</button>
      <button type="submit" :form="formId" class="btn btn-primary btn-sm" :disabled="isSaving || isMapWorkActive">{{ localize("Editor.SavePlace", "Save Place") }}</button>
    </template>
  </EditorSurface>
</template>
