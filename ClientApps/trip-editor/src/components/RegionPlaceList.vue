<script setup lang="ts">
import { nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { placeMarkerIconUrl, placeMarkerLabel } from '../displayHelpers';
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
  activePlaceEditorId: Guid | null;
  activeAreaId: Guid | null;
  activeRegionId: Guid | null;
  areaIdsByRegionId: Record<Guid, Guid[]>;
  forceExpandedRegionIds: Set<Guid>;
  isOrdering: boolean;
  isSaving: boolean;
  placeIdsByRegionId: Record<Guid, Guid[]>;
  regions: EditorRegion[];
  searchActive: boolean;
  state: EditorTripState;
}>();

const emit = defineEmits<{
  addPlace: [region: EditorRegion];
  addArea: [region: EditorRegion];
  editArea: [area: EditorArea];
  editPlace: [place: EditorPlace];
  editRegion: [region: EditorRegion];
  selectPlace: [place: EditorPlace];
  areaReorder: [regionId: Guid, ids: Guid[], previousIds: Guid[]];
  placeReorder: [regionId: Guid, ids: Guid[], previousIds: Guid[]];
  regionReorder: [ids: Guid[], previousIds: Guid[]];
}>();

const regionList = ref<HTMLElement | null>(null);
const collapsedRegionIds = ref<Set<Guid>>(new Set());
const searchCollapsedSnapshot = ref<Set<Guid> | null>(null);
const placeSortables = new Map<string, { destroy: () => void }>();
const areaSortables = new Map<string, { destroy: () => void }>();
let sortable: { destroy: () => void } | null = null;
let reorderSnapshotIds: Guid[] | null = null;
let placeReorderSnapshot: { regionId: Guid; ids: Guid[] } | null = null;
let areaReorderSnapshot: { regionId: Guid; ids: Guid[] } | null = null;

watch(
  () => `${props.searchActive}|${props.regions.map(region => region.id).join('|')}|${Object.entries(props.placeIdsByRegionId).map(([regionId, ids]) => `${regionId}:${ids.join(',')}`).join('|')}|${Object.entries(props.areaIdsByRegionId).map(([regionId, ids]) => `${regionId}:${ids.join(',')}`).join('|')}`,
  async () => {
    await nextTick();
    attachSortables();
  }
);

watch(
  () => props.searchActive,
  value => {
    if (value) {
      searchCollapsedSnapshot.value = new Set(collapsedRegionIds.value);
      return;
    }

    if (searchCollapsedSnapshot.value) {
      collapsedRegionIds.value = new Set(searchCollapsedSnapshot.value);
      searchCollapsedSnapshot.value = null;
    }
  }
);

watch(
  () => props.activePlaceId,
  async placeId => {
    if (!placeId) {
      return;
    }

    await nextTick();
    regionList.value
      ?.querySelector<HTMLElement>(`[data-place-id="${placeId}"]`)
      ?.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'nearest' });
  }
);

onMounted(attachSortables);

onUnmounted(() => {
  sortable?.destroy();
  destroyPlaceSortables();
  destroyAreaSortables();
});

function attachSortables(): void {
  attachRegionSortable();
  attachPlaceSortables();
  attachAreaSortables();
}

