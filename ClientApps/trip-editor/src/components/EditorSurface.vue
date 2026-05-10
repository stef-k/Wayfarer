<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import type { EditorSurfaceController, EditorTarget } from '../composables/useEditorSurface';

const props = defineProps<{
  controller: EditorSurfaceController;
  target: EditorTarget;
  statusText: string;
}>();

const closeButton = ref<HTMLButtonElement | null>(null);
const titleId = computed(() => `trip-editor-surface-title-${props.target.identity.replace(/[^a-z0-9_-]/gi, '-')}`);
const isActive = computed(() => props.controller.isTargetActive(props.target));
const isDocked = computed(() => isActive.value && props.controller.surfaceMode.value === 'docked');
const isExpanded = computed(() => isActive.value && props.controller.surfaceMode.value === 'expanded');

watch(isExpanded, async expanded => {
  if (!expanded) {
    return;
  }

  await nextTick();
  closeButton.value?.focus();
});
</script>

<template>
  <section v-if="isDocked" class="trip-editor-surface trip-editor-surface--docked" :aria-labelledby="titleId">
    <header class="trip-editor-surface__header">
      <div>
        <p class="trip-editor-surface__eyebrow">{{ target.mode === 'add' ? 'New' : 'Editing' }} {{ target.kind }}</p>
        <h2 :id="titleId">{{ target.title }}</h2>
        <small v-if="target.subtitle">{{ target.subtitle }}</small>
      </div>
      <div class="trip-editor-surface__controls">
        <span class="trip-editor-save-state">{{ statusText }}</span>
        <button type="button" class="btn btn-outline-light btn-sm" @click="controller.expand(target)">Expand Editor</button>
        <button type="button" class="btn btn-outline-secondary btn-sm" @click="controller.closeActiveTarget()">Close</button>
      </div>
    </header>
    <div class="trip-editor-surface__body">
      <slot name="body"></slot>
    </div>
    <footer class="trip-editor-surface__footer">
      <slot name="footer"></slot>
    </footer>
  </section>

  <section v-else-if="isActive" class="trip-editor-surface-context" :class="{ 'trip-editor-surface-context--map-work': controller.surfaceMode.value === 'map-work' }">
    <div>
      <strong>{{ target.title }}</strong>
      <small>{{ controller.surfaceMode.value === 'map-work' ? 'Map work active' : 'Expanded editor active' }}</small>
    </div>
    <button
      v-if="controller.surfaceMode.value === 'expanded'"
      type="button"
      class="btn btn-outline-light btn-sm"
      @click="controller.dock(target)"
    >
      Dock to sidebar
    </button>
  </section>

  <Teleport to="body">
    <div v-if="isExpanded" class="trip-editor-expanded">
      <div class="trip-editor-expanded__backdrop" aria-hidden="true"></div>
      <section class="trip-editor-expanded__dialog" role="dialog" aria-modal="true" :aria-labelledby="titleId">
        <header class="trip-editor-surface__header trip-editor-expanded__header">
          <div>
            <p class="trip-editor-surface__eyebrow">{{ target.mode === 'add' ? 'New' : 'Editing' }} {{ target.kind }}</p>
            <h2 :id="titleId">{{ target.title }}</h2>
            <small v-if="target.subtitle">{{ target.subtitle }}</small>
          </div>
          <div class="trip-editor-surface__controls">
            <span class="trip-editor-save-state">{{ statusText }}</span>
            <button type="button" class="btn btn-outline-light btn-sm" @click="controller.dock(target)">Dock to sidebar</button>
            <button ref="closeButton" type="button" class="btn btn-outline-secondary btn-sm" @click="controller.closeActiveTarget()">Close</button>
          </div>
        </header>
        <div class="trip-editor-expanded__body">
          <slot name="body"></slot>
        </div>
        <footer class="trip-editor-surface__footer trip-editor-expanded__footer">
          <slot name="footer"></slot>
        </footer>
      </section>
    </div>
  </Teleport>
</template>
