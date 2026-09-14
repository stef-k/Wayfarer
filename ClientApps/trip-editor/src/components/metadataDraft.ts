import type { EditorTag, EditorTripMetadata, EditorTripMetadataUpdateRequest } from '../types';
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

