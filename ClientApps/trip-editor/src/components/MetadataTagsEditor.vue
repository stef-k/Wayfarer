<script setup lang="ts">
import { ref, watch } from 'vue';
import { message as localize } from '../../../../wwwroot/js/localization/messages.js';
import { suggestTags } from '../api/tripEditorApi';
import { normalizeTagNameKey, normalizeTagNames } from './metadataDraft';
import type { EditorOptions, TagSuggestion } from '../types';

const props = defineProps<{ tags: string[]; options: EditorOptions['tag']; fieldErrors: (key: string) => string[] }>();
const emit = defineEmits<{ 'update:tags': [tags: string[]] }>();
const tagInput = ref('');
const tagSuggestions = ref<TagSuggestion[]>([]);
const tagSuggestionError = ref<string | null>(null);
let suggestionRequestId = 0;
watch(tagInput, () => { void loadTagSuggestions(); });

/** Publishes suggestions only for the latest input. */
async function loadTagSuggestions(): Promise<void> {
  const query = tagInput.value.trim();
  const requestId = ++suggestionRequestId;
  tagSuggestionError.value = null;

  if (!query) {
    tagSuggestions.value = [];
    return;
  }

  try {
    const items = await suggestTags(query, props.options.suggestionTake);
    if (requestId === suggestionRequestId) {
      const existingNames = new Set(normalizeTagNames(props.tags).map(normalizeTagNameKey));
      tagSuggestions.value = items.filter(item => !existingNames.has(normalizeTagNameKey(item.name)));
    }
  } catch {
    if (requestId === suggestionRequestId) {
      tagSuggestions.value = [];
      tagSuggestionError.value = localize('Editor.TagSuggestionsUnavailable', 'Suggestions unavailable.');
    }
  }
}

function addTag(value: string): void {
  const name = value.trim();
  if (!name || props.tags.length >= props.options.maxTags) {
    return;
  }

  const existing = new Set(normalizeTagNames(props.tags).map(normalizeTagNameKey));
  if (!existing.has(normalizeTagNameKey(name))) {
    emit('update:tags', [...props.tags, name]);
  }

  tagInput.value = '';
  tagSuggestions.value = [];
}

function removeTag(index: number): void {
  emit('update:tags', props.tags.filter((_, position) => position !== index));
}

</script>

<template>
  <section class="trip-editor-settings-group" aria-labelledby="trip-editor-tags-heading">
          <h3 id="trip-editor-tags-heading">{{ localize("Editor.Tags", "Tags") }}</h3>
          <div class="trip-editor-tags trip-editor-tags--editable">
            <span v-for="(tag, index) in tags" :key="`${normalizeTagNameKey(tag)}-${index}`">
              {{ tag }}
              <button type="button" :aria-label="localize('Editor.RemoveTag', 'Remove tag {0}', tag)" @click="removeTag(index)">{{ localize("Editor.Remove", "Remove") }}</button>
            </span>
          </div>
          <div class="trip-editor-tag-entry">
            <label class="trip-editor-field">
              <span>{{ localize("Editor.AddTag", "Add tag") }}</span>
              <input
                v-model="tagInput"
                type="text"
                autocomplete="off"
                :aria-describedby="tagSuggestionError ? 'trip-editor-tag-suggestion-error' : undefined"
                @keydown.enter.prevent="addTag(tagInput)"
              />
            </label>
            <button type="button" class="btn btn-outline-light btn-sm" :disabled="!tagInput.trim() || tags.length >= options.maxTags" @click="addTag(tagInput)">{{ localize("Editor.Add", "Add") }}</button>
          </div>
          <div v-if="tagSuggestions.length > 0" class="trip-editor-tag-suggestions">
            <button v-for="suggestion in tagSuggestions" :key="suggestion.slug" type="button" @click="addTag(suggestion.name)">
              {{ suggestion.name }}
            </button>
          </div>
          <small id="trip-editor-tag-suggestion-error" v-if="tagSuggestionError">{{ tagSuggestionError }}</small>
          <small v-for="message in fieldErrors('tags')" :key="message">{{ message }}</small>
          <template v-for="(_, index) in tags" :key="`tag-error-${index}`">
            <small v-for="message in fieldErrors(`tags[${index}]`)" :key="message">{{ message }}</small>
          </template>
        </section>
</template>
