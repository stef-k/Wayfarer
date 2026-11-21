import { message } from '../../../../wwwroot/js/localization/messages.js';

/** Authored civil components; no inferred browser zone or date/time defaults. */
export interface PlanningValue { precision: 'month' | 'date' | 'dateTime'; local: string; zone?: string; selectedOffsetSeconds?: number }
/** Missing endpoints and explicit uncertainty remain different intentions. */
export interface PlanningBound { state: 'known' | 'unspecified' | 'unknown' | 'flexible'; value?: PlanningValue }
/** Exclusive At, period or deliberately undated timing. */
export interface PlanningTiming { form: 'at' | 'period' | 'unspecified' | 'unknown' | 'flexible'; value?: PlanningValue; start?: PlanningBound; end?: PlanningBound }
/** Stable entry identity survives refinement and later editing. */
export interface PlanningEntry { id: string; when: PlanningTiming; label?: string; kind?: 'window' | 'presence' | 'stay' | 'travel'; allocationMinutes?: number }
/** Complete ordered comparison unit on one entity. */
export interface PlanningAggregate { format: 1; entries: PlanningEntry[] }
/** Narrow protected request; absent aggregate is explicit, never synthesized from display defaults. */
export interface PlanningRequest { expected: { parentId: string; planning: PlanningAggregate | null }; operation: 'set' | 'clear'; value?: PlanningAggregate }
/** Confirmed response for one submitted attempt, not an idempotency receipt. */
export interface PlanningAcknowledgement { contract: string; success: boolean; applied: boolean; targetKind: string; targetId: string; parentId: string; planning: PlanningAggregate | null }


/** Display names for editable timing values remain separate from the submitted form keys. */
export const planningFormText = (form: PlanningTiming['form']): string => {
  const labels = {
    at: ['Editor.PlanningTimingAt', 'At'], period: ['Editor.PlanningTimingPeriod', 'Period'],
    unspecified: ['Editor.PlanningTimingUndated', 'Undated, unspecified'],
    unknown: ['Editor.ExplicitlyUnknown', 'Explicitly unknown'], flexible: ['Editor.DeliberatelyFlexible', 'Deliberately flexible']
  } as const;
  return message(...labels[form]);
};

/** Localizes known precision labels without changing the wire key. */
export const planningPrecisionText = (precision: PlanningValue['precision']): string => {
  const labels = {
    month: ['Editor.PlanningMonth', 'Month'], date: ['Editor.PlanningDate', 'Date'],
    dateTime: ['Editor.PlanningMinute', 'Minute date-time']
  } as const;
  return message(...labels[precision]);
};

/** A civil date or clock stays verbatim; only its precision and absent-zone labels are translated. */
export const describePlanningValue = (value: PlanningValue): string => {
  const precision = planningPrecisionText(value.precision);
  return value.precision === 'dateTime' ? message('Editor.PlanningCivilWithZone', '{0} ({1}, {2})',
    value.local, precision, value.zone ?? message('Editor.PlanningZoneUnspecified', 'Zone unspecified'))
    : message('Editor.PlanningCivil', '{0} ({1})', value.local, precision);
};

/** Summarizes known and unresolved bounds without turning an endpoint into a deadline. */
export const describePlanningTiming = (when: PlanningTiming): string => {
  const bound = (item?: PlanningBound) => item?.value ? describePlanningValue(item.value)
    : item?.state === 'unknown' || item?.state === 'flexible' ? planningFormText(item.state) : message('Editor.Unspecified', 'Unspecified');
  if (when.form === 'at' && when.value) return describePlanningValue(when.value);
  if (when.form === 'period') return message('Editor.PlanningPeriodSummary', 'Start {0} → End {1}', bound(when.start), bound(when.end));
  return planningFormText(when.form);
};
