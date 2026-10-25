import { reactive } from 'vue';
import { completePlace } from './placeStructuralEditing';
import { completeSegment } from './segmentNotesDraft';
import { completeVisitProgress } from './placeConditionalDraft';
import { completeDeletionMetadata } from './placeDeletion';
import type { EditorMutationResult, Guid } from '../types';

export type Disposition = 'keep' | 'delete';
export type RegionDeletionChoices = { places: Disposition; areas: Disposition };
type Sample = { count: number; ids: Guid[]; hasMore: boolean };
export type RegionDeletionPreview = RegionDeletionChoices & {
  contractVersion: number; tripId: Guid; sourceRegionId: Guid; sourceRegionName: string;
  destination: { mode: 'existing' | 'create' | 'not-required'; regionId: Guid | null; name: string | null };
  consequences: { movedPlaces: Sample; deletedPlaces: Sample; movedAreas: Sample; deletedAreas: Sample;
    deletedSegments: Sample; updatedSegments: Sample; detachedWaypoints: { count: number; ids: { segmentId: string; placeId: string }[]; hasMore: boolean } };
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
  const matches = (preview: RegionDeletionPreview): boolean => preview.contractVersion === 1
    && preview.tripId === options.tripId && preview.sourceRegionId === options.sourceId
    && preview.places === state.places && preview.areas === state.areas
    && typeof preview.confirmation?.token === 'string' && Boolean(preview.confirmation.token)
    && Number.isFinite(Date.parse(preview.confirmation.expiresAt))
    && ['movedPlaces', 'deletedPlaces', 'movedAreas', 'deletedAreas', 'deletedSegments', 'updatedSegments', 'detachedWaypoints']
      .every(key => validSample(preview.consequences?.[key as keyof typeof preview.consequences]));
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
    if (!Number.isFinite(Date.parse(state.preview.confirmation.expiresAt)) || Date.parse(state.preview.confirmation.expiresAt) <= Date.now()) {
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
        validateRegionDeletionAcknowledgement(body, state.preview);
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

/** Counts and bounded samples must remain internally consistent before consent or response adoption. */
const validSample = (sample: { count: number; ids: unknown[]; hasMore: boolean }): boolean => Boolean(sample
  && Number.isInteger(sample.count) && sample.count >= 0 && Array.isArray(sample.ids)
  && sample.ids.length === Math.min(sample.count, 5) && sample.hasMore === (sample.count > 5));

/** Region acknowledgements retain the delivered sampled preview, checking complete returned effects and affected scope. */
export const validateRegionDeletionAcknowledgement = (result: EditorMutationResult<RegionDeletionResult>, p: RegionDeletionPreview): void => {
  const a = result?.affected;
  const d = result?.data;
  const deleted = result?.deletedIds;
  const same = (x: unknown, y: unknown): boolean => JSON.stringify(x) === JSON.stringify(y);
  const ids = (x: unknown): x is string[] => Array.isArray(x) && x.every(id => typeof id === 'string') && new Set(x).size === x.length;
  const matchesSample = (values: string[], sample: Sample): boolean => ids(values) && validSample(sample)
    && values.length === sample.count && sample.ids.every(id => values.includes(id));
  const effects = p.consequences;
  const destination = p.destination.regionId;
  if (result?.success !== true || !d || d.tripId !== p.tripId || d.sourceRegionId !== p.sourceRegionId
    || d.places !== p.places || d.areas !== p.areas || d.destinationRegionId !== destination
    || !matchesSample(d.movedPlaceIds, effects.movedPlaces) || !matchesSample(d.movedAreaIds, effects.movedAreas)
    || !matchesSample(d.updatedSegmentIds, effects.updatedSegments)
    || !deleted || !same(deleted.regions, [p.sourceRegionId]) || !same(deleted.tags, [])
    || !matchesSample(deleted.places, effects.deletedPlaces) || !matchesSample(deleted.areas, effects.deletedAreas)
    || !matchesSample(deleted.segments, effects.deletedSegments)
    || d.movedPlaceIds.some(id => deleted.places.includes(id)) || d.movedAreaIds.some(id => deleted.areas.includes(id))
    || !Array.isArray(d.detachedWaypointAssociations) || d.detachedWaypointAssociations.length !== effects.detachedWaypoints.count
    || effects.detachedWaypoints.ids.some(w => !d.detachedWaypointAssociations.some(x => same(x, w)))
    || d.detachedWaypointAssociations.some(w => !w || typeof w !== 'object'
      || !d.updatedSegmentIds.includes((w as { segmentId: string }).segmentId) || !deleted.places.includes((w as { placeId: string }).placeId))
    || !a || !Array.isArray(a.regions) || !ids(a.regions.map(r => r.id)) || !ids(a.regionOrder)
    || !same(a.regions.map(r => r.id), a.regionOrder) || a.regionOrder.includes(p.sourceRegionId)
    || a.regions.some(r => r.tripId !== p.tripId || typeof r.name !== 'string' || typeof r.notesHtml !== 'string' || !Number.isInteger(r.displayOrder) || !r.capabilities || !r.originalValues)
    || destination !== null && !a.regions.some(r => r.id === destination && r.name === 'Unassigned Places')
    || !Array.isArray(a.places) || !ids(a.places.map(x => x.id)) || a.places.some(x => !completePlace(x) || x.tripId !== p.tripId || x.regionId !== destination)
    || !Array.isArray(a.areas) || !ids(a.areas.map(x => x.id)) || a.areas.some(x => x.tripId !== p.tripId || x.regionId !== destination
      || typeof x.name !== 'string' || typeof x.notesHtml !== 'string' || x.geometry?.type !== 'Polygon' || !Array.isArray(x.geometry.coordinates) || typeof x.fillHex !== 'string'
      || !x.originalValues || !['regionId', 'name', 'notesHtml', 'fillHex', 'geometry'].every(key => Object.hasOwn(x.originalValues!, key)) || !x.capabilities)
    || !a.placeOrdersByRegionId || !a.areaOrdersByRegionId
    || ![...Object.keys(a.placeOrdersByRegionId), ...Object.keys(a.areaOrdersByRegionId)].every(id => id === destination)
    || !d.movedPlaceIds.every(id => a.places.some(x => x.id === id)) || !d.movedAreaIds.every(id => a.areas.some(x => x.id === id))
    || !same(d.movedPlaceIds.length ? { [destination!]: a.places.map(x => x.id) } : {}, a.placeOrdersByRegionId)
    || !same(d.movedAreaIds.length ? { [destination!]: a.areas.map(x => x.id) } : {}, a.areaOrdersByRegionId)
    || !Array.isArray(a.segments) || !ids(a.segments.map(x => x.id)) || a.segments.some(x => !completeSegment(x) || x.tripId !== p.tripId || deleted.segments.includes(x.id))
    || !d.updatedSegmentIds.every(id => a.segments.some(x => x.id === id))
    || (deleted.segments.length === 0 ? a.segmentOrder !== null || !same(a.segments.map(x => x.id).sort(), [...d.updatedSegmentIds].sort())
      : !ids(a.segmentOrder) || !same([...a.segmentOrder].sort(), a.segments.map(x => x.id).sort()))
    || !completeDeletionMetadata(a.metadata, p.tripId) || !completeVisitProgress(a.visitProgress!)
    || !same(a.tags, []) || [a.tagOrder, a.options].some(x => x !== null) || !same(result.warnings, []))
    throw new Error('Region deletion acknowledgement is incomplete. Outcome unknown; keep the operation held without replay.');
};
