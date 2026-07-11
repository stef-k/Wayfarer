import type { EntityType, Guid, TripViewerState, ViewerNotes, ViewerPlace, ViewerSegment } from './types';
import { buildRegionGroups, buildSegmentSummaries, distanceDisplay, durationDisplay, orderedTags, segmentModeLabel, segmentTitle } from './viewModel';

export interface SearchResult {
  key: string;
  type: EntityType | 'tag' | 'notes' | 'address';
  label: string;
  context: string;
  selection: { type: EntityType; id: Guid };
}

interface SearchDocument {
  key: string;
  type: SearchResult['type'];
  label: string;
  context: string;
  selection: { type: EntityType; id: Guid };
  haystack: string;
}

export function searchViewerState(state: TripViewerState, query: string): SearchResult[] {
  const tokens = tokenize(query);
  if (tokens.length === 0) return [];

  return buildSearchDocuments(state)
    .filter(document => tokens.every(token => document.haystack.includes(token)))
    .map(({ haystack: _haystack, ...result }) => result);
}

export function hasSearchQuery(query: string): boolean {
  return tokenize(query).length > 0;
}

function buildSearchDocuments(state: TripViewerState): SearchDocument[] {
  const documents: SearchDocument[] = [];
  const tripSelection = { type: 'trip' as const, id: state.trip.id };
  const tripNotes = notesText(state.trip.notes);

  documents.push(document('trip:summary', 'trip', state.trip.name, 'Trip summary', tripSelection, [
    state.trip.name,
    tripNotes,
    orderedTags(state).map(tag => tag.name).join(' ')
  ]));

  orderedTags(state).forEach(tag => {
    documents.push(document(`tag:${tag.slug}`, 'tag', tag.name, 'Trip tag', tripSelection, [tag.name]));
  });

  buildRegionGroups(state).forEach(group => {
    const regionSelection = { type: 'region' as const, id: group.region.id };
    documents.push(document(`region:${group.region.id}`, 'region', group.region.name, 'Region', regionSelection, [
      group.region.name,
      notesText(group.region.notes)
    ]));

    group.places.forEach(place => {
      const placeSelection = { type: 'place' as const, id: place.id };
      documents.push(document(`place:${place.id}`, 'place', place.name, `Place in ${group.region.name}`, placeSelection, [
        place.name,
        place.address,
        coordinateSearchLabel(place),
        notesText(place.notes)
      ]));

      if (place.address) {
        documents.push(document(`address:${place.id}`, 'address', place.address, `Address for ${place.name}`, placeSelection, [place.address]));
      }
    });

    group.areas.forEach(area => {
      documents.push(document(`area:${area.id}`, 'area', area.name, `Area in ${group.region.name}`, { type: 'area', id: area.id }, [
        area.name,
        notesText(area.notes)
      ]));
    });
  });

  buildSegmentSummaries(state).forEach(summary => {
    documents.push(document(`segment:${summary.segment.id}`, 'segment', segmentTitle(summary), segmentContext(summary.segment), { type: 'segment', id: summary.segment.id }, [
      segmentTitle(summary),
      segmentModeLabel(summary.segment.mode),
      distanceDisplay(summary.segment.estimatedDistanceKm).compact ?? '',
      durationDisplay(summary.segment.estimatedDurationMinutes).compact ?? '',
      notesText(summary.segment.notes)
    ]));
  });

  return documents;
}

function document(
  key: string,
  type: SearchResult['type'],
  label: string,
  context: string,
  selection: { type: EntityType; id: Guid },
  values: string[]
): SearchDocument {
  return { key, type, label, context, selection, haystack: normalize(values.filter(Boolean).join(' ')) };
}

function notesText(notes: ViewerNotes): string {
  return notes.hasTextContent ? notes.plainText : '';
}

function coordinateSearchLabel(place: ViewerPlace): string {
  return place.location ? `${place.location.latitude.toFixed(5)} ${place.location.longitude.toFixed(5)}` : '';
}

function segmentContext(segment: ViewerSegment): string {
  const pieces = ['Segment'];
  const mode = segmentModeLabel(segment.mode);
  const distance = distanceDisplay(segment.estimatedDistanceKm).compact;
  const duration = durationDisplay(segment.estimatedDurationMinutes).compact;
  if (mode) pieces.push(mode);
  if (distance) pieces.push(distance);
  if (duration) pieces.push(duration);
  return pieces.join(' · ');
}

function tokenize(value: string): string[] {
  return normalize(value).split(/\s+/).filter(Boolean);
}

function normalize(value: string): string {
  return value
    .normalize('NFD')
    .replace(/\p{Diacritic}/gu, '')
    .toLocaleLowerCase();
}
