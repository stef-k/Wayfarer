<script setup lang="ts">
import { editorErrorText } from './editorFeedbackText';
import { editorFieldError } from '../editorValidation';
import { date, message as localize } from '../../../../wwwroot/js/localization/messages.js';
import { isLanguageUnload, registerLanguageDraft } from '../../../../wwwroot/js/localization/languageNavigation.js';
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { confirmMetadataExit } from './metadataExit';
import type { EditorSurfaceController, EditorTarget } from '../composables/useEditorSurface';
import type { TripEditorMapView } from '../map/mapViewport';
import { buildMetadataRequest, capturedViewDraft, createMetadataDraftState, equivalentViewDraft, normalizeTagNames,
  viewDraft } from './metadataDraft';
import EditorSurface from './EditorSurface.vue';
import RichNotesEditor from './RichNotesEditor.vue';
import TripCreditsEditor from './TripCreditsEditor.vue';
import MetadataTagsEditor from './MetadataTagsEditor.vue';
import ReceiptComparison from '../recovery/ReceiptComparison.vue';
import { custodyMessage, useMetadataCustody } from '../recovery/useMetadataCustody';
import { confirmFirstPublication } from './publicationPolicy';
import type {
  EditorMutationResult,
  EditorOptions,
  EditorTag,
  EditorTripMetadata
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
  mutationApplied: [result: EditorMutationResult<unknown>];
}>();

const draftState = createMetadataDraftState(props.metadata, props.tagOrder, props.tagsBySlug);
const draft = draftState.draft;
const custody = useMetadataCustody(props, draftState);
const isSaving = ref(false);
const lastSavedAt = ref<string | null>(null);
const saveError = ref<string | null>(null);
const validationErrors = ref<Record<string, string[]>>({});
const exitInProgress = ref(false);
let unregisterSurfaceHandler: (() => void) | null = null;
/// Discarding this interaction revokes pending exit permission without retiring an HTTP attempt.
const editSession = ref(0);
const persistedDraft = computed(() => draftState.baseline);
const isMetadataDirty = computed(() => [...draftState.retained].some(field => field !== 'tags' && field !== 'shareProgressEnabled') ||
  JSON.stringify(buildMetadataRequest(draft)) !== JSON.stringify(buildMetadataRequest(persistedDraft.value)));
const isTagsDirty = computed(() => draftState.retained.has('tags') ||
  JSON.stringify(normalizeTagNames(draft.tags)) !== JSON.stringify(normalizeTagNames(persistedDraft.value.tags)));
const isShareProgressDirty = computed(() => draftState.retained.has('shareProgressEnabled') ||
  draft.shareProgressEnabled !== persistedDraft.value.shareProgressEnabled);
const saveUnconfirmed = computed(() => custody.hold.value.length > 0);
const isDirty = computed(() => isMetadataDirty.value || isTagsDirty.value || isShareProgressDirty.value || saveUnconfirmed.value);
/// Combines settings and the sidebar drafts for navigation prompts.
const hasUnsavedEditorChanges = computed(() => isDirty.value || props.hasRegionDraftChanges);
const shareProgressUnavailable = computed(() => !draft.isPublic || !props.metadata.isPublic);
const progressUrl = computed(() => (!shareProgressUnavailable.value && draft.shareProgressEnabled ? props.metadata.progressPublicUrl : null));
/// Reports persisted share-progress state for read-only Trip summaries.
const shareProgressSummary = computed(() => {
  if (!props.metadata.isPublic) {
    return localize('Editor.ProgressNeedsPublic', 'Unavailable until trip is public');
  }

  return props.metadata.shareProgressEnabled ? localize('Editor.Enabled', 'Enabled') : localize('Editor.Disabled', 'Disabled');
});
const summaryProgressUrl = computed(() => (props.metadata.isPublic && props.metadata.shareProgressEnabled ? props.metadata.progressPublicUrl : null));
const target = computed<EditorTarget>(() => ({
  key: 'metadata',
  identity: 'metadata',
  kind: 'metadata',
  mode: 'edit',
  title: localize('Editor.EditNamedTrip', 'Edit Trip - {0}', props.metadata.name),
  subtitle: props.metadata.isPublic ? localize('Editor.PublicTrip', 'Public trip') : localize('Editor.PrivateTrip', 'Private trip')
}));
const isActive = computed(() => props.editorSurface.isTargetActive(target.value));

