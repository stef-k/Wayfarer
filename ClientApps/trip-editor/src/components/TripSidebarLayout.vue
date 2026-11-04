<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { bindSidebarResize, readSidebarPreference, sidebarWidth, SIDEBAR_MIN, SIDEBAR_DESKTOP, writeSidebarPreference, type SidebarPreference } from '../../../../wwwroot/js/Trip/sidebarWidth.js';
import type { EditorSurfaceMode } from '../composables/useEditorSurface';

const props = defineProps<{ surfaceMode: EditorSurfaceMode }>();
const preferenceKey = 'wayfarer.trip-editor.sidebar';
const preference = ref<SidebarPreference>(readSidebarPreference(preferenceKey));
const layout = ref<HTMLElement | null>(null);
const showButton = ref<HTMLButtonElement | null>(null);
const selector = ref<HTMLSelectElement | null>(null);
const handle = ref<HTMLElement | null>(null);
const workspaceWidth = ref(0);
const desktop = ref(false);
const hidden = ref(false);
const resizing = ref(false);
const docked = computed(() => props.surfaceMode === 'docked');
const hiddenDock = computed(() => desktop.value && docked.value && hidden.value);
const width = computed(() => sidebarWidth(docked.value ? preference.value : { mode: 'standard', width: 500 }, workspaceWidth.value));
const overlay = computed(() => desktop.value && !hiddenDock.value && width.value >= workspaceWidth.value);
let observer: ResizeObserver | undefined;
let resizer: ReturnType<typeof bindSidebarResize> | undefined;
let media: MediaQueryList;

/** Persist only completed user choices, never workspace resize results. */
const commit = () => writeSidebarPreference(preferenceKey, preference.value);
const choose = (event: Event) => {
  resizer?.cancel();
  preference.value = { mode: (event.target as HTMLSelectElement).value as SidebarPreference['mode'], width: 500 };
  commit();
};
/** Visibility is layout state; the slotted sidebar and all draft owners stay mounted. */
const hide = async () => {
  resizer?.cancel();
  hidden.value = true;
  await nextTick();
  showButton.value?.focus();
};
const show = async () => {
  hidden.value = false;
  await nextTick();
  selector.value?.focus();
};
const updateDesktop = () => { resizer?.cancel(); desktop.value = media.matches; };
watch(() => props.surfaceMode, () => resizer?.cancel());

onMounted(() => {
  media = window.matchMedia(`(min-width: ${SIDEBAR_DESKTOP + 1}px)`);
  updateDesktop();
  media.addEventListener('change', updateDesktop);
  const workspace = layout.value!.parentElement!;
  workspaceWidth.value = workspace.clientWidth;
  observer = new ResizeObserver(() => { resizer?.cancel(); workspaceWidth.value = workspace.clientWidth; });
  observer.observe(workspace);
  resizer = bindSidebarResize(handle.value!, {
    getPreference: () => preference.value, getWidth: () => width.value, getMax: () => workspaceWidth.value,
    change: value => { preference.value = value; }, commit, active: value => { resizing.value = value; }
  });
});
onUnmounted(() => { resizer?.dispose(); observer?.disconnect(); media?.removeEventListener('change', updateDesktop); });
</script>

<template>
  <div ref="layout" class="trip-sidebar-layout" :class="{ 'is-hidden': hiddenDock, 'is-overlay': overlay, 'is-resizing': resizing }"
    :style="{ '--sidebar-width': `${width}px` }" :data-sidebar-mode="preference.mode">
    <div class="trip-sidebar-dock" :inert="hiddenDock" :aria-hidden="hiddenDock || undefined">
      <div v-show="docked" class="trip-sidebar-controls">
        <label>Sidebar width
          <select ref="selector" :value="preference.mode" @change="choose">
            <option value="standard">Standard</option><option value="half">Half</option><option value="full">Full</option>
            <option value="custom" disabled>Custom</option>
          </select>
        </label>
        <button type="button" class="btn btn-outline-light btn-sm" aria-label="Hide sidebar" aria-expanded="true" aria-controls="editor-sidebar-content" @click="hide">Hide</button>
      </div>
      <div id="editor-sidebar-content" class="trip-sidebar-content"><slot /></div>
      <div v-show="docked" ref="handle" class="trip-sidebar-resizer" role="separator" tabindex="0" aria-label="Resize sidebar" aria-orientation="vertical"
        :aria-valuemin="SIDEBAR_MIN" :aria-valuemax="workspaceWidth" :aria-valuenow="Math.round(width)"
        :aria-valuetext="`${preference.mode}, ${Math.round(width)} pixels`" aria-controls="editor-sidebar-content"
        title="Resize sidebar: arrows 20px, Shift arrows 100px, Home minimum, End full width, Escape cancels drag"></div>
    </div>
    <button v-if="hiddenDock" ref="showButton" type="button" class="trip-sidebar-show btn btn-light border shadow" aria-label="Show sidebar" aria-expanded="false" aria-controls="editor-sidebar-content" @click="show">Show sidebar</button>
    <slot name="map" />
  </div>
