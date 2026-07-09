<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref } from 'vue';
import type { ViewerAction, ViewerActions } from '../types';

type CopyActionKey = 'share' | 'copyPublicUrl' | 'copyCoverUrl' | 'copyMapSnapshotUrl';
type MenuItem = { key: string; label: string; action?: ViewerAction; copyKey?: CopyActionKey; deferred?: boolean; print?: boolean };

const props = defineProps<{
  actions: ViewerActions;
  embed: boolean;
}>();

const emit = defineEmits<{
  readable: [];
  print: [];
}>();

const menuOpen = ref(false);
const trigger = ref<HTMLButtonElement | null>(null);
const menu = ref<HTMLElement | null>(null);
const feedback = ref<{ message: string; failedUrl: string | null; label: string }>({ message: '', failedUrl: null, label: '' });

// A URL may be navigated only when the server explicitly returned GET semantics.
const isGetNavigation = (action: ViewerAction): boolean =>
  Boolean(action.url) && (action.allowed || action.requiresAuthentication) && (action.method == null || action.method.toUpperCase() === 'GET');

const isAllowedLocal = (action: ViewerAction): boolean => action.allowed && !props.embed;
const isAllowedCopy = (action: ViewerAction): boolean => action.allowed && Boolean(action.url) && (action.method == null || action.method.toUpperCase() === 'GET');
const isDeferredPost = (action: ViewerAction): boolean => action.allowed && !isGetNavigation(action);

const primaryReadable = computed(() => isAllowedLocal(props.actions.readable));
const primaryEdit = computed(() => props.actions.edit.allowed && isGetNavigation(props.actions.edit));

// Builds the exact #347 Share, Export, Trip group order from server-returned action facts.
const menuGroups = computed(() => {
  if (props.embed) return [];

  const share: MenuItem[] = [];
  if (isAllowedCopy(props.actions.share)) {
    share.push({ key: 'share', label: 'Share (copy link)', copyKey: 'share', action: props.actions.share });
  }
  if (isAllowedCopy(props.actions.copyPublicUrl) && (!isAllowedCopy(props.actions.share) || props.actions.copyPublicUrl.url !== props.actions.share.url)) {
    share.push({ key: 'copyPublicUrl', label: 'Copy public URL', copyKey: 'copyPublicUrl', action: props.actions.copyPublicUrl });
  }
  if (isAllowedCopy(props.actions.copyCoverUrl)) {
    share.push({ key: 'copyCoverUrl', label: 'Copy cover image URL', copyKey: 'copyCoverUrl', action: props.actions.copyCoverUrl });
  }
  if (isAllowedCopy(props.actions.copyMapSnapshotUrl)) {
    share.push({ key: 'copyMapSnapshotUrl', label: 'Copy map snapshot URL', copyKey: 'copyMapSnapshotUrl', action: props.actions.copyMapSnapshotUrl });
  }

  const exportItems: MenuItem[] = [
    { key: 'exportWayfarerKml', label: 'Wayfarer KML', action: props.actions.exportWayfarerKml },
    { key: 'exportGoogleMyMapsKml', label: 'Google My Maps KML', action: props.actions.exportGoogleMyMapsKml },
    { key: 'exportPdf', label: 'Export PDF', action: props.actions.exportPdf }
  ].filter(item => item.action !== undefined && isGetNavigation(item.action));

  const trip: MenuItem[] = [];
  if (isDeferredPost(props.actions.clone)) {
    trip.push({ key: 'clone', label: 'Clone to My Trips', action: props.actions.clone, deferred: true });
  } else if (isGetNavigation(props.actions.clone) && props.actions.clone.requiresAuthentication) {
    trip.push({ key: 'clone-login', label: 'Sign in to clone', action: props.actions.clone });
  }
  if (isAllowedLocal(props.actions.print)) {
    trip.push({ key: 'print', label: 'Print', print: true });
  }

  return [
    { label: 'Share', items: share },
    { label: 'Export', items: exportItems },
    { label: 'Trip', items: trip }
  ].filter(group => group.items.length > 0);
});

function toggleMenu(): void {
  if (menuOpen.value) {
    closeMenu();
    return;
  }

  feedback.value = { message: '', failedUrl: null, label: '' };
  menuOpen.value = true;
}

// Every dismissal restores the trigger so keyboard users retain their action context.
function closeMenu(): void {
  if (!menuOpen.value) return;
  menuOpen.value = false;
  // Defer beyond the originating pointer click so outside-click dismissal cannot steal focus back.
  void nextTick(() => window.setTimeout(() => trigger.value?.focus(), 0));
}

function focusMenuItem(direction: 'first' | 'last' | 'next' | 'previous'): void {
  const items = Array.from(menu.value?.querySelectorAll<HTMLElement>('[data-action-menu-item]:not([aria-disabled="true"])') ?? []);
  if (!items.length) return;
  const current = items.indexOf(document.activeElement as HTMLElement);
  const nextIndex = direction === 'first' ? 0
    : direction === 'last' ? items.length - 1
      : direction === 'next' ? (current + 1 + items.length) % items.length
        : (current - 1 + items.length) % items.length;
  items[nextIndex].focus();
}

