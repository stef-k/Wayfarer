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
  { key: 'share', label: 'Share' },
  { key: 'copyPublicUrl', label: 'Public URL' },
  { key: 'copyCoverUrl', label: 'Cover URL' },
  { key: 'copyMapSnapshotUrl', label: 'Map Snapshot' },
  { key: 'fullscreen', label: 'Open Fullscreen' },
  { key: 'openCanonical', label: 'Open' },
  { key: 'readable', label: 'Readable' },
  { key: 'print', label: 'Print' }
];

const visibleUrlAction = (action: ViewerAction): boolean =>
  Boolean(action.url) && (action.allowed || action.requiresAuthentication) && (action.method == null || action.method.toUpperCase() === 'GET');

// #337 renders safe navigation actions only. Non-GET clone and no-URL readable/print remain #340/parity work,
// but they are represented here so returned #335 action flags are not silently dropped.
const deferredAction = (action: ViewerAction): boolean =>
  !visibleUrlAction(action) && (action.allowed || action.requiresAuthentication);
</script>

<template>
  <nav class="trip-viewer-actions" :class="{ 'trip-viewer-actions--embed': props.embed }" aria-label="Trip actions">
    <template v-for="item in actionItems" :key="item.key">
      <a
        v-if="visibleUrlAction(props.actions[item.key])"
        class="trip-viewer-action"
        :href="props.actions[item.key].url ?? '#'"
        :target="item.key === 'fullscreen' || item.key === 'openCanonical' ? '_blank' : undefined"
        :rel="item.key === 'fullscreen' || item.key === 'openCanonical' ? 'noopener noreferrer' : undefined"
      >
        {{ item.label }}<span v-if="props.actions[item.key].requiresAuthentication"> sign-in</span>
      </a>
      <button
        v-else-if="deferredAction(props.actions[item.key])"
        type="button"
        class="trip-viewer-action trip-viewer-action--deferred"
        disabled
      >
        {{ item.label }}
      </button>
    </template>
  </nav>
</template>
