import type { InjectionKey, Ref } from 'vue';
import { message as localize } from '../../../../wwwroot/js/localization/messages.js';
import type { EditorTripMetadata } from '../types';
import { CustodyRefusal, readCustody, reconcile, remember, type Custody } from './custody';
import { lookupAttempt, OwnerScopeUnavailable, readOwnerMetadata, type TerminalState } from './receiptApi';

/** Identifies the authenticated Editor bootstrap independently of the Trip graph. */
export const metadataAccount: InjectionKey<string> = Symbol('trip-metadata-account');
/** Clears the mounted graph if a fresh recovery read confirms the owner scope is gone. */
export const metadataWorkspaceError: InjectionKey<Ref<string | null>> = Symbol('trip-metadata-workspace-error');
export type Recovery = { row: Custody; current: EditorTripMetadata | null; inaccessible?: boolean; settled?: TerminalState[] };

/** Resolves attempts through read-only routes, then reconciles against fresh owner comparison values. */
export async function recoverMetadata(account: string, tripId: string, endpoint: string): Promise<Recovery> {
  let row = await readCustody(account, tripId);
  for (const attempt of row.attempts) {
    if (row.outcomes[attempt.permit.operationId]) continue;
    const evidence = await lookupAttempt(endpoint, attempt).catch(() => null);
    if (evidence) row = await remember(row, attempt, evidence);
  }
  // A tab may publish a newer generation while the owner request is in flight.
  for (let read = 0; read < 3; read++) {
    row = await readCustody(account, tripId);
    if (!Object.keys(row.fields).length && !row.attempts.length) return { row, current: null };
    let current: EditorTripMetadata;
    try {
      current = await readOwnerMetadata(endpoint, account, tripId);
    } catch (error) {
      return { row: await readCustody(account, tripId), current: null, inaccessible: error instanceof OwnerScopeUnavailable };
    }
    try {
      return { ...await reconcile(row, current), current };
    } catch (error) {
      if (!(error instanceof CustodyRefusal) || error.reason !== 'stale') throw error;
    }
  }
  return { row: await readCustody(account, tripId), current: null };
}

/** Presents the historical result separately from the current comparison values. */
export function receiptStatus(state: TerminalState): string {
  switch (state) {
    case 'applied': return localize('Editor.ReceiptApplied', 'The earlier Trip save was confirmed.');
    case 'checkedNoOp': return localize('Editor.ReceiptNoOp', 'The earlier save confirmed those values were already saved.');
    case 'terminalUnapplied': return localize('Editor.ReceiptUnapplied', 'The earlier save was refused. Review the current values before saving again.');
    case 'retired': return localize('Editor.ReceiptRetired', 'The earlier save can no longer run. Its historical result is unavailable.');
  }
}
