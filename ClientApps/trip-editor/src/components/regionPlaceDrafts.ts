import type { EditorCoordinate, EditorPlace, EditorPlaceDraft, EditorPlaceSaveRequest, EditorRegion, EditorRegionSaveRequest } from '../types';

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
    notesHtml: region.notesHtml,
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
    notesHtml: value.notesHtml,
    coverImage: coverImageRawUrl ? { rawUrl: coverImageRawUrl } : null,
    center: hasPartialCenter ? { latitude: latitude ? Number(latitude) : Number.NaN, longitude: longitude ? Number(longitude) : Number.NaN } : null
  };
}

export function emptyPlaceDraft(regionId: string | null = null): EditorPlaceDraft {
  return { id: null, regionId, name: '', notesHtml: '', address: '', latitude: '', longitude: '', iconName: '', markerColor: '', reverseGeocode: false };
}

export function toPlaceDraft(place: EditorPlace | null, fallbackRegionId: string | null): EditorPlaceDraft {
  if (!place) {
    return emptyPlaceDraft(fallbackRegionId);
  }

  return {
    id: place.id,
    regionId: place.regionId,
    name: place.name,
    notesHtml: place.notesHtml,
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
    notesHtml: value.notesHtml,
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

function coordinateText(coordinate: EditorCoordinate | null, key: keyof EditorCoordinate): string {
  return coordinate ? String(coordinate[key]) : '';
}

/// Normalizes Vue number-input values before validation and API serialization.
function draftText(value: string | number): string {
  return String(value ?? '').trim();
}
