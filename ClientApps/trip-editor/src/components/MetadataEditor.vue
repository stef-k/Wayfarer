<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { EditorMutationHttpError, EditorValidationError, patchMetadata, patchShareProgress, putTags, suggestTags } from '../api/tripEditorApi';
import { confirm } from '../composables/useConfirmDialog';
import type { EditorSurfaceController, EditorTarget } from '../composables/useEditorSurface';
import type { TripEditorMapView } from '../map/mapViewport';
import { buildMetadataRequest, capturedViewDraft, createMetadataDraftState, equivalentViewDraft, normalizeTagNameKey, normalizeTagNames,
  viewDraft, type MetadataDraft } from './metadataDraft';
import EditorSurface from './EditorSurface.vue';
import RichNotesEditor from './RichNotesEditor.vue';
import type {
  EditorMutationResult,
  EditorOptions,
  EditorTag,
  EditorTripMetadata,
  EditorWarning,
  TagSuggestion
} from '../types';

const props = defineProps<{
  metadata: EditorTripMetadata;
  /** A new adapter gesture notification; MetadataEditor remains the sole draft owner. */
  capturedMapView?: TripEditorMapView | null;
  tagsBySlug: Record<string, EditorTag>;
  tagOrder: string[];
  tagOptions: EditorOptions['tag'];
  editorSurface: EditorSurfaceController;
  editorEndpoint: string;
  antiforgeryToken: string;
  tripIndexUrl: string;
  hasRegionDraftChanges: boolean;
  autoOpen?: boolean;
}>();

const emit = defineEmits<{
  saved: [metadata: EditorTripMetadata];
  mutationApplied: [result: EditorMutationResult<unknown>];
}>();

const draftState = createMetadataDraftState(props.metadata, props.tagOrder, props.tagsBySlug);
const draft = draftState.draft;
const isSaving = ref(false);
const lastSavedAt = ref<string | null>(null);
const saveError = ref<string | null>(null);
const validationErrors = ref<Record<string, string[]>>({});
const warnings = ref<EditorWarning[]>([]);
const exitInProgress = ref(false);
const tagInput = ref('');
const tagSuggestions = ref<TagSuggestion[]>([]);
const tagSuggestionError = ref<string | null>(null);
let unregisterSurfaceHandler: (() => void) | null = null;
let suggestionRequestId = 0;
/// Distinguishes explicit sharing input during this save from privacy-driven clearing.
let shareProgressEditedDuringSave = false;
const persistedDraft = computed(() => draftState.baseline);
const isMetadataDirty = computed(() => JSON.stringify(buildMetadataRequest(draft)) !== JSON.stringify(buildMetadataRequest(persistedDraft.value)));
const isTagsDirty = computed(() => JSON.stringify(normalizeTagNames(draft.tags)) !== JSON.stringify(normalizeTagNames(persistedDraft.value.tags)));
const isShareProgressDirty = computed(() => draftState.sharingAttempt.value !== null || normalizeShareProgress(draft) !== normalizeShareProgress(persistedDraft.value));
const sharingUnconfirmed = computed(() => draftState.sharingAttempt.value?.outcome === 'unknown');
/// Explain the held choice and explicit abandonment without promising remote rollback or settlement.
const sharingHoldMessage = computed(() =>
  `We could not confirm the sharing change. Your choice (${draft.shareProgressEnabled ? 'Enabled' : 'Disabled'}) is retained in this editor. ` +
  'Saving is paused because the earlier change may still finish. Keep this editor open to retain your choice, or leave without saving. ' +
  'Leaving or reloading loses the retained choice and does not undo the earlier change.');
