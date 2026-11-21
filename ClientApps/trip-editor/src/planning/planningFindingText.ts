import { message, number } from '../../../../wwwroot/js/localization/messages.js';

const identities = new Map<string, string>([
  ["Choose explicitly relevant parent windows; ownership alone imposes no constraint.", "Editor.ParentWindowSelect"],
  ["More than 256 admissible placements; refine the selected windows or folds.", "Editor.ParentWindowPlacements"],
  ["Child and selected windows need the same explicit instant or interpreted civil frame.", "Editor.ParentWindowFrame"],
  ["Complete child coverage is inside the selected parent-window union.", "Editor.ParentWindowInside"],
  ["Some supported placements exceed the selected union; others are contained. Resolve the fold for certainty.", "Editor.ParentWindowPossible"],
  ["Occupation needs a common comparable frame.", "Editor.PhysicalFrame"],
  ["No occupied overlap in these placements. Adjacency establishes no transfer time.", "Editor.PhysicalNoOverlap"],
  ["Occupied overlap needs an explicit single-traveller premise and assessed incompatible locations.", "Editor.PhysicalPremises"],
  ["One traveller requires simultaneous presence at assessed incompatible locations.", "Editor.PhysicalContradiction"],
  ["Some supported placements require simultaneous presence at incompatible locations.", "Editor.PhysicalPossible"],
  ["Elapsed travel needs resolved zones.", "Editor.TravelZones"],
  ["Every supported gap is negative.", "Editor.TravelAllNegative"],
  ["Travel gap depends on an unresolved fold; choose the occurrence.", "Editor.TravelFold"],
  ["Allowance or necessary connection information remains unknown; known estimate risk is retained.", "Editor.TravelAllowanceUnknown"],
  ["No supported placements.", "Editor.CheckPlacementsUnavailable"],
  ["Meaning is unspecified. Clarify only for this check, or leave unresolved.", "Editor.CheckMeaningUnknown"],
  ["Stay availability/planning windows do not imply continuous physical presence; overlaps are legitimate.", "Editor.CheckStayOverlap"],
  ["Journey intention is retained; choose related movement entries to assess travel. Via Places are not timed stops.", "Editor.CheckJourneySelect"],
  ["Movement has no selected pair. Choose release → arrival entries; display order does not establish travel.", "Editor.CheckPairSelect"],
  ["Explicitly outside this temporary selection; remains saved and unassessed.", "Editor.CheckOutsideSelection"],
  ["Movement pair is outside this selection.", "Editor.CheckPairOutside"],
  ["Choose relevant parent windows or leave context unresolved; ownership alone is not a time constraint.", "Editor.CheckParentChoose"],
  ["A selected context is no longer a relevant parent window; review the selection.", "Editor.CheckParentChanged"],
  ["Every admissible placement has coverage outside the selected union.", "Editor.CheckParentOutside"],
  ["Mixed/coarse occupied intervals are unsupported; refine only if needed.", "Editor.CheckCoarse"],
  ["Whole-entry coverage needs an end or explicit elapsed allocation; a date is not occupation.", "Editor.CheckCoverageEnd"],
  ["Coverage needs supported known bounds; missing, unknown and flexible values are not zero.", "Editor.CheckCoverageBounds"],
  ["Release/end is unknown.", "Editor.CheckReleaseUnknown"],
  ["Arrival/start is unknown.", "Editor.CheckArrivalUnknown"],
  ["Dwell/allocation is unknown; At is not a release.", "Editor.CheckDwellUnknown"],
  ["A comparable minute release/arrival is needed.", "Editor.CheckComparableMinute"],
  ["Elapsed comparison needs minute precision and a relevant zone.", "Editor.CheckElapsedPrecision"],
  ["Current civil value cannot be resolved.", "Editor.CheckCivilInvalid"],
  ["A necessary zone is unspecified; choose one for this check or leave unresolved.", "Editor.CheckZoneNeeded"],
  ["The zone is unsupported by current rules.", "Editor.CheckZoneUnsupported"],
  ["Current rules place this clock in a zone gap.", "Editor.CheckZoneGap"],
  ["The selected fold offset is unavailable under current rules.", "Editor.CheckFoldOffset"],
  ["Invalid civil window.", "Editor.CheckInvalidWindow"],
  ["Zoned coverage beyond year 9999 is unsupported.", "Editor.CheckBeyondCalendar"],
  ["The endpoints have no common frame.", "Editor.CheckEndpointFrame"],
  ["The interval is internally reversed.", "Editor.CheckReversed"],
  ["Unresolved occurrences permit reversed ordering; choose the relevant fold.", "Editor.CheckFoldReversed"],
]);


// Dynamic legacy findings contain bounded numbers only. Source and deadline values travel separately.
const numericFindings: [RegExp, string, string][] = [
  [/^([\d.,]+) minutes definitely outside the selected parent-window union\.$/, 'Editor.CheckOutsideMinutes', '{0} minutes definitely outside the selected parent-window union.'],
  [/^Negative exact gap: ([\d.,]+) minutes in the wrong order\.$/, 'Editor.CheckNegativeMinutes', 'Negative exact gap: {0} minutes in the wrong order.'],
  [/^([\d.,]+)-minute gap; a relevant travel estimate is unknown, not zero\.$/, 'Editor.CheckGapUnknown', '{0}-minute gap; a relevant travel estimate is unknown, not zero.'],
  [/^([\d.,]+)-minute estimate risk before extras\.$/, 'Editor.CheckRiskExtras', '{0}-minute estimate risk before extras.'],
  [/^([\d.,]+) minutes estimated slack \(([\d.,]+) gap − ([\d.,]+) estimate − ([\d.,]+) additional\)\. No arrival guarantee\.$/, 'Editor.CheckSlack', '{0} minutes estimated slack ({1} gap − {2} estimate − {3} additional). No arrival guarantee.'],
  [/^([\d.,]+)-minute estimate risk including additional allowance\/connection\.$/, 'Editor.CheckRiskAll', '{0}-minute estimate risk including additional allowance/connection.'],
  [/^(\d+) further physical pairs are unassessed beyond the 2048-pair bound\. Narrow the displayed selection explicitly\.$/, 'Editor.CheckPairBound', '{0} further physical pairs are unassessed beyond the 2048-pair bound. Narrow the displayed selection explicitly.']
];

