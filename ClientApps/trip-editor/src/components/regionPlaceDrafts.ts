import type { EditorArea, EditorAreaDraft, EditorAreaSaveRequest, EditorCoordinate, EditorPlace, EditorPlaceDraft, EditorPlaceSaveRequest, EditorRegion, EditorRegionSaveRequest, EditorSegment, EditorSegmentDraft, EditorSegmentSaveRequest } from '../types';
import { normalizeNotesHtml } from '../notes/notesHtml';

export const defaultPlaceIconName = 'marker';
export const defaultPlaceMarkerColor = 'bg-blue';

export type RegionDraft = {
  id: string | null;
  name: string;
  notesHtml: string;
  coverImageRawUrl: string;
  centerLatitude: string | number;
  centerLongitude: string | number;
};

export function emptyRegionDraft(): RegionDraft {
  return { id: null, name: '', notesHtml: '', coverImageRawUrl: '', centerLatitude: '', centerLongitude: '' };
}

export function toRegionDraft(region: EditorRegion | null): RegionDraft {
  if (!region) {
    return emptyRegionDraft();
  }

  return {
    id: region.id,
    name: region.name,
    notesHtml: normalizeNotesHtml(region.notesHtml),
    coverImageRawUrl: region.coverImage?.rawUrl ?? '',
    centerLatitude: coordinateText(region.center, 'latitude'),
    centerLongitude: coordinateText(region.center, 'longitude')
  };
}

export function buildRegionRequest(value: RegionDraft): EditorRegionSaveRequest {
  const latitude = draftText(value.centerLatitude);
  const longitude = draftText(value.centerLongitude);
  const coverImageRawUrl = draftText(value.coverImageRawUrl);
  const hasPartialCenter = Boolean(latitude || longitude);

  return {
    name: value.name,
    notesHtml: normalizeNotesHtml(value.notesHtml),
    coverImage: coverImageRawUrl ? { rawUrl: coverImageRawUrl } : null,
    center: hasPartialCenter ? { latitude: latitude ? Number(latitude) : Number.NaN, longitude: longitude ? Number(longitude) : Number.NaN } : null
  };
}

export function emptyPlaceDraft(regionId: string | null = null): EditorPlaceDraft {
  return { id: null, regionId, name: '', notesHtml: '', address: '', latitude: '', longitude: '', iconName: defaultPlaceIconName, markerColor: defaultPlaceMarkerColor, reverseGeocode: false };
}

export function toPlaceDraft(place: EditorPlace | null, fallbackRegionId: string | null): EditorPlaceDraft {
  if (!place) {
    return emptyPlaceDraft(fallbackRegionId);
  }

  return {
    id: place.id,
    regionId: place.regionId,
    name: place.name,
    notesHtml: normalizeNotesHtml(place.notesHtml),
    address: place.address,
    latitude: coordinateText(place.location, 'latitude'),
    longitude: coordinateText(place.location, 'longitude'),
    iconName: place.iconName,
    markerColor: place.markerColor,
    reverseGeocode: false
  };
}

export function buildPlaceRequest(value: EditorPlaceDraft): EditorPlaceSaveRequest {
  const latitude = draftText(value.latitude);
  const longitude = draftText(value.longitude);
  const hasLocation = Boolean(latitude || longitude);
  return {
    regionId: value.regionId ?? undefined,
    name: value.name,
    notesHtml: normalizeNotesHtml(value.notesHtml),
    address: value.address || null,
    location: hasLocation ? { latitude: latitude ? Number(latitude) : Number.NaN, longitude: longitude ? Number(longitude) : Number.NaN } : null,
    iconName: value.iconName,
    markerColor: value.markerColor,
    reverseGeocode: value.reverseGeocode
  };
}

export function withoutRegionId(request: EditorPlaceSaveRequest): EditorPlaceSaveRequest {
  const { regionId: _regionId, ...createRequest } = request;
  return createRequest;
}

export function emptyAreaDraft(regionId: string | null = null, fillHex = '#ff6600'): EditorAreaDraft {
  return { id: null, regionId, name: '', notesHtml: '', fillHex, geometry: null };
}

export function toAreaDraft(area: EditorArea | null, fallbackRegionId: string | null, fillHex = '#ff6600'): EditorAreaDraft {
  if (!area) {
    return emptyAreaDraft(fallbackRegionId, fillHex);
  }

  return {
    id: area.id,
    regionId: area.regionId,
    name: area.name,
    notesHtml: normalizeNotesHtml(area.notesHtml),
    fillHex: area.fillHex || fillHex,
    geometry: cloneGeometry(area.geometry)
  };
}

export function buildAreaRequest(value: EditorAreaDraft): EditorAreaSaveRequest {
  return {
    name: value.name,
    notesHtml: normalizeNotesHtml(value.notesHtml),
    fillHex: value.fillHex,
    geometry: cloneGeometry(value.geometry)
  };
}

export function emptySegmentDraft(): EditorSegmentDraft {
  return { id: null, fromPlaceId: null, toPlaceId: null, mode: '', estimatedDistanceKm: '', estimatedDurationMinutes: '', estimatedDurationSource: 'Automatic', notesHtml: '', route: null };
}

export function toSegmentDraft(segment: EditorSegment | null): EditorSegmentDraft {
  if (!segment) {
    return emptySegmentDraft();
  }

  return {
    id: segment.id,
    fromPlaceId: segment.fromPlaceId,
    toPlaceId: segment.toPlaceId,
    mode: segment.mode,
    estimatedDistanceKm: segment.estimatedDistanceKm ?? '',
    estimatedDurationMinutes: segment.estimatedDurationMinutes ?? '',
    estimatedDurationSource: segment.estimatedDurationSource,
    notesHtml: normalizeNotesHtml(segment.notesHtml),
    route: cloneGeometry(segment.route)
  };
}

export function buildSegmentRequest(value: EditorSegmentDraft): EditorSegmentSaveRequest {
  return {
    fromPlaceId: value.fromPlaceId || null,
    toPlaceId: value.toPlaceId || null,
    mode: value.mode || null,
    estimatedDistanceKm: nullableNumber(value.estimatedDistanceKm),
    estimatedDurationMinutes: nullableNumber(value.estimatedDurationMinutes),
    estimatedDurationSource: value.estimatedDurationSource,
    notesHtml: normalizeNotesHtml(value.notesHtml),
    route: cloneGeometry(value.route)
  };
}

function cloneGeometry<T>(geometry: T): T {
  return geometry ? JSON.parse(JSON.stringify(geometry)) as T : geometry;
}

function coordinateText(coordinate: EditorCoordinate | null, key: keyof EditorCoordinate): string {
  return coordinate ? String(coordinate[key]) : '';
}

/// Normalizes Vue number-input values before validation and API serialization.
function draftText(value: string | number): string {
  return String(value ?? '').trim();
}

function nullableNumber(value: string | number): number | null {
  const text = draftText(value);
  return text ? Number(text) : null;
}
