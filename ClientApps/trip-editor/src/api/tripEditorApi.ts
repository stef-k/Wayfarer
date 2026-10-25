import type { RegionCreationRequest, PlaceCreationRequest } from './creationTypes';
import type { PlaceStructureRequest, PlaceStructurePreview } from '../components/placeStructuralEditing';
import type {
  EditorMutationResult,
  EditorLifecycleConflict,
  EditorGeocodeSearchResponse,
  EditorArea,
  EditorAreaDeleteResult,
  EditorAreaOrderRequest,
  EditorAreaOrderResult,
  EditorAreaSaveRequest,
  EditorAreaConditionalRequest,
  EditorPlace,
  EditorPlaceDeleteResult,
  EditorPlaceOrderRequest,
  EditorPlaceOrderResult,
  EditorPlaceSaveRequest,
  EditorPlaceConditionalRequest,
  EditorRegion,
  EditorRegionOrderRequest,
  EditorRegionOrderResult,
  EditorRegionSaveRequest,
  EditorRegionConditionalRequest,
  EditorSegment,
  EditorSegmentConflict,
  EditorSegmentDeleteResult,
  EditorSegmentOrderRequest,
  EditorSegmentOrderResult,
  EditorSegmentSaveRequest,
  EditorSegmentNotesConditionalRequest,
  EditorShareProgressUpdateRequest,
  EditorTag,
  EditorTripTagsUpdateRequest,
  EditorTripMetadata,
  EditorMetadataConditionalRequest,
  EditorTripState,
  ExternalRouteProposal,
  TagSuggestion,
  ValidationProblemDetails
} from '../types';

export class ExternalRouteProposalError extends Error {
  constructor(readonly code: string) { super(code); }
}

/** Generates a proposal from pinned route evidence in the strict C4 dialect. */
export const generateExternalRouteProposal = async (
  tripId: string, segmentId: string, antiforgeryToken: string, originalRouteToken: string | null,
  providerMode: string, signal: AbortSignal
): Promise<ExternalRouteProposal> => {
  const response = await fetch(`/api/trip-editor/${encodeURIComponent(tripId)}/segments/${encodeURIComponent(segmentId)}/route-proposals`, {
    method: 'POST', credentials: 'same-origin', signal,
    headers: { Accept: 'application/json', 'Content-Type': 'application/json', RequestVerificationToken: antiforgeryToken },
    body: JSON.stringify({ contract: 'segment-route-notes-1', originalRouteToken, providerMode })
  });
  requireSession(response);
  if (!response.ok) {
    const error = await response.json().catch(() => ({ code: 'external-routing-unavailable' })) as { code?: string };
    throw new ExternalRouteProposalError(error.code ?? 'external-routing-unavailable');
  }
  return await response.json() as ExternalRouteProposal;
};

/// Error raised when an editor mutation returns ASP.NET validation problem details.
export class EditorValidationError extends Error {
  readonly errors: Record<string, string[]>;

  constructor(problem: ValidationProblemDetails) {
    super(problem.title ?? 'Validation failed.');
    this.errors = problem.errors ?? {};
  }
}

/** Retains HTTP status so a caller can distinguish a rejected write from an uncertain server failure. */
export class EditorMutationHttpError extends Error {
  constructor(message: string, readonly status: number) { super(message); }
}

/** Authentication rejection leaves the current editor/draft mounted and explains how to regain access. */
const requireSession = (response: Response): void => {
  if (response.status === 401) throw new EditorMutationHttpError(
    'Your session has ended or your account is disabled. Sign in again. Keep any unsaved changes before leaving this page.', 401);
};

/// Error raised when canonical lifecycle dependencies require confirmation or changed before retry.
export class EditorLifecycleConfirmationError extends Error {
  readonly conflict: EditorLifecycleConflict;

  constructor(conflict: EditorLifecycleConflict) {
    super(conflict.code);
    this.conflict = conflict;
  }
}

/// Error raised for Segment-specific stale or route-clear conflicts.
export class EditorSegmentConflictError extends Error {
  constructor(readonly conflict: EditorSegmentConflict, readonly confirmationToken: string | null) {
    super(conflict.code);
  }
}

