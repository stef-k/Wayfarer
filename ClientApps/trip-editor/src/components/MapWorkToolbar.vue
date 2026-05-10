<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import type { EditorSurfaceController } from '../composables/useEditorSurface';

const props = defineProps<{
  controller: EditorSurfaceController;
}>();

const doneButton = ref<HTMLButtonElement | null>(null);
const mapWork = computed(() => props.controller.mapWork.value);
const statusText = computed(() => {
  const status = mapWork.value?.statusText;
  return typeof status === 'function' ? status() : status;
});
const canFinish = computed(() => mapWork.value?.canFinish() ?? false);

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
    doneButton.value?.focus();
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
      <button ref="doneButton" type="button" class="btn btn-primary btn-sm" :disabled="!canFinish" @click="controller.finishMapWork()">Done</button>
      <button type="button" class="btn btn-outline-light btn-sm" @click="controller.cancelMapWork()">Cancel</button>
    </div>
  </div>
</template>
