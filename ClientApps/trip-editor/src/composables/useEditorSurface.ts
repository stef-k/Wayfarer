import { computed, readonly, ref, type Ref } from 'vue';
import { confirm } from './useConfirmDialog';

export type EditorSurfaceMode = 'docked' | 'expanded' | 'map-work';
export type EditorTargetKind = 'metadata' | 'region' | 'place';
export type EditorTargetMode = 'edit' | 'add';

export interface EditorTarget {
  key: string;
  kind: EditorTargetKind;
  mode: EditorTargetMode;
  title: string;
  subtitle?: string;
}

export interface EditorTargetHandler {
  isDirty: () => boolean;
  discard: () => void;
}

export interface MapWorkOptions {
  modeName: string;
  instruction: string;
  statusText?: string;
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
  statusText: string;
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
    closeActiveTarget,
    dock,
    expand,
    enterMapWork,
    finishMapWork,
    isTargetActive,
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
  if (activeTarget.value?.key === target.key) {
    activeTarget.value = target;
    return true;
  }

  if (!(await discardActiveTarget('Discard unsaved changes before switching editors?'))) {
    return false;
  }

  activeTarget.value = target;
  surfaceMode.value = 'docked';
  mapWork.value = null;
  return true;
}

export async function closeActiveTarget(message = 'Discard unsaved changes and close this editor?'): Promise<boolean> {
  if (!(await discardActiveTarget(message))) {
    return false;
  }

  activeTarget.value = null;
  surfaceMode.value = 'docked';
  mapWork.value = null;
  return true;
}

export function expand(targetKey: string): void {
  if (activeTarget.value?.key === targetKey && surfaceMode.value !== 'map-work') {
    surfaceMode.value = 'expanded';
  }
}

export function dock(targetKey?: string): void {
  if (!targetKey || activeTarget.value?.key === targetKey) {
    surfaceMode.value = 'docked';
  }
}

export function isTargetActive(targetKey: string): boolean {
  return activeTarget.value?.key === targetKey;
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
}

export async function cancelMapWork(): Promise<void> {
  if (!mapWork.value) {
    return;
  }

  const work = mapWork.value;
  work.rollback(work.snapshot);
  await work.cancel?.();
  surfaceMode.value = work.previousSurface;
  activeTarget.value = work.target;
  mapWork.value = null;
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
