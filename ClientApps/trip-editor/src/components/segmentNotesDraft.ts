import { computed, ref, watch, type Ref } from 'vue';
import { createSegment, EditorMutationHttpError, EditorSegmentConflictError, EditorValidationError, patchSegmentNotes, updateSegment } from '../api/tripEditorApi';
import type { EditorMutationResult, EditorSegment, EditorSegmentConflict, EditorSegmentDraft, EditorSegmentSaveRequest, EditorSegmentWaypointDraftRow } from '../types';
import { buildSegmentRequest, toSegmentDraft } from './regionPlaceDrafts';
import { confirm } from '../composables/useConfirmDialog';

/** Existing aggregate fields remain a single intent with their pinned route token, separate from Notes display/originals. */
const routeIntent = (draft: EditorSegmentDraft, request = buildSegmentRequest(draft)): string => {
  const { notesHtml: _notes, aggregateConcurrencyToken: _token, ...route } = request;
  return JSON.stringify({ ...route, transportProfileId: draft.transportProfileId });
};

/** Requires a complete published route view before adopting a Segment save or a Place lifecycle effect. */
export const completeSegment = (next: EditorSegment): boolean => {
  const nullableString = (value: unknown): boolean => value === null || typeof value === 'string';
  const nullableNumber = (value: unknown): boolean => value === null || typeof value === 'number' && Number.isFinite(value);
  const geometry = (value: EditorSegment['route']): boolean => value === null
    || value?.type === 'LineString' && Array.isArray(value.coordinates) && value.coordinates.length >= 2
      && value.coordinates.every(c => Array.isArray(c) && c.length === 2 && c.every(n => typeof n === 'number' && Number.isFinite(n)));
  return typeof next?.id === 'string' && typeof next.tripId === 'string' && typeof next.notesHtml === 'string'
    && typeof next.originalRouteToken === 'string' && next.originalRouteToken.length > 0
    && typeof next.aggregateConcurrencyToken === 'string' && next.aggregateConcurrencyToken.length > 0
    && Array.isArray(next.waypointPlaceIds) && Array.isArray(next.waypointRouteVertexIndices)
    && nullableString(next.fromPlaceId) && nullableString(next.toPlaceId) && nullableString(next.transportProfileId)
    && typeof next.mode === 'string' && nullableNumber(next.estimatedDistanceKm) && nullableNumber(next.estimatedDurationMinutes)
    && ['Manual', 'Automatic'].includes(next.estimatedDurationSource) && geometry(next.route) && geometry(next.effectiveRoute)
    && next.waypointPlaceIds.every(id => typeof id === 'string')
    && next.waypointRouteVertexIndices.length === next.waypointPlaceIds.length
    && next.waypointRouteVertexIndices.every(i => i === null || Number.isInteger(i))
    && typeof next.capabilities?.canEdit === 'boolean' && typeof next.displayOrder === 'number';
};

/** Compares the two published JSON views without making object property order part of the wire contract. */
const samePublishedValue = (left: unknown, right: unknown): boolean => {
  if (left === right) return true;
  if (!left || !right || typeof left !== 'object' || typeof right !== 'object' || Array.isArray(left) !== Array.isArray(right)) return false;
  const keys = Object.keys(left);
  return keys.length === Object.keys(right).length && keys.every(key => Object.hasOwn(right, key)
    && samePublishedValue((left as Record<string, unknown>)[key], (right as Record<string, unknown>)[key]));
};