function handleTriggerKeydown(event: KeyboardEvent): void {
  if (!['Enter', ' ', 'ArrowDown'].includes(event.key)) return;
  event.preventDefault();
  if (!menuOpen.value) toggleMenu();
  void nextTick(() => focusMenuItem('first'));
}

function handleMenuKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    event.preventDefault();
    closeMenu();
  } else if (event.key === 'ArrowDown') {
    event.preventDefault();
    focusMenuItem('next');
  } else if (event.key === 'ArrowUp') {
    event.preventDefault();
    focusMenuItem('previous');
  } else if (event.key === 'Home') {
    event.preventDefault();
    focusMenuItem('first');
  } else if (event.key === 'End') {
    event.preventDefault();
    focusMenuItem('last');
  }
}

function handleOutsidePointer(event: PointerEvent): void {
  const target = event.target as Node;
  if (menuOpen.value && !menu.value?.contains(target) && !trigger.value?.contains(target)) closeMenu();
}

// Copies the exact server URL, falling back only to a selected transient textarea.
async function copyAction(key: CopyActionKey, label: string): Promise<void> {
  const action = props.actions[key];
  if (!isAllowedCopy(action) || !action.url) return;

  let copied = false;
  try {
    if (!navigator.clipboard?.writeText) throw new Error('Clipboard API is unavailable.');
    await navigator.clipboard.writeText(action.url);
    copied = true;
  } catch {
    const input = document.createElement('textarea');
    input.value = action.url;
    input.setAttribute('readonly', '');
    input.style.position = 'fixed';
    input.style.opacity = '0';
    document.body.append(input);
    input.select();
    copied = document.execCommand('copy');
    input.remove();
  }

  feedback.value = copied
    ? { message: `${label} copied.`, failedUrl: null, label }
    : { message: `Could not copy ${label}. Copy it manually.`, failedUrl: action.url, label };
}

function activateLocalPrint(): void {
  closeMenu();
  emit('print');
}

document.addEventListener('pointerdown', handleOutsidePointer);
onBeforeUnmount(() => document.removeEventListener('pointerdown', handleOutsidePointer));
</script>

<template>
  <nav v-if="!embed" class="trip-viewer-actions" aria-label="Trip actions">
    <button v-if="primaryReadable" type="button" class="trip-viewer-action" @click="emit('readable')">Readable itinerary</button>
    <a
      v-if="primaryEdit"
      class="trip-viewer-action"
      :href="actions.edit.url ?? '#'"
      target="_blank"
      rel="noopener noreferrer"
    >Edit</a>
    <div class="trip-viewer-actions__menu-wrap">
      <button
        ref="trigger"
        type="button"
        class="trip-viewer-action trip-viewer-actions__trigger"
        aria-haspopup="menu"
        :aria-expanded="menuOpen"
        aria-controls="trip-viewer-more-actions"
        @click="toggleMenu"
        @keydown="handleTriggerKeydown"
      ><span aria-hidden="true">⋯</span> More actions</button>
      <section
        v-if="menuOpen"
        id="trip-viewer-more-actions"
        ref="menu"
        class="trip-viewer-actions__menu"
        role="menu"
        aria-label="More trip actions"
        @keydown="handleMenuKeydown"
      >
        <template v-for="(group, index) in menuGroups" :key="group.label">
          <div v-if="index" class="trip-viewer-actions__divider" role="separator"></div>
          <span class="trip-viewer-actions__group-label">{{ group.label }}</span>
          <template v-for="item in group.items" :key="item.key">
            <button
              v-if="item.copyKey"
              type="button"
              class="trip-viewer-actions__menu-item"
              role="menuitem"
              data-action-menu-item
              @click="copyAction(item.copyKey, item.label)"
            >{{ item.label }}</button>
            <button
              v-else-if="item.deferred"
              type="button"
              class="trip-viewer-actions__menu-item trip-viewer-action--deferred"
              role="menuitem"
              aria-disabled="true"
              aria-describedby="trip-viewer-clone-deferred"
              title="Cloning is not available from the preview yet"
              disabled
            >{{ item.label }}</button>
            <button
              v-else-if="item.print"
              type="button"
              class="trip-viewer-actions__menu-item"
              role="menuitem"
              data-action-menu-item
              @click="activateLocalPrint"
            >{{ item.label }}</button>
            <a
              v-else-if="item.action"
              class="trip-viewer-actions__menu-item"
              role="menuitem"
              data-action-menu-item
              :href="item.action.url ?? '#'"
              :target="item.key === 'exportPdf' ? '_blank' : undefined"
              :rel="item.key === 'exportPdf' ? 'noopener noreferrer' : undefined"
              @click="closeMenu"
            >{{ item.label }}</a>
          </template>
        </template>
      </section>
    </div>
    <span id="trip-viewer-clone-deferred" class="visually-hidden">Cloning is not available from the preview yet.</span>
    <output v-if="feedback.message" class="trip-viewer-actions__feedback" role="status">
      {{ feedback.message }}
      <input v-if="feedback.failedUrl" :aria-label="`Copy ${feedback.label} manually`" :value="feedback.failedUrl" readonly @focus="event => (event.target as HTMLInputElement).select()">
    </output>
  </nav>
</template>
