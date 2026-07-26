<script setup lang="ts">
import type { EditorSurfaceController, EditorTarget } from '../composables/useEditorSurface';
import type { EditorSegment, EditorSegmentDraft, EditorTripState } from '../types';
import EditorSurface from './EditorSurface.vue';
import SegmentEditorForm from './SegmentEditorForm.vue';

defineProps<{
  activeSegment: EditorSegment | null;
  controller: EditorSurfaceController;
  draft: EditorSegmentDraft;
  fieldErrors: (key: string) => string[];
  formId: string;
  formSummaryErrors: string[];
  isDirty: boolean;
  isSaving: boolean;
  state: EditorTripState;
  statusText: string;
  target: EditorTarget;
}>();

defineEmits<{
  cancel: [];
  clearRoute: [];
  delete: [];
  drawRoute: [];
  reset: [];
  save: [];
}>();
</script>

<template>
  <EditorSurface :controller="controller" :target="target" :status-text="statusText">
    <template #body>
      <SegmentEditorForm
        :draft="draft"
        :field-errors="fieldErrors"
        :form-id="formId"
        :form-summary-errors="formSummaryErrors"
        :is-dirty="isDirty"
        :state="state"
        @save="$emit('save')"
      />
    </template>

    <template #footer>
      <button v-if="activeSegment?.capabilities.canDelete" type="button" class="btn btn-outline-danger btn-sm me-auto" :disabled="isSaving" @click="$emit('delete')">Delete</button>
      <button type="button" class="btn btn-outline-light btn-sm" :disabled="isSaving" @click="$emit('drawRoute')">Draw/Edit Route</button>
      <button type="button" class="btn btn-outline-light btn-sm" :disabled="isSaving || draft.route === null" @click="$emit('clearRoute')">Clear Route</button>
      <button type="button" class="btn btn-outline-light btn-sm" :disabled="isSaving" @click="$emit('cancel')">Cancel</button>
      <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="isSaving || !isDirty" @click="$emit('reset')">Reset</button>
      <button type="submit" :form="formId" class="btn btn-primary btn-sm" :disabled="isSaving">Save Segment</button>
    </template>
  </EditorSurface>
</template>
