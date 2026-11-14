import { createApp } from 'vue';
import App from './App.vue';
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

if (compositionElement) {
  // The same entry/theme and shared editors serve the selected comparison layout.
  void import('./composition/CompositionApp.vue').then(({ default: CompositionApp }) => {
    const token = document.querySelector<HTMLInputElement>('#trip-editor-antiforgery input')?.value ?? '';
    createApp(CompositionApp, { account: compositionElement.dataset.accountId ?? '',
      compositionId: compositionElement.dataset.compositionId ?? '', token }).mount(compositionElement);
  });
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

createApp(App, { config }).mount(mountElement);
}
