<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { TripViewerMountConfig } from './main';
import type { TripViewerState, ViewerAction, ViewerSelection } from './types';
import { normalizeViewerState } from './state';
import { buildRegionGroups, buildSegmentSummaries, selectedEntity, tripSelection } from './viewModel';
import MobileDrawer from './components/MobileDrawer.vue';
import type { DrawerState } from './components/MobileDrawer.vue';
import ActionsBar from './components/ActionsBar.vue';
import ReadableDocument from './components/ReadableDocument.vue';
import SearchPanel from './components/SearchPanel.vue';
import TripMap from './components/TripMap.vue';
import TripSidebar from './components/TripSidebar.vue';
import TripDetail from './components/TripDetail.vue';

type LoadStatus = 'loading' | 'loaded' | 'auth' | 'not-found' | 'server-error' | 'network-error' | 'invalid-config';

const props = defineProps<{
  config: TripViewerMountConfig | null;
  configError: string | null;
}>();

const status = ref<LoadStatus>(props.configError ? 'invalid-config' : 'loading');
const state = ref<TripViewerState | null>(null);
const selection = ref<ViewerSelection | null>(null);
const detail = ref(props.configError ?? '');
const drawerState = ref<DrawerState>('peek');
const detailReturnTarget = ref<'peek' | 'hierarchy'>('peek');
const isCompactViewport = ref(false);
// Defers responsive surfaces until the client has measured the actual viewport.
const viewportReady = ref(false);
const layoutSignal = ref(0);
const fullTripViewSignal = ref(0);
const readableOpen = ref(false);
const readableTrigger = ref<HTMLElement | null>(null);
const searchQuery = ref('');
const desktopPanelOpen = ref(true);
const desktopSurfaceMode = ref<'contents' | 'detail'>('contents');
const desktopContentsBody = ref<HTMLElement | null>(null);
const mobileDrawer = ref<InstanceType<typeof MobileDrawer> | null>(null);
const desktopContentsScrollTop = ref<number | null>(null);
const desktopContentsOverflowing = ref(false);
const showDesktopContentsBack = ref(false);
let availableHeightFrame = 0;
let appResizeObserver: ResizeObserver | null = null;
let documentMutationObserver: MutationObserver | null = null;

const isEmbed = computed(() => props.config?.viewerMode === 'embed' || state.value?.viewerMode === 'embed');
const regionGroups = computed(() => state.value ? buildRegionGroups(state.value) : []);
const segments = computed(() => state.value ? buildSegmentSummaries(state.value) : []);
const selected = computed(() => state.value && selection.value ? selectedEntity(state.value, selection.value) : null);
const expandedMobileDrawer = computed(() => isCompactViewport.value && (drawerState.value === 'hierarchy' || drawerState.value === 'detail'));
const showDesktopDetail = computed(() => !isCompactViewport.value && desktopSurfaceMode.value === 'detail');
const desktopCoverVisible = ref(true);

// Permits only server-authorized GET (or #335-compatible omitted-method) navigation actions.
function isNavigationAction(action: ViewerAction): boolean {
  return action.allowed
    && Boolean(action.url)
    && (action.method == null || action.method.toUpperCase() === 'GET');
}

const embedOpenAction = computed(() => {
  if (!state.value || state.value.viewerMode !== 'embed') return null;

  const canonicalAction = state.value.actions.openCanonical;
  if (isNavigationAction(canonicalAction)) return canonicalAction;

  const fullscreenAction = state.value.actions.fullscreen;
  return isNavigationAction(fullscreenAction) ? fullscreenAction : null;
});

onMounted(() => {
  startAvailableHeightTracking();
  updateViewportMode();
  viewportReady.value = true;
  window.addEventListener('resize', updateViewportMode);
  window.addEventListener('orientationchange', updateViewportMode);

  if (!props.config) {
    return;
  }

  void loadViewerState();
});

onBeforeUnmount(() => {
  window.removeEventListener('resize', updateViewportMode);
  window.removeEventListener('orientationchange', updateViewportMode);
  stopAvailableHeightTracking();
  document.body.classList.remove('trip-viewer-body--drawer-open');
});

watch(expandedMobileDrawer, expanded => {
  document.body.classList.toggle('trip-viewer-body--drawer-open', expanded);
  signalLayoutAfterTransition();
});

watch(drawerState, () => {
  signalLayoutAfterTransition();
});

watch(status, () => {
  scheduleAvailableHeightUpdate();
});

