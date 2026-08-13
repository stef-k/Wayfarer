import type { EditorSurfaceController } from '../composables/useEditorSurface';
import type { SegmentRouteWorkOptions } from '../map/leafletAdapter';
import type { EditorSegmentDraft, EditorTripState } from '../types';
import {
  clearAnonymousNodes,
  cloneSegmentRouteWorkState,
  constructSegmentRouteWorkState,
  insertAnonymousNode,
  moveAnonymousNode,
  projectSegmentRouteWork,
  removeAnonymousNode,
  type RouteCoordinate,
  type SegmentRouteWorkNode,
  type SegmentRouteWorkState
} from './segmentRouteWorkState.ts';

export type SegmentRouteEditor = {
  setSegmentRouteWorkState: (state: SegmentRouteWorkState) => void;
  startSegmentRouteWork: (options: SegmentRouteWorkOptions) => () => void;
};

type SegmentRouteDraftSnapshot = {
  route: EditorSegmentDraft['route'];
  waypointRouteVertexIndices: Array<number | null>;
};

export type SegmentRouteMapWorkLifecycleState = {
  work: SegmentRouteWorkState | null;
  stopEdit: (() => void) | null;
};

export type SegmentRoutePointEditorController = {
  nodes: () => SegmentRouteWorkNode[];
  insertAfter: (key: string) => string | null;
  move: (key: string, coordinate: RouteCoordinate) => boolean;
  remove: (key: string) => boolean;
};

/** Builds the unsaved ordered-anchor fallback used by route summaries. */
export function fallbackRoute(draft: Pick<EditorSegmentDraft, 'fromPlaceId' | 'toPlaceId' | 'waypointRows'>, state: EditorTripState): EditorSegmentDraft['route'] {
  const placeIds = [draft.fromPlaceId, ...draft.waypointRows.map(row => row.placeId), draft.toPlaceId];
  if (placeIds.length < 2 || placeIds.some(id => !id || !state.placesById[id]?.location)) return null;
  return {
    type: 'LineString',
    coordinates: placeIds.map(id => {
      const location = state.placesById[id!].location!;
      return [location.longitude, location.latitude];
    })
  };
}

/** Starts anchor-aware W while retaining an exact immutable pre-work D snapshot. */
export function beginSegmentRouteMapWork(
  identity: string,
  draft: EditorSegmentDraft,
  editorSurface: EditorSurfaceController,
  routeEditor: SegmentRouteEditor,
  lifecycle: SegmentRouteMapWorkLifecycleState,
  editorState: EditorTripState,
  restoreFocus: () => void
): string | null {
  const constructed = constructSegmentRouteWorkState(draft, editorState);
  if (!constructed.ok) return constructed.message;

  const draftSnapshot = snapshotDraft(draft);
  const initialWork = cloneSegmentRouteWorkState(constructed.state);
  stopSegmentRouteEdit(lifecycle);
  lifecycle.work = cloneSegmentRouteWorkState(constructed.state);

  const sync = (): void => {
    if (lifecycle.work) routeEditor.setSegmentRouteWorkState(lifecycle.work);
  };
  const pointEditor: SegmentRoutePointEditorController = {
    nodes: () => lifecycle.work?.nodes ?? [],
    insertAfter: key => {
      if (!lifecycle.work) return null;
      const node = insertAnonymousNode(lifecycle.work, key);
      sync();
      return node?.key ?? null;
    },
    move: (key, coordinate) => {
      if (!lifecycle.work || !moveAnonymousNode(lifecycle.work, key, coordinate)) return false;
      sync();
      return true;
    },
    remove: key => {
      if (!lifecycle.work || !removeAnonymousNode(lifecycle.work, key)) return false;
      sync();
      return true;
    }
  };

  lifecycle.stopEdit = routeEditor.startSegmentRouteWork({
    identity,
    initialState: lifecycle.work,
    onChanged: work => {
      lifecycle.work = cloneSegmentRouteWorkState(work);
    }
  });

  const entered = editorSurface.enterMapWork({
    modeName: 'Edit segment route',
    instruction: 'Saved Place anchors are fixed. Add, move, or remove anonymous route points.',
    statusText: () => segmentRouteStatus(lifecycle.work),
    canFinish: () => Boolean(lifecycle.work && projectSegmentRouteWork(lifecycle.work)),
    isDirty: () => JSON.stringify(lifecycle.work) !== JSON.stringify(initialWork),
    routePointEditor: pointEditor,
    snapshot: () => draftSnapshot,
    rollback: snapshot => restoreDraft(draft, snapshot as SegmentRouteDraftSnapshot),
    clear: () => {
      if (!lifecycle.work) return;
      clearAnonymousNodes(lifecycle.work);
      sync();
    },
    done: () => {
      const projection = lifecycle.work ? projectSegmentRouteWork(lifecycle.work) : null;
      if (!projection) return;
      draft.route = clone(projection.route);
      draft.waypointRouteVertexIndices = [...projection.waypointRouteVertexIndices];
      draft.waypointRows.forEach((row, index) => { row.routeVertexIndex = projection.waypointRouteVertexIndices[index] ?? null; });
      stopSegmentRouteEdit(lifecycle);
      queueMicrotask(restoreFocus);
    },
    cancel: () => {
      stopSegmentRouteEdit(lifecycle);
      queueMicrotask(restoreFocus);
    }
  });
  if (!entered) {
    stopSegmentRouteEdit(lifecycle);
    return 'Route work could not start because another map task is active.';
  }
  return null;
}

/** Clears all adapter-owned route-work state and handlers. */
export function stopSegmentRouteEdit(state: SegmentRouteMapWorkLifecycleState): void {
  state.stopEdit?.();
  state.stopEdit = null;
  state.work = null;
}

function snapshotDraft(draft: EditorSegmentDraft): SegmentRouteDraftSnapshot {
  return { route: clone(draft.route), waypointRouteVertexIndices: [...draft.waypointRouteVertexIndices] };
}

function restoreDraft(draft: EditorSegmentDraft, snapshot: SegmentRouteDraftSnapshot): void {
  draft.route = clone(snapshot.route);
  draft.waypointRouteVertexIndices = [...snapshot.waypointRouteVertexIndices];
  draft.waypointRows.forEach((row, index) => { row.routeVertexIndex = snapshot.waypointRouteVertexIndices[index] ?? null; });
}

function segmentRouteStatus(work: SegmentRouteWorkState | null): string {
  if (!work) return 'Route work unavailable';
  const anonymousCount = work.nodes.filter(node => node.kind === 'anonymous').length;
  const status = work.cleared ? 'Fallback route pending' : work.origin === 'fallback' && !work.changedCustom ? 'Fallback route unchanged' : 'Custom route pending';
  return `${status} · ${work.nodes.length} route points · ${anonymousCount} editable`;
}

function clone<T>(value: T): T {
  return value ? JSON.parse(JSON.stringify(value)) as T : value;
}
