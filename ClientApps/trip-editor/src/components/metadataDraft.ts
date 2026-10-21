import { reactive } from 'vue';
import type { EditorMetadataOriginalValues, EditorMetadataConditionalRequest, EditorTag, EditorTripMetadata, EditorTripMetadataUpdateRequest } from '../types';
import type { TripEditorMapView } from '../map/mapViewport';
import { normalizeNotesHtml } from '../notes/notesHtml';

/** Editable metadata values; conversion preserves the existing manual-field/API contract. */
export type MetadataDraft = {
  name: string;
  isPublic: boolean;
  shareProgressEnabled: boolean;
  notesHtml: string;
  coverImageRawUrl: string;
  // Vue number inputs emit numbers for valid input and strings for empty/partial input.
  centerLatitude: string | number;
  centerLongitude: string | number;
  zoom: string | number;
  tags: string[];
};

/** Compare viewport fields at map precision without normalizing arbitrary manual edits. */
export const equivalentViewDraft = (left: MetadataDraft, right: ViewDraft): boolean =>
  (['centerLatitude', 'centerLongitude', 'zoom'] as const).every(key => {
    const a = String(left[key]).trim();
    const b = String(right[key]).trim();
    if (!a || !b) return a === b;
    const numbers = [Number(a), Number(b)];
    if (!numbers.every(Number.isFinite)) return false;
    if (key === 'zoom') return numbers.every(Number.isInteger) && numbers[0] === numbers[1];
    // Opposite antimeridian representations identify the same reloadable map center.
    const coordinate = (value: number): number => {
      const rounded = Number(value.toFixed(6));
      return key === 'centerLongitude' && rounded === 180 ? -180 : rounded;
    };
    return coordinate(numbers[0]) === coordinate(numbers[1]);
  });

/** A narrow snapshot permits capture retention without changing unrelated draft refresh rules. */
export type ViewDraft = Pick<MetadataDraft, 'centerLatitude' | 'centerLongitude' | 'zoom'>;
/** Snapshot just the fields owned by a viewport capture. */
export const viewDraft = (draft: MetadataDraft): ViewDraft => ({
  centerLatitude: draft.centerLatitude, centerLongitude: draft.centerLongitude, zoom: draft.zoom
});
/** Present adapter values at the same precision as the permalink. */
export const capturedViewDraft = (view: TripEditorMapView): ViewDraft => ({
  centerLatitude: view.center.latitude.toFixed(6), centerLongitude: view.center.longitude.toFixed(6), zoom: String(view.zoom)
});

/** Convert authoritative metadata and ordered tags to editable form fields. */
export function toDraft(metadata: EditorTripMetadata, tagOrder: string[], tagsBySlug: Record<string, EditorTag>): MetadataDraft {
  return {
    ...toMetadataDraft(metadata),
    tags: tagOrder.map(slug => tagsBySlug[slug]?.name).filter(Boolean) as string[]
  };
}

/** Restore metadata fields using authoritative server values. */
export function toMetadataDraft(metadata: EditorTripMetadata): Omit<MetadataDraft, 'tags'> {
  return {
    name: metadata.name,
    isPublic: metadata.isPublic,
    shareProgressEnabled: metadata.isPublic && metadata.shareProgressEnabled,
    notesHtml: normalizeNotesHtml(metadata.notesHtml),
    coverImageRawUrl: metadata.coverImage?.rawUrl ?? '',
    centerLatitude: metadata.center ? String(metadata.center.latitude) : '',
    centerLongitude: metadata.center ? String(metadata.center.longitude) : '',
    zoom: metadata.zoom === null ? '' : String(metadata.zoom)
  };
}

/** Preserve the existing nullable request shape and server validation of partial/invalid input. */
export function buildMetadataRequest(value: MetadataDraft): EditorTripMetadataUpdateRequest {
  const centerLatitude = String(value.centerLatitude).trim();
  const centerLongitude = String(value.centerLongitude).trim();
  const zoom = String(value.zoom).trim();
  const coverImageRawUrl = value.coverImageRawUrl.trim();
  const hasPartialCenter = Boolean(centerLatitude || centerLongitude);

  return {
    name: value.name,
    notesHtml: normalizeNotesHtml(value.notesHtml),
    isPublic: value.isPublic,
    coverImage: coverImageRawUrl ? { rawUrl: coverImageRawUrl } : null,
    center: hasPartialCenter
      ? { latitude: centerLatitude ? Number(centerLatitude) : Number.NaN, longitude: centerLongitude ? Number(centerLongitude) : Number.NaN }
      : null,
    zoom: zoom ? Number(zoom) : null
  };
}

/** Fields in one metadata operation; each retains its own original even when other props refresh. */
const metadataFields = ['name', 'notesHtml', 'isPublic', 'coverImage', 'center', 'zoom'] as const;
type MetadataField = typeof metadataFields[number];
const same = (left: unknown, right: unknown): boolean => JSON.stringify(left) === JSON.stringify(right);

