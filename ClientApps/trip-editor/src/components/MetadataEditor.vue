<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, watch } from 'vue';
import { EditorValidationError, patchMetadata, patchShareProgress, putTags, suggestTags } from '../api/tripEditorApi';
import { confirm } from '../composables/useConfirmDialog';
import type { EditorSurfaceController, EditorTarget } from '../composables/useEditorSurface';
import EditorSurface from './EditorSurface.vue';
import RichNotesEditor from './RichNotesEditor.vue';
import type {
  EditorMutationResult,
  EditorOptions,
  EditorTag,
  EditorTripMetadata,
  EditorTripMetadataUpdateRequest,
  EditorWarning,
  TagSuggestion
} from '../types';

const props = defineProps<{
  metadata: EditorTripMetadata;
  tagsBySlug: Record<string, EditorTag>;
  tagOrder: string[];
  tagOptions: EditorOptions['tag'];
  editorSurface: EditorSurfaceController;
  editorEndpoint: string;
  antiforgeryToken: string;
  tripIndexUrl: string;
  hasRegionDraftChanges: boolean;
}>();

const emit = defineEmits<{
  saved: [metadata: EditorTripMetadata];
  mutationApplied: [result: EditorMutationResult<unknown>];
}>();

type MetadataDraft = {
  name: string;
  isPublic: boolean;
  shareProgressEnabled: boolean;
  notesHtml: string;
  coverImageRawUrl: string;
  centerLatitude: string;
  centerLongitude: string;
  zoom: string;
  tags: string[];
};

const draft = reactive<MetadataDraft>(toDraft(props.metadata, props.tagOrder, props.tagsBySlug));
const isSaving = ref(false);
const lastSavedAt = ref<string | null>(null);
const saveError = ref<string | null>(null);
const validationErrors = ref<Record<string, string[]>>({});
const warnings = ref<EditorWarning[]>([]);
const savedExitInProgress = ref(false);
const tagInput = ref('');
const tagSuggestions = ref<TagSuggestion[]>([]);
const tagSuggestionError = ref<string | null>(null);
let unregisterSurfaceHandler: (() => void) | null = null;
let suggestionRequestId = 0;

const persistedDraft = computed(() => toDraft(props.metadata, props.tagOrder, props.tagsBySlug));
const isMetadataDirty = computed(() => JSON.stringify(normalizeMetadataDraft(draft)) !== JSON.stringify(normalizeMetadataDraft(persistedDraft.value)));
const isTagsDirty = computed(() => JSON.stringify(normalizeTagNames(draft.tags)) !== JSON.stringify(normalizeTagNames(persistedDraft.value.tags)));
const isShareProgressDirty = computed(() => normalizeShareProgress(draft) !== normalizeShareProgress(persistedDraft.value));
const isDirty = computed(() => isMetadataDirty.value || isTagsDirty.value || isShareProgressDirty.value);
const shareProgressUnavailable = computed(() => !draft.isPublic || !props.metadata.isPublic);
const visibleShareProgressEnabled = computed({
  get: () => !shareProgressUnavailable.value && draft.shareProgressEnabled,
  set: value => {
    draft.shareProgressEnabled = value;
  }
});
const progressUrl = computed(() => (!shareProgressUnavailable.value && draft.shareProgressEnabled ? props.metadata.progressPublicUrl : null));
const target = computed<EditorTarget>(() => ({
  key: 'metadata',
  identity: 'metadata',
  kind: 'metadata',
  mode: 'edit',
  title: `Edit Trip - ${props.metadata.name}`,
  subtitle: props.metadata.isPublic ? 'Public trip' : 'Private trip'
}));
const isActive = computed(() => props.editorSurface.isTargetActive(target.value));

const statusText = computed(() => {
  if (isSaving.value) {
    return 'Saving...';
  }

  if (saveError.value) {
    return 'Save failed';
  }

  if (isDirty.value) {
    return 'Unsaved changes';
  }

  return lastSavedAt.value ? `Saved ${lastSavedAt.value}` : 'Saved';
});

watch(
  () => [props.metadata, props.tagOrder, props.tagsBySlug] as const,
  () => {
    if (isSaving.value) {
      return;
    }

    Object.assign(draft, toDraft(props.metadata, props.tagOrder, props.tagsBySlug));
    validationErrors.value = {};
    saveError.value = null;
  }
);

watch(tagInput, () => {
  void loadTagSuggestions();
});

watch(
  () => draft.isPublic,
  isPublic => {
    if (!isPublic) {
      draft.shareProgressEnabled = false;
    }
  }
);

