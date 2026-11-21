<script setup lang="ts">
import { message as localize } from '../../../../wwwroot/js/localization/messages.js';
import type { EditorRegion } from '../types';
import RichNotesEditor from './RichNotesEditor.vue';
import type { RegionDraft } from './regionPlaceDrafts';

const props = defineProps<{
  activeRegion: EditorRegion | null;
  draft: RegionDraft;
  fieldErrors: (key: string) => string[];
  formId: string;
  /** Owner-selected visible Notes replacement; local echoes do not advance it. */
  notesAdoption: number;
  /** An explicit reset/session change retires the mounted Notes instance. */
  notesSession?: number;
  formSummaryErrors: string[];
  isSaving: boolean;
}>();

const emit = defineEmits<{
  save: [];
}>();
</script>

<template>
  <form :id="props.formId" class="trip-editor-region-form" @submit.prevent="emit('save')">
    <div v-if="props.formSummaryErrors.length > 0" class="trip-editor-form-error" role="alert">
      <p v-for="message in props.formSummaryErrors" :key="message">{{ message }}</p>
    </div>

    <label class="trip-editor-field">
      <span>{{ localize("Editor.Name", "Name") }}</span>
      <input v-model="props.draft.name" type="text" autocomplete="off" />
      <small v-for="message in props.fieldErrors('name')" :key="message">{{ message }}</small>
    </label>

    <RichNotesEditor :adoption-revision="notesAdoption" :key="props.notesSession" :editor-id="`${props.formId}-notes`" v-model="props.draft.notesHtml" :label="localize('Shell.Notes', 'Notes')" :validation-messages="props.fieldErrors('notesHtml')" />

    <label class="trip-editor-field">
      <span>{{ localize("Editor.CoverImageURL", "Cover Image URL") }}</span>
      <input v-model="props.draft.coverImageRawUrl" type="url" autocomplete="off" />
      <small v-for="message in props.fieldErrors('coverImage.rawUrl')" :key="message">{{ message }}</small>
    </label>

    <div class="trip-editor-grid">
      <label class="trip-editor-field">
        <span>{{ localize("Editor.CenterLatitude", "Center Latitude") }}</span>
        <input v-model="props.draft.centerLatitude" type="number" step="any" />
        <small v-for="message in props.fieldErrors('center.latitude')" :key="message">{{ message }}</small>
      </label>
      <label class="trip-editor-field">
        <span>{{ localize("Editor.CenterLongitude", "Center Longitude") }}</span>
        <input v-model="props.draft.centerLongitude" type="number" step="any" />
        <small v-for="message in props.fieldErrors('center.longitude')" :key="message">{{ message }}</small>
      </label>
    </div>
  </form>
</template>