/** Copies one logical API field, keeping a coordinate pair together. */
function assignField(target: MetadataDraft, source: MetadataDraft, field: MetadataField): void {
  switch (field) {
    case 'coverImage': target.coverImageRawUrl = source.coverImageRawUrl; break;
    case 'center': target.centerLatitude = source.centerLatitude; target.centerLongitude = source.centerLongitude; break;
    default: Object.assign(target, { [field]: source[field] });
  }
}

/** Owns per-field originals and acknowledgements so refreshes and older replies cannot erase authored intent. */
export function createMetadataDraftState(metadata: EditorTripMetadata, tagOrder: string[], tagsBySlug: Record<string, EditorTag>) {
  const draft = reactive(toDraft(metadata, tagOrder, tagsBySlug));
  const baseline = reactive(toDraft(metadata, tagOrder, tagsBySlug));
  const originals = reactive<Partial<EditorMetadataOriginalValues>>({ ...metadata.originalValues });

  /** Adopts only clean fields; dirty fields retain both their desired value and their original compare. */
  const refresh = (next: EditorTripMetadata, order: string[], tags: Record<string, EditorTag>): void => {
    const current = buildMetadataRequest(draft);
    const before = buildMetadataRequest(baseline);
    const incoming = toDraft(next, order, tags);
    for (const field of metadataFields) {
      if (same(current[field], before[field])) adopt(field, incoming, next);
    }
    if (same(normalizeTagNames(draft.tags), normalizeTagNames(baseline.tags))) {
      draft.tags = [...incoming.tags]; baseline.tags = [...incoming.tags];
    }
    if (draft.shareProgressEnabled === baseline.shareProgressEnabled) {
      draft.shareProgressEnabled = incoming.shareProgressEnabled;
      baseline.shareProgressEnabled = incoming.shareProgressEnabled;
    }
  };

  /** Adopts one clean original without deriving missing evidence from a lossy display projection. */
  const adopt = (field: MetadataField, incoming: MetadataDraft, next: EditorTripMetadata): void => {
    assignField(draft, incoming, field);
    assignField(baseline, incoming, field);
    delete originals[field];
    if (next.originalValues && Object.hasOwn(next.originalValues, field)) {
      Object.assign(originals, { [field]: next.originalValues[field] });
    }
  };

  /** Builds the protected request from dirty intent; older server payloads fail closed. */
  const request = (): EditorMetadataConditionalRequest => {
    const current = buildMetadataRequest(draft);
    const before = buildMetadataRequest(baseline);
    const expected: Partial<EditorMetadataOriginalValues> = {};
    const changes: Partial<EditorTripMetadataUpdateRequest> = {};
    for (const field of metadataFields) {
      if (same(current[field], before[field])) continue;
      if (!Object.hasOwn(originals, field)) throw new Error('Original Trip values are unavailable. Keep your draft and reload before saving.');
      Object.assign(expected, { [field]: originals[field] });
      Object.assign(changes, { [field]: current[field] });
    }
    return { expected, changes };
  };

  /** Acknowledges submitted fields without erasing later manual edits or explicit sharing choices. */
  const acknowledge = (next: EditorTripMetadata, submitted: EditorMetadataConditionalRequest, sharingEditedDuringSave: boolean): void => {
    const current = buildMetadataRequest(draft);
    const before = buildMetadataRequest(baseline);
    const sharingWasClean = draft.shareProgressEnabled === baseline.shareProgressEnabled;
    const incoming = toDraft(next, [], {});
    for (const field of metadataFields) {
      const wasSubmitted = Object.hasOwn(submitted.changes, field);
      if (!wasSubmitted && !same(current[field], before[field])) continue;
      const retained = { ...draft };
      adopt(field, incoming, next);
      if (wasSubmitted && !same(current[field], submitted.changes[field])) assignField(draft, retained, field);
    }
    // A derived sharing disable advances the baseline but cannot settle a newer checkbox choice.
    if (sharingWasClean && !sharingEditedDuringSave) draft.shareProgressEnabled = next.shareProgressEnabled;
    baseline.shareProgressEnabled = next.shareProgressEnabled;
  };

  /** Explicit discard adopts the latest visible server base and clears every local field obligation. */
  const reset = (next: EditorTripMetadata, order: string[], tags: Record<string, EditorTag>): void => {
    Object.assign(draft, toDraft(next, order, tags));
    Object.assign(baseline, toDraft(next, order, tags));
    for (const field of metadataFields) delete originals[field];
    Object.assign(originals, next.originalValues ?? {});
  };

  return { draft, baseline, refresh, request, acknowledge, reset };
}

/** Trim and deduplicate tags in their entered order. */
export function normalizeTagNames(values: string[]): string[] {
  const seen = new Set<string>();
  const tags: string[] = [];
  values.forEach(value => {
    const tag = value.trim();
    const key = normalizeTagNameKey(tag);
    if (tag && !seen.has(key)) {
      seen.add(key);
      tags.push(tag);
    }
  });
  return tags;
}

/** Share the case-insensitive identity used by tag editing and suggestions. */
export function normalizeTagNameKey(value: string): string {
  return value.trim().toLocaleLowerCase();
}