export const loadEditorState = async (endpoint: string): Promise<EditorTripState> => {
  const response = await fetch(endpoint, {
    headers: { Accept: 'application/json' },
    credentials: 'same-origin'
  });
  requireSession(response);

  if (!response.ok) {
    throw new Error(`Trip Editor API returned ${response.status}`);
  }

  return (await response.json()) as EditorTripState;
};

/// Searches geocode results through the same-origin Trip Editor proxy only.
export const searchGeocode = async (
  endpoint: string,
  query: string,
  limit: number,
  antiforgeryToken: string,
  signal?: AbortSignal
): Promise<EditorGeocodeSearchResponse> => {
  const response = await fetch(`${endpoint}/geocode/search`, {
    method: 'POST',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json', RequestVerificationToken: antiforgeryToken },
    credentials: 'same-origin',
    body: JSON.stringify({ query, limit }),
    signal
  });
  requireSession(response);

  if (response.status === 400) {
    throw new EditorValidationError((await response.json()) as ValidationProblemDetails);
  }

  if (response.status === 429) {
    throw new Error('geocode-rate-limited');
  }

  if (response.status === 503) {
    throw new Error('geocode-provider-unavailable');
  }

  if (!response.ok) {
    throw new Error(`Trip Editor geocode search returned ${response.status}`);
  }

  return (await response.json()) as EditorGeocodeSearchResponse;
};

/** Saves through the protected route only; unavailable contracts never fall back to a legacy full draft. */
export const patchMetadata = async (
  endpoint: string,
  antiforgeryToken: string,
  request: EditorMetadataConditionalRequest
): Promise<EditorMutationResult<EditorTripMetadata>> => {
  const response = await fetch(`${endpoint}/metadata/conditional`, {
    method: 'PATCH',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      RequestVerificationToken: antiforgeryToken
    },
    credentials: 'same-origin',
    body: JSON.stringify(request)
  });
  requireSession(response);

  if (response.status === 400) {
    throw new EditorValidationError((await response.json()) as ValidationProblemDetails);
  }

  if (response.status === 409) {
    throw new Error('Trip fields changed on another device. Your draft is retained. Review current Trip values before choosing a new base and saving.');
  }

  if (!response.ok) {
    throw new Error(`Trip settings could not be confirmed (${response.status}). Keep your draft and review current values before saving again.`);
  }

  return (await response.json()) as EditorMutationResult<EditorTripMetadata>;
};

/// Replaces the complete trip-level tag set through the same-origin editor API.
export const putTags = async (
  endpoint: string,
  antiforgeryToken: string,
  request: EditorTripTagsUpdateRequest
): Promise<EditorMutationResult<EditorTag[]>> => sendMutation(`${endpoint}/tags`, 'PUT', antiforgeryToken, request, 'tag save');

/// Toggles public progress sharing through the same-origin editor API.
export const patchShareProgress = async (
  endpoint: string,
  antiforgeryToken: string,
  request: EditorShareProgressUpdateRequest
): Promise<EditorMutationResult<EditorTripMetadata>> =>
  sendMutation(`${endpoint}/share-progress`, 'PATCH', antiforgeryToken, request, 'share-progress save');

/// Loads existing public tag suggestions without mutating editor state.
export const suggestTags = async (query: string, take: number): Promise<TagSuggestion[]> => {
  const url = `/api/tags/suggest?q=${encodeURIComponent(query)}&take=${encodeURIComponent(String(take))}`;
  const response = await fetch(url, {
    headers: { Accept: 'application/json' },
    credentials: 'same-origin'
  });

  if (!response.ok) {
    throw new Error(`Tag suggestions returned ${response.status}`);
  }

  return (await response.json()) as TagSuggestion[];
};

/// Creates a region through the same-origin editor API.
export const createRegion = async (
  endpoint: string,
  antiforgeryToken: string,
  request: RegionCreationRequest
): Promise<EditorMutationResult<EditorRegion>> => sendMutation(`${endpoint}/regions/conditional`, 'POST', antiforgeryToken, request, 'region create');

/// Uses pinned originals; an unsupported server never receives a fallback PUT.
export const updateRegion = async (
  endpoint: string,
  regionId: string,
  antiforgeryToken: string,
  request: EditorRegionConditionalRequest
): Promise<EditorMutationResult<EditorRegion>> => sendMutation(`${endpoint}/regions/${regionId}/conditional`, 'PATCH', antiforgeryToken, request, 'region update');

