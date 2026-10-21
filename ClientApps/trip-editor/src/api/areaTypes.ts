import type { EditorAreaSaveRequest, Guid } from '../types';

/** Server-origin originals preserve nulls and exact Polygon evidence independently of the displayed form. */
export interface EditorAreaOriginalValues {
  regionId: Guid;
  name: string;
  notesHtml: string | null;
  fillHex: string | null;
  geometry: string;
}

/** Parent identity is comparison evidence, never a writable field; each dirty field supplies its own original. */
export interface EditorAreaConditionalRequest {
  expected: Partial<EditorAreaOriginalValues> & { regionId: Guid };
  changes: Partial<EditorAreaSaveRequest>;
}
