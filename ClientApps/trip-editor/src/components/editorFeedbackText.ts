import { message } from '../../../../wwwroot/js/localization/messages.js';
import { EditorMutationHttpError, EditorSegmentConflictError, EditorValidationError } from '../api/tripEditorApi';
import { StaleStructureDraft } from '../recovery/structureCustody';

/** Localizes the displayed status while existing styling and workflow checks retain their inputs. */
export const editorStatusText = (value: string): string => {
  const known: Record<string, string> = {
    'Saving...': 'Editor.Saving',
    'Saving order...': 'Editor.SavingOrder',
    'Save failed': 'Editor.SaveFailed',
    'Unsaved changes': 'Editor.UnsavedChanges',
    'Place saved': 'Editor.PlaceSaved',
    'Saved': 'Editor.Saved',
    'Planning unsaved': 'Editor.PlanningUnsaved',
    'Planning save pending': 'Editor.PlanningPending',
    'Planning save not confirmed': 'Editor.PlanningUnconfirmed',
    'Deleted target Planning retained': 'Editor.PlanningDeleted'
  };
  if (known[value]) return message(known[value], value);
  if (value.endsWith(' with warning')) return message('Editor.SavedWithWarning', '{0} with warning', editorStatusText(value.slice(0, -13)));
  if (value.startsWith('Place saved ')) return message('Editor.PlaceSavedAt', 'Place saved {0}', value.slice(12));
  if (value.startsWith('Saved ')) return message('Editor.SavedAt', 'Saved {0}', value.slice(6));
  return value;
};

/** Describes the current editor mode around the localized kind, without relabelling authored names. */
export const editorModeText = (kind: string, mode: string): string => {
  const label = message(`Editor.Entity${kind[0].toUpperCase()}${kind.slice(1)}`, kind);
  return mode === 'add' ? message('Editor.NewEntity', 'New {0}', label) : message('Editor.EditingEntity', 'Editing {0}', label);
};

/** Maps recognized request failures without changing which attempts remain unconfirmed. */
export const editorErrorText = (error: unknown, fallback: string): string => {
  if (error instanceof StaleStructureDraft)
    return message('Editor.StructureStaleDraft', 'This draft is out of date. Review the retained draft in Trip save recovery before trying again.');
  if (error instanceof EditorValidationError) return message('Editor.InvalidForm', 'Review the highlighted fields before saving.');
  if (error instanceof EditorSegmentConflictError)
    return message('Editor.SegmentConflictReview', 'The saved Segment or its route changed. Keep editing to retain your draft, or reload the current saved Segment below.');
  const detail = error instanceof Error ? operationDetails.get(error.message) : undefined;
  if (detail && error instanceof Error) return message(detail, error.message);
  if (error instanceof EditorMutationHttpError) {
    switch (error.status) {
      case 401:
      case 403:
        return message('Editor.SessionEnded', 'Your session has ended or your account is disabled. Sign in again. Keep any unsaved changes before leaving this page.');
      case 404:
        return message('Editor.ChangeUnavailable', 'This Trip or item is no longer available.');
      case 405:
        return message('Editor.ChangeReloadEditor', 'This editor action is unavailable. Keep any unsaved changes, then reload the editor before trying again.');
      case 429:
        return message('Editor.ChangeRateLimited', 'Too many requests. Keep your draft and try again later.');
      case 409:
        return message('Editor.Conflict', 'Your changes could not be saved because the Trip changed. Review the current version before trying again.');
    }
  }
  if (!(error instanceof Error)) return fallback;
  return message('Editor.ChangeUnconfirmed', 'The change could not be confirmed. Keep your draft and review the current state before trying again.');
};

