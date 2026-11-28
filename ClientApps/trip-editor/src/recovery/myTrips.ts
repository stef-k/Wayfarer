import { createApp } from 'vue';
import MyTripsRecovery from './MyTripsRecovery.vue';

// The authenticated My Trips page can load custody without loading the map or editor graph.
const root = document.getElementById('trip-metadata-recovery');
if (root?.dataset.accountId && root.dataset.editorUrl) {
  createApp(MyTripsRecovery, { account: root.dataset.accountId, editorUrl: root.dataset.editorUrl }).mount(root);
}