onMounted(() => {
  window.addEventListener('beforeunload', confirmUnload);
  unregisterSurfaceHandler = props.editorSurface.registerTargetHandler(target.value.key, {
    isDirty: () => isDirty.value,
    discard: resetDraft
  });
  void openMetadata();
});

onUnmounted(() => {
  unregisterSurfaceHandler?.();
  window.removeEventListener('beforeunload', confirmUnload);
});

const resetDraft = (): void => {
  Object.assign(draft, toDraft(props.metadata, props.tagOrder, props.tagsBySlug));
  tagInput.value = '';
  tagSuggestions.value = [];
  tagSuggestionError.value = null;
  validationErrors.value = {};
  saveError.value = null;
  warnings.value = [];
};

const saveAndExit = async (): Promise<void> => {
  if (hasUnsavedNonMetadataEditorChanges() && !(await confirmDiscardTripEditorChanges())) {
    return;
  }

  await save(true);
};

const save = async (exitAfterSave: boolean): Promise<void> => {
  isSaving.value = true;
  savedExitInProgress.value = false;
  saveError.value = null;
  validationErrors.value = {};
  warnings.value = [];

  let savedMetadata = props.metadata;
  let failed = false;

  try {
    if (isMetadataDirty.value) {
      const result = await patchMetadata(props.editorEndpoint, props.antiforgeryToken, buildMetadataRequest(draft));
      savedMetadata = result.affected.metadata ?? result.data;
      warnings.value = result.warnings;
      Object.assign(draft, { ...draft, ...toMetadataDraft(savedMetadata) });
      emit('saved', savedMetadata);
      emit('mutationApplied', result as EditorMutationResult<unknown>);
    }

    if (isTagsDirty.value) {
      const result = await putTags(props.editorEndpoint, props.antiforgeryToken, { tags: normalizeTagNames(draft.tags) });
      draft.tags = result.data.map(tag => tag.name);
      emit('mutationApplied', result as EditorMutationResult<unknown>);
    }

    if (isShareProgressDirty.value && savedMetadata.isPublic) {
      const result = await patchShareProgress(props.editorEndpoint, props.antiforgeryToken, { enabled: draft.shareProgressEnabled });
      savedMetadata = result.affected.metadata ?? result.data;
      draft.shareProgressEnabled = savedMetadata.shareProgressEnabled;
      emit('saved', savedMetadata);
      emit('mutationApplied', result as EditorMutationResult<unknown>);
    }
  } catch (error) {
    failed = true;
    if (error instanceof EditorValidationError) {
      validationErrors.value = error.errors;
      saveError.value = error.message;
    } else {
      saveError.value = error instanceof Error ? error.message : 'Trip settings save failed.';
    }
  } finally {
    isSaving.value = false;
  }

  if (!failed) {
    lastSavedAt.value = new Intl.DateTimeFormat(undefined, { timeStyle: 'short' }).format(new Date());
    if (exitAfterSave) {
      savedExitInProgress.value = true;
      window.location.assign(props.tripIndexUrl);
    }
  }
};

const backToTrips = async (): Promise<void> => {
  if (!hasUnsavedEditorChanges() || (await confirmDiscardTripEditorChanges())) {
    window.location.assign(props.tripIndexUrl);
  }
};

function confirmUnload(event: BeforeUnloadEvent): void {
  if (savedExitInProgress.value) {
    return;
  }

  if (!hasUnsavedEditorChanges()) {
    return;
  }

  event.preventDefault();
  event.returnValue = '';
}

/// Combines metadata, tag, share-progress, and child-region drafts for navigation prompts.
function hasUnsavedEditorChanges(): boolean {
  return isDirty.value || props.hasRegionDraftChanges;
}

/// Tracks editor-owned drafts that Save & Exit cannot persist through the metadata surface.
function hasUnsavedNonMetadataEditorChanges(): boolean {
  return props.hasRegionDraftChanges;
}

/// Confirms before discarding Trip Editor drafts that are not saved by the current action.
function confirmDiscardTripEditorChanges(): Promise<boolean> {
  return confirm({
    title: 'Discard changes?',
    message: 'Discard unsaved trip editor changes and return to Trips?',
    confirmLabel: 'Discard',
    cancelLabel: 'Stay',
    variant: 'warning'
  });
}

async function openMetadata(): Promise<void> {
  await props.editorSurface.activateTarget(target.value);
}

