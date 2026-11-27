<script setup lang="ts">
import { message as localize } from '../../../../wwwroot/js/localization/messages.js';
import type { EditorMetadataOriginalValues, EditorTripMetadata } from '../types';
import type { SettingsField } from './custody';

defineProps<{ current: EditorTripMetadata; fields: SettingsField[]; disabled?: boolean }>();
defineEmits<{ adopt: [field: SettingsField] }>();

/** Localizes labels for fields that may require explicit comparison-base adoption. */
const label = (field: SettingsField): string => {
  switch (field) {
    case 'name': return localize('Editor.Name', 'Name');
    case 'notesHtml': return localize('Shell.Notes', 'Notes');
    case 'coverImage': return localize('Editor.CoverImageURL', 'Cover Image URL');
    case 'center': return localize('Editor.ReceiptMapCenter', 'Map center');
    case 'zoom': return localize('Editor.Zoom', 'Zoom');
    case 'isPublic': return localize('Editor.PublicTrip', 'Public trip');
    case 'contributionPolicy': return localize('Editor.PublicationPolicy', 'Publication policy');
    case 'credits': return localize('Editor.SourcesCredits', 'Sources & credits');
    default: return field;
  }
};

/** Shows the exact published value as text, keeping null distinct from Clear. */
function valueText(current: EditorTripMetadata, field: SettingsField): string {
  const value = current.originalValues?.[field as keyof EditorMetadataOriginalValues];
  if (value === null) return localize('Editor.ReceiptNull', 'Not set');
  if (value === '') return localize('Editor.ReceiptEmpty', 'Empty');
  return typeof value === 'string' ? value : JSON.stringify(value) ?? '';
}
</script>

<template>
  <section v-if="fields.length" role="status" class="trip-editor-form-warning">
    <p>{{ localize('Editor.ReceiptReview', 'Review the current Trip value and explicitly use it as the comparison base.') }}</p>
    <div v-for="field in fields" :key="field">
      <strong>{{ localize('Editor.ReceiptCurrent', 'Current {0}', label(field)) }}</strong>
      <pre class="text-wrap text-break">{{ valueText(current, field) }}</pre>
      <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="disabled" @click="$emit('adopt', field)">
        {{ localize('Editor.ReceiptAdopt', 'Use current {0} as comparison base', label(field)) }}
      </button>
    </div>
  </section>
</template>
