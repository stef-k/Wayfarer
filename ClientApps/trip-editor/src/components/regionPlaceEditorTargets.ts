import type { EditorTarget } from '../composables/useEditorSurface';
import { message } from '../../../../wwwroot/js/localization/messages.js';
import { regionDisplayName } from '../regionDisplayName.js';
import type { EditorArea, EditorPlace, EditorRegion, EditorSegment } from '../types';

export const regionDraftKey = 'region-draft';
export const placeDraftKey = 'place-draft';
export const areaDraftKey = 'area-draft';
export const segmentDraftKey = 'segment-draft';

export function buildRegionCreateTarget(): EditorTarget {
  return {
    key: regionDraftKey,
    identity: 'region:add',
    kind: 'region',
    mode: 'add',
    title: message('Editor.AddRegion', 'Add Region'),
    subtitle: message('Editor.NewRegion', 'New region')
  };
}

export function buildRegionEditTarget(region: EditorRegion): EditorTarget {
  return {
    key: regionDraftKey,
    identity: `region:edit:${region.id}`,
    kind: 'region',
    mode: 'edit',
    title: message('Editor.EditNamedRegion', 'Edit Region - {0}', region.name),
    subtitle: message('Editor.RegionDetails', 'Region details'),
    entityId: region.id
  };
}

export function buildPlaceCreateTarget(region: EditorRegion): EditorTarget {
  return {
    key: placeDraftKey,
    identity: `place:add:${region.id}`,
    kind: 'place',
    mode: 'add',
    title: message('Editor.AddPlace', 'Add Place'),
    subtitle: regionDisplayName(region),
    parentRegionId: region.id
  };
}

export function buildPlaceEditTarget(place: EditorPlace, subtitle?: string): EditorTarget {
  return {
    key: placeDraftKey,
    identity: `place:edit:${place.id}`,
    kind: 'place',
    mode: 'edit',
    title: message('Editor.EditNamedPlace', 'Edit Place - {0}', place.name),
    subtitle,
    entityId: place.id,
    parentRegionId: place.regionId
  };
}

export function buildAreaCreateTarget(region: EditorRegion): EditorTarget {
  return {
    key: areaDraftKey,
    identity: `area:add:${region.id}`,
    kind: 'area',
    mode: 'add',
    title: message('Editor.AddArea', 'Add Area'),
    subtitle: region.name,
    parentRegionId: region.id
  };
}

export function buildAreaEditTarget(area: EditorArea, subtitle?: string): EditorTarget {
  return {
    key: areaDraftKey,
    identity: `area:edit:${area.id}`,
    kind: 'area',
    mode: 'edit',
    title: message('Editor.EditNamedArea', 'Edit Area - {0}', area.name),
    subtitle,
    entityId: area.id,
    parentRegionId: area.regionId
  };
}

export function buildSegmentCreateTarget(): EditorTarget {
  return {
    key: segmentDraftKey,
    identity: 'segment:add',
    kind: 'segment',
    mode: 'add',
    title: message('Editor.AddSegment', 'Add Segment'),
    subtitle: message('Editor.NewSegment', 'New segment')
  };
}

export function buildSegmentEditTarget(segment: EditorSegment, title: string): EditorTarget {
  return {
    key: segmentDraftKey,
    identity: `segment:edit:${segment.id}`,
    kind: 'segment',
    mode: 'edit',
    title: message('Editor.EditNamedSegment', 'Edit Segment - {0}', title),
    subtitle: message('Editor.TripSegment', 'Trip segment'),
    entityId: segment.id
  };
}
