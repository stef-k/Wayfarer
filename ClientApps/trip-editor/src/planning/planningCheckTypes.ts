import type { PlanningEntry, PlanningValue } from './planningTypes';

/** Check-only facts are never planning mutation originals or portable Trip content. */
export interface CheckEntryInput {
  kind?: PlanningEntry['kind'] | null; zone?: string | null; allocationMinutes?: number | null;
  context?: string[] | null; dependency?: string | null; contextDependency?: string | null;
}
/** Each allowance is explicitly additional, included, unnecessary or unresolved. */
export type CheckTreatment = 'unknown' | 'none' | 'included' | 'additional';
/** User-chosen direction and relevant estimate, independently of saved Segment existence. */
export interface CheckTransfer {
  from: string; to: string; segmentId?: string | null; estimateMinutes?: number | null; relevant: boolean;
  allowance: CheckTreatment; allowanceMinutes?: number | null; connection: CheckTreatment; connectionMinutes?: number | null;
  dependency?: string | null; whatIfRelease?: PlanningValue | null; whatIfAt?: string | null; whatIfDependency?: string | null;
}
/** Only one bounded latest setup is retained on this device; dismissals/results are transient. */
export interface CheckSetup {
  selection: string[] | null; singleTraveller: boolean; travellerDependency?: string | null;
  entries: Record<string, CheckEntryInput>; transfers: CheckTransfer[]; incompatible: Record<string, string | null>;
}
/** Inventory includes explicit exclusions so a partial review cannot become a whole-Trip success. */
export interface CheckItem {
  key: string; kind: string; targetId: string; parentId: string; name: string; entry: PlanningEntry;
  selected: boolean; parentWindows: string[]; dependency: string;
}
/** Displayed finding counts survive collapse; the next request recomputes all results. */
export interface CheckFinding {
  id: string; check: string; entries: string[];
  result: { status: 'completed' | 'unknown' | 'inconsistent' | 'possible' | 'mismatch' | 'contradiction' | 'risk' | 'excluded'; message: string; minutes?: number | null };
}
/** Fresh server comparison result with sanitized dependency-bound local reuse. */
export interface CheckResult {
  contract: string; ownerId: string; tripId: string; zoneRulesVersion: string;
  inventory: CheckItem[]; findings: CheckFinding[]; setup: CheckSetup; reused: string[]; invalidated: string[];
}

/** Whole current inventory is the default, with no implicit premises, zero estimates or allowances. */
export const emptyCheckSetup = (): CheckSetup => ({ selection: null, singleTraveller: false, entries: {}, transfers: [], incompatible: {} });
