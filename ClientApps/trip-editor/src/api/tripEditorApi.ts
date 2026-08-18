import type {
  EditorMutationResult,
  EditorLifecycleConflict,
  EditorGeocodeSearchResponse,
  EditorArea,
  EditorAreaDeleteResult,
  EditorAreaOrderRequest,
  EditorAreaOrderResult,
  EditorAreaSaveRequest,
  EditorPlace,
  EditorPlaceDeleteResult,
  EditorPlaceOrderRequest,
  EditorPlaceOrderResult,
  EditorPlaceSaveRequest,
  EditorRegion,
  EditorRegionOrderRequest,
  EditorRegionOrderResult,
  EditorRegionSaveRequest,
  EditorSegment,
  EditorSegmentConflict,
  EditorSegmentDeleteResult,
  EditorSegmentOrderRequest,
  EditorSegmentOrderResult,
  EditorSegmentSaveRequest,
  EditorShareProgressUpdateRequest,
  EditorTag,
  EditorTripTagsUpdateRequest,
  EditorTripMetadata,
  EditorTripMetadataUpdateRequest,
  EditorTripState,
  ExternalRouteProposal,
  AcceptedExternalRouteProposal,
  TagSuggestion,
  ValidationProblemDetails
} from '../types';

export class ExternalRouteProposalError extends Error {
  constructor(readonly code: string) { super(code); }
}

/** Generates a proposal using only the opaque aggregate token as browser input. */
export const generateExternalRouteProposal = async (
  tripId: string, segmentId: string, antiforgeryToken: string, aggregateConcurrencyToken: string,
  signal: AbortSignal
): Promise<ExternalRouteProposal> => {
  const response = await fetch(`/api/trip-editor/${encodeURIComponent(tripId)}/segments/${encodeURIComponent(segmentId)}/route-proposals`, {
    method: 'POST', credentials: 'same-origin', signal,
    headers: { Accept: 'application/json', 'Content-Type': 'application/json', RequestVerificationToken: antiforgeryToken },
    body: JSON.stringify({ aggregateConcurrencyToken })
  });
  if (!response.ok) {
    const error = await response.json().catch(() => ({ code: 'external-routing-unavailable' })) as { code?: string };
    throw new ExternalRouteProposalError(error.code ?? 'external-routing-unavailable');
  }
  return await response.json() as ExternalRouteProposal;
};

/** Revalidates a protected proposal without persistence or provider contact. */
export const acceptExternalRouteProposal = async (
  tripId: string, proposal: ExternalRouteProposal, antiforgeryToken: string
): Promise<AcceptedExternalRouteProposal> => {
  const response = await fetch(`/api/trip-editor/${encodeURIComponent(tripId)}/segments/${encodeURIComponent(proposal.segmentId)}/route-proposals/${encodeURIComponent(proposal.proposalId)}/accept`, {
    method: 'POST', credentials: 'same-origin',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json', RequestVerificationToken: antiforgeryToken },
    body: JSON.stringify({ geometry: proposal.geometry, waypointIndices: proposal.waypointIndices, protectedContext: proposal.protectedContext })
  });
  if (!response.ok) {
    const error = await response.json().catch(() => ({ code: 'route-proposal-stale' })) as { code?: string };
    throw new ExternalRouteProposalError(error.code ?? 'route-proposal-stale');
  }
  return await response.json() as AcceptedExternalRouteProposal;
};

/// Error raised when an editor mutation returns ASP.NET validation problem details.
export class EditorValidationError extends Error {
  readonly errors: Record<string, string[]>;

  constructor(problem: ValidationProblemDetails) {
    super(problem.title ?? 'Validation failed.');
    this.errors = problem.errors ?? {};
  }
}

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
  signal?: AbortSignal
): Promise<EditorGeocodeSearchResponse> => {
  const url = `${endpoint}/geocode/search?q=${encodeURIComponent(query)}&limit=${encodeURIComponent(String(limit))}`;
  const response = await fetch(url, {
    headers: { Accept: 'application/json' },
    credentials: 'same-origin',
    signal
  });

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

export const patchMetadata = async (
  endpoint: string,
  antiforgeryToken: string,
  request: EditorTripMetadataUpdateRequest
): Promise<EditorMutationResult<EditorTripMetadata>> => {
  const response = await fetch(`${endpoint}/metadata`, {
    method: 'PATCH',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      RequestVerificationToken: antiforgeryToken
    },
    credentials: 'same-origin',
    body: JSON.stringify(request)
  });

  if (response.status === 400) {
    throw new EditorValidationError((await response.json()) as ValidationProblemDetails);
  }

  if (!response.ok) {
    throw new Error(`Trip Editor metadata save returned ${response.status}`);
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
  request: EditorRegionSaveRequest
): Promise<EditorMutationResult<EditorRegion>> => sendMutation(`${endpoint}/regions`, 'POST', antiforgeryToken, request, 'region create');

/// Updates a region through the same-origin editor API.
export const updateRegion = async (
  endpoint: string,
  regionId: string,
  antiforgeryToken: string,
  request: EditorRegionSaveRequest
): Promise<EditorMutationResult<EditorRegion>> => sendMutation(`${endpoint}/regions/${regionId}`, 'PUT', antiforgeryToken, request, 'region update');

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

/// Creates a place in a normal region through the same-origin editor API.
export const createPlace = async (
  endpoint: string,
  regionId: string,
  antiforgeryToken: string,
  request: EditorPlaceSaveRequest
): Promise<EditorMutationResult<EditorPlace>> => sendMutation(`${endpoint}/regions/${regionId}/places`, 'POST', antiforgeryToken, request, 'place create');

/// Updates or moves a place through the same-origin editor API.
export const updatePlace = async (
  endpoint: string,
  placeId: string,
  antiforgeryToken: string,
  request: EditorPlaceSaveRequest
): Promise<EditorMutationResult<EditorPlace>> => sendMutation(`${endpoint}/places/${placeId}`, 'PUT', antiforgeryToken, request, 'place update');

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

/// Updates an area through the same-origin editor API.
export const updateArea = async (
  endpoint: string,
  areaId: string,
  antiforgeryToken: string,
  request: EditorAreaSaveRequest
): Promise<EditorMutationResult<EditorArea>> => sendMutation(`${endpoint}/areas/${areaId}`, 'PUT', antiforgeryToken, request, 'area update');

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
  if (response.status === 400) {
    throw new EditorValidationError((await response.json()) as ValidationProblemDetails);
  }

  if (response.status === 409) {
    const conflict = await response.json() as EditorLifecycleConflict | EditorSegmentConflict;
    if (String(conflict.code).startsWith('segment-'))
      throw new EditorSegmentConflictError(conflict as EditorSegmentConflict, response.headers.get('X-Wayfarer-Clear-Route-Confirmation'));
    throw new EditorLifecycleConfirmationError(conflict as EditorLifecycleConflict);
  }

  if (!response.ok) {
    throw new Error(`Trip Editor ${label} returned ${response.status}`);
  }

  return (await response.json()) as EditorMutationResult<TData>;
};
