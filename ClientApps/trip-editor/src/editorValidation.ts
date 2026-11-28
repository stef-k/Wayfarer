import { message, number } from '../../../wwwroot/js/localization/messages.js';

const validationDetails = new Map<string, string>([
  ["Icon name must be one of the editor options.", "Editor.ValidationIcon"],
  ["Marker color must be one of the editor options.", "Editor.ValidationMarker"],
  ["Reverse geocoding requires a location.", "Editor.ValidationAddressLocation"],
  ["Notes cannot contain data image sources.", "Editor.ValidationNoteImages"],
  ["Notes images must use external image URLs, not data:image sources.", "Editor.ValidationNoteImages"],
  ["The reserved shadow region name cannot be used.", "Editor.ValidationReservedRegion"],
  ["Cover image URL must be an absolute HTTP or HTTPS URL.", "Editor.ValidationCoverUrl"],
  ["Zoom must be an integer from 0 through 19.", "Editor.ValidationZoom"],
  ["Manual duration is required.", "Editor.ValidationManualDuration"],
  ["Duration source must be Automatic or Manual.", "Editor.ValidationDurationSource"],
  ["Reload the editor before saving; estimated duration source is required.", "Editor.ValidationDurationReload"],
  ["Title must be a string of at most 200 characters, or null.", "Editor.ValidationTitleLength"],
  ["Title must contain at most 200 characters.", "Editor.ValidationTitleLength"],
  ["Original route evidence is required.", "Editor.ValidationRouteEvidence"],
  ["Unsupported Segment route contract. Reload the editor.", "Editor.ValidationRouteContract"],
  ["Waypoint IDs and route vertex indices must have matching lengths.", "Editor.ValidationWaypointLengths"],
  ["The route vertex index must be null when route is null.", "Editor.ValidationWaypointNull"],
  ["The route vertex index must be an integer or null.", "Editor.ValidationWaypointIndex"],
  ["A custom route requires the waypoint index.", "Editor.ValidationWaypointRequiredIndex"],
  ["Waypoint indices must be strictly increasing.", "Editor.ValidationWaypointIncreasing"],
  ["The waypoint index is outside the route interior.", "Editor.ValidationWaypointInterior"],
  ["Waypoint place is duplicated.", "Editor.ValidationWaypointDuplicate"],
  ["A waypoint cannot duplicate an endpoint.", "Editor.ValidationWaypointEndpoint"],
  ["A From place is required when waypoints exist.", "Editor.ValidationFromRequired"],
  ["A To place is required when waypoints exist.", "Editor.ValidationToRequired"],
  ["The anchor requires a location.", "Editor.ValidationAnchorLocation"],
  ["The waypoint anchor requires a location.", "Editor.ValidationWaypointLocation"],
  ["Fill color must be a #RRGGBB hex color.", "Editor.ValidationAreaColor"],
  ["LineString must contain at least two positions.", "Editor.ValidationRouteSize"],
  ["Coordinates must use longitude -180..180 and latitude -90..90.", "Editor.ValidationCoordinateBounds"],
  ["Polygon must include an exterior ring.", "Editor.ValidationAreaExterior"],
  ["Every polygon ring must be closed.", "Editor.ValidationAreaClosed"],
  ["Every polygon ring must contain at least four positions.", "Editor.ValidationAreaSize"],
  ["Share progress can only be enabled for public trips.", "Editor.ValidationSharingPublic"],
  ["One or more tags are invalid.", "Editor.ValidationTags"],
  ["Check the contribution policy and complete readable credits.", "Editor.ValidationCredits"],
  ["The route proposal cannot be saved. Discard it or explicitly generate a new proposal.", "Editor.ProposalRegenerate"],
  ["The pending proposal is malformed. Discard or regenerate it before saving.", "Editor.ProposalMalformed"],
  ["Waypoint place is not in this Trip or is unavailable.", "Editor.ValidationWaypointOwner"],
  ["From place must belong to this trip.", "Editor.ValidationFromTrip"],
  ["To place must belong to this trip.", "Editor.ValidationToTrip"],
  ["Waypoint place was not found in this Trip.", "Editor.ValidationWaypointOwner"],
  ["The indexed route coordinate does not match the waypoint anchor.", "Editor.ValidationWaypointCoordinates"],
  ["The route coordinate does not match the endpoint anchor.", "Editor.ValidationEndpointCoordinates"],
  ["Tag value cannot be blank.", "Editor.ValidationTagBlank"],
  ["Tag uses unsupported characters or is too long.", "Editor.ValidationTagFormat"],
  ["Tag cannot be empty.", "Editor.ValidationTagBlank"],
  ["Tag too long (max 64 characters).", "Editor.ValidationTagFormat"],
  ["Tags may include letters, numbers, spaces, hyphen, apostrophe.", "Editor.ValidationTagFormat"],
  ["Search query is required.", "Editor.ValidationSearchRequired"],
  ["Search query must be at least 3 characters.", "Editor.ValidationSearchMinimum"],
  ["Search query must be at most 200 characters.", "Editor.ValidationSearchMaximum"],
  ["Geometry must be a GeoJSON Polygon object.", "Editor.ValidationAreaGeoJson"],
  ["Route must be a GeoJSON LineString object or null.", "Editor.ValidationRouteGeoJson"],
  ["Every position must contain finite numeric longitude and latitude.", "Editor.ValidationRouteNumeric"],
  ["Authored Notes require the exact nullable original.", "Editor.ValidationNotesOriginal"],
]);

/** Preserves field bindings and maps known legacy validation text only at the Web presentation boundary. */
export const editorFieldError = (error: string): string => {
  const fields: Record<string, string> = {
    'Name is required.': 'Editor.NameRequired',
    'Name must contain 1 through 200 characters.': 'Editor.NameLength',
    'The field is required.': 'Editor.FieldRequired',
    'Latitude must be between -90 and 90.': 'Editor.LatitudeMustBeBetween90And90',
    'Longitude must be between -180 and 180.': 'Editor.LongitudeMustBeBetween180And180'
  };
  const key = validationDetails.get(error) ?? fields[error];
  if (key) return message(key, error);
  const tagLimit = /^(?:A trip can have up to|You can add up to) (\d+) tags(?: per trip)?\.$/.exec(error);
  if (tagLimit) return message('Editor.ValidationTagLimit', 'A trip can have up to {0} tags.', number(Number(tagLimit[1])));
  return message('Editor.InvalidField', 'Review the value in this field.');
};

/** Uses the existing proposal rejection code while leaving validation keys and retry behavior intact. */
export const editorProposalError = (code: string | undefined): string | null => {
  switch (code) {
    case 'route-proposal-invalid-or-expired':
      return message('Editor.ProposalExpired', 'This route proposal is invalid or has expired. Discard it or generate a new proposal before saving.');
    case 'route-proposal-stale':
      return message('Editor.ProposalStale', 'Route details changed since this proposal was generated. Discard it or generate a new proposal before saving.');
    default:
      return null;
  }
};
