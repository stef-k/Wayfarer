<script setup lang="ts">
import type { EditorSurfaceController, EditorTarget } from '../composables/useEditorSurface';
import type { EditorArea, EditorAreaDraft } from '../types';
import AreaEditorForm from './AreaEditorForm.vue';
import EditorSurface from './EditorSurface.vue';

defineProps<{
  activeArea: EditorArea | null;
  controller: EditorSurfaceController;
  draft: EditorAreaDraft;
  fieldErrors: (key: string) => string[];
  formId: string;
  formSummaryErrors: string[];
  isDirty: boolean;
  isSaving: boolean;
  statusText: string;
  target: EditorTarget;
}>();

defineEmits<{
  cancel: [];
  delete: [];
  drawArea: [];
  reset: [];
  save: [];
}>();
</script>

<template>
  <EditorSurface :controller="controller" :target="target" :status-text="statusText">
    <template #body>
      <AreaEditorForm
        :draft="draft"
        :field-errors="fieldErrors"
        :form-id="formId"
        :form-summary-errors="formSummaryErrors"
        @save="$emit('save')"
      />
    </template>

    <template #footer>
      <button v-if="activeArea?.capabilities.canDelete" type="button" class="btn btn-outline-danger btn-sm me-auto" :disabled="isSaving" @click="$emit('delete')">
        Delete
      </button>
      <button type="button" class="btn btn-outline-light btn-sm" :disabled="isSaving" @click="$emit('drawArea')">Draw/Edit Area</button>
      <button type="button" class="btn btn-outline-light btn-sm" :disabled="isSaving" @click="$emit('cancel')">Cancel</button>
      <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="isSaving || !isDirty" @click="$emit('reset')">Reset</button>
      <button type="submit" :form="formId" class="btn btn-primary btn-sm" :disabled="isSaving">Save Area</button>
    </template>
  </EditorSurface>
</template>
