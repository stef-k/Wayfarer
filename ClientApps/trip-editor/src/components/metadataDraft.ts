import { reactive, ref } from 'vue';
import type { ContributionPolicy, TripCredit, EditorMetadataOriginalValues, EditorMetadataConditionalRequest, EditorTag, EditorTripMetadata, EditorTripMetadataUpdateRequest } from '../types';
import type { TripEditorMapView } from '../map/mapViewport';
import { normalizeNotesHtml } from '../notes/notesHtml';
import { ownerOriginal, sharingBase, tagBase, type DraftFields, type DraftValue, type SettingsField } from '../recovery/custody';

/** Editable metadata values; conversion preserves the existing manual-field/API contract. */
export type MetadataDraft = {
  name: string;
  isPublic: boolean;
  contributionPolicy: ContributionPolicy | null;
  credits: TripCredit[];
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
    contributionPolicy: metadata.contributionPolicy ?? null,
    credits: JSON.parse(JSON.stringify(metadata.credits ?? [])),
    shareProgressEnabled: metadata.shareProgressEnabled,
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
    contributionPolicy: value.contributionPolicy,
    credits: value.credits.map(c => ({ ...c, licenceUrl: c.licenceUrl?.trim() || null, sourceUrl: c.sourceUrl?.trim() || null })),
    coverImage: coverImageRawUrl ? { rawUrl: coverImageRawUrl } : null,
    center: hasPartialCenter
      ? { latitude: centerLatitude ? Number(centerLatitude) : Number.NaN, longitude: centerLongitude ? Number(centerLongitude) : Number.NaN }
      : null,
    zoom: zoom ? Number(zoom) : null
  };
}

/** Fields in one metadata operation; each retains its own original even when other props refresh. */
const metadataFields = ['name', 'notesHtml', 'isPublic', 'contributionPolicy', 'credits', 'coverImage', 'center', 'zoom'] as const;
type MetadataField = typeof metadataFields[number];
const settingsFields = [...metadataFields, 'tags', 'shareProgressEnabled'] as const;
const same = (left: unknown, right: unknown): boolean => JSON.stringify(left) === JSON.stringify(right);

/** Copies one logical API field, keeping a coordinate pair together. */
function assignField(target: Partial<MetadataDraft>, source: MetadataDraft, field: SettingsField): void {
  switch (field) {
    case 'credits': target.credits = JSON.parse(JSON.stringify(source.credits)); break;
    case 'tags': target.tags = [...source.tags]; break;
    case 'coverImage': target.coverImageRawUrl = source.coverImageRawUrl; break;
    case 'center': target.centerLatitude = source.centerLatitude; target.centerLongitude = source.centerLongitude; break;
    default: Object.assign(target, { [field]: source[field] });
  }
}

