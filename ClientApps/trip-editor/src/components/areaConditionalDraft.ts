import { reactive, ref } from 'vue';
import type { EditorArea, EditorAreaConditionalRequest, EditorAreaDraft, EditorAreaOriginalValues } from '../types';
import { buildAreaRequest, emptyAreaDraft, toAreaDraft } from './regionPlaceDrafts';

const fields = ['name', 'notesHtml', 'fillHex', 'geometry'] as const;
type Field = typeof fields[number];
const same = (left: unknown, right: unknown): boolean => JSON.stringify(left) === JSON.stringify(right);
const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T;

/** Copies a complete authored Polygon, including holes, without sharing nested coordinates with props or baselines. */
const assign = (target: EditorAreaDraft, source: EditorAreaDraft, field: Field): void => {
  Object.assign(target, { [field]: clone(source[field]) });
};

/** Owns only the Area draft's pinned originals and acknowledgement; Region/Place ownership stays independent. */
export const createAreaDraftState = () => {
  const draft = reactive(emptyAreaDraft());
  const baseline = reactive(emptyAreaDraft());
  const originals = reactive<Partial<EditorAreaOriginalValues>>({});
  const pending = ref(false);
  const uncertain = ref(false);
  let session = 0;

  /** Advances one clean field, never inventing missing server evidence from display values. */
  const adopt = (field: Field, incoming: EditorAreaDraft, next: EditorArea): void => {
    assign(draft, incoming, field);
    assign(baseline, incoming, field);
    delete originals[field];
    if (next.originalValues && Object.hasOwn(next.originalValues, field))
      Object.assign(originals, { [field]: clone(next.originalValues[field]) });
  };

  /** Explicit open/reset/discard adopts a reviewed base; a reset never asserts that an earlier attempt failed. */
  const reset = (next: EditorArea | null): void => {
    session++;
    uncertain.value = false;
    Object.assign(draft, toAreaDraft(next, null));
    Object.assign(baseline, toAreaDraft(next, null));
    for (const key of Object.keys(originals)) delete originals[key as keyof EditorAreaOriginalValues];
    if (next?.originalValues) originals.regionId = next.originalValues.regionId;
    if (next) for (const field of fields) adopt(field, toAreaDraft(next, null), next);
  };

  /** Refreshes clean fields only; parent drift cannot silently rebind an existing draft to another Region. */
  const refresh = (next: EditorArea | null): void => {
    if (!next || next.id !== draft.id || next.regionId !== baseline.regionId || pending.value || uncertain.value) return;
    const current = buildAreaRequest(draft);
    const before = buildAreaRequest(baseline);
    const incoming = toAreaDraft(next, null);
    for (const field of fields) if (same(current[field], before[field])) adopt(field, incoming, next);
  };

  /** Selects exact dirty intent and fails closed if the server did not publish the required originals. */
  const request = (): EditorAreaConditionalRequest => {
    if (!originals.regionId) throw new Error('Original Area parent is unavailable. Keep your draft and reload before saving.');
    const result: EditorAreaConditionalRequest = { expected: { regionId: originals.regionId }, changes: {} };
    const current = buildAreaRequest(draft);
    const before = buildAreaRequest(baseline);
    for (const field of fields) {
      if (same(current[field], before[field])) continue;
      if (!Object.hasOwn(originals, field)) throw new Error('Original Area values are unavailable. Keep your draft and reload before saving.');
      Object.assign(result.expected, { [field]: originals[field] });
      Object.assign(result.changes, { [field]: current[field] });
    }
    return result;
  };

  /** Acknowledges before Vue updates parent props, preserving unsent fields and newer edits or polygon reversals. */
  const acknowledge = (next: EditorArea, submitted: EditorAreaConditionalRequest): void => {
    const current = buildAreaRequest(draft);
    const before = buildAreaRequest(baseline);
    const retained = clone(draft);
    const incoming = toAreaDraft(next, null);
    for (const field of fields) {
      const sent = Object.hasOwn(submitted.changes, field);
      if (!sent && !same(current[field], before[field])) continue;
      adopt(field, incoming, next);
      if (sent && !same(current[field], submitted.changes[field])) assign(draft, retained, field);
    }
    draft.id = baseline.id = next.id;
    draft.regionId = baseline.regionId = next.regionId;
    originals.regionId = next.originalValues?.regionId;
  };

  return { draft, baseline, pending, uncertain, reset, refresh, request, acknowledge, session: () => session };
};
