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
