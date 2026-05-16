import type { EditorSurfaceController } from '../composables/useEditorSurface';
import type { CoordinatePickOptions } from '../map/leafletAdapter';
import type { EditorCoordinate, EditorPlaceDraft, Guid } from '../types';

export type PlaceCoordinatePicker = {
  applyPlaceDraftCoordinate?: (placeId: Guid, coordinate: EditorCoordinate) => void;
  startCoordinatePick: (options: CoordinatePickOptions) => () => void;
};

type PlaceCoordinateSnapshot = {
  latitude: string | number;
  longitude: string | number;
};

export type PlaceCoordinateMapWorkState = {
  coordinate: EditorCoordinate | null;
  stopPick: (() => void) | null;
};

/// Starts coordinate-only map-work for the active place draft.
export function beginPlaceCoordinateMapWork(
  draft: EditorPlaceDraft,
  editorSurface: EditorSurfaceController,
  coordinatePicker: PlaceCoordinatePicker,
  state: PlaceCoordinateMapWorkState
): void {
  const coordinateSnapshot = snapshotPlaceCoordinate(draft);
  state.coordinate = coordinateFromSnapshot(coordinateSnapshot);
  stopPlaceCoordinatePick(state);
  state.stopPick = coordinatePicker.startCoordinatePick({
    initialCoordinate: state.coordinate,
    onPicked: coordinate => {
      state.coordinate = coordinate;
    }
  });

  const entered = editorSurface.enterMapWork({
    modeName: 'Pick place location',
    instruction: 'Click the map to choose this place location.',
    statusText: () => placeCoordinatePickStatus(state.coordinate),
    canFinish: () => isValidCoordinate(state.coordinate),
    isDirty: () => !sameCoordinate(state.coordinate, coordinateFromSnapshot(coordinateSnapshot)),
    snapshot: () => coordinateSnapshot,
    rollback: snapshot => {
      restorePlaceCoordinate(draft, state, snapshot as PlaceCoordinateSnapshot);
    },
    done: () => {
      if (isValidCoordinate(state.coordinate)) {
        draft.latitude = state.coordinate.latitude;
        draft.longitude = state.coordinate.longitude;
        if (draft.id) {
          coordinatePicker.applyPlaceDraftCoordinate?.(draft.id, state.coordinate);
        }
      }
      stopPlaceCoordinatePick(state);
    },
    cancel: () => {
      stopPlaceCoordinatePick(state);
    }
  });
  if (!entered) {
    stopPlaceCoordinatePick(state);
  }
}

/// Clears any active adapter-owned temporary coordinate marker/listener.
export function stopPlaceCoordinatePick(state: PlaceCoordinateMapWorkState): void {
  state.stopPick?.();
  state.stopPick = null;
}

function snapshotPlaceCoordinate(draft: EditorPlaceDraft): PlaceCoordinateSnapshot {
  return { latitude: draft.latitude, longitude: draft.longitude };
}

function restorePlaceCoordinate(draft: EditorPlaceDraft, state: PlaceCoordinateMapWorkState, snapshot: PlaceCoordinateSnapshot): void {
  draft.latitude = snapshot.latitude;
  draft.longitude = snapshot.longitude;
  state.coordinate = coordinateFromSnapshot(snapshot);
}

function coordinateFromSnapshot(snapshot: PlaceCoordinateSnapshot): EditorCoordinate | null {
  const latitudeText = String(snapshot.latitude ?? '').trim();
  const longitudeText = String(snapshot.longitude ?? '').trim();
  if (!latitudeText || !longitudeText) {
    return null;
  }

  const latitude = Number(latitudeText);
  const longitude = Number(longitudeText);
  const coordinate = { latitude, longitude };
  return isValidCoordinate(coordinate) ? coordinate : null;
}

function isValidCoordinate(coordinate: EditorCoordinate | null): coordinate is EditorCoordinate {
  return coordinate !== null &&
    Number.isFinite(coordinate.latitude) &&
    Number.isFinite(coordinate.longitude) &&
    coordinate.latitude >= -90 &&
    coordinate.latitude <= 90 &&
    coordinate.longitude >= -180 &&
    coordinate.longitude <= 180;
}

function sameCoordinate(current: EditorCoordinate | null, snapshot: EditorCoordinate | null): boolean {
  if (!current || !snapshot) {
    return current === snapshot;
  }

  return current.latitude === snapshot.latitude && current.longitude === snapshot.longitude;
}

function placeCoordinatePickStatus(coordinate: EditorCoordinate | null): string {
  if (!isValidCoordinate(coordinate)) {
    return 'No coordinate selected';
  }

  return `Selected ${formatCoordinate(coordinate.latitude)}, ${formatCoordinate(coordinate.longitude)}`;
}

function formatCoordinate(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toFixed(6).replace(/0+$/, '').replace(/\.$/, '');
}
