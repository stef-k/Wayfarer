import type { EditorTarget } from '../composables/useEditorSurface';
import type { EditorPlace, EditorRegion } from '../types';

export const regionDraftKey = 'region-draft';
export const placeDraftKey = 'place-draft';

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
    subtitle: 'Region details'
  };
}

export function buildPlaceCreateTarget(region: EditorRegion): EditorTarget {
  return {
    key: placeDraftKey,
    identity: `place:add:${region.id}`,
    kind: 'place',
    mode: 'add',
    title: 'Add Place',
    subtitle: region.name
  };
}

export function buildPlaceEditTarget(place: EditorPlace, subtitle?: string): EditorTarget {
  return {
    key: placeDraftKey,
    identity: `place:edit:${place.id}`,
    kind: 'place',
    mode: 'edit',
    title: `Edit Place - ${place.name}`,
    subtitle
  };
}
