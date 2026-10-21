import { reactive, ref } from 'vue';
import type { EditorRegion, EditorRegionConditionalRequest, EditorRegionOriginalValues } from '../types';
import { buildRegionRequest, emptyRegionDraft, toRegionDraft, type RegionDraft } from './regionPlaceDrafts';

const fields = ['name', 'notesHtml', 'coverImage', 'center'] as const;
type Field = typeof fields[number];
const same = (left: unknown, right: unknown): boolean => JSON.stringify(left) === JSON.stringify(right);

/** Copies one logical scalar while keeping the center coordinate pair a single intention. */
const assign = (target: RegionDraft, source: RegionDraft, field: Field): void => {
  if (field === 'coverImage') target.coverImageRawUrl = source.coverImageRawUrl;
  else if (field === 'center') {
    target.centerLatitude = source.centerLatitude;
    target.centerLongitude = source.centerLongitude;
  }
  else target[field] = source[field];
};

/** Owns Region originals, clean-field refresh and acknowledgement of precisely submitted intent. */
export const createRegionDraftState = () => {
  const draft = reactive(emptyRegionDraft());
  const baseline = reactive(emptyRegionDraft());
  const originals = reactive<Partial<EditorRegionOriginalValues>>({});
  const pending = ref(false);
  const uncertain = ref(false);

  /** Adopts a clean field without synthesizing absent originals from display values. */
  const adopt = (field: Field, incoming: RegionDraft, next: EditorRegion): void => {
    assign(draft, incoming, field);
    assign(baseline, incoming, field);
    delete originals[field];
    if (next.originalValues && Object.hasOwn(next.originalValues, field))
      Object.assign(originals, { [field]: JSON.parse(JSON.stringify(next.originalValues[field])) });
  };

  /** Explicit open/reset/discard replaces the base; unrelated props never invoke this operation. */
  const reset = (next: EditorRegion | null): void => {
    uncertain.value = false;
    Object.assign(draft, toRegionDraft(next));
    Object.assign(baseline, toRegionDraft(next));
    for (const field of fields) delete originals[field];
    if (next) for (const field of fields) adopt(field, toRegionDraft(next), next);
  };

  /** Clean fields follow props; dirty values and their original comparisons stay pinned. */
  const refresh = (next: EditorRegion | null): void => {
    if (!next || next.id !== draft.id || pending.value || uncertain.value) return;
    const current = buildRegionRequest(draft);
    const before = buildRegionRequest(baseline);
    const incoming = toRegionDraft(next);
    for (const field of fields) if (same(current[field], before[field])) adopt(field, incoming, next);
  };

  /** Dirty-only requests fail closed when the server omitted exact original evidence. */
  const request = (): EditorRegionConditionalRequest => {
    const current = buildRegionRequest(draft);
    const before = buildRegionRequest(baseline);
    const result: EditorRegionConditionalRequest = { expected: {}, changes: {} };
    for (const field of fields) {
      if (same(current[field], before[field])) continue;
      if (!Object.hasOwn(originals, field)) throw new Error('Original Region values are unavailable. Keep your draft and reload before saving.');
      Object.assign(result.expected, { [field]: originals[field] });
      Object.assign(result.changes, { [field]: current[field] });
    }
    return result;
  };

  /** Advances acknowledged bases before parent props update, retaining newer edits and explicit reversals. */
  const acknowledge = (next: EditorRegion, submitted: EditorRegionConditionalRequest): void => {
    const current = buildRegionRequest(draft);
    const before = buildRegionRequest(baseline);
    const incoming = toRegionDraft(next);
    const retained = { ...draft };
    for (const field of fields) {
      const sent = Object.hasOwn(submitted.changes, field);
      if (!sent && !same(current[field], before[field])) continue;
      adopt(field, incoming, next);
      if (sent && !same(current[field], submitted.changes[field])) assign(draft, retained, field);
    }
    draft.id = next.id;
    baseline.id = next.id;
  };

  return { draft, baseline, pending, uncertain, reset, refresh, request, acknowledge };
};