const statusText = computed(() => {
  if (isSaving.value) {
    return 'Saving...';
  }

  if (saveUnconfirmed.value) return localize('Editor.MetadataUnconfirmedStatus', 'Trip settings change not confirmed');

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

watch(
  () => draft.isPublic,
  isPublic => {
    if (isPublic && draft.contributionPolicy === null) draft.contributionPolicy = 'CC-BY-4.0';
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

// Pending attempts and the sidebar's retained drafts remain owned by the mounted editor.
const unregisterLanguageDraft = registerLanguageDraft(() => hasUnsavedEditorChanges.value || isSaving.value || saveUnconfirmed.value);

onMounted(() => {
  window.addEventListener('beforeunload', confirmUnload);
  void recoverDraft();
  unregisterSurfaceHandler = props.editorSurface.registerTargetHandler(target.value.key, {
    isDirty: () => isDirty.value,
    save: () => save(false),
    discard: () => { void resetDraft(); }
  });
  if (props.autoOpen !== false) {
    void openMetadata();
  }
});

onUnmounted(() => {
  editSession.value++;
  unregisterSurfaceHandler?.();
  unregisterLanguageDraft();
  window.removeEventListener('beforeunload', confirmUnload);
});

const resetDraft = async (): Promise<void> => {
  if (!await custody.discard()) return;
  editSession.value++;
  validationErrors.value = {};
  saveError.value = null;
};

/// Save settings before checking the current unsaved drafts through the shared exit action.
const saveAndExit = async (): Promise<void> => {
  await save(true);
};

/** Saves the captured branches in order. Recovery is a separate read-only action. */
const save = async (exitAfterSave: boolean): Promise<void> => {
  if (isSaving.value || !custody.ready.value) return;
  if (draft.isPublic && !props.metadata.isPublic && props.metadata.contributionPolicy === null && !await confirmFirstPublication(draft)) return;
  const savingSession = editSession.value;
  isSaving.value = true;
  saveError.value = null;
  validationErrors.value = {};
  try {
    const captured = await custody.captureSave();
    let current = props.metadata;
    for (const branch of captured.branches) {
      if (branch.kind === 'editor-sharing' && !current.isPublic && JSON.parse(branch.body).enabled === true) {
        saveError.value = localize('Editor.ProgressNeedsPublic', 'Unavailable until trip is public');
        return;
      }
      const result = await custody.submit(branch, captured.generation);
      if (result.metadata) {
        current = result.metadata;
        publishOwner(current);
      }
      if (!result.continueSave || savingSession !== editSession.value) return;
    }
    lastSavedAt.value = date(new Date(), { timeStyle: 'short' });
    await nextTick();
    if (exitAfterSave && savingSession === editSession.value && !isDirty.value) await backToTrips();
  } catch (error) {
    saveError.value = editorErrorText(error, 'Trip settings save failed.');
  } finally {
    isSaving.value = false;
  }
};

/** Projects a fresh owner read while keeping recovery separate from the foreground save sequence. */
async function recoverDraft(reopening = false): Promise<void> {
  const current = await custody.recover(reopening);
  if (current) publishOwner(current);
}

/** Uses the existing metadata-only workspace projection. */
function publishOwner(metadata: EditorTripMetadata): void {
  emit('mutationApplied', {
    success: true, data: metadata, warnings: [], deletedIds: { regions: [], places: [], areas: [], segments: [], tags: [] },
    affected: { metadata, regions: [], regionOrder: null, places: [], placeOrdersByRegionId: {}, areas: [],
      areaOrdersByRegionId: {}, segments: [], segmentOrder: null, tags: metadata.tags ?? [], tagOrder: metadata.tags?.map(tag => tag.slug) ?? null, visitProgress: null, options: null }
  });
}

const backToTrips = async (): Promise<void> => {
  await custody.persist();
  if (!hasUnsavedEditorChanges.value || (await confirmMetadataExit(saveUnconfirmed.value, custodyMessage('held')))) {
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
  if (isLanguageUnload(event) || approvedExit || !hasUnsavedEditorChanges.value) {
    return;
  }

  event.preventDefault();
  event.returnValue = '';
}

/** Opens the existing metadata surface without replacing pending drafts. */
const openMetadata = (): Promise<boolean> => props.editorSurface.activateTarget(target.value);

const fieldErrors = (key: string): string[] => (validationErrors.value[key] ?? []).map(editorFieldError);
/** Keeps the collection-level error attached to the credit controls across unrelated draft edits. */
const creditErrors = computed(() => fieldErrors('credits'));
</script>

<template>
  <section v-if="!isActive" class="trip-editor-panel trip-editor-editor-summary">
    <div>
      <h2>{{ localize("Editor.TripSettings", "Trip Settings") }}</h2>
      <p>{{ metadata.isPublic ? localize('Editor.PublicTrip', 'Public trip') : localize('Editor.PrivateTrip', 'Private trip') }}</p>
      <p>{{ localize("Editor.ShareProgressLabel", "Share progress:") }} {{ shareProgressSummary }}</p>
      <div v-if="metadata.publicUrl || summaryProgressUrl" class="trip-editor-editor-summary__links">
        <a v-if="metadata.publicUrl" :href="metadata.publicUrl" target="_blank" rel="noopener">{{ localize("Editor.OpenPublicTrip", "Open public trip") }}</a>
        <a v-if="summaryProgressUrl" :href="summaryProgressUrl" target="_blank" rel="noopener">{{ localize("Editor.OpenProgressURL", "Open progress URL") }}</a>
      </div>
    </div>
    <button type="button" class="btn btn-outline-light btn-sm" @click="openMetadata">{{ localize("Editor.EditTrip", "Edit Trip") }}</button>
  </section>

  <EditorSurface v-else :controller="editorSurface" :target="target" :status-text="statusText">
    <template #body>
      <div v-if="custody.feedback.value || custody.hold.value.length" class="trip-editor-form-warning" role="status">
        <p>{{ custody.feedback.value || custodyMessage('held') }}</p>
        <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="isSaving || custody.busy.value" @click="recoverDraft(custody.stale.value)">
          {{ custody.stale.value ? localize('Editor.ReceiptReopen', 'Reopen latest draft') : localize('Editor.ReceiptRecover', 'Recover Trip save') }}
        </button>
      </div>
      <ReceiptComparison v-if="custody.comparison.value" :current="custody.comparison.value" :fields="custody.review.value"
        :disabled="isSaving || custody.busy.value || custody.stale.value" @adopt="custody.adopt" />
      <p v-for="status in new Set(custody.historicalStatus.value)" :key="status" role="status">{{ status }}</p>
      <form id="trip-editor-metadata-form" class="trip-editor-metadata" :inert="!custody.ready.value || custody.stale.value || custody.editingBlocked.value" @submit.prevent="save(false)">
        <div v-if="saveError" class="trip-editor-form-error" role="alert">{{ saveError }}</div>

        <label class="trip-editor-field">
          <span>{{ localize("Editor.Name", "Name") }}</span>
          <input v-model="draft.name" type="text" autocomplete="off" />
          <small v-for="message in fieldErrors('name')" :key="message">{{ message }}</small>
        </label>

        <label class="trip-editor-toggle">
          <input v-model="draft.isPublic" type="checkbox" />
          <span>{{ localize("Editor.PublicTrip", "Public trip") }}</span>
        </label>
        <TripCreditsEditor :draft="draft" :errors="creditErrors" />

        <section class="trip-editor-settings-group" aria-labelledby="trip-editor-share-progress-heading">
          <h3 id="trip-editor-share-progress-heading">{{ localize("Editor.ShareProgress", "Share Progress") }}</h3>
          <label class="trip-editor-toggle">
            <input v-model="draft.shareProgressEnabled" type="checkbox" :disabled="shareProgressUnavailable" />
            <span>{{ localize("Editor.ShowVisitProgressOnPublicTrip", "Show visit progress on public trip") }}</span>
          </label>
          <a v-if="progressUrl" :href="progressUrl" target="_blank" rel="noopener">{{ localize("Editor.OpenProgressURL", "Open progress URL") }}</a>
          <small v-for="message in fieldErrors('enabled')" :key="message">{{ message }}</small>
          <small v-for="message in fieldErrors('shareProgressEnabled')" :key="message">{{ message }}</small>
        </section>

        <MetadataTagsEditor :key="editSession" v-model:tags="draft.tags" :options="tagOptions" :field-errors="fieldErrors" />

            <RichNotesEditor :adoption-revision="draftState.notesAdoption.value" :key="editSession" editor-id="trip-editor-metadata-notes" v-model="draft.notesHtml" :label="localize('Shell.Notes', 'Notes')" :validation-messages="fieldErrors('notesHtml')" />

        <label class="trip-editor-field">
          <span>{{ localize("Editor.CoverImageURL", "Cover Image URL") }}</span>
          <input v-model="draft.coverImageRawUrl" type="url" autocomplete="off" />
          <small v-for="message in fieldErrors('coverImage.rawUrl')" :key="message">{{ message }}</small>
        </label>

        <div class="trip-editor-grid">
          <label class="trip-editor-field">
            <span>{{ localize("Editor.CenterLatitude", "Center Latitude") }}</span>
            <input v-model="draft.centerLatitude" type="number" step="any" />
            <small v-for="message in fieldErrors('center.latitude')" :key="message">{{ message }}</small>
          </label>

          <label class="trip-editor-field">
            <span>{{ localize("Editor.CenterLongitude", "Center Longitude") }}</span>
            <input v-model="draft.centerLongitude" type="number" step="any" />
            <small v-for="message in fieldErrors('center.longitude')" :key="message">{{ message }}</small>
          </label>
        </div>

        <label class="trip-editor-field">
          <span>{{ localize("Editor.Zoom", "Zoom") }}</span>
          <input v-model="draft.zoom" type="number" min="0" max="19" step="1" />
          <small v-for="message in fieldErrors('zoom')" :key="message">{{ message }}</small>
        </label>
      </form>
    </template>

    <template #footer>
      <button type="submit" form="trip-editor-metadata-form" class="btn btn-primary btn-sm"  :disabled="isSaving || !custody.ready.value || custody.busy.value">{{ localize("Editor.SaveSettingsContinue", "Save settings & Continue") }}</button>
      <button type="button" class="btn btn-outline-light btn-sm"  :disabled="isSaving || !custody.ready.value || custody.busy.value" @click="saveAndExit">{{ localize("Editor.SaveSettingsExit", "Save settings & Exit") }}</button>
      <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="isSaving || saveUnconfirmed || !isDirty" @click="resetDraft">{{ localize("Editor.CancelReset", "Cancel / Reset") }}</button>
      <button type="button" class="btn btn-link btn-sm" :disabled="isSaving" @click="backToTrips">{{ saveUnconfirmed ? localize('Editor.LeaveWithoutSaving', 'Leave without saving') : localize('Editor.BackToTrips', 'Back to Trips') }}</button>
    </template>
  </EditorSurface>
</template>
