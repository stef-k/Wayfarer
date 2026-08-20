import { computed, readonly, ref, type Ref } from 'vue';
import { confirm } from './useConfirmDialog';
import type { Guid } from '../types';
import type { SegmentRoutePointEditorController } from '../components/segmentRouteMapWork';

export type EditorSurfaceMode = 'docked' | 'expanded' | 'map-work';
export type EditorTargetKind = 'metadata' | 'region' | 'place' | 'area' | 'segment';
export type EditorTargetMode = 'edit' | 'add';

export interface EditorTarget {
  key: string;
  identity: string;
  kind: EditorTargetKind;
  mode: EditorTargetMode;
  title: string;
  subtitle?: string;
  entityId?: Guid;
  parentRegionId?: Guid;
}

export interface EditorTargetHandler {
  isDirty: () => boolean;
  discard: () => void;
}

export interface MapWorkOptions {
  modeName: string;
  instruction: string;
  statusText?: string | (() => string);
  canFinish?: () => boolean;
  isDirty?: () => boolean;
  clear?: () => void | Promise<void>;
  routePointEditor?: SegmentRoutePointEditorController;
  restoreFocus?: () => void;
  snapshot: () => unknown;
  rollback: (snapshot: unknown) => void;
  done: () => void | Promise<void>;
  cancel?: () => void | Promise<void>;
}

interface ActiveMapWork {
  previousSurface: Exclude<EditorSurfaceMode, 'map-work'>;
  target: EditorTarget;
  modeName: string;
  instruction: string;
  statusText: string | (() => string);
  canFinish: () => boolean;
  isDirty: () => boolean;
  clear?: () => void | Promise<void>;
  routePointEditor?: SegmentRoutePointEditorController;
  restoreFocus?: () => void;
  snapshot: unknown;
  rollback: (snapshot: unknown) => void;
  done: () => void | Promise<void>;
  cancel?: () => void | Promise<void>;
}

const surfaceMode = ref<EditorSurfaceMode>('docked');
const activeTarget = ref<EditorTarget | null>(null);
const mapWork = ref<ActiveMapWork | null>(null);
const handlers = new Map<string, EditorTargetHandler>();

const isMapWorkActive = computed(() => surfaceMode.value === 'map-work' && mapWork.value !== null);

/// Coordinates one active Trip Editor draft across docked, expanded, and map-work surfaces.
export function useEditorSurface() {
  return {
    activeTarget: readonly(activeTarget),
    isMapWorkActive,
    mapWork: readonly(mapWork) as Readonly<Ref<ActiveMapWork | null>>,
    surfaceMode: readonly(surfaceMode),
    activateTarget,
    cancelMapWork,
    clearActiveTarget,
    closeActiveTarget,
    dock,
    expand,
    enterMapWork,
    finishMapWork,
    invalidateMapWork,
    isActiveTargetDirty,
    isTargetActive,
    replaceActiveTarget,
    registerTargetHandler
  };
}

export type EditorSurfaceController = ReturnType<typeof useEditorSurface>;

export function registerTargetHandler(targetKey: string, handler: EditorTargetHandler): () => void {
  handlers.set(targetKey, handler);
  return () => {
    if (handlers.get(targetKey) === handler) {
      handlers.delete(targetKey);
    }
  };
}

export async function activateTarget(target: EditorTarget): Promise<boolean> {
  if (isSameConcreteTarget(activeTarget.value, target)) {
    activeTarget.value = target;
    if (mapWork.value) {
      mapWork.value = { ...mapWork.value, target };
    }
    return true;
  }

  if (!(await cancelActiveMapWork())) {
    return false;
  }

  if (!(await discardActiveTarget('Discard unsaved changes before switching editors?'))) {
    return false;
  }

  activeTarget.value = target;
  surfaceMode.value = 'docked';
  return true;
}

