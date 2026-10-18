<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue';
import { heldRegionDeletionKey, unknownRegionDeletionMessage } from './regionDeletionState';
const held = ref(false);
/// A refresh displays current facts but cannot resolve an earlier unknown commit.
const update = (): void => {
  const mount = document.getElementById('trip-editor-app');
  if (!mount?.dataset.accountId || !mount.dataset.tripId) return;
  try { held.value = Boolean(localStorage.getItem(heldRegionDeletionKey(location.origin, mount.dataset.accountId, mount.dataset.tripId))); }
  catch { held.value = false; }
};
onMounted(() => { update(); window.addEventListener('storage', update); window.addEventListener('region-deletion-hold', update); });
onUnmounted(() => { window.removeEventListener('storage', update); window.removeEventListener('region-deletion-hold', update); });
</script>
<template>
  <div v-if="held" role="alert" class="alert alert-warning position-fixed bottom-0 start-0 m-3" style="z-index: 1040; max-width: 32rem">
    {{ unknownRegionDeletionMessage }} Region deletion remains held until the earlier request's terminal outcome is explicitly reconciled.
  </div>
</template>
