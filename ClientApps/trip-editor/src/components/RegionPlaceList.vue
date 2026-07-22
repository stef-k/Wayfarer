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
  placePreviewById: Record<Guid, Pick<EditorPlace, 'iconName' | 'markerColor'>>;
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
const optimisticRegionIds = ref<Guid[] | null>(null);
const optimisticPlaceIdsByRegionId = ref<Record<Guid, Guid[]>>({});
const placeSortables = new Map<string, { destroy: () => void }>();
const areaSortables = new Map<string, { destroy: () => void }>();
let sortable: { destroy: () => void } | null = null;
let reorderSnapshotIds: Guid[] | null = null;
let placeReorderSnapshot: { regionId: Guid; ids: Guid[] } | null = null;
let areaReorderSnapshot: { regionId: Guid; ids: Guid[] } | null = null;
let reorderEmissionLocked = false;

watch(
  () => props.isOrdering,
  value => {
    if (!value) {
      reorderEmissionLocked = false;
    }
  }
);

watch(
  () => props.state.regionOrder.join('|'),
  () => {
    optimisticRegionIds.value = null;
  }
);

watch(
  () => Object.entries(props.state.placeOrderByRegionId).map(([regionId, ids]) => `${regionId}:${ids.join(',')}`).join('|'),
  () => {
    optimisticPlaceIdsByRegionId.value = {};
  }
);

watch(
  () => `${props.searchActive}|${props.isOrdering}|${props.regions.map(region => region.id).join('|')}|${Object.entries(props.placeIdsByRegionId).map(([regionId, ids]) => `${regionId}:${ids.join(',')}`).join('|')}|${Object.entries(props.areaIdsByRegionId).map(([regionId, ids]) => `${regionId}:${ids.join(',')}`).join('|')}`,
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
  if (props.searchActive || props.isOrdering || !regionList.value || !window.Sortable) {
    return;
  }

  sortable = window.Sortable.create(regionList.value, {
    animation: 150,
    draggable: '.trip-editor-region-card--normal',
    handle: '.trip-editor-drag-handle',
    onStart: () => {
      if (reorderLocked()) {
        reorderSnapshotIds = null;
        return;
      }

      reorderSnapshotIds = normalRegionIds();
    },
    onEnd: () => {
      if (reorderLocked() || !regionList.value) {
        reorderSnapshotIds = null;
        return;
      }

      const previousIds = reorderSnapshotIds ?? normalRegionIds();
      reorderSnapshotIds = null;
      const ids = Array.from(regionList.value.querySelectorAll<HTMLElement>('[data-region-id][data-reorderable="true"]')).map(element => element.dataset.regionId!);
      if (ids.join('|') !== previousIds.join('|')) {
        reorderEmissionLocked = true;
        optimisticRegionIds.value = ids;
        emit('regionReorder', ids, previousIds);
      }
    }
  });
}