async function loadTagSuggestions(): Promise<void> {
  const query = tagInput.value.trim();
  const requestId = ++suggestionRequestId;
  tagSuggestionError.value = null;

  if (!query) {
    tagSuggestions.value = [];
    return;
  }

  try {
    const items = await suggestTags(query, props.tagOptions.suggestionTake);
    if (requestId === suggestionRequestId) {
      const existingNames = new Set(normalizeTagNames(draft.tags).map(normalizeTagNameKey));
      tagSuggestions.value = items.filter(item => !existingNames.has(normalizeTagNameKey(item.name)));
    }
  } catch {
    if (requestId === suggestionRequestId) {
      tagSuggestions.value = [];
      tagSuggestionError.value = 'Suggestions unavailable.';
    }
  }
}

function addTag(value: string): void {
  const name = value.trim();
  if (!name || draft.tags.length >= props.tagOptions.maxTags) {
    return;
  }

  const existing = new Set(normalizeTagNames(draft.tags).map(normalizeTagNameKey));
  if (!existing.has(normalizeTagNameKey(name))) {
    draft.tags.push(name);
  }

  tagInput.value = '';
  tagSuggestions.value = [];
}

function removeTag(index: number): void {
  draft.tags.splice(index, 1);
}

function toDraft(metadata: EditorTripMetadata, tagOrder: string[], tagsBySlug: Record<string, EditorTag>): MetadataDraft {
  return {
    ...toMetadataDraft(metadata),
    tags: tagOrder.map(slug => tagsBySlug[slug]?.name).filter(Boolean) as string[]
  };
}

function toMetadataDraft(metadata: EditorTripMetadata): Omit<MetadataDraft, 'tags'> {
  return {
    name: metadata.name,
    isPublic: metadata.isPublic,
    shareProgressEnabled: metadata.isPublic && metadata.shareProgressEnabled,
    notesHtml: metadata.notesHtml,
    coverImageRawUrl: metadata.coverImage?.rawUrl ?? '',
    centerLatitude: metadata.center ? String(metadata.center.latitude) : '',
    centerLongitude: metadata.center ? String(metadata.center.longitude) : '',
    zoom: metadata.zoom === null ? '' : String(metadata.zoom)
  };
}

function normalizeMetadataDraft(value: MetadataDraft): EditorTripMetadataUpdateRequest {
  return buildMetadataRequest(value);
}

function normalizeShareProgress(value: MetadataDraft): boolean {
  return value.isPublic && props.metadata.isPublic && value.shareProgressEnabled;
}

function buildMetadataRequest(value: MetadataDraft): EditorTripMetadataUpdateRequest {
  const centerLatitude = value.centerLatitude.trim();
  const centerLongitude = value.centerLongitude.trim();
  const zoom = value.zoom.trim();
  const coverImageRawUrl = value.coverImageRawUrl.trim();
  const hasPartialCenter = Boolean(centerLatitude || centerLongitude);

  return {
    name: value.name,
    notesHtml: value.notesHtml,
    isPublic: value.isPublic,
    coverImage: coverImageRawUrl ? { rawUrl: coverImageRawUrl } : null,
    center: hasPartialCenter
      ? { latitude: centerLatitude ? Number(centerLatitude) : Number.NaN, longitude: centerLongitude ? Number(centerLongitude) : Number.NaN }
      : null,
    zoom: zoom ? Number(zoom) : null
  };
}

function normalizeTagNames(values: string[]): string[] {
  const seen = new Set<string>();
  const tags: string[] = [];
  values.forEach(value => {
    const tag = value.trim();
    const key = normalizeTagNameKey(tag);
    if (tag && !seen.has(key)) {
      seen.add(key);
      tags.push(tag);
    }
  });
  return tags;
}

function normalizeTagNameKey(value: string): string {
  return value.trim().toLocaleLowerCase();
}

const fieldErrors = (key: string): string[] => validationErrors.value[key] ?? [];
</script>

