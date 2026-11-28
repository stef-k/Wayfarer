import { message } from '../../../../wwwroot/js/localization/messages.js';
import { initShareProgressToggle } from '../../../../wwwroot/js/Areas/User/Trip/shareProgressToggle.js';
import { EditorMutationHttpError } from '../api/tripEditorApi';
import { adoptedDraft } from '../components/metadataDraft';
import { adoptBases, authorize, CustodyRefusal, heldFields, readCustody, remember, saveDraft, sharingBase, type Custody } from './custody';
import { freezeRequest, issuePermit, OwnerScopeUnavailable, readOwnerMetadata, sendAttempt } from './receiptApi';
import { receiptStatus, recoverMetadata } from './recoverMetadata';
import { custodyMessage } from './useMetadataCustody';
import type { EditorTripMetadata } from '../types';

/** Connects the Viewer switches to the same account/Trip custody used by Editor and My Trips. */
function mount(root: HTMLElement, account: string, tripId: string): void {
  const endpoint = `/api/trips/${tripId}/editor`;
  const token = root.querySelector<HTMLInputElement>('input[name="__RequestVerificationToken"]')?.value ?? '';
  const switches = document.querySelectorAll<HTMLInputElement>('#shareProgressSwitch, #modalShareProgressSwitch');
  const status = root.querySelector<HTMLElement>('[role="status"]')!;
  const recover = root.querySelector<HTMLButtonElement>('[data-recover]')!;
  const adopt = root.querySelector<HTMLButtonElement>('[data-adopt]')!;
  const submit = root.querySelector<HTMLButtonElement>('[data-submit]')!;
  const field = 'shareProgressEnabled';
  let current: EditorTripMetadata | null = null;
  let row: Custody | null = null;
  let busy = false;
  let feedback = '';

  /** Keep controls coherent with the last owner read and show any retained choice independently. */
  function render(): void {
    const held = row ? heldFields(row).includes(field) : false;
    const review = row?.review.includes(field) ?? false;
    const choice = row?.fields[field]?.desired.shareProgressEnabled;
    const enabled = !!current?.isPublic && !!current.shareProgressEnabled;
    for (const toggle of switches) {
      if (current) toggle.checked = choice ?? enabled;
      toggle.disabled = busy || !current?.isPublic || held || review;
    }
    for (const badge of document.querySelectorAll('#shareProgressStatus, #modalShareStatus')) {
      if (!current) continue;
      badge.classList.toggle('bg-success', enabled);
      badge.classList.toggle('bg-secondary', !enabled);
      badge.textContent = enabled ? message('Viewer.Public', 'Visible to public') : message('Viewer.Private', 'Private');
    }
    if (current) document.getElementById('btnCopyProgressLink')?.classList.toggle('d-none', !enabled);
    recover.disabled = busy;
    adopt.hidden = !review || held || !current;
    adopt.disabled = busy;
    submit.hidden = choice === undefined || held || review || !current || choice && !current.isPublic;
    submit.disabled = busy;
    const retained = choice === undefined ? '' : message('Viewer.ShareRetainedChoice', 'Retained sharing choice ({0}).',
      choice ? message('Editor.Enabled', 'Enabled') : message('Editor.Disabled', 'Disabled'));
    const publication = current && !current.isPublic && choice
      ? message('Viewer.ShareRequiresPublicTrip', 'Make this Trip public before sharing visit progress.') : '';
    const comparison = review && current
      ? message('Viewer.ShareCurrentState', 'Current Trip is {0}, with sharing {1}.',
        current.isPublic ? message('Viewer.Public', 'Public') : message('Viewer.Private', 'Private'),
        current.shareProgressEnabled ? message('Editor.Enabled', 'Enabled') : message('Editor.Disabled', 'Disabled')) : '';
    status.textContent = [feedback, retained, publication, comparison].filter(Boolean).join(' ');
  }

  /** Recovery reads evidence and owner state without dispatching a retained choice. */
  async function refresh(): Promise<void> {
    const result = await recoverMetadata(account, tripId, endpoint);
    row = result.row;
    current = result.current;
    if (!current && !result.inaccessible && !row.attempts.length && !Object.keys(row.fields).length) {
      const read = await readOwnerMetadata(endpoint, account, tripId);
      const latest = await readCustody(account, tripId);
      if (latest.generation !== row.generation || latest.attempts.length) throw new CustodyRefusal('stale');
      row = latest;
      current = read;
    }
    feedback = [heldFields(row).includes(field) ? custodyMessage('held') : '',
      !current ? custodyMessage('owner') : '', ...(result.settled ?? []).map(receiptStatus)].filter(Boolean).join(' ');
  }

  /** Persists the Viewer choice and freezes only its own submitted generation before dispatch. */
  async function save(enabled: boolean): Promise<void> {
    if (!row || !current || enabled && !current.isPublic) return;
    const base = row.fields[field] ?? {
      baseline: { shareProgressEnabled: current.shareProgressEnabled }, original: sharingBase(current)
    };
    row = await saveDraft(row, { ...row.fields, [field]: {
      desired: { shareProgressEnabled: enabled }, baseline: base.baseline, original: base.original
    } }, current);
    await authorize(row, [field]);
    const body = freezeRequest({ enabled });
    const permit = await issuePermit(endpoint, token, account, tripId, body, 'viewer-sharing').catch(() => null);
    if (!permit) {
      feedback = custodyMessage('issuance');
      return;
    }
    const attempt = await authorize(row, [field], { permit, body });
    if (!attempt) return;
    row = { ...row, attempts: [...row.attempts, attempt] };
    try {
      const evidence = await sendAttempt(endpoint, token, attempt);
      await remember(row, attempt, evidence);
      await refresh();
      if (evidence.state === 'terminalUnapplied') feedback = receiptStatus(evidence.state);
    } catch (error) {
      feedback = custodyMessage('held');
      // Access guidance accompanies the hold while recovery settles the saved attempt.
      if (error instanceof EditorMutationHttpError) {
        if (error.status === 401) feedback += ' ' + message('Viewer.ShareSessionEnded',
          'Your session has ended or your account is disabled. Sign in again. Keep any unsaved changes before leaving this page.');
        else if (error.status === 404) feedback += ' ' + message('Viewer.ShareAuthorityUnavailable',
          'This Trip is unavailable or you no longer have permission to change its sharing settings.');
      }
    }
  }

  /** Serializes Viewer actions without holding IndexedDB open across the network. */
  async function perform(action: () => Promise<void>): Promise<void> {
    if (busy) return;
    busy = true;
    render();
    try { await action(); }
    catch (error) {
      const reason = error instanceof CustodyRefusal ? error.reason : error instanceof OwnerScopeUnavailable ? 'owner' : 'storage';
      feedback = custodyMessage(reason);
      if (reason === 'stale' || reason === 'owner') current = null;
    } finally {
      busy = false;
      render();
    }
  }

  for (const toggle of switches) toggle.addEventListener('change', () => { const enabled = toggle.checked; void perform(() => save(enabled)); });
  recover.addEventListener('click', () => { void perform(refresh); });
  submit.addEventListener('click', () => {
    const choice = row?.fields[field]?.desired.shareProgressEnabled;
    if (typeof choice === 'boolean') void perform(() => save(choice));
  });
  adopt.addEventListener('click', () => { void perform(async () => {
    const entry = row?.fields[field];
    if (!row || !entry || !current) return;
    row = await adoptBases(row, { [field]: adoptedDraft(field, entry, current) });
    feedback = '';
  }); });
  initShareProgressToggle();
  void perform(refresh);
}

const root = document.getElementById('viewer-sharing-recovery');
if (root?.dataset.accountId && root.dataset.tripId) mount(root, root.dataset.accountId, root.dataset.tripId);
