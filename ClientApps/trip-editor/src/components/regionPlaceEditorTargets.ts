import type { EditorTarget } from '../composables/useEditorSurface';
import type { EditorArea, EditorPlace, EditorRegion } from '../types';

export const regionDraftKey = 'region-draft';
export const placeDraftKey = 'place-draft';
export const areaDraftKey = 'area-draft';

export function buildRegionCreateTarget(): EditorTarget {
  return {
    key: regionDraftKey,
    identity: 'region:add',
    kind: 'region',
    mode: 'add',
    title: 'Add Region',
    subtitle: 'New region'
  };
}

export function buildRegionEditTarget(region: EditorRegion): EditorTarget {
  return {
    key: regionDraftKey,
    identity: `region:edit:${region.id}`,
    kind: 'region',
    mode: 'edit',
    title: `Edit Region - ${region.name}`,
    subtitle: 'Region details',
    entityId: region.id
  };
}

export function buildPlaceCreateTarget(region: EditorRegion): EditorTarget {
  return {
    key: placeDraftKey,
    identity: `place:add:${region.id}`,
    kind: 'place',
    mode: 'add',
    title: 'Add Place',
    subtitle: region.name,
    parentRegionId: region.id
  };
}

export function buildPlaceEditTarget(place: EditorPlace, subtitle?: string): EditorTarget {
  return {
    key: placeDraftKey,
    identity: `place:edit:${place.id}`,
    kind: 'place',
    mode: 'edit',
    title: `Edit Place - ${place.name}`,
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
    title: 'Add Area',
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
    title: `Edit Area - ${area.name}`,
    subtitle,
    entityId: area.id,
    parentRegionId: area.regionId
  };
}
