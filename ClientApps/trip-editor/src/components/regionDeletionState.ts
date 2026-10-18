import { reactive } from 'vue';
import type { EditorMutationResult, Guid } from '../types';

export type Disposition = 'keep' | 'delete';
export type RegionDeletionChoices = { places: Disposition; areas: Disposition };
type Sample = { count: number; ids: Guid[]; hasMore: boolean };
export type RegionDeletionPreview = RegionDeletionChoices & {
  contractVersion: number; tripId: Guid; sourceRegionId: Guid; sourceRegionName: string;
  destination: { mode: 'existing' | 'create' | 'not-required'; regionId: Guid | null; name: string | null };
  consequences: { movedPlaces: Sample; deletedPlaces: Sample; movedAreas: Sample; deletedAreas: Sample;
    deletedSegments: Sample; updatedSegments: Sample; detachedWaypoints: { count: number } };
  confirmation: { token: string; expiresAt: string };
};
export type RegionDeletionResult = RegionDeletionChoices & { tripId: Guid; sourceRegionId: Guid; destinationRegionId: Guid | null;
  movedPlaceIds: Guid[]; movedAreaIds: Guid[]; updatedSegmentIds: Guid[]; detachedWaypointAssociations: unknown[] };
export type HeldRegionDeletion = RegionDeletionChoices & { sourceId: Guid; attemptId: string };
export const unknownRegionDeletionMessage = 'Outcome unknown — refresh to inspect the current Trip.';

/// Contextual final activation always describes both explicit dispositions.
export const regionDeletionLabel = (choices: RegionDeletionChoices): string => choices.places === 'keep'
  ? choices.areas === 'keep' ? 'Delete Region · keep Places and Areas' : 'Delete Region and Areas · keep Places'
  : choices.areas === 'keep' ? 'Delete Region and Places · keep Areas' : 'Delete Region, Places and Areas';

/// Scope contains server/account/Trip only; persisted holds contain no content, credentials or approval token.
export const heldRegionDeletionKey = (server: string, account: string, trip: string): string =>
  `wayfarer:region-deletion:v1:${server}:${account}:${trip}`;

/// One dialog attempt owns previews, final activation and the durable unknown-outcome hold.
export const createRegionDeletionController = (options: {
  sourceId: Guid; tripId: Guid; endpoint: string; antiforgeryToken: string; storage: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;
  scopeKey: string; isCurrent: () => boolean;
  fetch: typeof fetch; onSuccess: (result: EditorMutationResult<RegionDeletionResult>) => void;
}) => {
  const state = reactive({ places: 'keep' as Disposition, areas: 'keep' as Disposition,
    preview: null as RegionDeletionPreview | null, loading: false, submitting: false,
    held: Boolean(options.storage.getItem(options.scopeKey)), error: '', generation: 0 });
  const choices = (): RegionDeletionChoices => ({ places: state.places, areas: state.areas });
  const matches = (preview: RegionDeletionPreview): boolean => preview.contractVersion === 1
    && preview.tripId === options.tripId && preview.sourceRegionId === options.sourceId
    && preview.places === state.places && preview.areas === state.areas;
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
      const response = await post('deletion-preview', choices());
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
    if (Date.parse(state.preview.confirmation.expiresAt) <= Date.now()) {
      state.preview = null;
      state.error = 'Preview expired. Refresh the preview and confirm again.';
      return false;
    }
    // Recheck other editor tabs immediately before dispatch. A hold is written before any network effect.
    if (options.storage.getItem(options.scopeKey)) { state.held = true; return false; }
    const attempt: HeldRegionDeletion = { ...choices(), sourceId: options.sourceId, attemptId: crypto.randomUUID() };
    const encoded = JSON.stringify(attempt);
    state.submitting = true;
    try {
      options.storage.setItem(options.scopeKey, encoded);
      if (typeof window !== 'undefined') window.dispatchEvent(new Event('region-deletion-hold'));
    } catch {
      state.submitting = false;
      state.error = 'Browser storage is unavailable. Enable storage before confirming this operation.';
      return false;
    }
    const ownsAttempt = (): boolean => options.isCurrent() && options.storage.getItem(options.scopeKey) === encoded;
    try {
      const response = await post('deletion', { ...choices(), confirmationToken: state.preview.confirmation.token });
      const body = await response.json();
      if (!ownsAttempt()) return false;
      if (response.ok && body.success === true && body.data?.tripId === options.tripId
          && body.data?.sourceRegionId === options.sourceId && body.data?.places === attempt.places && body.data?.areas === attempt.areas) {
        // Apply slices before releasing the hold or clearing the deleted target/draft.
        options.onSuccess(body);
        options.storage.removeItem(options.scopeKey);
        if (typeof window !== 'undefined') window.dispatchEvent(new Event('region-deletion-hold'));
        state.preview = null;
        return true;
      }
      if ([400, 401, 403, 404].includes(response.status) || (response.status === 409
          && ['region-deletion-confirmation-required', 'region-deletion-preview-stale', 'unassigned_region_conflict'].includes(body.code))) {
        options.storage.removeItem(options.scopeKey);
        if (typeof window !== 'undefined') window.dispatchEvent(new Event('region-deletion-hold'));
        state.preview = null;
        state.error = body.title ?? 'Request rejected. Refresh the preview and confirm again.';
        return false;
      }
      state.held = true;
    } catch {
      if (ownsAttempt()) state.held = true;
    } finally {
      state.submitting = false;
    }
    return false;
  };
  return { state, refresh, select, submit };
};
export type RegionDeletionController = ReturnType<typeof createRegionDeletionController>;