// Fetches the server-emitted #335 state endpoint without deriving privileged URLs client-side.
async function loadViewerState(): Promise<void> {
  if (!props.config) {
    return;
  }

  status.value = 'loading';
  detail.value = '';

  try {
    const response = await fetch(props.config.viewerStateEndpoint, {
      credentials: 'same-origin',
      headers: { Accept: 'application/json' }
    });

    if (response.status === 401 || response.status === 403) {
      status.value = 'auth';
      detail.value = 'Authentication is required or this trip is not available to this account.';
      return;
    }

    if (response.status === 404) {
      status.value = 'not-found';
      detail.value = 'The trip was not found or is not public.';
      return;
    }

    if (!response.ok) {
      status.value = 'server-error';
      detail.value = `Trip Viewer state failed with HTTP ${response.status}.`;
      return;
    }

    const loadedState = normalizeViewerState(await response.json());
    state.value = loadedState;
    selection.value = initialSelection(loadedState);
    drawerState.value = 'peek';
    detailReturnTarget.value = 'peek';
    desktopPanelOpen.value = true;
    desktopSurfaceMode.value = selection.value.type === 'trip' ? 'contents' : 'detail';
    searchQuery.value = '';
    desktopCoverVisible.value = true;
    status.value = 'loaded';
    signalLayoutAfterTransition();
  } catch (error) {
    status.value = 'network-error';
    detail.value = error instanceof Error ? error.message : 'The Trip Viewer state request failed.';
  }
}

function selectEntity(nextSelection: ViewerSelection, source: 'map' | 'desktop' | 'drawer' | 'hierarchy' = 'desktop'): void {
  if (nextSelection.type !== 'trip') {
    captureActiveContentsScroll();
  }

  selection.value = nextSelection;
  if (!isCompactViewport.value) {
    desktopPanelOpen.value = true;
    desktopSurfaceMode.value = nextSelection.type === 'trip' ? 'contents' : 'detail';
    signalLayoutAfterTransition();
    return;
  }

  if (nextSelection.type === 'trip') {
    drawerState.value = source === 'hierarchy' ? 'detail' : 'peek';
    detailReturnTarget.value = source === 'hierarchy' ? 'hierarchy' : 'peek';
    return;
  }

  drawerState.value = 'detail';
  detailReturnTarget.value = source === 'hierarchy' ? 'hierarchy' : 'peek';
}

function showDesktopContents(): void {
  desktopPanelOpen.value = true;
  desktopSurfaceMode.value = 'contents';
  signalLayoutAfterTransition();
}

// Returns to hierarchy without invoking the #346 saved-trip map reset path.
function returnToDesktopContents(): void {
  if (!state.value) return;

  selection.value = tripSelection(state.value);
  showDesktopContents();
  restoreDesktopContentsScroll();
}

function hideDesktopPanel(): void {
  desktopPanelOpen.value = false;
  signalLayoutAfterTransition();
}

function restoreFullTripView(): void {
  if (!state.value) return;

  selection.value = tripSelection(state.value);
  desktopPanelOpen.value = true;
  desktopSurfaceMode.value = 'contents';
  drawerState.value = 'peek';
  detailReturnTarget.value = 'peek';
  fullTripViewSignal.value += 1;
  signalLayoutAfterTransition();
}

function initialSelection(loadedState: TripViewerState): ViewerSelection {
  if (loadedState.viewerMode === 'embed') {
    return tripSelection(loadedState);
  }

  const placeId = new URLSearchParams(window.location.search).get('placeId');
  return placeId && loadedState.placesById[placeId]
    ? { type: 'place', id: placeId }
    : tripSelection(loadedState);
}

function openReadable(): void {
  // The server-returned action is the authority for entering readable mode.
  if (!state.value || isEmbed.value || !state.value.actions.readable.allowed) {
    return;
  }

  readableTrigger.value = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  readableOpen.value = true;
}

function closeReadable(): void {
  readableOpen.value = false;
  void nextTick(() => readableTrigger.value?.focus());
}

function printReadable(): void {
  // Browser print remains a local action and never navigates to the PDF export URL.
  if (!state.value || isEmbed.value || !state.value.actions.print.allowed) {
    return;
  }

  readableOpen.value = true;
  window.setTimeout(() => window.print(), 0);
}

function updateDrawerState(nextState: DrawerState): void {
  drawerState.value = nextState;
  if (nextState === 'hierarchy') {
    detailReturnTarget.value = 'hierarchy';
  }
}

// Keeps the active hierarchy reading position when an entity replaces that body with detail.
function captureActiveContentsScroll(): void {
  if (isCompactViewport.value) {
    mobileDrawer.value?.captureContentsScroll();
    return;
  }

  if (desktopSurfaceMode.value === 'contents' && desktopContentsBody.value) {
    desktopContentsScrollTop.value = desktopContentsBody.value.scrollTop;
  }
}

