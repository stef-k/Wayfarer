import { createApp } from 'vue';
import App from './App.vue';
import type { BootstrapConfig } from './types';
import './theme.css';
import './styles.css';
import './surfaces.css';

const mountElement = document.getElementById('trip-editor-app');

if (!mountElement) {
  throw new Error('Trip Editor mount element was not found.');
}

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
