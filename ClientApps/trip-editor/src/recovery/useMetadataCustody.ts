import { computed, inject, nextTick, ref, shallowRef, watch } from 'vue';
import { message as localize } from '../../../../wwwroot/js/localization/messages.js';
import { EditorValidationError } from '../api/tripEditorApi';
import { adoptedDraft, type createMetadataDraftState } from '../components/metadataDraft';
import type { EditorTag, EditorTripMetadata } from '../types';
import { adoptBases, authorize, CustodyRefusal, heldFields, knownHistory, readCustody, remember, saveDraft, type Custody, type DraftFields, type SettingsField } from './custody';
import { freezeRequest, issuePermit, sendAttempt } from './receiptApi';
import type { WebOperation } from './receiptV2';
import { metadataAccount, metadataWorkspaceError, receiptStatus, recoverMetadata, type Recovery } from './recoverMetadata';

type CapturedSave = { generation: number; branches: { kind: WebOperation; fields: SettingsField[]; body: string }[] };

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
  let lastCapture: DraftFields | null = null;

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

  /** Captures edited fields, including a return to the old original while an attempt is held. */
  const capture = (): DraftFields => state.capture(hold.value.filter(field => row.value?.fields[field]));

  /** Captures at execution time so an older queued write cannot resurrect fields settled by recovery. */
  async function persistDraft(): Promise<void> {
    if (!row.value || stale.value) return;
    const fields = capture();
    if (JSON.stringify(fields) === JSON.stringify(lastCapture)) return;
    // A buffered projection keeps its comparison until the draft and review state commit together.
    const current = comparison.value ?? props.metadata;
    try {
      row.value = await saveDraft(row.value, fields, current);
      lastCapture = fields;
      unavailable.value = false;
    } catch (error) { report(error); }
  }

  const persist = (): Promise<void> => account === null || !row.value ? Promise.resolve() : enqueue(persistDraft);

  watch(state.draft, () => { void persist(); }, { flush: 'post' });

  /** Projects the durable generation with any input authored during this Editor's own settlement. */
  function project(view: Custody, current: EditorTripMetadata | null, fields?: DraftFields): void {
    row.value = view;
    comparison.value = current;
    const tags = current?.tags;
    state.restore(fields ?? view.fields, current ?? props.metadata,
      tags?.map(tag => tag.slug) ?? props.tagOrder,
      tags ? Object.fromEntries(tags.map(tag => [tag.slug, tag])) : props.tagsBySlug);
    lastCapture = fields ? null : capture();
  }

  /** Overlays only edits made since the last durable capture, preserving their original comparison bases. */
  function bufferedFields(view: Custody): DraftFields | undefined {
    const live = capture();
    const prior = lastCapture ?? {};
    const changed = Object.keys({ ...prior, ...live }).filter(key =>
      JSON.stringify(live[key as SettingsField]) !== JSON.stringify(prior[key as SettingsField])) as SettingsField[];
    if (!changed.length) return;
    const fields: DraftFields = { ...view.fields };
    for (const field of changed) {
      const value = live[field];
      if (value) fields[field] = value;
      else delete fields[field];
    }
    return fields;
  }

  /** Completes settlement and projection in the local write queue, fencing edits against other tabs. */
  async function completeRecovery(recovered: Recovery, reopening: boolean, beforeSettlement?: number): Promise<Recovery> {
    // Confirmed access loss hides the workspace even when local draft projection is blocked.
    if (recovered.inaccessible && workspaceError) {
      workspaceError.value = custodyMessage('owner');
      return { ...recovered, current: null };
    }

    const latest = await readCustody(recovered.row.account, recovered.row.tripId);
    const matchesRecovery = latest.generation === recovered.row.generation;
    const current = matchesRecovery ? recovered.current : null;
    const fields = row.value && !reopening ? bufferedFields(latest) : undefined;
    const canKeepFields = row.value?.generation === latest.generation ||
      matchesRecovery && row.value?.generation === beforeSettlement;
    if (fields && !canKeepFields) report(new CustodyRefusal('stale'));
    // The explicit reopen may replace the failed local draft once the owner read matches this generation.
    const canReopenAfterStorageFailure = reopening && current !== null;
    if ((!reopening && stale.value) || (unavailable.value && row.value && !canReopenAfterStorageFailure))
      return { ...recovered, current: null };

    stale.value = unavailable.value = false;
    project(latest, current, fields);
    if (fields) await persistDraft();

    const history = knownHistory(latest);
    historicalStatus.value = [...recovered.settled ?? [], ...history].map(receiptStatus);
    if (stale.value || unavailable.value) return { ...recovered, current: null };
    if (!current && (history.length || Object.keys(latest.fields).length && !hold.value.length))
      feedback.value = custodyMessage('owner');
    else feedback.value = hold.value.length ? custodyMessage('held') : '';
    return { ...recovered, row: latest, current };
  }

  /** Only the reopen action may replace stale local input. Recovery has no mutation or continuation authority. */
  async function recover(reopening = false): Promise<EditorTripMetadata | null> {
    if (account === null || busy.value) return null;
    if (!account) {
      report(new Error());
      return null;
    }
    busy.value = true;
    try {
      await nextTick();
      await persist();
      const recovered = await recoverMetadata(account, props.metadata.id, props.editorEndpoint, (settle, before) =>
        enqueue(async () => completeRecovery(await settle(), reopening, before)));
      return recovered.current;
    } catch (error) {
      report(error);
      return null;
    } finally {
      busy.value = false;
    }
  }

  /** Freezes exactly the branches selected by the current Save gesture. */
  async function captureSave(): Promise<CapturedSave> {
    const view = await ready();
    const metadata = state.request();
    const branches: CapturedSave['branches'] = [];
    const fields = Object.keys(metadata.changes) as SettingsField[];
    if (fields.length) branches.push({ kind: 'metadata', fields, body: freezeRequest(metadata) });
    const tags = view.fields.tags?.desired.tags;
    if (tags) branches.push({ kind: 'tags-replace', fields: ['tags'], body: freezeRequest({ tags }) });
    const enabled = view.fields.shareProgressEnabled?.desired.shareProgressEnabled;
    if (typeof enabled === 'boolean')
      branches.push({ kind: 'editor-sharing', fields: ['shareProgressEnabled'], body: freezeRequest({ enabled }) });
    return { generation: view.generation, branches };
  }

  /** Authorizes the captured branch only while every selected field still has its submitted generation. */
  async function submit(branch: CapturedSave['branches'][number], generation: number) {
    if (!account) throw new Error(custodyMessage('storage'));
    const view = await ready();
    if (branch.fields.some(field => !view.fields[field] || view.fields[field].generation > generation))
      return { metadata: null, continueSave: false };
    try {
      await authorize(view, branch.fields);
      const permit = await issuePermit(props.editorEndpoint, props.antiforgeryToken, account, view.tripId, branch.body, branch.kind)
        .catch(() => { throw new Error(custodyMessage('issuance')); });
      await ready();
      const attempt = await authorize(view, branch.fields, { permit, body: branch.body });
      if (!attempt) throw new Error(custodyMessage('storage'));
      row.value = { ...view, attempts: [...view.attempts, attempt] };
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
        return { metadata, continueSave: false };
      }
      const newer = Object.keys(attempt.submitted).some(field => row.value?.fields[field as SettingsField]);
      return { metadata, continueSave: !!metadata && !row.value?.attempts.some(candidate => candidate.permit.operationId === attempt.permit.operationId) && !stale.value && !newer };
    } catch (error) {
      feedback.value = custodyMessage('held');
      if (error instanceof EditorValidationError) throw error;
      return { metadata: null, continueSave: false };
    }
  }

  /** Storage failure prevents dispatch, and a stale interaction must reopen the current draft. */
  async function ready(): Promise<Custody> {
    await persist();
    if (stale.value) throw new Error(custodyMessage('stale'));
    if (!row.value || unavailable.value || busy.value) throw new Error(custodyMessage('storage'));
    return row.value;
  }

  /** Discards only this displayed generation, keeping immutable attempt evidence. */
  async function discard(): Promise<boolean> {
    if (account === null) {
      state.restore({}, props.metadata, props.tagOrder, props.tagsBySlug);
      return true;
    }
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

  return { ready: computed(() => row.value !== null && !unavailable.value), editable: computed(() => row.value !== null),
    hold, review, busy, stale, feedback, comparison, historicalStatus, editingBlocked, recover, captureSave, submit, discard, adopt, persist };
}

/** Shared localized action feedback for the Editor and My Trips. */
export function custodyMessage(reason: 'stale' | 'held' | 'review' | 'storage' | 'owner' | 'issuance'): string {
  switch (reason) {
    case 'stale': return localize('Editor.ReceiptDraftChanged', 'This draft changed in another tab. Reopen the latest draft.');
    case 'held': return localize('Editor.ReceiptHeld', 'A previous Trip save is unconfirmed. Recover the save before sending overlapping changes.');
    case 'review': return localize('Editor.ReceiptReview', 'Review the current Trip value and explicitly use it as the comparison base.');
    case 'storage': return localize('Editor.ReceiptStorage', 'Trip draft storage is unavailable. Close older Trip tabs, then reload or recover the draft.');
    case 'owner': return localize('Editor.ReceiptOwnerUnavailable', 'Current Trip values are unavailable. Your retained draft is available in this browser.');
    case 'issuance': return localize('Editor.ReceiptIssuance', 'The save could not be prepared. Your draft is retained and can be sent with a new Save.');
  }
}
