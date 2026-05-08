<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, watch } from 'vue';
import { EditorValidationError, patchMetadata } from '../api/tripEditorApi';
import type { EditorTripMetadata, EditorTripMetadataUpdateRequest, EditorWarning } from '../types';

const props = defineProps<{
  metadata: EditorTripMetadata;
  editorEndpoint: string;
  antiforgeryToken: string;
  tripIndexUrl: string;
  hasRegionDraftChanges: boolean;
}>();

const emit = defineEmits<{
  saved: [metadata: EditorTripMetadata];
}>();

type MetadataDraft = {
  name: string;
  isPublic: boolean;
  notesHtml: string;
  coverImageRawUrl: string;
  centerLatitude: string;
  centerLongitude: string;
  zoom: string;
};

const draft = reactive<MetadataDraft>(toDraft(props.metadata));
const isSaving = ref(false);
const lastSavedAt = ref<string | null>(null);
const saveError = ref<string | null>(null);
const validationErrors = ref<Record<string, string[]>>({});
const warnings = ref<EditorWarning[]>([]);
const savedExitInProgress = ref(false);

const persistedDraft = computed(() => toDraft(props.metadata));
const isDirty = computed(() => JSON.stringify(normalizeDraft(draft)) !== JSON.stringify(normalizeDraft(persistedDraft.value)));

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
  () => props.metadata,
  metadata => {
    Object.assign(draft, toDraft(metadata));
    validationErrors.value = {};
    saveError.value = null;
  }
);

onMounted(() => {
  window.addEventListener('beforeunload', confirmUnload);
});

onUnmounted(() => {
  window.removeEventListener('beforeunload', confirmUnload);
});

const resetDraft = (): void => {
  Object.assign(draft, toDraft(props.metadata));
  validationErrors.value = {};
  saveError.value = null;
  warnings.value = [];
};

const saveAndExit = async (): Promise<void> => {
  if (hasUnsavedNonMetadataEditorChanges() && !window.confirm('Discard unsaved trip editor changes and return to Trips?')) {
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

  try {
    const result = await patchMetadata(props.editorEndpoint, props.antiforgeryToken, buildRequest(draft));
    const metadata = result.affected.metadata ?? result.data;
    warnings.value = result.warnings;
    if (exitAfterSave) {
      savedExitInProgress.value = true;
    }

    emit('saved', metadata);
    lastSavedAt.value = new Intl.DateTimeFormat(undefined, { timeStyle: 'short' }).format(new Date());
    if (exitAfterSave) {
      window.location.assign(props.tripIndexUrl);
    }
  } catch (error) {
    if (error instanceof EditorValidationError) {
      validationErrors.value = error.errors;
      saveError.value = error.message;
    } else {
      saveError.value = error instanceof Error ? error.message : 'Metadata save failed.';
    }
  } finally {
    isSaving.value = false;
  }
};

const backToTrips = (): void => {
  if (!hasUnsavedEditorChanges() || window.confirm('Discard unsaved trip editor changes and return to Trips?')) {
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

/// Combines metadata changes with the active region draft dirty state owned by RegionManager.
function hasUnsavedEditorChanges(): boolean {
  return isDirty.value || props.hasRegionDraftChanges;
}

/// Tracks editor-owned drafts that Save & Exit cannot persist through the metadata endpoint.
function hasUnsavedNonMetadataEditorChanges(): boolean {
  return props.hasRegionDraftChanges;
}

function toDraft(metadata: EditorTripMetadata): MetadataDraft {
  return {
    name: metadata.name,
    isPublic: metadata.isPublic,
    notesHtml: metadata.notesHtml,
    coverImageRawUrl: metadata.coverImage?.rawUrl ?? '',
    centerLatitude: metadata.center ? String(metadata.center.latitude) : '',
    centerLongitude: metadata.center ? String(metadata.center.longitude) : '',
    zoom: metadata.zoom === null ? '' : String(metadata.zoom)
  };
}

function normalizeDraft(value: MetadataDraft): EditorTripMetadataUpdateRequest {
  return buildRequest(value);
}

function buildRequest(value: MetadataDraft): EditorTripMetadataUpdateRequest {
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

const fieldErrors = (key: string): string[] => validationErrors.value[key] ?? [];
</script>

<template>
  <section class="trip-editor-panel trip-editor-metadata">
    <div class="trip-editor-panel__line">
      <h2>Trip Settings</h2>
      <span class="trip-editor-save-state">{{ statusText }}</span>
    </div>

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

    <label class="trip-editor-field">
      <span>Notes HTML</span>
      <textarea v-model="draft.notesHtml" rows="7"></textarea>
      <small v-for="message in fieldErrors('notesHtml')" :key="message">{{ message }}</small>
    </label>

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

    <div class="trip-editor-actions">
      <button type="button" class="btn btn-primary btn-sm" :disabled="isSaving" @click="save(false)">Save &amp; Continue</button>
      <button type="button" class="btn btn-outline-light btn-sm" :disabled="isSaving" @click="saveAndExit">Save &amp; Exit</button>
      <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="isSaving || !isDirty" @click="resetDraft">Cancel / Reset</button>
      <button type="button" class="btn btn-link btn-sm" :disabled="isSaving" @click="backToTrips">Back to Trips</button>
    </div>
  </section>
</template>