function updateDesktopContentsScrollState(): void {
  const body = desktopContentsBody.value;
  if (!body) {
    desktopContentsOverflowing.value = false;
    showDesktopContentsBack.value = false;
    return;
  }

  desktopContentsOverflowing.value = body.scrollHeight > body.clientHeight + 1;
  showDesktopContentsBack.value = desktopContentsOverflowing.value && body.scrollTop > 160;
}

function returnToDesktopContentsTop(): void {
  desktopContentsBody.value?.scrollTo({ top: 0, behavior: 'smooth' });
  void nextTick(() => document.querySelector<HTMLElement>('.trip-viewer-command-header__identity h1')?.focus());
}

function restoreDesktopContentsScroll(): void {
  const scrollTop = desktopContentsScrollTop.value;
  if (scrollTop == null) return;

  void nextTick(() => {
    window.requestAnimationFrame(() => {
      window.requestAnimationFrame(() => {
        desktopContentsBody.value?.scrollTo({ top: scrollTop });
        updateDesktopContentsScrollState();
      });
    });
  });
}

function updateViewportMode(): void {
  scheduleAvailableHeightUpdate();
  const nextCompact = window.matchMedia('(max-width: 1023px)').matches;
  const wasCompact = isCompactViewport.value;
  isCompactViewport.value = nextCompact;

  if (nextCompact && !wasCompact && selection.value) {
    drawerState.value = selection.value.type === 'trip' ? 'peek' : 'detail';
    detailReturnTarget.value = 'peek';
  }

  if (!nextCompact) {
    document.body.classList.remove('trip-viewer-body--drawer-open');
  }

  signalLayoutAfterTransition();
}

function startAvailableHeightTracking(): void {
  const app = document.getElementById('trip-viewer-app');
  if (!app) return;

  if ('ResizeObserver' in window) {
    appResizeObserver = new ResizeObserver(scheduleAvailableHeightUpdate);
    appResizeObserver.observe(app);
    appResizeObserver.observe(document.body);
    document.querySelectorAll('header, nav, main, footer').forEach(element => appResizeObserver?.observe(element));
  }

  if ('MutationObserver' in window) {
    documentMutationObserver = new MutationObserver(scheduleAvailableHeightUpdate);
    documentMutationObserver.observe(document.body, {
      attributes: true,
      childList: true,
      subtree: true
    });
  }

  scheduleAvailableHeightUpdate();
}

function stopAvailableHeightTracking(): void {
  if (availableHeightFrame) {
    window.cancelAnimationFrame(availableHeightFrame);
    availableHeightFrame = 0;
  }

  appResizeObserver?.disconnect();
  appResizeObserver = null;
  documentMutationObserver?.disconnect();
  documentMutationObserver = null;
}

function scheduleAvailableHeightUpdate(): void {
  if (availableHeightFrame) return;

  availableHeightFrame = window.requestAnimationFrame(() => {
    availableHeightFrame = 0;
    updateAvailableHeight();
  });
}

function updateAvailableHeight(): void {
  const app = document.getElementById('trip-viewer-app');
  if (!app) return;

  const top = app.getBoundingClientRect().top;
  const footerHeight = document.querySelector('footer')?.getBoundingClientRect().height ?? 0;
  const availableHeight = Math.max(320, window.innerHeight - top - footerHeight);
  const nextHeight = `${availableHeight}px`;
  if (app.style.getPropertyValue('--trip-viewer-available-height') !== nextHeight) {
    app.style.setProperty('--trip-viewer-available-height', nextHeight);
  }
}

function signalLayoutAfterTransition(): void {
  window.setTimeout(() => {
    void nextTick(() => {
      layoutSignal.value += 1;
    });
  }, 170);
}
</script>

