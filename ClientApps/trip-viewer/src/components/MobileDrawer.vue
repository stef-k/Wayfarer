<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import type { RegionGroup, SegmentSummary, SelectedEntity } from '../viewModel';
import { notesPreview } from '../viewModel';
import type { TripViewerState, ViewerSelection } from '../types';
import SearchPanel from './SearchPanel.vue';
import ActionsBar from './ActionsBar.vue';
import TripDetail from './TripDetail.vue';
import TripSidebar from './TripSidebar.vue';

export type DrawerState = 'collapsed' | 'peek' | 'hierarchy' | 'detail';

const props = defineProps<{
  state: TripViewerState;
  groups: RegionGroup[];
  segments: SegmentSummary[];
  selection: ViewerSelection;
  entity: SelectedEntity;
  drawerState: DrawerState;
  returnTarget: 'peek' | 'hierarchy';
  notesBackEnabled: boolean;
  searchQuery: string;
}>();

const emit = defineEmits<{
  'update:drawerState': [state: DrawerState];
  select: [selection: ViewerSelection, source: 'drawer' | 'hierarchy'];
  focus: [selection: ViewerSelection];
  clear: [];
  readable: [];
  print: [];
  'update:searchQuery': [query: string];
}>();

const drawerElement = ref<HTMLElement | null>(null);
const closeButton = ref<HTMLButtonElement | null>(null);
const contentsBody = ref<HTMLElement | null>(null);
const coverVisible = ref(true);
const contentsScrollTop = ref<number | null>(null);
const contentsOverflowing = ref(false);
const showContentsBack = ref(false);
const isExpanded = computed(() => props.drawerState === 'hierarchy' || props.drawerState === 'detail');
const selectedSummary = computed(() => notesPreview(props.entity.notes, 86));

watch(() => props.drawerState, state => {
  if (state === 'hierarchy' || state === 'detail') {
    void nextTick(() => {
      closeButton.value?.focus();
    });
  }
});

const setDrawerState = (state: DrawerState): void => emit('update:drawerState', state);

function toggleCollapsed(): void {
  setDrawerState(props.drawerState === 'collapsed' ? 'peek' : 'collapsed');
}

function selectFromHierarchy(selection: ViewerSelection): void {
  captureContentsScroll();
  emit('select', selection, 'hierarchy');
}

function closeSubview(): void {
  const target = props.drawerState === 'detail' ? props.returnTarget : 'peek';
  setDrawerState(target);
  if (target === 'hierarchy') {
    restoreContentsScroll();
  }
}

// Exposed to App so map-originated detail selection preserves the visible compact hierarchy too.
function captureContentsScroll(): void {
  if (props.drawerState === 'hierarchy' && contentsBody.value) {
    contentsScrollTop.value = contentsBody.value.scrollTop;
  }
}

function updateContentsScrollState(): void {
  const body = contentsBody.value;
  if (!body) {
    contentsOverflowing.value = false;
    showContentsBack.value = false;
    return;
  }

  contentsOverflowing.value = body.scrollHeight > body.clientHeight + 1;
  showContentsBack.value = contentsOverflowing.value && body.scrollTop > 160;
}

function returnToContentsTop(): void {
  contentsBody.value?.scrollTo({ top: 0, behavior: 'smooth' });
  void nextTick(() => drawerElement.value?.querySelector<HTMLElement>('.trip-viewer-command-header__identity h1')?.focus());
}

function restoreContentsScroll(): void {
  const scrollTop = contentsScrollTop.value;
  if (scrollTop == null) return;

  void nextTick(() => {
    window.requestAnimationFrame(() => {
      window.requestAnimationFrame(() => {
        contentsBody.value?.scrollTo({ top: scrollTop });
        updateContentsScrollState();
      });
    });
  });
}

defineExpose({ captureContentsScroll });

function handleKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Escape' || !isExpanded.value) return;
  event.preventDefault();
  setDrawerState('peek');
}
</script>

