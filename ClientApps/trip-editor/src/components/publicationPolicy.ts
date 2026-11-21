import { message } from '../../../../wwwroot/js/localization/messages.js';
import type { MetadataDraft } from './metadataDraft';
import { confirm } from '../composables/useConfirmDialog';

/** First publication confirms the policy already visible in the author form; existing explicit choices are retained. */
export async function confirmFirstPublication(draft: MetadataDraft): Promise<boolean> {
  const policy = draft.contributionPolicy;
  const label = policy === 'view-only' ? message('Editor.PublicViewOnly', 'Public view-only')
    : policy ?? message('Editor.UnlicensedEffectivelyViewOnly', 'Unlicensed — effectively view-only');
  const accepted = await confirm({ title: message('Editor.PublishTripQuestion', 'Publish Trip?'),
    message: message('Editor.PublishTripPolicy', 'Owner\'s contributions: {0}. Inherited terms and notices remain separate. Public view-only does not prevent manual copying or revoke inherited rights.', label),
    confirmLabel: message('Editor.PublishTrip', 'Publish'), cancelLabel: message('Shell.KeepEditing', 'Keep editing') });
  // A choice edited while the dialog was pending needs its own confirmation.
  return accepted && policy === draft.contributionPolicy;
}
