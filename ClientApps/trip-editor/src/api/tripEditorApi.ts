import type {
  EditorMutationResult,
  EditorTripMetadata,
  EditorTripMetadataUpdateRequest,
  EditorTripState,
  ValidationProblemDetails
} from '../types';

/// Error raised when an editor mutation returns ASP.NET validation problem details.
export class EditorValidationError extends Error {
  readonly errors: Record<string, string[]>;

  constructor(problem: ValidationProblemDetails) {
    super(problem.title ?? 'Validation failed.');
    this.errors = problem.errors ?? {};
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