export async function closeActiveTarget(message = 'Discard unsaved changes and close this editor?'): Promise<boolean> {
  if (!(await cancelActiveMapWork())) {
    return false;
  }

  if (!(await discardActiveTarget(message))) {
    return false;
  }

  activeTarget.value = null;
  surfaceMode.value = 'docked';
  return true;
}

export function clearActiveTarget(target?: EditorTarget): void {
  if (target && !isSameConcreteTarget(activeTarget.value, target)) {
    return;
  }

  activeTarget.value = null;
  surfaceMode.value = 'docked';
  mapWork.value = null;
}

/// Re-labels the open editor after a successful save changes an add draft into an edit draft.
export function replaceActiveTarget(target: EditorTarget): void {
  activeTarget.value = target;
  if (mapWork.value) {
    mapWork.value = { ...mapWork.value, target };
  }
}

export function expand(target: EditorTarget): void {
  if (isSameConcreteTarget(activeTarget.value, target) && surfaceMode.value !== 'map-work') {
    surfaceMode.value = 'expanded';
  }
}

export function dock(target?: EditorTarget): void {
  if (!target || isSameConcreteTarget(activeTarget.value, target)) {
    surfaceMode.value = 'docked';
  }
}

export function isTargetActive(target: EditorTarget): boolean {
  return isSameConcreteTarget(activeTarget.value, target);
}

/// Reads the active editor handler's dirty state without prompting or mutating draft ownership.
export function isActiveTargetDirty(): boolean {
  const target = activeTarget.value;
  if (!target) {
    return false;
  }

  return handlers.get(target.key)?.isDirty() ?? false;
}

export function enterMapWork(options: MapWorkOptions): boolean {
  if (!activeTarget.value || surfaceMode.value === 'map-work') {
    return false;
  }

  mapWork.value = {
    previousSurface: surfaceMode.value,
    target: activeTarget.value,
    modeName: options.modeName,
    instruction: options.instruction,
    statusText: options.statusText ?? 'Map work active',
    canFinish: options.canFinish ?? (() => true),
    isDirty: options.isDirty ?? (() => true),
    clear: options.clear,
    routePointEditor: options.routePointEditor,
    restoreFocus: options.restoreFocus,
    snapshot: options.snapshot(),
    rollback: options.rollback,
    done: options.done,
    cancel: options.cancel
  };
  surfaceMode.value = 'map-work';
  return true;
}

export async function finishMapWork(): Promise<void> {
  if (!mapWork.value) {
    return;
  }

  const work = mapWork.value;
  await work.done();
  surfaceMode.value = work.previousSurface;
  activeTarget.value = work.target;
  mapWork.value = null;
  work.restoreFocus?.();
}

export async function cancelMapWork(): Promise<boolean> {
  return await cancelActiveMapWork();
}