/// Deletes a region through the same-origin editor API.
export const deleteRegion = async (
  endpoint: string,
  regionId: string,
  antiforgeryToken: string,
  confirmationToken?: string
): Promise<EditorMutationResult<EditorRegion | null>> => sendMutation(`${endpoint}/regions/${regionId}`, 'DELETE', antiforgeryToken, null, 'region delete', confirmationToken);

/// Persists the complete normal-region order through the same-origin editor API.
export const orderRegions = async (
  endpoint: string,
  antiforgeryToken: string,
  request: EditorRegionOrderRequest
): Promise<EditorMutationResult<EditorRegionOrderResult>> => sendMutation(`${endpoint}/regions/order`, 'PUT', antiforgeryToken, request, 'region order');

/** Sends selected Place scalars without any structural authority or legacy fallback. */
export const updatePlaceConditional = async (
  endpoint: string, placeId: string, antiforgeryToken: string, request: EditorPlaceConditionalRequest
): Promise<EditorMutationResult<EditorPlace>> =>
  sendMutation(`${endpoint}/places/${placeId}/conditional`, 'PATCH', antiforgeryToken, request, 'Place scalar save');

/// Creates a place in a normal region through the same-origin editor API.
export const createPlace = async (
  endpoint: string,
  regionId: string,
  antiforgeryToken: string,
  request: PlaceCreationRequest
): Promise<EditorMutationResult<EditorPlace>> => sendMutation(`${endpoint}/regions/${regionId}/places/conditional`, 'POST', antiforgeryToken, request, 'place create');

/// Updates or moves a place through the same-origin editor API.
export const updatePlace = async (
  endpoint: string,
  placeId: string,
  antiforgeryToken: string,
  request: PlaceStructureRequest
): Promise<EditorMutationResult<EditorPlace>> => sendMutation(`${endpoint}/places/${placeId}`, 'PUT', antiforgeryToken, request, 'place update');

/** Read-only preparation returns conditions for explicit review, never an adopted edit base. */
export const preparePlaceStructure = async (endpoint: string, path: string, antiforgeryToken: string,
  request: PlaceStructureRequest | EditorPlaceOrderRequest): Promise<PlaceStructurePreview> =>
  await sendMutation(`${endpoint}/${path}/prepare`, 'POST', antiforgeryToken, request, 'Place preparation') as unknown as PlaceStructurePreview;

/// Deletes a place through the same-origin editor API.
export const deletePlace = async (
  endpoint: string,
  placeId: string,
  antiforgeryToken: string,
  confirmationToken?: string
): Promise<EditorMutationResult<EditorPlaceDeleteResult>> => sendMutation(`${endpoint}/places/${placeId}`, 'DELETE', antiforgeryToken, null, 'place delete', confirmationToken);

/// Persists the complete place order for one normal region.
export const orderPlaces = async (
  endpoint: string,
  regionId: string,
  antiforgeryToken: string,
  request: EditorPlaceOrderRequest
): Promise<EditorMutationResult<EditorPlaceOrderResult>> =>
  sendMutation(`${endpoint}/regions/${regionId}/places/order`, 'PUT', antiforgeryToken, request, 'place order');

/// Creates an area in a normal region through the same-origin editor API.
export const createArea = async (
  endpoint: string,
  regionId: string,
  antiforgeryToken: string,
  request: EditorAreaSaveRequest
): Promise<EditorMutationResult<EditorArea>> => sendMutation(`${endpoint}/regions/${regionId}/areas`, 'POST', antiforgeryToken, request, 'area create');

/// Updates only dirty Area fields using pinned originals; unsupported servers never receive a legacy fallback.
export const updateArea = async (
  endpoint: string,
  areaId: string,
  antiforgeryToken: string,
  request: EditorAreaConditionalRequest
): Promise<EditorMutationResult<EditorArea>> => sendMutation(`${endpoint}/areas/${areaId}/conditional`, 'PATCH', antiforgeryToken, request, 'area update');

/// Deletes an area through the same-origin editor API.
export const deleteArea = async (
  endpoint: string,
  areaId: string,
  antiforgeryToken: string
): Promise<EditorMutationResult<EditorAreaDeleteResult>> => sendMutation(`${endpoint}/areas/${areaId}`, 'DELETE', antiforgeryToken, null, 'area delete');