<template>
  <section class="trip-viewer-preview" :class="{ 'trip-viewer-preview--embed': isEmbed }" aria-live="polite">
    <div v-if="status === 'loading'" class="trip-viewer-state">
      <span class="trip-viewer-state__spinner" aria-hidden="true"></span>
      <div>
        <strong>Loading trip viewer</strong>
        <span>{{ props.config?.tripName ?? 'Fetching trip state' }}</span>
      </div>
    </div>

    <div
      v-else-if="status === 'loaded' && state && selected && selection"
      class="trip-viewer-workspace"
      :class="[
        `trip-viewer-workspace--drawer-${drawerState}`,
        {
          'trip-viewer-workspace--panel-hidden': !desktopPanelOpen,
          'trip-viewer-workspace--detail-open': desktopSurfaceMode === 'detail',
          // Allows print styles to target only the readable overlay's sibling viewer surfaces.
          'trip-viewer-workspace--readable-open': readableOpen
        }
      ]"
    >
      <aside v-if="!isEmbed && viewportReady && !isCompactViewport && desktopPanelOpen" class="trip-viewer-content-surface" aria-label="Trip content">
        <header class="trip-viewer-command-header">
          <template v-if="!showDesktopDetail">
            <!-- Only the server-returned display URL is eligible for sidebar identity. -->
            <img
              v-if="desktopCoverVisible && state.trip.coverImage?.displayUrl"
              class="trip-viewer-command-header__cover"
              :src="state.trip.coverImage.displayUrl"
              :alt="`Cover for ${state.trip.name}`"
              loading="eager"
              @error="desktopCoverVisible = false"
            >
            <div class="trip-viewer-command-header__identity">
              <h1 tabindex="-1">{{ state.trip.name }}</h1>
              <p>
                <template v-if="state.trip.ownerDisplayName">{{ state.trip.ownerDisplayName }} · </template>
                {{ state.regionOrder.length }} regions · {{ Object.keys(state.placesById).length }} places · {{ state.segmentOrder.length }} segments
              </p>
            </div>
            <SearchPanel
              v-model="searchQuery"
              :state="state"
              @select="selection => selectEntity(selection, 'desktop')"
              @clear="restoreFullTripView"
            />
            <ActionsBar :actions="state.actions" :embed="false" @readable="openReadable" @print="printReadable" />
          </template>
          <div v-else class="trip-viewer-command-header__detail-context">
            <button type="button" @click="returnToDesktopContents">Back to content</button>
            <span>Trip: {{ state.trip.name }}</span>
            <strong>{{ selected.eyebrow }}: {{ selected.title }}</strong>
          </div>
          <button type="button" class="trip-viewer-command-header__hide" @click="hideDesktopPanel">Hide trip contents</button>
        </header>

        <div
          v-if="!showDesktopDetail"
          ref="desktopContentsBody"
          class="trip-viewer-hierarchy-body"
          @scroll="updateDesktopContentsScrollState"
        >
          <button
            v-if="!readableOpen && desktopContentsOverflowing && showDesktopContentsBack"
            type="button"
            class="trip-viewer-contents-back"
            aria-label="Back to trip contents"
            title="Back to trip contents"
            @click="returnToDesktopContentsTop"
          >
            <span aria-hidden="true">↑</span>
          </button>
          <TripSidebar
            :state="state"
            :groups="regionGroups"
            :segments="segments"
            :selection="selection"
            :notes-back-enabled="!readableOpen"
            @select="selection => selectEntity(selection, 'desktop')"
          />
        </div>

        <div v-else class="trip-viewer-detail-body">
          <TripDetail
            :state="state"
            :entity="selected"
            @focus="selection => selectEntity(selection, 'desktop')"
          />
        </div>
      </aside>
      <button
        v-if="!isEmbed && viewportReady && !isCompactViewport && !desktopPanelOpen"
        type="button"
        class="trip-viewer-panel-toggle"
        @click="showDesktopContents"
      >
        Show trip
      </button>
      <TripMap
        :state="state"
        :segments="segments"
        :selection="selection"
        :layout-signal="layoutSignal"
        :full-trip-view-signal="fullTripViewSignal"
        @select="selection => selectEntity(selection, 'map')"
        @restore-full-trip="restoreFullTripView"
      />
      <MobileDrawer
        v-if="!isEmbed && viewportReady && isCompactViewport"
        ref="mobileDrawer"
        :state="state"
        :groups="regionGroups"
        :segments="segments"
        :selection="selection"
        :entity="selected"
        :drawer-state="drawerState"
        :return-target="detailReturnTarget"
        v-model:search-query="searchQuery"
        :notes-back-enabled="!readableOpen"
        @update:drawer-state="updateDrawerState"
        @select="selectEntity"
        @focus="selection => selectEntity(selection, 'drawer')"
        @clear="restoreFullTripView"
        @readable="openReadable"
        @print="printReadable"
      />
      <ReadableDocument
        v-if="!isEmbed && readableOpen"
        :state="state"
        :groups="regionGroups"
        :segments="segments"
        @close="closeReadable"
        @print="printReadable"
      />
      <a
        v-if="isEmbed && embedOpenAction"
        class="trip-viewer-embed-open"
        :href="embedOpenAction.url ?? '#'"
        target="_blank"
        rel="noopener noreferrer"
      >
        Open trip
      </a>
    </div>

    <div v-else class="trip-viewer-state trip-viewer-state--error" role="alert">
      <strong v-if="status === 'auth'">Trip unavailable</strong>
      <strong v-else-if="status === 'not-found'">Trip not found</strong>
      <strong v-else-if="status === 'invalid-config'">Trip Viewer configuration error</strong>
      <strong v-else>Trip Viewer failed to load</strong>
      <span>{{ detail }}</span>
      <button v-if="status === 'network-error' || status === 'server-error'" type="button" @click="loadViewerState">
        Retry
      </button>
    </div>
  </section>
</template>
