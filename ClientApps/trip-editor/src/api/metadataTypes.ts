import type { EditorCoordinate } from '../types';

/** Existing form validation shape used for conversion before selecting dirty fields. */
export interface EditorTripMetadataUpdateRequest {
  name: string;
  notesHtml: string | null;
  isPublic: boolean;
  coverImage: { rawUrl: string | null } | null;
  center: EditorCoordinate | null;
  zoom: number | null;
}

/** Exact original values for the opt-in metadata compare; never inferred from display defaults. */
export interface EditorMetadataOriginalValues {
  name: string;
  notesHtml: string | null;
  isPublic: boolean;
  coverImage: { rawUrl: string | null } | null;
  center: { latitude: number | null; longitude: number | null } | null;
  zoom: number | null;
}

/** Only authored fields and their independently pinned originals cross the conditional boundary. */
export interface EditorMetadataConditionalRequest {
  expected: Partial<EditorMetadataOriginalValues>;
  changes: Partial<EditorTripMetadataUpdateRequest>;
}
