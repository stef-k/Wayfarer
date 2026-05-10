<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import type { EditorSurfaceController } from '../composables/useEditorSurface';

const props = defineProps<{
  controller: EditorSurfaceController;
}>();

const doneButton = ref<HTMLButtonElement | null>(null);
const mapWork = computed(() => props.controller.mapWork.value);

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
  <div v-if="mapWork" class="trip-editor-map-work-toolbar" role="region" aria-label="Map work">
    <div>
      <p class="trip-editor-surface__eyebrow">{{ mapWork.target.title }}</p>
      <strong>{{ mapWork.modeName }}</strong>
      <span>{{ mapWork.instruction }}</span>
      <small>{{ mapWork.statusText }}</small>
    </div>
    <div class="trip-editor-map-work-toolbar__actions">
      <button ref="doneButton" type="button" class="btn btn-primary btn-sm" @click="controller.finishMapWork()">Done</button>
      <button type="button" class="btn btn-outline-light btn-sm" @click="controller.cancelMapWork()">Cancel</button>
    </div>
  </div>
</template>
