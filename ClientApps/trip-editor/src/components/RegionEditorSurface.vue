<script setup lang="ts">
import type { EditorSurfaceController, EditorTarget } from '../composables/useEditorSurface';
import type { EditorRegion } from '../types';
import EditorSurface from './EditorSurface.vue';
import RegionEditorForm from './RegionEditorForm.vue';
import type { RegionDraft } from './regionPlaceDrafts';

defineProps<{
  activeRegion: EditorRegion | null;
  controller: EditorSurfaceController;
  draft: RegionDraft;
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
  reset: [];
  save: [];
}>();
</script>

<template>
  <EditorSurface :controller="controller" :target="target" :status-text="statusText">
    <template #body>
      <RegionEditorForm
        :active-region="activeRegion"
        :draft="draft"
        :field-errors="fieldErrors"
        :form-id="formId"
        :form-summary-errors="formSummaryErrors"
        :is-saving="isSaving"
        @save="$emit('save')"
      />
    </template>

    <template #footer>
      <button v-if="activeRegion?.capabilities.canDelete" type="button" class="btn btn-outline-danger btn-sm me-auto" :disabled="isSaving" @click="$emit('delete')">
        Delete
      </button>
      <button type="button" class="btn btn-outline-light btn-sm" :disabled="isSaving" @click="$emit('cancel')">Cancel</button>
      <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="isSaving || !isDirty" @click="$emit('reset')">Reset</button>
      <button type="submit" :form="formId" class="btn btn-primary btn-sm" :disabled="isSaving">Save Region</button>
    </template>
  </EditorSurface>
</template>