/// Runs map-work callbacks against isolated surface state without rendering editor controls.
export async function verifyMapWorkLifecycle(options?: { dirty?: boolean }): Promise<{
  activeTargetPreserved: boolean;
  cancelCalled: boolean;
  cancelRejected: boolean;
  dirtyCancelPrompted: boolean;
  mapWorkPreservedAfterRejectedCancel: boolean;
  doneCalled: boolean;
  instruction: string | null;
  modeName: string | null;
  previousSurface: EditorSurfaceMode | null;
  rollbackValue: unknown;
  returnedToPreviousSurface: boolean;
  statusText: string | null;
}> {
  const target: EditorTarget = {
    key: 'metadata',
    identity: 'metadata',
    kind: 'metadata',
    mode: 'edit',
    title: 'Map work verification'
  };
  const savedActiveTarget = activeTarget.value;
  const savedMapWork = mapWork.value;
  const savedSurfaceMode = surfaceMode.value;
  let cancelCalled = false;
  let cancelRejected = false;
  let dirtyCancelPrompted = false;
  let doneCalled = false;
  let mapWorkPreservedAfterRejectedCancel = false;
  let rollbackValue: unknown = null;

  activeTarget.value = target;
  surfaceMode.value = 'expanded';
  enterMapWork({
    modeName: 'Verify map work',
    instruction: 'Exercise the shared map-work lifecycle.',
    isDirty: () => options?.dirty ?? true,
    snapshot: () => 'map-work-snapshot',
    rollback: snapshot => {
      rollbackValue = snapshot;
    },
    done: () => {
      doneCalled = true;
    },
    cancel: () => {
      cancelCalled = true;
    }
  });

  const work = mapWork.value;
  const activeTargetPreserved = isSameConcreteTarget(activeTarget.value, target) && isSameConcreteTarget(work?.target ?? null, target);
  await finishMapWork();
  enterMapWork({
    modeName: 'Verify map work',
    instruction: 'Exercise map-work cancel and rollback.',
    isDirty: () => options?.dirty ?? true,
    snapshot: () => 'map-work-snapshot',
    rollback: snapshot => {
      rollbackValue = snapshot;
    },
    done: () => {
      doneCalled = true;
    },
    cancel: () => {
      cancelCalled = true;
    }
  });
  cancelRejected = !(await cancelActiveMapWork({
    confirmDirty: async () => {
      dirtyCancelPrompted = true;
      return false;
    }
  }));
  mapWorkPreservedAfterRejectedCancel = isMapWorkActive.value;
  await cancelActiveMapWork({
    confirmDirty: async () => {
      dirtyCancelPrompted = true;
      return true;
    }
  });

  const returnedToPreviousSurface = surfaceMode.value === 'expanded';
  activeTarget.value = savedActiveTarget;
  mapWork.value = savedMapWork;
  surfaceMode.value = savedSurfaceMode;

  return {
    activeTargetPreserved,
    cancelCalled,
    cancelRejected,
    dirtyCancelPrompted,
    mapWorkPreservedAfterRejectedCancel,
    doneCalled,
    instruction: work?.instruction ?? null,
    modeName: work?.modeName ?? null,
    previousSurface: work?.previousSurface ?? null,
    rollbackValue,
    returnedToPreviousSurface,
    statusText: typeof work?.statusText === 'function' ? work.statusText() : work?.statusText ?? null
  };
}

async function cancelActiveMapWork(options?: { confirmDirty?: () => Promise<boolean> }): Promise<boolean> {
  if (!mapWork.value) {
    return true;
  }

  const work = mapWork.value;
  if (work.isDirty()) {
    const confirmed = await (options?.confirmDirty?.() ?? confirm({
      title: 'Discard map editing changes?',
      message: 'Your temporary map edits will be discarded.',
      confirmLabel: 'Discard',
      cancelLabel: 'Keep editing',
      variant: 'warning'
    }));
    if (!confirmed) {
      return false;
    }

    work.rollback(work.snapshot);
  }
  await work.cancel?.();
  surfaceMode.value = work.previousSurface;
  activeTarget.value = work.target;
  mapWork.value = null;
  work.restoreFocus?.();
  return true;
}

/** Stops stale map work without a discard prompt and restores its exact captured draft. */
export async function invalidateMapWork(): Promise<void> {
  if (!mapWork.value) return;
  const work = mapWork.value;
  work.rollback(work.snapshot);
  await work.cancel?.();
  surfaceMode.value = work.previousSurface;
  activeTarget.value = work.target;
  mapWork.value = null;
}

function isSameConcreteTarget(current: EditorTarget | null, next: EditorTarget | null): boolean {
  return current !== null && next !== null && current.identity === next.identity;
}

async function discardActiveTarget(message: string): Promise<boolean> {
  const target = activeTarget.value;
  if (!target) {
    return true;
  }

  const handler = handlers.get(target.key);
  if (!handler) {
    return true;
  }

  if (handler.isDirty()) {
    const confirmed = await confirm({
      title: 'Discard changes?',
      message,
      confirmLabel: 'Discard',
      cancelLabel: 'Keep editing',
      variant: 'warning'
    });
    if (!confirmed) {
      return false;
    }
  }

  handler.discard();
  return true;
}
