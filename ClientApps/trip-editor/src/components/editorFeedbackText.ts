import { locale, message } from '../../../../wwwroot/js/localization/messages.js';
import { EditorMutationHttpError, EditorValidationError } from '../api/tripEditorApi';

/** Localizes the displayed status while existing styling and workflow checks retain their inputs. */
export const editorStatusText = (value: string): string => {
  const known: Record<string, string> = {
    'Saving...': 'Editor.Saving',
    'Saving order...': 'Editor.SavingOrder',
    'Save failed': 'Editor.SaveFailed',
    'Unsaved changes': 'Editor.UnsavedChanges',
    'Saved': 'Editor.Saved',
    'Planning unsaved': 'Editor.PlanningUnsaved',
    'Planning save pending': 'Editor.PlanningPending',
    'Planning save not confirmed': 'Editor.PlanningUnconfirmed',
    'Deleted target Planning retained': 'Editor.PlanningDeleted'
  };
  if (known[value]) return message(known[value], value);
  if (value.endsWith(' with warning')) return message('Editor.SavedWithWarning', '{0} with warning', editorStatusText(value.slice(0, -13)));
  if (value.startsWith('Saved ')) return message('Editor.SavedAt', 'Saved {0}', value.slice(6));
  return value;
};

/** Describes the current editor mode around the localized kind, without relabelling authored names. */
export const editorModeText = (kind: string, mode: string): string => {
  const label = message(`Editor.Entity${kind[0].toUpperCase()}${kind.slice(1)}`, kind);
  return mode === 'add' ? message('Editor.NewEntity', 'New {0}', label) : message('Editor.EditingEntity', 'Editing {0}', label);
};

/** Maps recognized request failures without changing which attempts remain unconfirmed. */
export const editorErrorText = (error: unknown, fallback: string): string => {
  if (locale === 'en-US') return error instanceof Error ? error.message : fallback;
  if (error instanceof EditorValidationError) return message('Editor.InvalidForm', 'Review the highlighted fields before saving.');
  if (error instanceof EditorMutationHttpError) {
    switch (error.status) {
      case 401:
      case 403:
        return message('Editor.SessionEnded', 'Your session has ended or your account is disabled. Sign in again. Keep any unsaved changes before leaving this page.');
      case 404:
        return message('Editor.ChangeUnavailable', 'This Trip or item is no longer available.');
      case 409:
        return message('Editor.Conflict', 'Your changes could not be saved because the Trip changed. Review the current version before trying again.');
    }
  }
  return message('Editor.ChangeUnconfirmed', 'The change could not be confirmed. Keep your draft and review the current state before trying again.');
};
