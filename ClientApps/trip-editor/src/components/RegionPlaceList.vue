<script setup lang="ts">
import { nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import type { EditorArea, EditorPlace, EditorRegion, EditorTripState, Guid } from '../types';

declare global {
  interface Window {
    Sortable?: {
      create: (element: HTMLElement, options: Record<string, unknown>) => { destroy: () => void };
    };
  }
}

const props = defineProps<{
  activePlaceId: Guid | null;
  activeRegionId: Guid | null;
  isOrdering: boolean;
  isSaving: boolean;
  regions: EditorRegion[];
  state: EditorTripState;
}>();

const emit = defineEmits<{
  addPlace: [region: EditorRegion];
  editPlace: [place: EditorPlace];
  editRegion: [region: EditorRegion];
  placeReorder: [regionId: Guid, ids: Guid[], previousIds: Guid[]];
  regionReorder: [ids: Guid[], previousIds: Guid[]];
}>();

const regionList = ref<HTMLElement | null>(null);
const collapsedRegionIds = ref<Set<Guid>>(new Set());
const placeSortables = new Map<string, { destroy: () => void }>();
let sortable: { destroy: () => void } | null = null;
let reorderSnapshotIds: Guid[] | null = null;
let placeReorderSnapshot: { regionId: Guid; ids: Guid[] } | null = null;

watch(
  () => `${props.regions.map(region => region.id).join('|')}|${Object.entries(props.state.placeOrderByRegionId).map(([regionId, ids]) => `${regionId}:${ids.join(',')}`).join('|')}`,
  async () => {
    await nextTick();
    attachSortables();
  }
);

onMounted(attachSortables);

onUnmounted(() => {
  sortable?.destroy();
  destroyPlaceSortables();
});

function attachSortables(): void {
  attachRegionSortable();
  attachPlaceSortables();
}

function attachRegionSortable(): void {
  sortable?.destroy();
  sortable = null;
  if (!regionList.value || !window.Sortable) {
    return;
  }

  sortable = window.Sortable.create(regionList.value, {
    animation: 150,
    draggable: '.trip-editor-region-card--normal',
    handle: '.trip-editor-drag-handle',
    onStart: () => {
      reorderSnapshotIds = normalRegionIds();
    },
    onEnd: () => {
      if (!regionList.value) {
        return;
      }

      const previousIds = reorderSnapshotIds ?? normalRegionIds();
      reorderSnapshotIds = null;
      const ids = Array.from(regionList.value.querySelectorAll<HTMLElement>('[data-region-id][data-reorderable="true"]')).map(element => element.dataset.regionId!);
      if (ids.join('|') !== previousIds.join('|')) {
        emit('regionReorder', ids, previousIds);
      }
    }
  });
}

function attachPlaceSortables(): void {
  destroyPlaceSortables();
  if (!window.Sortable) {
    return;
  }

  document.querySelectorAll<HTMLElement>('[data-place-list-region-id]').forEach(element => {
    const regionId = element.dataset.placeListRegionId!;
    placeSortables.set(regionId, window.Sortable!.create(element, {
      animation: 150,
      draggable: '.trip-editor-place-row',
      handle: '.trip-editor-place-drag-handle',
      onStart: () => {
        placeReorderSnapshot = { regionId, ids: [...(props.state.placeOrderByRegionId[regionId] ?? [])] };
      },
      onEnd: () => {
        const previousIds = placeReorderSnapshot?.ids ?? [...(props.state.placeOrderByRegionId[regionId] ?? [])];
        placeReorderSnapshot = null;
        const ids = Array.from(element.querySelectorAll<HTMLElement>('[data-place-id]')).map(row => row.dataset.placeId!);
        if (ids.join('|') !== previousIds.join('|')) {
          emit('placeReorder', regionId, ids, previousIds);
        }
      }
    }));
  });
}

function destroyPlaceSortables(): void {
  placeSortables.forEach(instance => instance.destroy());
  placeSortables.clear();
}

function normalRegionIds(): Guid[] {
  return props.regions.filter(region => !region.isShadow).map(region => region.id);
}

function orderedPlaces(regionId: Guid): EditorPlace[] {
  return (props.state.placeOrderByRegionId[regionId] ?? []).map(id => props.state.placesById[id]).filter(Boolean) as EditorPlace[];
}

function orderedAreas(regionId: Guid): EditorArea[] {
  return (props.state.areaOrderByRegionId[regionId] ?? []).map(id => props.state.areasById[id]).filter(Boolean) as EditorArea[];
}

function isCollapsed(regionId: Guid): boolean {
  return collapsedRegionIds.value.has(regionId);
}

function toggleRegion(regionId: Guid): void {
  const next = new Set(collapsedRegionIds.value);
  if (next.has(regionId)) {
    next.delete(regionId);
  } else {
    next.add(regionId);
  }

  collapsedRegionIds.value = next;
}
</script>

<template>
  <div ref="regionList" class="trip-editor-region-list">
    <article
      v-for="region in props.regions"
      :key="region.id"
      class="trip-editor-region-card"
      :class="{
        'trip-editor-region-card--active': props.activeRegionId === region.id,
        'trip-editor-region-card--normal': !region.isShadow,
        'trip-editor-region-card--shadow': region.isShadow
      }"
      :data-region-id="region.id"
      :data-reorderable="!region.isShadow"
    >
      <header class="trip-editor-region-card__header">
        <button
          v-if="!region.isShadow"
          type="button"
          class="trip-editor-icon-button trip-editor-drag-handle"
          title="Drag to reorder region"
          aria-label="Drag to reorder region"
        >
          <span aria-hidden="true">::</span>
        </button>
        <div>
          <h3>{{ region.name }}</h3>
          <small v-if="region.isShadow">Shadow region</small>
        </div>
        <div class="trip-editor-region-card__actions">
          <button
            type="button"
            class="btn btn-outline-light btn-sm"
            :aria-expanded="!isCollapsed(region.id)"
            :aria-controls="`trip-editor-region-children-${region.id}`"
            @click="toggleRegion(region.id)"
          >
            {{ isCollapsed(region.id) ? 'Expand' : 'Collapse' }}
          </button>
          <button v-if="!region.isShadow" type="button" class="btn btn-outline-light btn-sm" :disabled="props.isSaving" @click="emit('editRegion', region)">Edit</button>
        </div>
      </header>

      <slot name="region-editor" :region="region"></slot>

      <ul v-show="!isCollapsed(region.id)" :id="`trip-editor-region-children-${region.id}`" :data-place-list-region-id="region.id">
        <li
          v-for="place in orderedPlaces(region.id)"
          :key="place.id"
          class="trip-editor-place-row"
          :class="{ 'trip-editor-place-row--active': props.activePlaceId === place.id }"
          :data-place-id="place.id"
        >
          <button
            v-if="!region.isShadow"
            type="button"
            class="trip-editor-icon-button trip-editor-place-drag-handle"
            title="Drag to reorder place"
            aria-label="Drag to reorder place"
          >
            <span aria-hidden="true">::</span>
          </button>
          <span>{{ place.name }}</span>
          <small v-if="place.visitSummary.isVisited">{{ place.visitSummary.visitCount }} visit(s)</small>
          <button type="button" class="btn btn-outline-light btn-sm" :disabled="props.isSaving || props.isOrdering" @click="emit('editPlace', place)">Edit</button>
          <slot name="place-editor" :place="place"></slot>
        </li>
        <li v-for="area in orderedAreas(region.id)" :key="area.id">
          <span>{{ area.name }}</span>
          <small>Area</small>
        </li>
      </ul>
      <button v-if="!region.isShadow" type="button" class="btn btn-outline-light btn-sm" :disabled="props.isSaving || props.isOrdering" @click="emit('addPlace', region)">
        Add Place
      </button>
      <slot name="add-place-editor" :region="region"></slot>
    </article>
  </div>
</template>
