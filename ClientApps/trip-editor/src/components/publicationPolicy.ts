import type { MetadataDraft } from './metadataDraft';
import { confirm } from '../composables/useConfirmDialog';

/** First publication confirms the policy already visible in the author form; existing explicit choices are retained. */
export async function confirmFirstPublication(draft: MetadataDraft): Promise<boolean> {
  const policy = draft.contributionPolicy;
  const accepted = await confirm({ title: 'Publish Trip?',
    message: `Owner's contributions: ${policy}. Inherited terms and notices remain separate. Public view-only does not prevent manual copying or revoke inherited rights.`,
    confirmLabel: 'Publish', cancelLabel: 'Keep editing' });
  // A choice edited while the dialog was pending needs its own confirmation.
  return accepted && policy === draft.contributionPolicy;
}
