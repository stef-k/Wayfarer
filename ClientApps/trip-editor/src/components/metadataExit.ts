import { confirm } from '../composables/useConfirmDialog';
import { message } from '../../../../wwwroot/js/localization/messages.js';

/** Confirms an explicit return to My Trips with the explanation owned by the active metadata attempt. */
export const confirmMetadataExit = (unconfirmed: boolean, holdMessage: string): Promise<boolean> => {
  if (unconfirmed) return confirm({
    title: message('Editor.LeaveWithoutSavingQuestion', 'Leave without saving?'),
    message: holdMessage,
    confirmLabel: message('Editor.LeaveWithoutSaving', 'Leave without saving'),
    cancelLabel: message('Shell.KeepEditing', 'Keep editing'),
    variant: 'warning'
  });

  return confirm({
    title: message('Editor.DiscardQuestion', 'Discard changes?'),
    message: message('Editor.MetadataExitDraft', 'Leave unsaved editor changes and return to Trips? Planning is saved separately with Save Planning; Trip settings actions do not save it. Leaving loses retained drafts and does not undo pending or unconfirmed attempts.'),
    confirmLabel: message('Editor.Discard', 'Discard'),
    cancelLabel: message('Editor.Stay', 'Stay'),
    variant: 'warning'
  });
};