</template>

<style scoped>
/* Desktop layout owns only geometry. Existing compact stacks and phone drawers pass through. */
.trip-sidebar-layout, .trip-sidebar-dock, .trip-sidebar-content { display: contents; }
.trip-sidebar-controls, .trip-sidebar-resizer { display: none; }
@media (min-width: 901px) {
  /* Search responds to the map column, which can be narrow even on a wide desktop. */
  .trip-sidebar-layout :deep(.trip-editor-map-shell) { container: trip-map / inline-size; }
  .trip-sidebar-layout { display: grid; grid-template-columns: var(--sidebar-width) minmax(0, 1fr); min-height: 0; min-width: 0; position: relative; }
  .trip-sidebar-dock { display: grid; grid-template-rows: auto minmax(0, 1fr); min-height: 0; min-width: 0; position: relative; background: var(--trip-editor-surface); }
  /* Reserve the grab edge outside the sidebar scrollbar and interactive content. */
  .trip-sidebar-content { display: grid; min-height: 0; min-width: 0; padding-right: 8px; }
  .trip-sidebar-content :deep(.trip-editor-sidebar) { min-width: 0; }
  .trip-sidebar-controls { display: flex; flex-wrap: wrap; gap: .5rem; align-items: center; justify-content: space-between; padding: .5rem 1rem; border-bottom: 1px solid var(--trip-editor-border); }
  .trip-sidebar-controls label { display: flex; flex-wrap: wrap; gap: .5rem; align-items: center; min-width: 0; }
  .trip-sidebar-controls select { max-width: 100%; background: var(--trip-editor-surface); color: var(--trip-editor-text); border: 1px solid var(--trip-editor-border); border-radius: .25rem; padding: .25rem; }
  .trip-sidebar-resizer { display: block; position: absolute; right: 0; top: 0; bottom: 0; width: 8px; cursor: col-resize; touch-action: none; z-index: 20; }
  /* A quiet centered grip marks the existing hit area without intercepting pointer input. */
  .trip-sidebar-resizer::after { content: ''; position: absolute; left: 50%; top: 50%; transform: translate(-50%, -50%); width: 3px; height: 24px; border-radius: 2px; background: var(--trip-editor-muted); opacity: .55; pointer-events: none; }
  .trip-sidebar-resizer:hover::after, .trip-sidebar-resizer:focus-visible::after { opacity: .85; }
  .trip-sidebar-resizer:focus-visible, .trip-sidebar-controls :focus-visible, .trip-sidebar-show:focus-visible { outline: 3px solid var(--trip-editor-accent); outline-offset: -3px; }
  .is-hidden, .is-overlay { grid-template-columns: minmax(0, 1fr); }
  .is-hidden .trip-sidebar-dock { display: none; }
  .is-overlay .trip-sidebar-dock { position: absolute; inset: 0; z-index: 1001; }
  .trip-sidebar-show { position: absolute; left: .75rem; bottom: 2rem; z-index: 1001; }
  .is-resizing { cursor: col-resize; user-select: none; }
  .is-resizing :deep(.trip-editor-map-shell) { pointer-events: none; }
}
@container trip-map (max-width: 500px) {
  .trip-sidebar-layout :deep(.trip-editor-map-search__form),
  .trip-sidebar-layout :deep(.trip-editor-map-search__add-panel) {
    align-items: stretch;
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