<template>
  <section v-if="!isActive" class="trip-editor-panel trip-editor-editor-summary">
    <div>
      <h2>Trip Settings</h2>
      <p>{{ metadata.isPublic ? 'Public trip' : 'Private trip' }}</p>
    </div>
    <button type="button" class="btn btn-outline-light btn-sm" @click="openMetadata">Edit Trip</button>
  </section>

  <EditorSurface v-else :controller="editorSurface" :target="target" :status-text="statusText">
    <template #body>
      <form id="trip-editor-metadata-form" class="trip-editor-metadata" @submit.prevent="save(false)">
        <div v-if="saveError" class="trip-editor-form-error" role="alert">{{ saveError }}</div>

        <div v-if="warnings.length > 0" class="trip-editor-form-warning" role="status">
          <p v-for="warning in warnings" :key="warning.code">{{ warning.message }}</p>
        </div>

        <label class="trip-editor-field">
          <span>Name</span>
          <input v-model="draft.name" type="text" autocomplete="off" />
          <small v-for="message in fieldErrors('name')" :key="message">{{ message }}</small>
        </label>

        <label class="trip-editor-toggle">
          <input v-model="draft.isPublic" type="checkbox" />
          <span>Public trip</span>
        </label>

        <section class="trip-editor-settings-group" aria-labelledby="trip-editor-share-progress-heading">
          <h3 id="trip-editor-share-progress-heading">Share Progress</h3>
          <label class="trip-editor-toggle">
            <input v-model="visibleShareProgressEnabled" type="checkbox" :disabled="shareProgressUnavailable" />
            <span>Show visit progress on public trip</span>
          </label>
          <a v-if="progressUrl" :href="progressUrl" target="_blank" rel="noopener">Open progress URL</a>
          <small v-for="message in fieldErrors('enabled')" :key="message">{{ message }}</small>
          <small v-for="message in fieldErrors('shareProgressEnabled')" :key="message">{{ message }}</small>
        </section>

        <section class="trip-editor-settings-group" aria-labelledby="trip-editor-tags-heading">
          <h3 id="trip-editor-tags-heading">Tags</h3>
          <div class="trip-editor-tags trip-editor-tags--editable">
            <span v-for="(tag, index) in draft.tags" :key="`${normalizeTagNameKey(tag)}-${index}`">
              {{ tag }}
              <button type="button" :aria-label="`Remove tag ${tag}`" @click="removeTag(index)">Remove</button>
            </span>
          </div>
          <div class="trip-editor-tag-entry">
            <label class="trip-editor-field">
              <span>Add tag</span>
              <input
                v-model="tagInput"
                type="text"
                autocomplete="off"
                :aria-describedby="tagSuggestionError ? 'trip-editor-tag-suggestion-error' : undefined"
                @keydown.enter.prevent="addTag(tagInput)"
              />
            </label>
            <button type="button" class="btn btn-outline-light btn-sm" :disabled="!tagInput.trim() || draft.tags.length >= tagOptions.maxTags" @click="addTag(tagInput)">Add</button>
          </div>
          <div v-if="tagSuggestions.length > 0" class="trip-editor-tag-suggestions">
            <button v-for="suggestion in tagSuggestions" :key="suggestion.slug" type="button" @click="addTag(suggestion.name)">
              {{ suggestion.name }}
            </button>
          </div>
          <small id="trip-editor-tag-suggestion-error" v-if="tagSuggestionError">{{ tagSuggestionError }}</small>
          <small v-for="message in fieldErrors('tags')" :key="message">{{ message }}</small>
          <template v-for="(_, index) in draft.tags" :key="`tag-error-${index}`">
            <small v-for="message in fieldErrors(`tags[${index}]`)" :key="message">{{ message }}</small>
          </template>
        </section>

        <RichNotesEditor editor-id="trip-editor-metadata-notes" v-model="draft.notesHtml" label="Notes" :validation-messages="fieldErrors('notesHtml')" />

        <label class="trip-editor-field">
          <span>Cover Image URL</span>
          <input v-model="draft.coverImageRawUrl" type="url" autocomplete="off" />
          <small v-for="message in fieldErrors('coverImage.rawUrl')" :key="message">{{ message }}</small>
        </label>

        <div class="trip-editor-grid">
          <label class="trip-editor-field">
            <span>Center Latitude</span>
            <input v-model="draft.centerLatitude" type="number" step="any" />
            <small v-for="message in fieldErrors('center.latitude')" :key="message">{{ message }}</small>
          </label>

          <label class="trip-editor-field">
            <span>Center Longitude</span>
            <input v-model="draft.centerLongitude" type="number" step="any" />
            <small v-for="message in fieldErrors('center.longitude')" :key="message">{{ message }}</small>
          </label>
        </div>

        <label class="trip-editor-field">
          <span>Zoom</span>
          <input v-model="draft.zoom" type="number" min="0" max="19" step="1" />
          <small v-for="message in fieldErrors('zoom')" :key="message">{{ message }}</small>
        </label>
      </form>
    </template>

    <template #footer>
      <button type="submit" form="trip-editor-metadata-form" class="btn btn-primary btn-sm" :disabled="isSaving">Save &amp; Continue</button>
      <button type="button" class="btn btn-outline-light btn-sm" :disabled="isSaving" @click="saveAndExit">Save &amp; Exit</button>
      <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="isSaving || !isDirty" @click="resetDraft">Cancel / Reset</button>
      <button type="button" class="btn btn-link btn-sm" :disabled="isSaving" @click="backToTrips">Back to Trips</button>
    </template>
  </EditorSurface>
</template>
