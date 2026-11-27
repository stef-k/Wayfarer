import { computed, inject, nextTick, ref, shallowRef, watch } from 'vue';
import { message as localize } from '../../../../wwwroot/js/localization/messages.js';
import { adoptedDraft, type createMetadataDraftState } from '../components/metadataDraft';
import type { EditorMetadataConditionalRequest, EditorTag, EditorTripMetadata } from '../types';
import { adoptBases, authorize, CustodyRefusal, heldFields, knownHistory, readCustody, remember, saveDraft, type Custody, type SettingsField } from './custody';
import { freezeRequest, issuePermit, sendAttempt } from './receiptApi';
import { metadataAccount, metadataWorkspaceError, receiptStatus, recoverMetadata } from './recoverMetadata';

type Props = { metadata: EditorTripMetadata; editorEndpoint: string; antiforgeryToken: string;
  tagOrder: string[]; tagsBySlug: Record<string, EditorTag> };

/** Owns the authenticated Editor's durable generation, handoff and read-only recovery. */
export function useMetadataCustody(props: Props, state: ReturnType<typeof createMetadataDraftState>) {
  const account = inject(metadataAccount, null);
  const workspaceError = inject(metadataWorkspaceError, null);
  const row = shallowRef<Custody | null>(null);
  const comparison = shallowRef<EditorTripMetadata | null>(null);
  const feedback = ref('');
  const unavailable = ref(false);
  const stale = ref(false);
  const busy = ref(false);
  const editingBlocked = ref(false);
  const historicalStatus = ref<string[]>([]);
  const hold = computed(() => row.value ? heldFields(row.value) : []);
  const review = computed(() => comparison.value ? row.value?.review ?? [] : []);
  let pending: Promise<unknown> = Promise.resolve();
  let lastCapture = '';

  /** Serializes local input persistence without holding a transaction across a network request. */
  const enqueue = <T>(action: () => Promise<T>): Promise<T> => {
    const job = pending.then(action);
    pending = job.catch(() => undefined);
    return job;
  };

  /** Keeps holds and storage errors actionable without treating them as server settlement. */
  function report(error: unknown): void {
    if (error instanceof CustodyRefusal) {
      stale.value = error.reason === 'stale';
      feedback.value = custodyMessage(error.reason);
      return;
    }
    unavailable.value = true;
    feedback.value = custodyMessage('storage');
  }

  /** Writes only after input changes. A touched field returning to its old value during a hold remains captured. */
  function persist(): Promise<void> {
    if (account === null || !row.value || stale.value) return Promise.resolve();
    const included = hold.value.filter(field => row.value?.fields[field]);
    const fields = state.capture(included);
    const capture = JSON.stringify(fields);
    if (capture === lastCapture) return pending.then(() => undefined);
    lastCapture = capture;
    return enqueue(async () => {
      if (!row.value || stale.value) return;
      try {
        row.value = await saveDraft(row.value, fields);
        unavailable.value = false;
      } catch (error) {
        lastCapture = '';
        report(error);
      }
    });
  }

  watch(state.draft, () => { void persist(); }, { flush: 'post' });

  /** Publishes only the current durable generation and preserves any dirty field's original. */
  function project(view: Custody, current: EditorTripMetadata | null): void {
    row.value = view;
    comparison.value = current;
    state.restore(view.fields, current ?? props.metadata, props.tagOrder, props.tagsBySlug);
    lastCapture = JSON.stringify(state.capture(heldFields(view).filter(field => view.fields[field])));
  }

  /** Explicit or reload recovery has no mutation or continuation authority. */
  async function recover(): Promise<EditorTripMetadata | null> {
    if (account === null || busy.value) return null;
    if (!account) {
      report(new Error());
      return null;
    }
    busy.value = true;
    try {
      await nextTick();
      await persist();
      const recovered = await recoverMetadata(account, props.metadata.id, props.editorEndpoint);
      await nextTick();
      await persist();
      const latest = await readCustody(account, props.metadata.id);
      const view = latest.generation === recovered.row.generation ? recovered : { row: latest, current: null, settled: recovered.settled };
      // Never replace an input the browser reported it could not retain.
      if (recovered.inaccessible && workspaceError) {
        workspaceError.value = custodyMessage('owner');
        return null;
      }
      if (unavailable.value && row.value && !stale.value) return null;
      project(view.row, view.current);
      const history = knownHistory(view.row);
      historicalStatus.value = [...view.settled ?? [], ...history].map(receiptStatus);
      stale.value = unavailable.value = false;
      if (!view.current && (history.length || Object.keys(view.row.fields).length && !hold.value.length))
        feedback.value = custodyMessage('owner');
      else feedback.value = hold.value.length ? custodyMessage('held') : '';
      return view.current;
    } catch (error) {
      report(error);
      return null;
    } finally {
      busy.value = false;
    }
  }

  /** Captures one submitted generation, checking the same holds for ordinary mixed metadata. */
  async function submit(request: EditorMetadataConditionalRequest): Promise<{ metadata: EditorTripMetadata | null; continueSave: boolean } | null> {
    if (account === null) return null;
    await ready();
    const fields = Object.keys(request.changes);
    const view = row.value!;
    try {
      await authorize(view, fields);
      if (!fields.length || fields.some(field => field !== 'name' && field !== 'notesHtml')) return null;
      const body = freezeRequest(request);
      const permit = await issuePermit(props.editorEndpoint, props.antiforgeryToken, account, view.tripId, body)
        .catch(() => { throw new Error(custodyMessage('issuance')); });
      await ready();
      const attempt = await authorize(view, fields, { permit, body });
      if (!attempt) throw new Error(custodyMessage('storage'));
      if (row.value) row.value = { ...row.value, attempts: [...row.value.attempts, attempt] };
      return await execute(view, attempt);
    } catch (error) {
      if (error instanceof CustodyRefusal) {
        report(error);
        throw new Error(custodyMessage(error.reason));
      }
      throw error;
    }
  }

  /** A foreground response can continue only after correlated success and a fresh owner read. */
  async function execute(view: Custody, attempt: NonNullable<Awaited<ReturnType<typeof authorize>>>) {
    try {
      const evidence = await sendAttempt(props.editorEndpoint, props.antiforgeryToken, attempt);
      if (!['applied', 'checkedNoOp', 'terminalUnapplied'].includes(evidence.state)) throw new Error();
      await remember(view, attempt, evidence);
      const metadata = await recover();
      if (evidence.state === 'terminalUnapplied') {
        feedback.value = custodyMessage('review');
        return { metadata: null, continueSave: false };
      }
      const newer = row.value && Object.keys(attempt.submitted).some(field => row.value?.fields[field as SettingsField]);
      return { metadata, continueSave: !!metadata && !hold.value.length && !stale.value && !newer };
    } catch {
      feedback.value = custodyMessage('held');
      return { metadata: null, continueSave: false };
    }
  }

  /** Storage failure prevents dispatch, and a stale interaction must reopen the current draft. */
  async function ready(): Promise<void> {
    await persist();
    if (stale.value) throw new Error(custodyMessage('stale'));
    if (!row.value || unavailable.value || busy.value) throw new Error(custodyMessage('storage'));
  }

  /** Discards only this displayed generation, keeping immutable attempt evidence. */
  async function discard(): Promise<boolean> {
    if (account === null) return true;
    editingBlocked.value = true;
    try {
      await ready();
      const next = await saveDraft(row.value!, {});
      project(next, comparison.value);
      return true;
    } catch (error) {
      if (error instanceof CustodyRefusal) report(error);
      else if (!stale.value) report(error);
      return false;
    } finally {
      editingBlocked.value = false;
    }
  }

  /** Explicitly adopts the comparison value the user reviewed. */
  async function adopt(field: SettingsField): Promise<void> {
    editingBlocked.value = true;
    try {
      await ready();
      if (!comparison.value || !row.value?.fields[field]) return;
      const value = adoptedDraft(field, row.value.fields[field], comparison.value);
      project(await adoptBases(row.value, { [field]: value }), comparison.value);
      feedback.value = '';
    } catch (error) { report(error); }
    finally { editingBlocked.value = false; }
  }

  return { enabled: account !== null, ready: computed(() => account === null || row.value !== null || unavailable.value),
    hold, review, busy, stale, feedback, comparison, historicalStatus, editingBlocked, recover, submit, discard, adopt, persist };
}

/** Shared localized action feedback for the Editor and My Trips. */
export function custodyMessage(reason: 'stale' | 'held' | 'review' | 'storage' | 'owner' | 'issuance'): string {
  switch (reason) {
    case 'stale': return localize('Editor.ReceiptDraftChanged', 'This draft changed in another tab. Reopen the latest draft.');
    case 'held': return localize('Editor.ReceiptHeld', 'A previous Trip save is unconfirmed. Recover the save before sending overlapping changes.');
    case 'review': return localize('Editor.ReceiptReview', 'Review the current Trip value and explicitly use it as the comparison base.');
    case 'storage': return localize('Editor.ReceiptStorage', 'Browser storage could not retain this edit. Keep this page open and try again.');
    case 'owner': return localize('Editor.ReceiptOwnerUnavailable', 'Current Trip values are unavailable. Your retained draft is available in this browser.');
    case 'issuance': return localize('Editor.ReceiptIssuance', 'The save could not be prepared. Your draft is retained and can be sent with a new Save.');
  }
}
