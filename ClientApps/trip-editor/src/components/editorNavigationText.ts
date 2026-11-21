import { message } from '../../../../wwwroot/js/localization/messages.js';
import type { EditorTarget, EditorTargetKind } from '../composables/useEditorSurface';
import type { FocusActiveEntityResult } from '../map/mapNavigation';

/** Describes the focus result announced by the map toolbar. */
export function focusStatusText(result: FocusActiveEntityResult, target: EditorTarget | null): string {
  switch (result) {
    case 'missing-target': return message('Editor.FocusMissingTarget', 'No active target to focus');
    case 'unsupported-target': return message('Editor.FocusUnsupportedTarget', 'Active target cannot be focused');
    case 'no-geometry': return message('Editor.FocusNoGeometry', 'No geometry to focus');
  }

  switch (target?.kind) {
    case 'metadata': return message('Editor.FocusedTripMap', 'Focused trip map');
    case 'region': return message('Editor.FocusedRegion', 'Focused region');
    case 'place': return target.mode === 'add'
      ? message('Editor.FocusedParentRegion', 'Focused parent region')
      : message('Editor.FocusedPlace', 'Focused place');
    case 'area': return message('Editor.FocusedArea', 'Focused area');
    case 'segment': return message('Editor.FocusedSegment', 'Focused segment');
    default: return message('Editor.FocusedEntity', 'Focused active entity');
  }
}

/** Uses the same whole discard phrases for manual and selection-driven tab switches. */
export function discardBeforeSwitchingTabs(kind: EditorTargetKind): string {
  switch (kind) {
    case 'metadata': return message('Editor.DiscardTripBeforeTab', 'Discard unsaved trip changes before switching tabs?');
    case 'region': return message('Editor.DiscardRegionBeforeTab', 'Discard unsaved region changes before switching tabs?');
    case 'place': return message('Editor.DiscardPlaceBeforeTab', 'Discard unsaved place changes before switching tabs?');
    case 'area': return message('Editor.DiscardAreaBeforeTab', 'Discard unsaved area changes before switching tabs?');
    case 'segment': return message('Editor.DiscardSegmentBeforeTab', 'Discard unsaved segment changes before switching tabs?');
  }
}

/** Names the draft that a different Place selection would close. */
export function discardBeforeSelectingPlace(kind: EditorTargetKind): string {
  switch (kind) {
    case 'metadata': return message('Editor.DiscardTripBeforePlace', 'Discard unsaved trip changes before selecting a Place?');
    case 'region': return message('Editor.DiscardRegionBeforePlace', 'Discard unsaved region changes before selecting a Place?');
    case 'place': return message('Editor.DiscardPlaceBeforePlace', 'Discard unsaved place changes before selecting another place?');
    case 'area': return message('Editor.DiscardAreaBeforePlace', 'Discard unsaved area changes before selecting a Place?');
    case 'segment': return message('Editor.DiscardSegmentBeforePlace', 'Discard unsaved segment changes before selecting a Place?');
  }
}
