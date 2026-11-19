import { locale, message } from '../../../../wwwroot/js/localization/messages.js';

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
]);

/** Presents known server findings independently of the existing action and comparison rules. */
export const planningFindingText = (text: string): string => {
  const key = identities.get(text);
  if (key) return message(key, text);
  if (locale === 'en-US') return text;
  return message('Editor.CheckDetailFallback', 'Review the listed entries and check assumptions for this result.');
};