/** Owns per-field originals and acknowledgements so refreshes and older replies cannot erase authored intent. */
export function createMetadataDraftState(metadata: EditorTripMetadata, tagOrder: string[], tagsBySlug: Record<string, EditorTag>) {
  const draft = reactive(toDraft(metadata, tagOrder, tagsBySlug));
  const baseline = reactive(toDraft(metadata, tagOrder, tagsBySlug));
  // Owner-selected visible replacements have an identity independent of emitted HTML.
  const notesAdoption = ref(0);
  const originals = reactive<Partial<EditorMetadataOriginalValues>>({ ...metadata.originalValues });
  // A later edit back to the old value remains intent while an earlier save may have changed the server.
  const retained = reactive(new Set<SettingsField>());
  let tagsOriginal: unknown = tagBase(Object.values(tagsBySlug));
  let sharingOriginal: unknown = sharingBase(metadata);
  let policyOriginal: unknown = metadata.originalValues?.contributionPolicy;
  /** Adopts only clean fields; dirty fields retain both their desired value and their original compare. */
  const refresh = (next: EditorTripMetadata, order: string[], tags: Record<string, EditorTag>): void => {
    const previousNotes = draft.notesHtml;
    const current = buildMetadataRequest(draft);
    const before = buildMetadataRequest(baseline);
    const incoming = toDraft(next, order, tags);
    for (const field of metadataFields) {
      if (!retained.has(field) && same(current[field], before[field])) adopt(field, incoming, next);
    }
    if (!retained.has('tags') && same(normalizeTagNames(draft.tags), normalizeTagNames(baseline.tags))) {
      tagsOriginal = tagBase(Object.values(tags));
      draft.tags = [...incoming.tags]; baseline.tags = [...incoming.tags];
    }
    if (!retained.has('shareProgressEnabled') &&
        draft.shareProgressEnabled === baseline.shareProgressEnabled) {
      sharingOriginal = sharingBase(next);
      draft.shareProgressEnabled = incoming.shareProgressEnabled;
      baseline.shareProgressEnabled = incoming.shareProgressEnabled;
    }
    if (draft.notesHtml !== previousNotes) notesAdoption.value++;
  };

  /** Adopts one clean original without deriving missing evidence from a lossy display projection. */
  const adopt = (field: MetadataField, incoming: MetadataDraft, next: EditorTripMetadata): void => {
    assignField(draft, incoming, field);
    assignField(baseline, incoming, field);
    if (field === 'isPublic') policyOriginal = next.originalValues?.contributionPolicy;
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
      if (!retained.has(field) && same(current[field], before[field])) continue;
      if (!Object.hasOwn(originals, field)) throw new Error('Original Trip values are unavailable. Keep your draft and reload before saving.');
      Object.assign(expected, { [field]: originals[field] });
      Object.assign(changes, { [field]: current[field] });
    }
    if (changes.isPublic === true && !Object.hasOwn(changes, 'contributionPolicy')) {
      if (policyOriginal === undefined) throw new Error('The reviewed publication policy is unavailable.');
      Object.assign(expected, { contributionPolicy: policyOriginal });
    }
    return { expected, changes };
  };

  /** Reset/close/reopen cannot discard an unresolved metadata attempt or any newer metadata intent. */
  const reset = (next: EditorTripMetadata, order: string[], tags: Record<string, EditorTag>): void => {
    retained.clear();
    Object.assign(draft, toDraft(next, order, tags));
    Object.assign(baseline, toDraft(next, order, tags));
    for (const field of metadataFields) delete originals[field];
    Object.assign(originals, next.originalValues ?? {});
    tagsOriginal = tagBase(Object.values(tags));
    sharingOriginal = sharingBase(next);
    policyOriginal = next.originalValues?.contributionPolicy;
  };

  /** Captures only authored settings and their exact comparison bases. */
  const capture = (hold: SettingsField[]): DraftFields => {
    const fields: DraftFields = {};
    for (const field of settingsFields) {
      const desired: Partial<MetadataDraft> = {};
      const base: Partial<MetadataDraft> = {};
      assignField(desired, draft, field);
      assignField(base, baseline, field);
      if (!retained.has(field) && !hold.includes(field) && same(desired, base)) continue;
      const original = field === 'tags' ? tagsOriginal
        : field === 'shareProgressEnabled' ? sharingOriginal : originals[field];
      fields[field] = { desired, baseline: base, original,
        ...(field === 'isPublic' && draft.isPublic ? { policyOriginal } : {}) };
    }
    return JSON.parse(JSON.stringify(fields)) as DraftFields;
  };

  /** Overlays the durable authored generation onto fresh confirmed settings. */
  const restore = (fields: DraftFields, next: EditorTripMetadata, order: string[], tags: Record<string, EditorTag>): void => {
    const previousNotes = draft.notesHtml;
    reset(next, order, tags);
    for (const [key, value] of Object.entries(fields)) {
      const field = key as SettingsField;
      Object.assign(draft, value.desired);
      Object.assign(baseline, value.baseline);
      if (field === 'tags') tagsOriginal = value.original;
      else if (field === 'shareProgressEnabled') sharingOriginal = value.original;
      else if (value.original === undefined) delete originals[field];
      else Object.assign(originals, { [field]: value.original });
      if (field === 'isPublic') policyOriginal = value.policyOriginal;
      if (same(value.desired, value.baseline)) retained.add(field);
    }
    if (draft.notesHtml !== previousNotes) notesAdoption.value++;
  };

  return { draft, baseline, notesAdoption, refresh, request, capture, restore, retained };

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

/** Builds an explicit new base from the published owner value while retaining the authored edit. */
export function adoptedDraft(field: SettingsField, entry: DraftValue, current: EditorTripMetadata): DraftValue {
  const baseline: Partial<MetadataDraft> = {};
  assignField(baseline, { ...toMetadataDraft(current), tags: current.tags?.map(tag => tag.name) ?? [] }, field);
  return { desired: entry.desired, baseline, original: ownerOriginal(current, field),
    ...(field === 'isPublic' && entry.desired.isPublic ? { policyOriginal: current.originalValues?.contributionPolicy } : {}) };
}
