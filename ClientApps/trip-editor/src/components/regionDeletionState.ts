import { reactive } from 'vue';
import { message } from '../../../../wwwroot/js/localization/messages.js';
import { captureMutationPublication } from '../api/tripEditorApi';
import { CustodyRefusal } from '../recovery/custody';
import { submitRegionDeletion, StructureRefused } from '../recovery/structureMutation';
import { structureContract } from '../recovery/structureRoutes';
import type { EditorMutationResult, Guid } from '../types';

export type Disposition = 'keep' | 'delete';
export type RegionDeletionChoices = { places: Disposition; areas: Disposition };
type Sample = { count: number; ids: Guid[]; hasMore: boolean };
export type RegionDeletionPreview = RegionDeletionChoices & {
  contractVersion: number; contract: string; tripId: Guid; sourceRegionId: Guid; sourceRegionName: string;
  destination: { mode: 'existing' | 'create' | 'not-required'; regionId: Guid | null; name: string | null };
  consequences: { movedPlaces: Sample; deletedPlaces: Sample; movedAreas: Sample; deletedAreas: Sample;
    deletedSegments: Sample; updatedSegments: Sample; unlinkedVisits: number; deletedCandidates: number; detachedWaypoints: { count: number; ids: { segmentId: string; placeId: string }[]; hasMore: boolean } };
  confirmation: { token: string; expiresAt: string };
};
export type RegionDeletionResult = RegionDeletionChoices & { tripId: Guid; sourceRegionId: Guid; destinationRegionId: Guid | null;
  movedPlaceIds: Guid[]; movedAreaIds: Guid[]; updatedSegmentIds: Guid[]; detachedWaypointAssociations: unknown[] };
export type HeldRegionDeletion = RegionDeletionChoices & { sourceId: Guid; attemptId: string };
export const unknownRegionDeletionMessage = 'Outcome unknown — refresh to inspect the current Trip.';

/// Contextual final activation always describes both explicit dispositions.
export const regionDeletionLabel = (choices: RegionDeletionChoices): string => choices.places === 'keep'
  ? choices.areas === 'keep'
    ? message('Editor.DeleteRegionKeepBoth', 'Delete Region · keep Places and Areas')
    : message('Editor.DeleteRegionKeepPlaces', 'Delete Region and Areas · keep Places')
  : choices.areas === 'keep'
    ? message('Editor.DeleteRegionKeepAreas', 'Delete Region and Places · keep Areas')
    : message('Editor.DeleteRegionDeleteBoth', 'Delete Region, Places and Areas');

/// Scope contains server/account/Trip only; persisted holds contain no content, credentials or approval token.
export const heldRegionDeletionKey = (server: string, account: string, trip: string): string =>
  `wayfarer:region-deletion:v1:${server}:${account}:${trip}`;