/** Segment create/update publishes one matching Segment, an optional unique Segment order, and no unrelated mutations. */
const validateComplete = (result: EditorMutationResult<EditorSegment>, tripId: string, segmentId: string | null): void => {
  const next = result?.data;
  const affected = result?.affected;
  const unrelated = ['regions', 'places', 'areas', 'tags'] as const;
  const emptyOrders = [affected?.placeOrdersByRegionId, affected?.areaOrdersByRegionId].every(value => value !== null
    && typeof value === 'object' && !Array.isArray(value) && Object.keys(value).length === 0);
  if (result?.success !== true || !completeSegment(next) || !next.id || next.tripId !== tripId || segmentId !== null && next.id !== segmentId
    || typeof next.hasCustomRoute !== 'boolean' || typeof next.capabilities.canDelete !== 'boolean'
    || !next.originalValues || !Object.hasOwn(next.originalValues, 'notesHtml')
    || next.originalValues.notesHtml !== null && typeof next.originalValues.notesHtml !== 'string'
    || (next.originalValues.notesHtml ?? '') !== next.notesHtml
    || !Array.isArray(affected?.segments) || affected.segments.length !== 1 || !samePublishedValue(affected.segments[0], next)
    || unrelated.some(key => !Array.isArray(affected[key]) || affected[key].length !== 0)
    || !emptyOrders || [affected.metadata, affected.regionOrder, affected.tagOrder, affected.visitProgress, affected.options].some(value => value != null)
    || affected.segmentOrder != null && (!Array.isArray(affected.segmentOrder) || !affected.segmentOrder.every(id => typeof id === 'string')
      || new Set(affected.segmentOrder).size !== affected.segmentOrder.length || !affected.segmentOrder.includes(next.id))
    || segmentId === null && !affected.segmentOrder?.includes(next.id)
    || [...unrelated, 'segments' as const].some(key => !Array.isArray(result.deletedIds?.[key]) || result.deletedIds[key].length !== 0)
    || !Array.isArray(result.warnings) || result.warnings.some(warning => typeof warning?.code !== 'string' || typeof warning.message !== 'string'
      || warning.entityType !== null && typeof warning.entityType !== 'string' || warning.entityId !== null && typeof warning.entityId !== 'string'))
    throw new Error('The Segment save acknowledgement is incomplete. Keep your draft and review the unknown outcome.');
};

/** Only explicit admission/validation/conflict failures establish a rejected attempt; proxy timeouts can hide a commit. */
const knownRejected = (error: unknown): boolean => error instanceof EditorValidationError || error instanceof EditorSegmentConflictError
  || error instanceof EditorMutationHttpError && [401, 403, 404, 405, 409, 429].includes(error.status);