const operationDetails = new Map<string, string>([
  ["Trip fields changed on another device. Your draft is retained. Review current Trip values before choosing a new base and saving.", "Editor.MetadataConflict"],
  ["The earlier Segment save has an unknown outcome. Keep your draft and explicitly review current saved state before saving again.", "Editor.SegmentEarlierUnknown"],
  ["Route edits began during the earlier Segment save. Keep your route draft and explicitly review/reset current saved state before saving.", "Editor.SegmentRouteHeld"],
  ["Original Segment Notes are unavailable. Keep your draft and reload before saving.", "Editor.SegmentNotesOriginal"],
  ["Original Segment evidence is unavailable. Keep your draft and reload before saving.", "Editor.SegmentOriginal"],
  ["The earlier Region order may have committed. Keep your order; refresh/reset cannot prove noncommit. Do not replay it. Explicitly leave without saving if you choose to abandon this page.", "Editor.RegionOrderHeld"],
  ["The earlier Region order has an unknown outcome. Keep your draft; do not replay it.", "Editor.RegionOrderUnknown"],
  ["The Region order could not be confirmed and may have committed. Your intent is retained. Do not replay it; refresh/reset cannot prove noncommit. Explicitly leave without saving to abandon this page.", "Editor.RegionOrderUnconfirmed"],
  ["The earlier Region creation has an unknown outcome and may have committed. Keep your draft. A refresh cannot prove noncommit; do not replay this creation.", "Editor.RegionEarlierCreate"],
  ["The earlier Region save has an unknown outcome. Keep your draft, review current state and explicitly reset before another attempt.", "Editor.RegionEarlierSave"],
  ["An earlier Region deletion has an unresolved outcome. Reconcile it before changing Regions.", "Editor.RegionEarlierDeletion"],
  ["Original Region membership is unavailable. Explicitly review a new starting view.", "Editor.RegionCreationOriginal"],
  ["The original Region order is unavailable. Keep the order and explicitly review a fresh starting view.", "Editor.RegionOrderOriginal"],
  ["Outcome unknown — refresh to inspect the current Trip.", "Editor.RegionDeleteUnknown"],
  ["Preview expired. Refresh the preview and confirm again.", "Editor.RegionPreviewExpired"],
  ["Browser storage is unavailable. Enable storage before confirming this operation.", "Editor.RegionPreviewStorage"],
  ["Request rejected. Refresh the preview and confirm again.", "Editor.RegionPreviewRejected"],
  ["Preview unavailable. Try again.", "Editor.RegionPreviewUnavailable"],
  ["The earlier Place creation has an unknown outcome and may have committed. Keep your draft. A refresh cannot prove noncommit; do not replay this creation.", "Editor.PlaceEarlierCreate"],
  ["The earlier Place save has an unknown outcome. Keep your draft, review current state and explicitly reset before another attempt.", "Editor.PlaceEarlierSave"],
  ["Original creation destination is unavailable. Keep the draft and explicitly review a new starting view.", "Editor.PlaceCreationOriginal"],
  ["The earlier Place operation has an unknown outcome. Keep it held without retry. Abandoning this page does not undo a request.", "Editor.PlaceDeleteHeld"],
  ["Place deletion outcome unknown. Refresh to inspect current facts; keep this attempt held without retry. Leaving does not undo the request.", "Editor.PlaceDeleteUnknown"],
  ["The earlier Place operation has an unknown outcome. Do not replay this order.", "Editor.PlaceOrderHeld"],
  ["Reload the editor to verify your account.", "Editor.EditorReloadAccount"],
  ["Saved Place coordinates changed. Reload the current saved Segment before reopening route work.", "Editor.SegmentRouteAnchorsChanged"],
  ["The earlier Area save has an unknown outcome. Keep your draft, review current state and explicitly reset before another attempt.", "Editor.AreaEarlierSave"],
  ["Original Area parent is unavailable. Keep your draft and reload before saving.", "Editor.AreaOriginalParent"],
  ["Original Area values are unavailable. Keep your draft and reload before saving.", "Editor.AreaOriginalValues"],
  ["Original Place parent is unavailable. Keep your draft and reload before saving.", "Editor.PlaceOriginalParent"],
  ["Original Place values are unavailable. Keep your draft and reload before saving.", "Editor.PlaceOriginalValues"],
  ["Original Region values are unavailable. Keep your draft and reload before saving.", "Editor.RegionOriginalValues"],
  ["Original Trip values are unavailable. Keep your draft and reload before saving.", "Editor.TripOriginalValues"],
  ["The Area save acknowledgement is incomplete. Keep your draft and review the unknown outcome.", "Editor.AreaSaveIncomplete"],
  ["The Segment save acknowledgement is incomplete. Keep your draft and review the unknown outcome.", "Editor.SegmentSaveIncomplete"],
  ["The Segment Notes save acknowledgement is incomplete. Keep your draft and review the unknown outcome.", "Editor.SegmentNotesSaveIncomplete"],
  ["The edited Region fields changed. Keep your draft, review current values and explicitly choose a new base before saving.", "Editor.RegionBaseStale"],
  ["The Area parent changed. Keep your draft and review its current Region.", "Editor.AreaParentChanged"],
  ["The Place parent changed. Keep your draft and review its current Region.", "Editor.PlaceParentChanged"],
  ["The Segment parent changed. Keep your draft and review its current Trip.", "Editor.SegmentParentChanged"],
  ["The Region scope changed. Keep your draft and review its current parent.", "Editor.RegionScopeChanged"],
  ["The edited Area fields changed. Keep your draft, review current values and explicitly choose a new base before saving.", "Editor.AreaBaseChanged"],
  ["The edited Place fields changed. Keep your draft, review current values and explicitly choose a new base before saving.", "Editor.PlaceBaseChanged"],
  ["Segment Notes changed. Keep your draft, review current Notes and explicitly choose a new base before saving.", "Editor.SegmentNotesChanged"],
  ["The original Place, order or dependencies changed. Keep your draft and explicitly review a new base.", "Editor.PlaceDependencyChanged"],
  ["Keep your draft. Review the prepared dependency base before applying this operation.", "Editor.PlaceDependencyReview"],
  ["Review the current deletion preview and explicitly confirm.", "Editor.PlaceDeletionConfirm"],
  ["The preview expired or the Place, ordering or dependencies changed. Review a fresh preview and confirm again.", "Editor.PlaceDeletionRefresh"],
  ["Review and confirm the current consequences.", "Editor.RegionDeletionConfirm"],
  ["The preview expired or the Trip contents changed. Get a fresh preview and confirm again.", "Editor.RegionDeletionRefresh"],
  ["The reserved Region cannot be edited by this operation.", "Editor.RegionReserved"],
  ["Unassigned Places cannot be deleted.", "Editor.RegionReservedDelete"],
  ["Places in Unassigned Places cannot be reordered from this editor action.", "Editor.PlaceReservedOrder"],
  ["Trip or Region not found.", "Editor.RegionOrTripUnavailable"],
  ["Route work requires located From and To anchors.", "Editor.RouteWorkNeedsAnchors"],
  ["The saved custom route is malformed. Reload or repair the Segment before editing its route.", "Editor.RouteWorkMalformed"],
  ["The custom route is missing a required endpoint anchor.", "Editor.RouteWorkMissingEndpoint"],
  ["The custom route has a missing waypoint index.", "Editor.RouteWorkMissingIndex"],
  ["The custom route has an invalid waypoint index order.", "Editor.RouteWorkIndexOrder"],
  ["A custom route anchor no longer matches its saved Place. Reload before route editing.", "Editor.RouteWorkAnchorStale"],
  ["The custom route start no longer matches its saved Place.", "Editor.RouteWorkStartStale"],
  ["The custom route end no longer matches its saved Place.", "Editor.RouteWorkEndStale"],
  ["Waypoint identity and route-index state do not match. Reload before route editing.", "Editor.RouteWorkIndexStale"],
  ["Waypoint route state is stale. Reload before route editing.", "Editor.RouteWorkWaypointStale"],
  ["Waypoint routes require From and To Places.", "Editor.RouteWorkWaypointsNeedEndpoints"],
  ["A route anchor is missing its saved Place location.", "Editor.RouteWorkMissingLocation"],
]);

/** Formats first-party text held by legacy state owners; unknown diagnostics stay out of the UI. */
export const editorOperationText = (text: string, fallback: string): string => {
  const key = operationDetails.get(text);
  return key ? message(key, text) : fallback;
};

/** Mutation warnings are successful writes; the suggestion explains what was kept. */
export const editorWarningText = (code: string): string => {
  if (code === 'reverse-geocode-unavailable') return message('Editor.AddressManualSaved', 'Reverse geocoding was unavailable; the manual address value was saved.');
  if (code === 'share-progress-disabled') return message('Editor.SharingDisabledPrivate', 'Share progress was disabled because the trip is private.');
  return message('Editor.SavedReviewWarnings', 'The change was saved with a warning. Review the saved values before continuing.');
};