function attachRegionSortable(): void {
  sortable?.destroy();
  sortable = null;
  if (props.searchActive || !regionList.value || !window.Sortable) {
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
  if (props.searchActive || !window.Sortable) {
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

function attachAreaSortables(): void {
  destroyAreaSortables();
  if (props.searchActive || !window.Sortable) {
    return;
  }

  document.querySelectorAll<HTMLElement>('[data-area-list-region-id]').forEach(element => {
    const regionId = element.dataset.areaListRegionId!;
    areaSortables.set(regionId, window.Sortable!.create(element, {
      animation: 150,
      draggable: '.trip-editor-area-row',
      handle: '.trip-editor-area-drag-handle',
      onStart: () => {
        areaReorderSnapshot = { regionId, ids: [...(props.state.areaOrderByRegionId[regionId] ?? [])] };
      },
      onEnd: () => {
        const previousIds = areaReorderSnapshot?.ids ?? [...(props.state.areaOrderByRegionId[regionId] ?? [])];
        areaReorderSnapshot = null;
        const ids = Array.from(element.querySelectorAll<HTMLElement>('[data-area-id]')).map(row => row.dataset.areaId!);
        if (ids.join('|') !== previousIds.join('|')) {
          emit('areaReorder', regionId, ids, previousIds);
        }
      }
    }));
  });
}

function destroyAreaSortables(): void {
  areaSortables.forEach(instance => instance.destroy());
  areaSortables.clear();
}

function normalRegionIds(): Guid[] {
  return props.regions.filter(region => !region.isShadow).map(region => region.id);
}

function orderedPlaces(regionId: Guid): EditorPlace[] {
  return (props.placeIdsByRegionId[regionId] ?? []).map(id => props.state.placesById[id]).filter(Boolean) as EditorPlace[];
}

function orderedAreas(regionId: Guid): EditorArea[] {
  return (props.areaIdsByRegionId[regionId] ?? []).map(id => props.state.areasById[id]).filter(Boolean) as EditorArea[];
}

function canAddArea(region: EditorRegion): boolean {
  return props.state.permissions.canEditAreas && !region.isShadow && region.capabilities.canAddChildren;
}

function isCollapsed(regionId: Guid): boolean {
  if (props.forceExpandedRegionIds.has(regionId)) {
    return false;
  }

  return collapsedRegionIds.value.has(regionId);
}

function toggleRegion(regionId: Guid): void {
  if (props.searchActive) {
    return;
  }

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
          :disabled="props.searchActive"
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
            :disabled="props.searchActive"
            @click="toggleRegion(region.id)"
          >
            {{ isCollapsed(region.id) ? 'Expand' : 'Collapse' }}
          </button>
          <button v-if="!region.isShadow" type="button" class="btn btn-outline-light btn-sm" :disabled="props.isSaving" @click="emit('editRegion', region)">Edit</button>
        </div>
      </header>

      <slot name="region-editor" :region="region"></slot>

      <ul v-show="!isCollapsed(region.id)" :id="`trip-editor-region-children-${region.id}`" :data-place-list-region-id="region.id">
        <template v-for="place in orderedPlaces(region.id)" :key="place.id">
          <li
            class="trip-editor-place-row"
            :class="{ 'trip-editor-place-row--active': props.activePlaceId === place.id }"
            :data-place-id="place.id"
            tabindex="0"
            @click="emit('selectPlace', place)"
            @keydown.enter.prevent="emit('selectPlace', place)"
          >
            <button
              v-if="!region.isShadow"
              type="button"
              class="trip-editor-icon-button trip-editor-place-drag-handle"
              title="Drag to reorder place"
              aria-label="Drag to reorder place"
              :disabled="props.searchActive"
            >
              <span aria-hidden="true">::</span>
            </button>
            <span class="trip-editor-place-row__icon" aria-hidden="true">
              <img :src="placeMarkerIconUrl(place.iconName, place.markerColor)" width="24" height="39" alt="" data-sidebar-place-icon />
              <span v-if="place.visitSummary.isVisited" class="trip-editor-place-row__visit-badge">{{ place.visitSummary.visitCount === 1 ? '✓' : place.visitSummary.visitCount }}</span>
            </span>
            <span class="trip-editor-place-row__content">
              <span class="trip-editor-place-row__name">{{ place.name }}</span>
              <small v-if="place.visitSummary.isVisited">{{ placeMarkerLabel(place) }}</small>
            </span>
            <button type="button" class="btn btn-outline-light btn-sm" :disabled="props.isSaving || props.isOrdering" @click.stop="emit('editPlace', place)">Edit</button>
          </li>
          <li v-if="props.activePlaceEditorId === place.id" class="trip-editor-place-editor-row" aria-live="polite">
            <slot name="place-editor" :place="place"></slot>
          </li>
        </template>
        <li v-if="orderedAreas(region.id).length > 0" class="trip-editor-child-section">
          <span>Areas</span>
        </li>
        <li v-if="orderedAreas(region.id).length > 0" v-show="!isCollapsed(region.id)" :data-area-list-region-id="region.id" class="trip-editor-area-list">
          <template v-for="area in orderedAreas(region.id)" :key="area.id">
            <div
              class="trip-editor-area-row"
              :class="{ 'trip-editor-area-row--active': props.activeAreaId === area.id }"
              :data-area-id="area.id"
            >
              <button
                v-if="!region.isShadow"
                type="button"
                class="trip-editor-icon-button trip-editor-area-drag-handle"
                title="Drag to reorder area"
                aria-label="Drag to reorder area"
                :disabled="props.searchActive || props.isOrdering"
              >
                <span aria-hidden="true">::</span>
              </button>
              <span>{{ area.name }}</span>
              <small>Area</small>
              <button type="button" class="btn btn-outline-light btn-sm" :disabled="props.isSaving || props.isOrdering" @click="emit('editArea', area)">Edit</button>
            </div>
            <div v-if="props.activeAreaId === area.id" class="trip-editor-place-editor-row" aria-live="polite">
              <slot name="area-editor" :area="area"></slot>
            </div>
          </template>
        </li>
      </ul>
      <button v-if="!region.isShadow" type="button" class="btn btn-outline-light btn-sm" :disabled="props.isSaving || props.isOrdering" @click="emit('addPlace', region)">
        Add Place
      </button>
      <button v-if="canAddArea(region)" type="button" class="btn btn-outline-light btn-sm" :disabled="props.isSaving || props.isOrdering" @click="emit('addArea', region)">
        Add Area
      </button>
      <slot name="add-place-editor" :region="region"></slot>
      <slot name="add-area-editor" :region="region"></slot>
    </article>
  </div>
</template>