const isDirty = computed(() => isMetadataDirty.value || isTagsDirty.value || isShareProgressDirty.value);
/// Combines metadata, tag, share-progress, region, and segment drafts for navigation prompts.
const hasUnsavedEditorChanges = computed(() => isDirty.value || props.hasRegionDraftChanges);
const shareProgressUnavailable = computed(() => !draft.isPublic || !props.metadata.isPublic);
const visibleShareProgressEnabled = computed({
  get: () => !shareProgressUnavailable.value && draft.shareProgressEnabled,
  set: value => {
    draft.shareProgressEnabled = value;
    if (isSaving.value) shareProgressEditedDuringSave = true;
  }
});
const progressUrl = computed(() => (!shareProgressUnavailable.value && draft.shareProgressEnabled ? props.metadata.progressPublicUrl : null));
/// Reports persisted share-progress state for read-only Trip summaries.
const shareProgressSummary = computed(() => {
  if (sharingUnconfirmed.value) return 'Sharing change not confirmed';
  if (!props.metadata.isPublic) {
    return 'Unavailable until trip is public';
  }

  return props.metadata.shareProgressEnabled ? 'Enabled' : 'Disabled';
});
const summaryProgressUrl = computed(() => (props.metadata.isPublic && props.metadata.shareProgressEnabled ? props.metadata.progressPublicUrl : null));
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

  if (sharingUnconfirmed.value) return 'Sharing change not confirmed';

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

    // Dirty values and their original bases, including a visible conflict, survive unrelated refreshes.
    draftState.refresh(props.metadata, props.tagOrder, props.tagsBySlug);
  }
);

// Consume notifications once; reset must not replay the last capture prop.
watch(() => props.capturedMapView, view => {
  if (!view) return;
  const captured = capturedViewDraft(view);
  const matchesSaved = equivalentViewDraft(persistedDraft.value, captured);
  if (equivalentViewDraft(draft, captured)) return;
  Object.assign(draft, matchesSaved ? viewDraft(persistedDraft.value) : captured);
}, { flush: 'sync' });

watch(tagInput, () => {
  void loadTagSuggestions();
});

watch(
  () => draft.isPublic,
  isPublic => {
    if (!isPublic && !draftState.sharingAttempt.value) {
      draft.shareProgressEnabled = false;
    }
  }
);

watch(
  () => props.autoOpen,
  autoOpen => {
    if (autoOpen !== false && !isActive.value) {
      void openMetadata();
    }
  }
);

onMounted(() => {
  window.addEventListener('beforeunload', confirmUnload);
  unregisterSurfaceHandler = props.editorSurface.registerTargetHandler(target.value.key, {
    isDirty: () => isDirty.value,
    discard: resetDraft
  });
  if (props.autoOpen !== false) {
    void openMetadata();
  }
});

onUnmounted(() => {
  unregisterSurfaceHandler?.();
  window.removeEventListener('beforeunload', confirmUnload);
});

const resetDraft = (): void => {
  draftState.reset(props.metadata, props.tagOrder, props.tagsBySlug);
  tagInput.value = '';
  tagSuggestions.value = [];
  tagSuggestionError.value = null;
  validationErrors.value = {};
  saveError.value = null;
  warnings.value = [];
};

const saveAndExit = async (): Promise<void> => {
  if (isSaving.value || sharingUnconfirmed.value) return;
  if (props.hasRegionDraftChanges && !(await confirmDiscardTripEditorChanges())) {
    return;
  }

  await save(true);
};

const save = async (exitAfterSave: boolean): Promise<void> => {
  if (isSaving.value || sharingUnconfirmed.value) return;
  shareProgressEditedDuringSave = false;
  isSaving.value = true;
  saveError.value = null;
  validationErrors.value = {};
  warnings.value = [];

  let savedMetadata = props.metadata;
  let failed = false;

  try {
    if (isMetadataDirty.value) {
      const submitted = draftState.request();
      const result = await patchMetadata(props.editorEndpoint, props.antiforgeryToken, submitted);
      savedMetadata = result.affected.metadata ?? result.data;
      warnings.value = result.warnings;
      draftState.acknowledge(savedMetadata, submitted, shareProgressEditedDuringSave);
      emit('saved', savedMetadata);
      emit('mutationApplied', result as EditorMutationResult<unknown>);
    }

    if (isTagsDirty.value) {
      const submittedTags = normalizeTagNames(draft.tags);
      const result = await putTags(props.editorEndpoint, props.antiforgeryToken, { tags: submittedTags });
      if (JSON.stringify(normalizeTagNames(draft.tags)) === JSON.stringify(submittedTags)) draft.tags = result.data.map(tag => tag.name);
      draftState.baseline.tags = result.data.map(tag => tag.name);
      emit('mutationApplied', result as EditorMutationResult<unknown>);
    }

    // The response advances the baseline before Vue propagates publication into child props.
    if (savedMetadata.isPublic && draft.isPublic && draft.shareProgressEnabled !== persistedDraft.value.shareProgressEnabled) {
      const submittedSharing = draft.shareProgressEnabled;
      draftState.sharingAttempt.value = { enabled: submittedSharing, outcome: 'pending' };
      const result = await patchShareProgress(props.editorEndpoint, props.antiforgeryToken, { enabled: submittedSharing });
      savedMetadata = result.affected.metadata ?? result.data;
      draftState.acknowledgeSharing(savedMetadata);
      emit('saved', savedMetadata);
      emit('mutationApplied', result as EditorMutationResult<unknown>);
    }
  } catch (error) {
    failed = true;
    // Only explicit pre-write rejection retires this attempt. Transport/server/body failures remain unknown.
    if (draftState.sharingAttempt.value) {
      if (error instanceof EditorValidationError || (error instanceof EditorMutationHttpError && [401, 403].includes(error.status))) {
        draftState.sharingAttempt.value = null;
      } else {
        draftState.sharingAttempt.value.outcome = 'unknown';
        return;
      }
    }
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
    await nextTick(); // Let authoritative props reconcile before deciding whether a later capture remains.
    draftState.refresh(props.metadata, props.tagOrder, props.tagsBySlug);
    if (exitAfterSave && !isDirty.value) {
      leaveEditor();
    }
  }
};

