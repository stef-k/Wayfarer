<script setup lang="ts">
import type { ViewerNotes } from '../types';

defineProps<{
  notes: ViewerNotes;
  compact?: boolean;
}>();
</script>

<template>
  <section v-if="notes.hasRenderableContent" class="trip-viewer-notes" :class="{ 'trip-viewer-notes--compact': compact }">
    <!-- displayHtml is backend-owned display-safe HTML from the #334/#335 notes contract. -->
    <div class="trip-viewer-notes__content" v-html="notes.displayHtml"></div>
    <span v-if="notes.hasMediaContent && !notes.hasTextContent" class="trip-viewer-notes__media-label">Media note</span>
  </section>
  <p v-else class="trip-viewer-empty">No notes.</p>
</template>
