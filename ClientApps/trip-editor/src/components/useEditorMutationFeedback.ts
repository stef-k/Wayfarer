import { date } from '../../../../wwwroot/js/localization/messages.js';
import { editorFieldError } from '../editorValidation';
import { editorErrorText } from './editorFeedbackText';
import { computed, ref, type ComputedRef } from 'vue';
import { EditorValidationError } from '../api/tripEditorApi';

type FeedbackOptions = {
  isDirty: ComputedRef<boolean>;
  isAreaDraftOpen: ComputedRef<boolean>;
  isOrdering: ComputedRef<boolean>;
  isPlaceDraftOpen: ComputedRef<boolean>;
  isSaving: ComputedRef<boolean>;
  areaFields: string[];
  placeFields: string[];
  regionFields: string[];
};

/// Maps mutation status text onto existing Bootstrap/app feedback color semantics.
export function mutationFeedbackClass(statusText: string): string {
  if (/save failed/i.test(statusText)) {
    return 'text-bg-danger trip-editor-save-state--danger';
  }

  if (/warning/i.test(statusText)) {
    return 'text-bg-warning trip-editor-save-state--warning';
  }

  if (/^place saved\b/i.test(statusText) || /^saved\s+\S+/i.test(statusText)) {
    return 'text-bg-success trip-editor-save-state--success';
  }

  return '';
}

/// Tracks mutation feedback shared by region and place editor forms.
export function useEditorMutationFeedback(options: FeedbackOptions) {
  const saveError = ref<string | null>(null);
  const saveWarning = ref<string | null>(null);
  const validationErrors = ref<Record<string, string[]>>({});
  const lastSavedAt = ref<string | null>(null);
  const lastSavedLabel = ref('Saved');

  const statusText = computed(() => {
    if (options.isSaving.value) {
      return 'Saving...';
    }

    if (options.isOrdering.value) {
      return 'Saving order...';
    }

    if (saveError.value) {
      return 'Save failed';
    }

    if (saveWarning.value) {
      return lastSavedAt.value ? `${lastSavedLabel.value} ${lastSavedAt.value} with warning` : `${lastSavedLabel.value} with warning`;
    }

    if (options.isDirty.value) {
      return 'Unsaved changes';
    }

    return lastSavedAt.value ? `${lastSavedLabel.value} ${lastSavedAt.value}` : lastSavedLabel.value;
  });

  const formSummaryErrors = computed(() => {
    const fields = options.isAreaDraftOpen.value ? options.areaFields : options.isPlaceDraftOpen.value ? options.placeFields : options.regionFields;
    return Object.entries(validationErrors.value).filter(([key]) => !fields.includes(key)).flatMap(([, messages]) => messages.map(editorFieldError));
  });

  function applyError(error: unknown, fallback: string): void {
    saveWarning.value = null;

    if (error instanceof EditorValidationError) {
      validationErrors.value = error.errors;
      saveError.value = editorErrorText(error, fallback);
      return;
    }

    saveError.value = editorErrorText(error, fallback);
  }

  function fieldErrors(key: string): string[] {
    return (validationErrors.value[key] ?? []).map(editorFieldError);
  }

  function markSaved(warningMessages: string[] = [], savedLabel = 'Saved'): void {
    lastSavedAt.value = date(new Date(), { timeStyle: 'short' });
    lastSavedLabel.value = savedLabel;
    saveError.value = null;
    saveWarning.value = warningMessages.length > 0 ? warningMessages.join(' ') : null;
  }

  function resetFeedback(): void {
    saveError.value = null;
    saveWarning.value = null;
    validationErrors.value = {};
  }

  return { applyError, fieldErrors, formSummaryErrors, markSaved, resetFeedback, saveError, saveWarning, statusText };
}
