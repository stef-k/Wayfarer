<script setup lang="ts">
import { computed } from 'vue';
import type { EditorAreaDraft } from '../types';
import RichNotesEditor from './RichNotesEditor.vue';

const props = defineProps<{
  draft: EditorAreaDraft;
  fieldErrors: (key: string) => string[];
  formId: string;
  formSummaryErrors: string[];
}>();

const emit = defineEmits<{
  save: [];
}>();

const geometryStatus = computed(() => {
  const ring = props.draft.geometry?.coordinates?.[0] ?? [];
  const vertices = Math.max(0, ring.length - 1);
  return vertices >= 3 ? `${vertices} polygon vertices` : 'No polygon drawn';
});
</script>

<template>
  <form :id="props.formId" class="trip-editor-region-form" @submit.prevent="emit('save')">
    <div v-if="props.formSummaryErrors.length > 0" class="trip-editor-form-error" role="alert">
      <p v-for="message in props.formSummaryErrors" :key="message">{{ message }}</p>
    </div>

    <label class="trip-editor-field">
      <span>Name</span>
      <input v-model="props.draft.name" type="text" autocomplete="off" />
      <small v-for="message in props.fieldErrors('name')" :key="message">{{ message }}</small>
    </label>

    <RichNotesEditor :editor-id="`${props.formId}-notes`" v-model="props.draft.notesHtml" label="Notes" :validation-messages="props.fieldErrors('notesHtml')" />

    <div class="trip-editor-grid">
      <label class="trip-editor-field">
        <span>Fill Color</span>
        <input v-model="props.draft.fillHex" type="color" />
      </label>
      <label class="trip-editor-field">
        <span>Fill Hex</span>
        <input v-model="props.draft.fillHex" type="text" autocomplete="off" />
        <small v-for="message in props.fieldErrors('fillHex')" :key="message">{{ message }}</small>
      </label>
    </div>

    <div class="trip-editor-field">
      <span>Geometry</span>
      <output>{{ geometryStatus }}</output>
      <small v-for="message in props.fieldErrors('geometry')" :key="message">{{ message }}</small>
      <small v-for="message in props.fieldErrors('geometry.coordinates')" :key="message">{{ message }}</small>
    </div>
  </form>
</template>