/// Persists the complete area order for one normal region.
export const orderAreas = async (
  endpoint: string,
  regionId: string,
  antiforgeryToken: string,
  request: EditorAreaOrderRequest
): Promise<EditorMutationResult<EditorAreaOrderResult>> =>
  sendMutation(`${endpoint}/regions/${regionId}/areas/order`, 'PUT', antiforgeryToken, request, 'area order');

/// Creates a trip-level segment through the same-origin editor API.
export const createSegment = async (
  endpoint: string,
  antiforgeryToken: string,
  request: EditorSegmentSaveRequest
): Promise<EditorMutationResult<EditorSegment>> => sendMutation(`${endpoint}/segments`, 'POST', antiforgeryToken, request, 'segment create');

/// Updates a segment through the same-origin editor API.
export const updateSegment = async (
  endpoint: string,
  segmentId: string,
  antiforgeryToken: string,
  request: EditorSegmentSaveRequest,
  confirmationToken?: string
): Promise<EditorMutationResult<EditorSegment>> => sendMutation(`${endpoint}/segments/${segmentId}`, 'PUT', antiforgeryToken, request, 'segment update', confirmationToken, true);

/** Uses only pinned Notes originals; an unsupported protected endpoint never falls back to aggregate PUT. */
export const patchSegmentNotes = async (
  endpoint: string, segmentId: string, antiforgeryToken: string, request: EditorSegmentNotesConditionalRequest
): Promise<EditorMutationResult<EditorSegment>> =>
  sendMutation(`${endpoint}/segments/${segmentId}/conditional`, 'PATCH', antiforgeryToken, request, 'Segment Notes save');

/// Deletes a segment through the same-origin editor API.
export const deleteSegment = async (
  endpoint: string,
  segmentId: string,
  antiforgeryToken: string
): Promise<EditorMutationResult<EditorSegmentDeleteResult>> => sendMutation(`${endpoint}/segments/${segmentId}`, 'DELETE', antiforgeryToken, null, 'segment delete');

/// Persists the complete trip-level segment order.
export const orderSegments = async (
  endpoint: string,
  antiforgeryToken: string,
  request: EditorSegmentOrderRequest
): Promise<EditorMutationResult<EditorSegmentOrderResult>> => sendMutation(`${endpoint}/segments/order`, 'PUT', antiforgeryToken, request, 'segment order');

const sendMutation = async <TData>(
  url: string,
  method: string,
  antiforgeryToken: string,
  request: unknown,
  label: string,
  confirmationToken?: string,
  segmentConfirmation = false
): Promise<EditorMutationResult<TData>> => {
  const headers: Record<string, string> = {
    Accept: 'application/json',
    RequestVerificationToken: antiforgeryToken
  };
  const init: RequestInit = {
    method,
    headers,
    credentials: 'same-origin'
  };

  if (request !== null) {
    headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(request);
  }
  if (confirmationToken) {
    headers[segmentConfirmation ? 'X-Wayfarer-Clear-Route-Confirmation' : 'X-Wayfarer-Dependency-Confirmation'] = confirmationToken;
  }

  const response = await fetch(url, init);
  requireSession(response);
  if (response.status === 400) {
    throw new EditorValidationError((await response.json()) as ValidationProblemDetails);
  }

  if (response.status === 409) {
    const conflict = await response.json() as EditorLifecycleConflict | EditorSegmentConflict;
    // Scalar conflicts offer review guidance rather than a structural confirmation dialog.
    if (String(conflict.code).startsWith('mutation-'))
      throw new EditorMutationHttpError((conflict as { detail?: string }).detail ?? 'Keep your draft and review the current values before saving.', response.status);
    if (String(conflict.code).startsWith('segment-'))
      throw new EditorSegmentConflictError(conflict as EditorSegmentConflict, response.headers.get('X-Wayfarer-Clear-Route-Confirmation'));
    throw new EditorLifecycleConfirmationError(conflict as EditorLifecycleConflict);
  }

  if (!response.ok) {
    throw new EditorMutationHttpError(`Trip Editor ${label} returned ${response.status}`, response.status);
  }

  return (await response.json()) as EditorMutationResult<TData>;
};