function attachPlaceSortables(): void {
  destroyPlaceSortables();
  if (props.searchActive || props.isOrdering || !window.Sortable) {
    return;
  }

  document.querySelectorAll<HTMLElement>('[data-place-list-region-id]').forEach(element => {
    const regionId = element.dataset.placeListRegionId!;
    placeSortables.set(regionId, window.Sortable!.create(element, {
      animation: 150,
      draggable: '.trip-editor-place-row',
      handle: '.trip-editor-place-drag-handle',
      onStart: () => {
        if (reorderLocked()) {
          placeReorderSnapshot = null;
          return;
        }

        placeReorderSnapshot = { regionId, ids: [...(props.state.placeOrderByRegionId[regionId] ?? [])] };
      },
      onEnd: () => {
        if (reorderLocked()) {
          placeReorderSnapshot = null;
          return;
        }

        const previousIds = placeReorderSnapshot?.ids ?? [...(props.state.placeOrderByRegionId[regionId] ?? [])];
        placeReorderSnapshot = null;
        const ids = Array.from(element.querySelectorAll<HTMLElement>('[data-place-id]')).map(row => row.dataset.placeId!);
        if (ids.join('|') !== previousIds.join('|')) {
          reorderEmissionLocked = true;
          optimisticPlaceIdsByRegionId.value = { ...optimisticPlaceIdsByRegionId.value, [regionId]: ids };
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
  if (props.searchActive || props.isOrdering || !window.Sortable) {
    return;
  }

  document.querySelectorAll<HTMLElement>('[data-area-list-region-id]').forEach(element => {
    const regionId = element.dataset.areaListRegionId!;
    areaSortables.set(regionId, window.Sortable!.create(element, {
      animation: 150,
      draggable: '.trip-editor-area-row',
      handle: '.trip-editor-area-drag-handle',
      onStart: () => {
        if (reorderLocked()) {
          areaReorderSnapshot = null;
          return;
        }

        areaReorderSnapshot = { regionId, ids: [...(props.state.areaOrderByRegionId[regionId] ?? [])] };
      },
      onEnd: () => {
        if (reorderLocked()) {
          areaReorderSnapshot = null;
          return;
        }

        const previousIds = areaReorderSnapshot?.ids ?? [...(props.state.areaOrderByRegionId[regionId] ?? [])];
        areaReorderSnapshot = null;
        const ids = Array.from(element.querySelectorAll<HTMLElement>('[data-area-id]')).map(row => row.dataset.areaId!);
        if (ids.join('|') !== previousIds.join('|')) {
          reorderEmissionLocked = true;
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
  return props.state.regionOrder.filter(id => !props.state.regionsById[id]?.isShadow);
}

/// Blocks same-turn callbacks before the parent ordering prop has rendered.
function reorderLocked(): boolean {
  return props.isOrdering || reorderEmissionLocked;
}

/// Resolves a visible region label from the complete authoritative or transient normal-region order.
function regionLabel(region: EditorRegion): string {
  if (region.isShadow) {
    return `0-${region.name}`;
  }

  const ids = optimisticRegionIds.value
    ?? props.state.regionOrder.filter(id => !props.state.regionsById[id]?.isShadow);
  return `${ids.indexOf(region.id) + 1}-${region.name}`;
}

/// Resolves a place label from its complete authoritative or transient parent-region order.
function placeLabel(place: EditorPlace): string {
  const ids = optimisticPlaceIdsByRegionId.value[place.regionId]
    ?? props.state.placeOrderByRegionId[place.regionId]
    ?? [];
  return `${ids.indexOf(place.id) + 1}-${place.name}`;
}

function orderedPlaces(regionId: Guid): EditorPlace[] {
  return (props.placeIdsByRegionId[regionId] ?? []).map(id => props.state.placesById[id]).filter(Boolean) as EditorPlace[];
}

/// Applies active place draft display values without mutating persisted editor state.
function displayPlace(place: EditorPlace): EditorPlace {
  const preview = props.placePreviewById[place.id];
  return preview ? { ...place, ...preview } : place;
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

function moveAreaByKeyboard(regionId: Guid, areaId: Guid, offset: number): void {
  if (props.searchActive || props.isOrdering) {
    return;
  }

  const ids = [...(props.areaIdsByRegionId[regionId] ?? [])];
  const index = ids.indexOf(areaId);
  const nextIndex = index + offset;
  if (index < 0 || nextIndex < 0 || nextIndex >= ids.length) {
    return;
  }

  const previousIds = [...(props.state.areaOrderByRegionId[regionId] ?? [])];
  const [id] = ids.splice(index, 1);
  ids.splice(nextIndex, 0, id);
  emit('areaReorder', regionId, ids, previousIds);
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
      :data-region-name="region.name"
      :data-reorderable="!region.isShadow"
    >
      <header class="trip-editor-region-card__header">
        <button
          v-if="!region.isShadow"
          type="button"
          class="trip-editor-icon-button trip-editor-drag-handle"
          title="Drag to reorder region"
          aria-label="Drag to reorder region"
          :disabled="props.searchActive || props.isOrdering"
        >
          <span aria-hidden="true">::</span>
        </button>
        <div>
          <h3 class="itinerary-region-label">{{ regionLabel(region) }}</h3>
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
            :data-place-name="place.name"
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
              :disabled="props.searchActive || props.isOrdering"
            >
              <span aria-hidden="true">::</span>
            </button>
            <span class="trip-editor-place-row__icon" aria-hidden="true">
              <img :src="placeMarkerIconUrl(displayPlace(place).iconName, displayPlace(place).markerColor)" width="24" height="39" alt="" data-sidebar-place-icon />
              <span v-if="place.visitSummary.isVisited" class="trip-editor-place-row__visit-badge">{{ place.visitSummary.visitCount === 1 ? '✓' : place.visitSummary.visitCount }}</span>
            </span>
            <span class="trip-editor-place-row__content">
              <span class="trip-editor-place-row__name itinerary-place-label">{{ placeLabel(place) }}</span>
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
                @keydown.arrow-up.prevent="moveAreaByKeyboard(region.id, area.id, -1)"
                @keydown.arrow-down.prevent="moveAreaByKeyboard(region.id, area.id, 1)"
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
      <div v-if="!region.isShadow || canAddArea(region)" class="trip-editor-region-card__add-actions">
        <button v-if="!region.isShadow" type="button" class="btn btn-outline-light btn-sm" :disabled="props.isSaving || props.isOrdering" @click="emit('addPlace', region)">
          Add Place
        </button>
        <button v-if="canAddArea(region)" type="button" class="btn btn-outline-light btn-sm" :disabled="props.isSaving || props.isOrdering" @click="emit('addArea', region)">
          Add Area
        </button>
      </div>
      <slot name="add-place-editor" :region="region"></slot>
      <slot name="add-area-editor" :region="region"></slot>
    </article>
  </div>
</template>