/** Binds Notes-only acknowledgements and feedback to the originating editor session. */
export const createSegmentNotesDraft = (options: {
  draft: EditorSegmentDraft;
  baseline: Ref<EditorSegmentDraft>;
  isSaving: Ref<boolean>;
  current: () => EditorSegment | null;
  tripId: () => string;
  routeAuthored: () => boolean;
  endpoint: () => string;
  antiforgery: () => string;
  resetFeedback: () => void;
  applyError: (error: unknown, fallback: string, rows?: EditorSegmentWaypointDraftRow[]) => void;
  applied: (result: EditorMutationResult<EditorSegment>) => void;
  saved: () => void;
  buildRequest: () => EditorSegmentSaveRequest;
  accepted: (result: EditorMutationResult<EditorSegment>, wasCreate: boolean) => void;
  showConflict: (conflict: EditorSegmentConflict) => void;
  formId: string;
}) => {
  const { draft, baseline, isSaving } = options;
  const pending = ref(false);
  const uncertain = ref(false);
  const routeHeld = ref(false);
  const session = ref(0);
  let original: string | null | undefined;
  let routeChangedDuringSave = false;
  let submittedNotes: string | null | undefined;

  /** Takes only server-origin evidence, never inferring null from the displayed blank editor. */
  const pin = (segment: EditorSegment | null): void => {
    original = segment?.originalValues?.notesHtml;
  };

  /** Explicit open/reset/discard retires all late feedback and the old saving gate. */
  const reset = (segment: EditorSegment | null): void => {
    session.value++;
    pending.value = uncertain.value = routeHeld.value = false;
    isSaving.value = false;
    pin(segment);
  };

  watch([() => routeIntent(draft, options.buildRequest()), options.routeAuthored], () => {
    if (pending.value) routeChangedDuringSave = true;
  }, { flush: 'sync' });
  watch(options.current, next => {
    if (!next || next.id !== draft.id || pending.value || uncertain.value || routeHeld.value) return;
    if (buildSegmentRequest(draft).notesHtml !== buildSegmentRequest(baseline.value).notesHtml) return;
    draft.notesHtml = baseline.value.notesHtml = toSegmentDraft(next).notesHtml;
    pin(next);
    // Parent refresh alone never silently rebases the aggregate token or route dependencies.
  });

  /** A hold remains dirty even when a newer reversal equals the old display baseline. */
  const held = computed(() => pending.value || uncertain.value || routeHeld.value);
  const onlyNotes = (): boolean => Boolean(draft.id) && !options.routeAuthored()
    && routeIntent(draft) === routeIntent(baseline.value)
    && buildSegmentRequest(draft).notesHtml !== buildSegmentRequest(baseline.value).notesHtml;

  /** Blocks replay after uncertainty and implicit token advancement over a later route/proposal intention. */
  const blocked = (): boolean => {
    const message = uncertain.value
      ? 'The earlier Segment save has an unknown outcome. Keep your draft and explicitly review current saved state before saving again.'
      : routeHeld.value ? 'Route edits began during the earlier Segment save. Keep your route draft and explicitly review/reset current saved state before saving.' : null;
    if (message) options.applyError(new Error(message), message);
    return message !== null;
  };

  /** Rejects incomplete success before any local base or parent slice can change. */
  const validate = (result: EditorMutationResult<EditorSegment>): EditorSegment => {
    const next = result?.data;
    if (result?.success !== true || next?.id !== draft.id || next.tripId !== options.current()?.tripId
      || typeof next.notesHtml !== 'string' || !next.originalValues || !Object.hasOwn(next.originalValues, 'notesHtml')
      || next.originalValues.notesHtml !== null && typeof next.originalValues.notesHtml !== 'string'
      || (next.originalValues.notesHtml ?? '') !== next.notesHtml
      || !completeSegment(next)
      || result.affected?.segments?.length !== 1 || JSON.stringify(result.affected.segments[0]) !== JSON.stringify(next))
      throw new Error('The Segment Notes save acknowledgement is incomplete. Keep your draft and review the unknown outcome.');
    return next;
  };

  /** Performs exactly one Notes request, retaining later Notes and the original dependency proof of later route work. */
  const saveNotes = async (): Promise<void> => {
    const attempt = session.value;
    const currentAttempt = (): boolean => attempt === session.value;
    let attempted = false;
    options.resetFeedback();
    try {
      if (original === undefined) throw new Error('Original Segment Notes are unavailable. Keep your draft and reload before saving.');
      const desired = buildSegmentRequest(draft).notesHtml ?? '';
      pending.value = isSaving.value = true;
      routeChangedDuringSave = false;
      attempted = true;
      const result = await patchSegmentNotes(options.endpoint(), draft.id!, options.antiforgery(), {
        expected: { notesHtml: original }, changes: { notesHtml: desired }
      });
      if (!currentAttempt()) return;
      const next = validate(result);
      const newer = buildSegmentRequest(draft).notesHtml !== desired ? draft.notesHtml : null;
      const incoming = toSegmentDraft(next);
      // End observation before authoritative adoption; our own updates are not authored route changes.
      pending.value = false;
      if (routeChangedDuringSave) {
        baseline.value.notesHtml = incoming.notesHtml;
        draft.notesHtml = newer ?? incoming.notesHtml;
        routeHeld.value = true;
      } else {
        baseline.value = JSON.parse(JSON.stringify(incoming)) as EditorSegmentDraft;
        Object.assign(draft, incoming, { notesHtml: newer ?? incoming.notesHtml });
      }
      pin(next);
      options.applied(result);
      options.saved();
    } catch (error) {
      if (!currentAttempt()) return;
      uncertain.value = attempted && !knownRejected(error);
      options.applyError(error, 'Segment Notes save failed.');
    } finally {
      if (currentAttempt()) pending.value = isSaving.value = false;
    }
  };

  /** Adopts only a validated complete acknowledgement, retaining any newer Notes obligation. */
  const acceptComplete = (next: EditorSegment): EditorSegmentDraft => {
    const incoming = toSegmentDraft(next);
    const newer = buildSegmentRequest(draft).notesHtml !== submittedNotes ? draft.notesHtml : null;
    pending.value = false;
    if (routeChangedDuringSave) {
      // Creation installs only its persisted identity; later route work still requires explicit review/reset.
      if (!draft.id) draft.id = baseline.value.id = next.id;
      // Later route/proposal work retains its original evidence until explicit review/reset.
      baseline.value.notesHtml = incoming.notesHtml;
      draft.notesHtml = newer ?? incoming.notesHtml;
      routeHeld.value = true;
    } else {
      baseline.value = JSON.parse(JSON.stringify(incoming)) as EditorSegmentDraft;
      Object.assign(draft, incoming, { notesHtml: newer ?? incoming.notesHtml });
    }
    pin(next);
    return incoming;
  };

  /** Keeps complete route/proposal saves atomic, with explicit clear confirmation and the same retired-session fence. */
  const save = async (): Promise<void> => {
    if (isSaving.value || blocked()) return;
    if (onlyNotes()) { await saveNotes(); return; }
    const attempt = session.value;
    const currentAttempt = (): boolean => attempt === session.value;
    const request = options.buildRequest();
    submittedNotes = buildSegmentRequest(draft).notesHtml;
    const segmentId = draft.id;
    if (segmentId) {
      if (!draft.originalRouteToken || original === undefined) {
        options.applyError(new Error('Original Segment evidence is unavailable. Keep your draft and reload before saving.'), 'Reload the editor.');
        return;
      }
      request.contract = 'segment-route-notes-1';
      request.originalRouteToken = draft.originalRouteToken;
      if (submittedNotes !== buildSegmentRequest(baseline.value).notesHtml) {
        request.expected = { notesHtml: original };
        request.changes = { notesHtml: submittedNotes ?? '' };
      }
      delete request.notesHtml;
      delete request.aggregateConcurrencyToken;
    }
    routeChangedDuringSave = false;
    const tripId = options.tripId();
    // Every complete response crosses this boundary before draft, proposal, preview, parent or Saved adoption.
    const accept = (result: EditorMutationResult<EditorSegment>): void => {
      validateComplete(result, tripId, segmentId);
      options.accepted(result, segmentId === null);
    };
    const rows = draft.waypointRows.map(row => ({ ...row }));
    pending.value = isSaving.value = true;
    options.resetFeedback();
    try {
      const result = segmentId
        ? await updateSegment(options.endpoint(), segmentId, options.antiforgery(), request)
        : await createSegment(options.endpoint(), options.antiforgery(), request);
      if (currentAttempt()) accept(result);
    } catch (error) {
      if (!currentAttempt()) return;
      if (!(error instanceof EditorSegmentConflictError) || !segmentId) {
        uncertain.value = !knownRejected(error);
        options.applyError(error, 'Segment save failed.', rows);
        return;
      }
      if (!error.confirmationToken || error.conflict.code !== 'segment-route-clear-confirmation-required') {
        options.showConflict(error.conflict);
        options.applyError(error, 'Segment save failed.', rows);
        return;
      }
      const accepted = await confirm({ title: 'Clear custom route?', message: error.conflict.warning,
        confirmLabel: 'Clear route and save', cancelLabel: 'Keep editing', variant: 'warning' });
      if (!currentAttempt()) return;
      if (!accepted) {
        document.querySelector<HTMLElement>(`button[form="${options.formId}"]`)?.focus();
        return;
      }
      try {
        // Confirmation binds the submitted aggregate, never a rebuilt draft containing later edits.
        const result = await updateSegment(options.endpoint(), segmentId, options.antiforgery(), request, error.confirmationToken);
        if (currentAttempt()) accept(result);
      } catch (retryError) {
        if (!currentAttempt()) return;
        uncertain.value = !knownRejected(retryError);
        if (retryError instanceof EditorSegmentConflictError) options.showConflict(retryError.conflict);
        options.applyError(retryError, 'Segment save failed.', rows);
      }
    } finally {
      if (currentAttempt()) pending.value = isSaving.value = false;
    }
  };

  return { held, save, reset, acceptComplete, retainRoute: () => routeHeld.value };
};
