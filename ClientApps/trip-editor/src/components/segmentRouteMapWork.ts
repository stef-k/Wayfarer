import type { EditorSurfaceController } from '../composables/useEditorSurface';
import type { SegmentRouteWorkOptions } from '../map/leafletAdapter';
import type { EditorSegmentDraft, EditorTripState, GeoJsonLineString } from '../types';

export type SegmentRouteEditor = {
  setSegmentRouteWorkRoute: (route: GeoJsonLineString | null) => void;
  startSegmentRouteWork: (options: SegmentRouteWorkOptions) => () => void;
};

type SegmentRouteSnapshot = {
  route: GeoJsonLineString | null;
  workBaseline: GeoJsonLineString | null;
};

export type SegmentRouteMapWorkState = {
  route: GeoJsonLineString | null;
  stopEdit: (() => void) | null;
};

/// Starts route-only map-work for the active segment draft.
export function beginSegmentRouteMapWork(
  identity: string,
  draft: EditorSegmentDraft,
  editorSurface: EditorSurfaceController,
  routeEditor: SegmentRouteEditor,
  state: SegmentRouteMapWorkState,
  editorState: EditorTripState
): void {
  const snapshot = snapshotSegmentRoute(draft, editorState);
  state.route = cloneGeometry(snapshot.workBaseline);
  stopSegmentRouteEdit(state);
  state.stopEdit = routeEditor.startSegmentRouteWork({
    identity,
    initialRoute: state.route,
    initialRouteKind: draft.route === null ? 'fallback' : 'custom',
    onChanged: route => {
      state.route = cloneGeometry(route);
    }
  });

  const entered = editorSurface.enterMapWork({
    modeName: 'Draw segment route',
    instruction: 'Draw or edit one route polyline.',
    statusText: () => segmentRouteStatus(state.route),
    canFinish: () => hasValidRoute(state.route),
    isDirty: () => !sameGeometry(state.route, snapshot.workBaseline),
    snapshot: () => snapshot,
    rollback: rollbackSnapshot => {
      restoreSegmentRoute(draft, state, rollbackSnapshot as SegmentRouteSnapshot);
    },
    clear: () => {
      state.route = null;
      routeEditor.setSegmentRouteWorkRoute(null);
    },
    done: () => {
      draft.route = sameGeometry(state.route, snapshot.workBaseline)
        ? cloneGeometry(snapshot.route)
        : cloneGeometry(state.route);
      stopSegmentRouteEdit(state);
    },
    cancel: () => {
      stopSegmentRouteEdit(state);
    }
  });
  if (!entered) {
    stopSegmentRouteEdit(state);
  }
}

/// Clears any active adapter-owned temporary route/listener.
export function stopSegmentRouteEdit(state: SegmentRouteMapWorkState): void {
  state.stopEdit?.();
  state.stopEdit = null;
}

export function fallbackRoute(draft: Pick<EditorSegmentDraft, 'fromPlaceId' | 'toPlaceId'>, state: EditorTripState): GeoJsonLineString | null {
  const from = draft.fromPlaceId ? state.placesById[draft.fromPlaceId]?.location : null;
  const to = draft.toPlaceId ? state.placesById[draft.toPlaceId]?.location : null;
  return from && to ? { type: 'LineString', coordinates: [[from.longitude, from.latitude], [to.longitude, to.latitude]] } : null;
}

function snapshotSegmentRoute(draft: EditorSegmentDraft, state: EditorTripState): SegmentRouteSnapshot {
  return {
    route: cloneGeometry(draft.route),
    workBaseline: cloneGeometry(draft.route ?? fallbackRoute(draft, state))
  };
}

function restoreSegmentRoute(draft: EditorSegmentDraft, state: SegmentRouteMapWorkState, snapshot: SegmentRouteSnapshot): void {
  draft.route = cloneGeometry(snapshot.route);
  state.route = cloneGeometry(snapshot.route);
}

function hasValidRoute(route: GeoJsonLineString | null): boolean {
  return Boolean(route?.type === 'LineString' && route.coordinates.length >= 2 && route.coordinates.every(([longitude, latitude]) =>
    Number.isFinite(longitude) &&
    Number.isFinite(latitude) &&
    longitude >= -180 &&
    longitude <= 180 &&
    latitude >= -90 &&
    latitude <= 90));
}

function sameGeometry(left: GeoJsonLineString | null, right: GeoJsonLineString | null): boolean {
  return JSON.stringify(left) === JSON.stringify(right);
}

function segmentRouteStatus(route: GeoJsonLineString | null): string {
  const points = route?.coordinates.length ?? 0;
  return points >= 2 ? `Editing route · ${points} route points ready` : 'Editing route · no route ready';
}

function cloneGeometry<T>(geometry: T): T {
  return geometry ? JSON.parse(JSON.stringify(geometry)) as T : geometry;
}