<template>
  <aside
    ref="drawerElement"
    class="trip-viewer-mobile-drawer"
    :class="`trip-viewer-mobile-drawer--${drawerState}`"
    aria-label="Trip viewer drawer"
    :aria-expanded="drawerState !== 'collapsed'"
    @keydown="handleKeydown"
  >
    <header class="trip-viewer-mobile-drawer__chrome">
      <button
        type="button"
        class="trip-viewer-mobile-drawer__handle"
        :aria-label="drawerState === 'collapsed' ? 'Open trip drawer' : 'Collapse trip drawer'"
        @click="toggleCollapsed"
      >
        <span aria-hidden="true"></span>
      </button>

      <div class="trip-viewer-mobile-drawer__title">
        <span>{{ entity.eyebrow }}</span>
        <strong>{{ entity.title }}</strong>
      </div>

      <button
        v-if="drawerState === 'hierarchy' || drawerState === 'detail'"
        ref="closeButton"
        type="button"
        class="trip-viewer-mobile-drawer__icon-button"
        :aria-label="drawerState === 'detail' ? 'Back from trip details' : 'Close trip hierarchy'"
        @click="closeSubview"
      >
        Back
      </button>
    </header>

    <div v-if="drawerState === 'collapsed'" class="trip-viewer-mobile-drawer__collapsed">
      <span>{{ state.trip.name }}</span>
    </div>

    <section v-else-if="drawerState === 'peek'" class="trip-viewer-mobile-drawer__peek" aria-label="Selected trip summary">
      <p v-if="selectedSummary">{{ selectedSummary }}</p>
      <p v-else>{{ state.regionOrder.length }} regions · {{ Object.keys(state.placesById).length }} places · {{ state.segmentOrder.length }} segments</p>
      <div class="trip-viewer-mobile-drawer__actions">
        <button type="button" @click="setDrawerState('hierarchy')">Browse trip contents</button>
      </div>
    </section>

    <section v-else-if="drawerState === 'hierarchy'" class="trip-viewer-mobile-drawer__panel trip-viewer-mobile-drawer__panel--hierarchy" aria-label="Trip hierarchy">
      <header class="trip-viewer-command-header trip-viewer-command-header--drawer">
        <!-- Mobile reuses the same returned-only cover and ActionsBar contract as desktop. -->
        <img
          v-if="coverVisible && state.trip.coverImage?.displayUrl"
          class="trip-viewer-command-header__cover"
          :src="state.trip.coverImage.displayUrl"
          :alt="`Cover for ${state.trip.name}`"
          loading="eager"
          @error="coverVisible = false"
        >
        <div class="trip-viewer-command-header__identity">
          <h1 tabindex="-1">{{ state.trip.name }}</h1>
          <p>
            <template v-if="state.trip.ownerDisplayName">{{ state.trip.ownerDisplayName }} · </template>
            {{ state.regionOrder.length }} regions · {{ Object.keys(state.placesById).length }} places · {{ state.segmentOrder.length }} segments
          </p>
        </div>
        <SearchPanel
          :model-value="searchQuery"
          :state="state"
          @update:model-value="emit('update:searchQuery', $event)"
          @select="selectFromHierarchy"
          @clear="emit('clear')"
        />
        <ActionsBar :actions="state.actions" :embed="false" @readable="emit('readable')" @print="emit('print')" />
      </header>
      <div
        ref="contentsBody"
        class="trip-viewer-mobile-drawer__panel-body"
        @scroll="updateContentsScrollState"
      >
        <button
          v-if="notesBackEnabled && contentsOverflowing && showContentsBack"
          type="button"
          class="trip-viewer-contents-back"
          aria-label="Back to trip contents"
          title="Back to trip contents"
          @click="returnToContentsTop"
        >
          <span aria-hidden="true">↑</span>
        </button>
        <TripSidebar
          :state="state"
          :groups="groups"
          :segments="segments"
          :selection="selection"
          :notes-back-enabled="notesBackEnabled"
          @select="selectFromHierarchy"
        />
      </div>
    </section>

    <section v-else class="trip-viewer-mobile-drawer__panel" aria-label="Selected trip details">
      <TripDetail
        :state="state"
        :entity="entity"
        @focus="emit('focus', $event)"
      />
    </section>
  </aside>
</template>