/** Presents known safe explanations independently of the server text used for follow-up controls. */
export const planningFindingText = (text: string): string => {
  const key = identities.get(text);
  if (key) return message(key, text);
  for (const [pattern, name, fallback] of numericFindings) {
    const match = pattern.exec(text);
    if (match) return message(name, fallback, ...match.slice(1).map(value => number(Number(value.replace(',', '.')), { maximumFractionDigits: 3 })));
  }
  return message('Editor.CheckDetailFallback', 'Review the listed entries and check assumptions for this result.');
};

const checkNotes = new Map<string, string>([
  ["Single-traveller premise: selected participants changed.", "Editor.CheckTravellerChanged"],
  ["Single-traveller premise", "Editor.CheckTravellerReused"],
  ["Movement links to deleted entries were removed.", "Editor.CheckDeletedLinks"],
  ["Inputs for removed entries were discarded.", "Editor.CheckRemovedInputs"],
  ["Incompatible-location assessment", "Editor.CheckLocationReused"],
  ["Changed presence/location reopened incompatible-location assessment.", "Editor.CheckLocationChanged"],
  ["Movement route/location/estimate changed: confirm relevance and extras again.", "Editor.CheckMovementChanged"],
  ["Movement estimate relevance and allowance treatment", "Editor.CheckMovementReused"],
  ["What-if release expired or its timing evidence changed.", "Editor.CheckWhatIfChanged"],
]);
const checkNoteTemplates: [string, string, string][] = [
  [": changed entry/location/rules reopened interpretations.", "Editor.CheckEntryChanged", "{0}: changed entry/location/rules reopened interpretations."],
  [": entry interpretations", "Editor.CheckEntryReused", "{0}: entry interpretations"],
  [": context membership changed.", "Editor.CheckContextChanged", "{0}: context membership changed."],
  [": selected parent windows (coverage recomputed)", "Editor.CheckContextReused", "{0}: selected parent windows (coverage recomputed)"],
  [": changed clock/zone reopened occurrence choice.", "Editor.CheckOccurrenceChanged", "{0}: changed clock/zone reopened occurrence choice."],
];

/** Translates only the known suffix of an entry note, retaining the full authored name before it. */
export const planningCheckNote = (text: string): string => {
  const key = checkNotes.get(text);
  if (key) return message(key, text);
  for (const [suffix, name, fallback] of checkNoteTemplates) {
    if (text.endsWith(suffix)) return message(name, fallback, text.slice(0, -suffix.length));
  }
  return message('Editor.CheckDetailFallback', 'Review the listed entries and check assumptions for this result.');
};

const validation = new Map<string, string>([
  ["Provide 1–128 planning entries in format 1.", "Editor.PlanningValueScope"],
  ["Planning entry identities must be distinct and nonempty.", "Editor.PlanningValueIdentity"],
  ["A planning label may contain at most 200 characters.", "Editor.PlanningValueLabel"],
  ["Choose a supported meaning for this object.", "Editor.PlanningValueMeaning"],
  ["Choose a supported timing form.", "Editor.PlanningValueForm"],
  ["Elapsed allocation must be a nonnegative integer on an At or undated intention.", "Editor.PlanningValueAllocation"],
  ["At requires one value and no period bounds.", "Editor.PlanningValueAt"],
  ["A period requires at least one known bound.", "Editor.PlanningValuePeriod"],
  ["The period end is provably before its start. Retain and correct the draft.", "Editor.PlanningValueReversed"],
  ["Undated timing cannot contain values or bounds.", "Editor.PlanningValueUndated"],
  ["Choose a supported bound state.", "Editor.PlanningValueBound"],
  ["Only a known bound contains a value.", "Editor.PlanningValueBoundKnown"],
  ["Supply civil components at the selected precision.", "Editor.PlanningValueCivil"],
  ["Supply a valid month or date without a clock zone.", "Editor.PlanningValueDate"],
  ["Choose month, date or minute date-time precision.", "Editor.PlanningValuePrecision"],
  ["Supply a valid date and minute clock, without seconds.", "Editor.PlanningValueClock"],
  ["An occurrence offset requires an explicit zone.", "Editor.PlanningValueOffsetZone"],
  ["Choose UTC or a supported IANA zone.", "Editor.PlanningValueZone"],
  ["This clock falls in a zone gap; choose a valid civil time.", "Editor.PlanningValueGap"],
  ["The selected occurrence offset does not match this zone and clock.", "Editor.PlanningValueOccurrence"],
  ["Check setup exceeds its bounded scope; narrow the selection.", "Editor.PlanningCheckScope"],
  ["Choose supported check interpretations and nonnegative elapsed minutes.", "Editor.PlanningCheckInterpretation"],
  ["Choose distinct movement entries, supported treatment and nonnegative finite minutes.", "Editor.PlanningCheckMovementInput"],
]);

/** Only checked first-party explanations may appear in the Planning editor and the Check panel. */
export const planningValidationText = (text: unknown, fallback: string): string => {
  if (typeof text !== 'string') return fallback;
  const key = validation.get(text);
  return key ? message(key, text) : fallback;
};
