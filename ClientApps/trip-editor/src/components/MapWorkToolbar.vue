<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import type { EditorSurfaceController } from '../composables/useEditorSurface';
import SegmentRoutePointEditor from './SegmentRoutePointEditor.vue';

const props = defineProps<{
  controller: EditorSurfaceController;
}>();

type RoutePointEditorView = {
  hasInvalidCoordinates: () => boolean;
  focusFirstInvalid: () => void;
};

const doneButton = ref<HTMLButtonElement | null>(null);
const routePointEditorView = ref<RoutePointEditorView | null>(null);
const mapWork = computed(() => props.controller.mapWork.value);
const statusText = computed(() => {
  const status = mapWork.value?.statusText;
  return typeof status === 'function' ? status() : status;
});
const canFinish = computed(() => (mapWork.value?.canFinish() ?? false) && !routePointEditorView.value?.hasInvalidCoordinates());
const canClear = computed(() => Boolean(mapWork.value?.clear));
const routePointEditor = computed(() => mapWork.value?.routePointEditor ?? null);

/** Finishes valid work and otherwise returns focus to the first invalid route coordinate. */
const finish = async (): Promise<void> => {
  if (!canFinish.value) {
    routePointEditorView.value?.focusFirstInvalid();
    return;
  }
  await props.controller.finishMapWork();
};

const onKeydown = async (event: KeyboardEvent): Promise<void> => {
  if (event.key !== 'Escape') {
    return;
  }

  event.preventDefault();
  await props.controller.cancelMapWork();
};

watch(
  () => props.controller.isMapWorkActive.value,
  async active => {
    if (!active) {
      return;
    }

    await nextTick();
    doneButton.value?.focus({ preventScroll: true });
  }
);
</script>

<template>
  <div v-if="mapWork" class="trip-editor-map-work-toolbar" role="region" aria-label="Map work" tabindex="-1" @keydown="onKeydown">
    <div>
      <p class="trip-editor-surface__eyebrow">{{ mapWork.target.title }}</p>
      <strong>{{ mapWork.modeName }}</strong>
      <span>{{ mapWork.instruction }}</span>
      <small>{{ statusText }}</small>
    </div>
    <div class="trip-editor-map-work-toolbar__actions">
      <button ref="doneButton" type="button" class="btn btn-primary btn-sm" :disabled="!canFinish" @click="finish">Done</button>
      <button v-if="canClear" type="button" class="btn btn-outline-light btn-sm" @click="mapWork?.clear?.()">Clear Route</button>
      <button type="button" class="btn btn-outline-light btn-sm" @click="controller.cancelMapWork()">Cancel</button>
    </div>
    <SegmentRoutePointEditor v-if="routePointEditor" ref="routePointEditorView" :controller="routePointEditor" />
  </div>
</template>
