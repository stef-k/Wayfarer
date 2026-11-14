import { createApp } from 'vue';
import type { BootstrapConfig } from './types';
import './theme.css';
import './map.css';
import './styles.css';
import './mobileDrawer.css';
import './surfaces.css';
import './iconSelector.css';
import './richNotes.css';
import './visitProgress.css';

const mountElement = document.getElementById('trip-editor-app');
const compositionElement = document.getElementById('trip-composition-app');
/** Failed chunk loading leaves a normal recovery link and never retries a construction/mutation. */
const loadFailed = (element: HTMLElement): void => {
  const message = document.createElement('p'); message.setAttribute('role', 'alert');
  message.textContent = 'The Trip workspace could not load. Refresh this page or return to My Trips.';
  const link = document.createElement('a'); link.href = '/User/Trip'; link.textContent = 'My Trips';
  element.replaceChildren(message, link);
};

if (compositionElement) {
  // The same entry/theme and shared editors serve the selected comparison layout.
  void import('./composition/CompositionApp.vue').then(({ default: CompositionApp }) => {
    const token = document.querySelector<HTMLInputElement>('#trip-editor-antiforgery input')?.value ?? '';
    createApp(CompositionApp, { account: compositionElement.dataset.accountId ?? '',
      compositionId: compositionElement.dataset.compositionId ?? '', token }).mount(compositionElement);
  }).catch(() => loadFailed(compositionElement));
} else if (!mountElement) {
  throw new Error('Trip Editor mount element was not found.');
} else {

  const tokenInput = document.querySelector<HTMLInputElement>('#trip-editor-antiforgery input[name="__RequestVerificationToken"]');
  const config: BootstrapConfig = {
    tripId: mountElement.dataset.tripId ?? '',
    tripName: mountElement.dataset.tripName ?? '',
    editorEndpoint: mountElement.dataset.editorEndpoint ?? '',
    tripIndexUrl: mountElement.dataset.tripIndexUrl ?? '/User/Trip/Index',
    tilesUrl: mountElement.dataset.tilesUrl ?? '/Public/tiles/{z}/{x}/{y}.png',
    antiforgeryToken: tokenInput?.value ?? ''
  };

  // Load only the selected workspace. Shared Vue/Leaflet/editor code remains a common Vite chunk.
  void import('./App.vue').then(({ default: App }) => createApp(App, { config }).mount(mountElement)).catch(() => loadFailed(mountElement));
}
