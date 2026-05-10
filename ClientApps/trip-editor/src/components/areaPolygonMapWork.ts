import type { EditorSurfaceController } from '../composables/useEditorSurface';
import type { AreaPolygonWorkOptions } from '../map/leafletAdapter';
import type { EditorAreaDraft, GeoJsonPolygon } from '../types';

export type AreaPolygonEditor = {
  startAreaPolygonWork: (options: AreaPolygonWorkOptions) => () => void;
};

type AreaGeometrySnapshot = {
  geometry: GeoJsonPolygon | null;
};

export type AreaPolygonMapWorkState = {
  geometry: GeoJsonPolygon | null;
  stopEdit: (() => void) | null;
};

/// Starts polygon-only map-work for the active area draft.
export function beginAreaPolygonMapWork(
  draft: EditorAreaDraft,
  editorSurface: EditorSurfaceController,
  polygonEditor: AreaPolygonEditor,
  state: AreaPolygonMapWorkState
): void {
  const snapshot = snapshotAreaGeometry(draft);
  state.geometry = cloneGeometry(snapshot.geometry);
  stopAreaPolygonEdit(state);
  state.stopEdit = polygonEditor.startAreaPolygonWork({
    initialGeometry: state.geometry,
    fillHex: draft.fillHex || '#ff6600',
    onChanged: geometry => {
      state.geometry = cloneGeometry(geometry);
    }
  });

  const entered = editorSurface.enterMapWork({
    modeName: 'Draw area polygon',
    instruction: 'Click the map to place polygon vertices.',
    statusText: () => areaPolygonStatus(state.geometry),
    canFinish: () => hasValidPolygon(state.geometry),
    isDirty: () => !sameGeometry(state.geometry, snapshot.geometry),
    snapshot: () => snapshot,
    rollback: rollbackSnapshot => {
      restoreAreaGeometry(draft, state, rollbackSnapshot as AreaGeometrySnapshot);
    },
    done: () => {
      draft.geometry = cloneGeometry(state.geometry);
      stopAreaPolygonEdit(state);
    },
    cancel: () => {
      stopAreaPolygonEdit(state);
    }
  });
  if (!entered) {
    stopAreaPolygonEdit(state);
  }
}

/// Clears any active adapter-owned temporary polygon/listener.
export function stopAreaPolygonEdit(state: AreaPolygonMapWorkState): void {
  state.stopEdit?.();
  state.stopEdit = null;
}

function snapshotAreaGeometry(draft: EditorAreaDraft): AreaGeometrySnapshot {
  return { geometry: cloneGeometry(draft.geometry) };
}

function restoreAreaGeometry(draft: EditorAreaDraft, state: AreaPolygonMapWorkState, snapshot: AreaGeometrySnapshot): void {
  draft.geometry = cloneGeometry(snapshot.geometry);
  state.geometry = cloneGeometry(snapshot.geometry);
}

function hasValidPolygon(geometry: GeoJsonPolygon | null): boolean {
  return Boolean(geometry?.coordinates?.[0] && geometry.coordinates[0].length >= 4);
}

function sameGeometry(left: GeoJsonPolygon | null, right: GeoJsonPolygon | null): boolean {
  return JSON.stringify(left) === JSON.stringify(right);
}

function areaPolygonStatus(geometry: GeoJsonPolygon | null): string {
  const vertices = Math.max(0, (geometry?.coordinates?.[0]?.length ?? 1) - 1);
  return vertices >= 3 ? `${vertices} vertices ready` : 'No polygon ready';
}

function cloneGeometry<T>(geometry: T): T {
  return geometry ? JSON.parse(JSON.stringify(geometry)) as T : geometry;
}
