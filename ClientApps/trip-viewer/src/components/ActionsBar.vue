<script setup lang="ts">
import type { ViewerActions, ViewerAction } from '../types';

const props = defineProps<{
  actions: ViewerActions;
  embed: boolean;
}>();

const actionItems: Array<{ key: keyof ViewerActions; label: string }> = [
  { key: 'edit', label: 'Edit' },
  { key: 'clone', label: 'Clone' },
  { key: 'exportWayfarerKml', label: 'Wayfarer KML' },
  { key: 'exportGoogleMyMapsKml', label: 'Google KML' },
  { key: 'exportPdf', label: 'PDF' },
  { key: 'copyPublicUrl', label: 'Public URL' },
  { key: 'copyCoverUrl', label: 'Cover URL' },
  { key: 'copyMapSnapshotUrl', label: 'Map Snapshot' },
  { key: 'fullscreen', label: 'Open Fullscreen' },
  { key: 'openCanonical', label: 'Open' }
];

const visibleAction = (action: ViewerAction): boolean =>
  Boolean(action.url) && (action.allowed || action.requiresAuthentication) && (action.method == null || action.method.toUpperCase() === 'GET');
</script>

<template>
  <nav class="trip-viewer-actions" :class="{ 'trip-viewer-actions--embed': props.embed }" aria-label="Trip actions">
    <template v-for="item in actionItems" :key="item.key">
      <a
        v-if="visibleAction(props.actions[item.key])"
        class="trip-viewer-action"
        :href="props.actions[item.key].url ?? '#'"
        :target="item.key === 'fullscreen' || item.key === 'openCanonical' ? '_blank' : undefined"
        :rel="item.key === 'fullscreen' || item.key === 'openCanonical' ? 'noopener noreferrer' : undefined"
      >
        {{ item.label }}<span v-if="props.actions[item.key].requiresAuthentication"> sign-in</span>
      </a>
    </template>
  </nav>
</template>
