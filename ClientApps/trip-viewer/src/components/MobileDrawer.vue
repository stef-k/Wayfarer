<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import type { RegionGroup, SegmentSummary, SelectedEntity } from '../viewModel';
import { notesPreview } from '../viewModel';
import type { TripViewerState, ViewerSelection } from '../types';
import SearchPanel from './SearchPanel.vue';
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
}>();

const emit = defineEmits<{
  'update:drawerState': [state: DrawerState];
  select: [selection: ViewerSelection, source: 'drawer' | 'hierarchy'];
  focus: [selection: ViewerSelection];
  readable: [];
  print: [];
}>();

const drawerElement = ref<HTMLElement | null>(null);
const closeButton = ref<HTMLButtonElement | null>(null);
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
  emit('select', selection, 'hierarchy');
}

function closeSubview(): void {
  setDrawerState(props.drawerState === 'detail' ? props.returnTarget : 'peek');
}

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

    <section v-else-if="drawerState === 'hierarchy'" class="trip-viewer-mobile-drawer__panel" aria-label="Trip hierarchy">
      <SearchPanel :state="state" @select="selectFromHierarchy" />
      <TripSidebar
        :state="state"
        :groups="groups"
        :segments="segments"
        :selection="selection"
        @select="selectFromHierarchy"
      />
    </section>

    <section v-else class="trip-viewer-mobile-drawer__panel" aria-label="Selected trip details">
      <TripDetail
        :state="state"
        :entity="entity"
        :groups="groups"
        @focus="emit('focus', $event)"
        @readable="emit('readable')"
        @print="emit('print')"
      />
    </section>
  </aside>
</template>