const backToTrips = async (): Promise<void> => {
  if (!hasUnsavedEditorChanges.value || (await confirmDiscardTripEditorChanges())) {
    leaveEditor();
  }
};

/// Bypasses the shared unload warning only for an approved app-controlled exit.
function leaveEditor(): void {
  exitInProgress.value = true;
  try {
    window.location.assign(props.tripIndexUrl);
  } catch (error) {
    exitInProgress.value = false;
    throw error;
  }
}

/// Owns browser unload protection for metadata and all sidebar drafts.
function confirmUnload(event: BeforeUnloadEvent): void {
  // Consume approval once so interrupted navigation or a restored page retains protection.
  const approvedExit = exitInProgress.value;
  exitInProgress.value = false;
  if (approvedExit || !hasUnsavedEditorChanges.value) {
    return;
  }

  event.preventDefault();
  event.returnValue = '';
}

/// Confirms before discarding Trip Editor drafts that are not saved by the current action.
function confirmDiscardTripEditorChanges(): Promise<boolean> {
  if (sharingUnconfirmed.value) return confirm({
    title: 'Leave without saving?',
    message: 'The sharing change is not confirmed. The earlier change may still finish. Leaving will lose your retained sharing choice and any other unsaved edits; it will not undo the earlier change.',
    confirmLabel: 'Leave without saving',
    cancelLabel: 'Keep editing',
    variant: 'warning'
  });
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

function normalizeShareProgress(value: MetadataDraft): boolean {
  return value.isPublic && props.metadata.isPublic && value.shareProgressEnabled;
}

const fieldErrors = (key: string): string[] => validationErrors.value[key] ?? [];
</script>

<template>
  <section v-if="!isActive" class="trip-editor-panel trip-editor-editor-summary">
    <div>
      <h2>Trip Settings</h2>
      <p>{{ metadata.isPublic ? 'Public trip' : 'Private trip' }}</p>
      <p>Share progress: {{ shareProgressSummary }}</p>
      <p v-if="sharingUnconfirmed" class="trip-editor-form-warning" role="alert">{{ sharingHoldMessage }}</p>
      <div v-if="metadata.publicUrl || summaryProgressUrl" class="trip-editor-editor-summary__links">
        <a v-if="metadata.publicUrl" :href="metadata.publicUrl" target="_blank" rel="noopener">Open public trip</a>
        <a v-if="summaryProgressUrl" :href="summaryProgressUrl" target="_blank" rel="noopener">Open progress URL</a>
      </div>
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
          <p v-if="sharingUnconfirmed" class="trip-editor-form-warning" role="alert">{{ sharingHoldMessage }}</p>
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
      <button type="submit" form="trip-editor-metadata-form" class="btn btn-primary btn-sm" :disabled="isSaving || sharingUnconfirmed">Save &amp; Continue</button>
      <button type="button" class="btn btn-outline-light btn-sm" :disabled="isSaving || sharingUnconfirmed" @click="saveAndExit">Save &amp; Exit</button>
      <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="isSaving || sharingUnconfirmed || !isDirty" @click="resetDraft">Cancel / Reset</button>
      <button type="button" class="btn btn-link btn-sm" :disabled="isSaving" @click="backToTrips">{{ sharingUnconfirmed ? 'Leave without saving' : 'Back to Trips' }}</button>
    </template>
  </EditorSurface>
</template>
