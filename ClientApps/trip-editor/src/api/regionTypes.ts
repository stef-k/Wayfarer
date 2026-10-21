import type { EditorCoordinate, EditorRegionSaveRequest } from '../types';

/** Original evidence published by the Region compare/apply owner, independent of display defaults. */
export interface EditorRegionOriginalValues {
  name: string;
  notesHtml: string | null;
  coverImage: { rawUrl: string | null } | null;
  center: EditorCoordinate | null;
}

/** Only authored fields enter the protected update, paired with explicit originals. */
export interface EditorRegionConditionalRequest {
  expected: Partial<EditorRegionOriginalValues>;
  changes: Partial<EditorRegionSaveRequest>;
}