/// One dialog owns choice preview and explicit activation of the typed deletion transport.
export const createRegionDeletionController = (options: {
  sourceId: Guid; tripId: Guid; endpoint: string; antiforgeryToken: string; storage: Pick<Storage, 'getItem'>;
  scopeKey: string; isCurrent: () => boolean;
  fetch: typeof fetch; onSuccess: (result: EditorMutationResult<RegionDeletionResult>) => void;
}) => {
  const heldRecord = options.storage.getItem(options.scopeKey);
  let heldChoices: RegionDeletionChoices | null = null;
  try {
    const retained = JSON.parse(heldRecord ?? 'null') as HeldRegionDeletion | null;
    if (retained?.sourceId === options.sourceId && ['keep', 'delete'].includes(retained.places)
        && ['keep', 'delete'].includes(retained.areas)) {
      heldChoices = { places: retained.places, areas: retained.areas };
    }
  } catch {
    // Invalid retained records still hold the operation; they never authorize a new attempt.
  }
  const state = reactive({ places: heldChoices?.places ?? 'keep' as Disposition, areas: heldChoices?.areas ?? 'keep' as Disposition,
    preview: null as RegionDeletionPreview | null, loading: false, submitting: false,
    held: Boolean(heldRecord), error: '', generation: 0 });
  const choices = (): RegionDeletionChoices => ({ places: state.places, areas: state.areas });
  const matches = (preview: RegionDeletionPreview): boolean => preview.contractVersion === 2 && preview.contract === structureContract
    && Number.isSafeInteger(preview.consequences?.unlinkedVisits) && preview.consequences.unlinkedVisits >= 0
    && Number.isSafeInteger(preview.consequences?.deletedCandidates) && preview.consequences.deletedCandidates >= 0
    && preview.tripId === options.tripId && preview.sourceRegionId === options.sourceId
    && preview.places === state.places && preview.areas === state.areas
    && typeof preview.confirmation?.token === 'string' && Boolean(preview.confirmation.token)
    && Number.isFinite(Date.parse(preview.confirmation.expiresAt))
    && (['movedPlaces', 'deletedPlaces', 'movedAreas', 'deletedAreas', 'deletedSegments', 'updatedSegments', 'detachedWaypoints'] as const)
      .every(key => validSample(preview.consequences?.[key]));
  const post = (suffix: string, body: unknown): Promise<Response> => options.fetch(
    `${options.endpoint}/regions/${options.sourceId}/${suffix}`, { method: 'POST', credentials: 'same-origin',
      headers: { Accept: 'application/json', 'Content-Type': 'application/json', RequestVerificationToken: options.antiforgeryToken },
      body: JSON.stringify(body) });
  const refresh = async (): Promise<void> => {
    if (state.submitting || state.held) return;
    const generation = ++state.generation;
    state.preview = null;
    state.loading = true;
    state.error = '';
    try {
      const response = await post(`deletion-preview?contract=${structureContract}`, choices());
      const body = await response.json();
      if (!options.isCurrent() || generation !== state.generation) return;
      if (!response.ok || !matches(body)) throw new Error(body.title ?? 'Preview unavailable. Try again.');
      state.preview = body;
    } catch (error) {
      if (generation === state.generation) state.error = error instanceof Error ? error.message : 'Preview unavailable. Try again.';
    } finally {
      if (generation === state.generation) state.loading = false;
    }
  };
  const select = async (field: keyof RegionDeletionChoices, value: Disposition): Promise<void> => {
    if (state.submitting || state.held) return;
    state[field] = value;
    await refresh();
  };
  const submit = async (): Promise<boolean> => {
    if (!state.preview || state.loading || state.submitting || state.held || !options.isCurrent()) return false;
    if (!Number.isFinite(Date.parse(state.preview.confirmation.expiresAt)) || Date.parse(state.preview.confirmation.expiresAt) <= Date.now()) {
      state.preview = null;
      state.error = 'Preview expired. Refresh the preview and confirm again.';
      return false;
    }
    if (options.storage.getItem(options.scopeKey)) { state.held = true; return false; }
    state.submitting = true;
    try {
      const publish = captureMutationPublication(`${options.endpoint}/regions/${options.sourceId}/deletion/receipt`);
      const result = await submitRegionDeletion<RegionDeletionResult>(options.endpoint, options.tripId, options.sourceId, options.antiforgeryToken,
        { ...choices(), confirmationToken: state.preview.confirmation.token });
      if (!options.isCurrent()) return false;
      publish(result);
      options.onSuccess(result);
      state.preview = null;
      return true;
    } catch (error) {
      if (!options.isCurrent()) return false;
      state.held = error instanceof CustodyRefusal && error.reason === 'held' || !(error instanceof StructureRefused);
      state.error = error instanceof Error ? error.message : unknownRegionDeletionMessage;
      state.preview = null;
    } finally {
      state.submitting = false;
    }
    return false;
  };
  return { state, refresh, select, submit };
};
export type RegionDeletionController = ReturnType<typeof createRegionDeletionController>;

/** Counts and bounded samples must remain internally consistent before consent or response adoption. */
const validSample = (sample: { count: number; ids: unknown[]; hasMore: boolean }): boolean => Boolean(sample
  && Number.isInteger(sample.count) && sample.count >= 0 && Array.isArray(sample.ids)
  && sample.ids.length === Math.min(sample.count, 5) && sample.hasMore === (sample.count > 5));
