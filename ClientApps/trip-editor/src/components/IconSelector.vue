<script lang="ts">
let nextSelectorId = 0;
</script>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { placeMarkerIconUrl } from '../displayHelpers';

const props = defineProps<{
  icons: string[];
  markerColor: string;
  modelValue: string;
}>();

const emit = defineEmits<{
  'update:modelValue': [value: string];
}>();

const root = ref<HTMLElement | null>(null);
const trigger = ref<HTMLButtonElement | null>(null);
const searchInput = ref<HTMLInputElement | null>(null);
const isOpen = ref(false);
const searchQuery = ref('');
const highlightedIndex = ref(0);
const selectorId = `trip-editor-icon-selector-${++nextSelectorId}`;
const listboxId = `${selectorId}-listbox`;

const normalizedQuery = computed(() => searchQuery.value.trim().toLocaleLowerCase());
const selectedIcon = computed(() => props.icons.includes(props.modelValue) ? props.modelValue : props.icons[0] ?? 'marker');
const filteredIcons = computed(() => {
  if (!normalizedQuery.value) {
    return props.icons;
  }

  return props.icons.filter(icon => icon.toLocaleLowerCase().includes(normalizedQuery.value));
});
const highlightedIcon = computed(() => filteredIcons.value[highlightedIndex.value] ?? '');
const activeOptionId = computed(() => highlightedIcon.value ? optionId(highlightedIcon.value) : undefined);

/// Converts stored icon ids into the same readable labels used by the legacy searchable selector.
const iconLabel = (icon: string): string => icon.replace(/[-_]+/g, ' ');

function openSelector(seed = ''): void {
  searchQuery.value = seed;
  highlightedIndex.value = firstHighlightedIndex(seed);
  isOpen.value = true;
  void nextTick(() => searchInput.value?.focus());
}

function closeSelector(focusTrigger = false): void {
  isOpen.value = false;
  searchQuery.value = '';
  highlightedIndex.value = Math.max(0, props.icons.indexOf(selectedIcon.value));
  if (focusTrigger) {
    void nextTick(() => trigger.value?.focus());
  }
}

function toggleSelector(): void {
  if (isOpen.value) {
    closeSelector();
    return;
  }

  openSelector();
}

function selectIcon(icon: string): void {
  emit('update:modelValue', icon);
  closeSelector(true);
}

function onTriggerKeydown(event: KeyboardEvent): void {
  if (['Enter', ' ', 'ArrowDown'].includes(event.key)) {
    event.preventDefault();
    openSelector();
    return;
  }

  if (event.key === 'ArrowUp') {
    event.preventDefault();
    openSelector();
    highlightedIndex.value = Math.max(0, filteredIcons.value.length - 1);
    return;
  }

  if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey) {
    event.preventDefault();
    openSelector(event.key);
  }
}

function onSearchKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    event.preventDefault();
    closeSelector(true);
    return;
  }

  if (event.key === 'ArrowDown') {
    event.preventDefault();
    highlightedIndex.value = Math.min(highlightedIndex.value + 1, Math.max(0, filteredIcons.value.length - 1));
    return;
  }

  if (event.key === 'ArrowUp') {
    event.preventDefault();
    highlightedIndex.value = Math.max(0, highlightedIndex.value - 1);
    return;
  }

  if (event.key === 'Home') {
    event.preventDefault();
    highlightedIndex.value = 0;
    return;
  }

  if (event.key === 'End') {
    event.preventDefault();
    highlightedIndex.value = Math.max(0, filteredIcons.value.length - 1);
    return;
  }

  if (event.key === 'Enter' && highlightedIcon.value) {
    event.preventDefault();
    selectIcon(highlightedIcon.value);
  }
}

function onSearchInput(): void {
  highlightedIndex.value = 0;
}

function onFocusOut(): void {
  void nextTick(() => {
    if (!root.value?.contains(document.activeElement)) {
      closeSelector();
    }
  });
}

function optionId(icon: string): string {
  return `${selectorId}-option-${icon.replace(/[^a-z0-9_-]/gi, '-')}`;
}

function firstHighlightedIndex(seed: string): number {
  const query = seed.trim().toLocaleLowerCase();
  if (!query) {
    return Math.max(0, props.icons.indexOf(selectedIcon.value));
  }

  return Math.max(0, props.icons.findIndex(icon => icon.toLocaleLowerCase().includes(query)));
}

/// Keeps outside clicks from leaving the popover open while preserving normal tab flow.
function onDocumentPointerDown(event: PointerEvent): void {
  if (!root.value?.contains(event.target as Node)) {
    closeSelector();
  }
}

watch(filteredIcons, icons => {
  highlightedIndex.value = Math.min(highlightedIndex.value, Math.max(0, icons.length - 1));
});

watch(activeOptionId, async id => {
  if (!id) {
    return;
  }

  await nextTick();
  document.getElementById(id)?.scrollIntoView({ block: 'nearest' });
});

onMounted(() => document.addEventListener('pointerdown', onDocumentPointerDown));
onBeforeUnmount(() => document.removeEventListener('pointerdown', onDocumentPointerDown));
</script>

<template>
  <div ref="root" class="trip-editor-icon-selector" data-icon-selector @focusout="onFocusOut">
    <button
      ref="trigger"
      type="button"
      class="trip-editor-icon-selector__trigger"
      aria-haspopup="listbox"
      :aria-expanded="isOpen"
      :aria-controls="listboxId"
      data-icon-selector-trigger
      @click="toggleSelector"
      @keydown="onTriggerKeydown"
    >
      <span class="trip-editor-icon-selector__selected">
        <img :src="placeMarkerIconUrl(selectedIcon, props.markerColor || 'bg-blue')" width="24" height="39" alt="" data-icon-selector-selected-image />
        <span class="trip-editor-icon-selector__selected-name" data-icon-selector-selected-name>{{ selectedIcon }}</span>
      </span>
      <span class="trip-editor-icon-selector__chevron" aria-hidden="true">v</span>
    </button>

    <div v-if="isOpen" class="trip-editor-icon-selector__panel" data-icon-selector-panel>
      <label class="visually-hidden" :for="`${selectorId}-search`">Search icon</label>
      <input
        :id="`${selectorId}-search`"
        ref="searchInput"
        v-model="searchQuery"
        class="trip-editor-icon-selector__search"
        type="search"
        autocomplete="off"
        role="combobox"
        aria-autocomplete="list"
        :aria-expanded="isOpen"
        :aria-controls="listboxId"
        :aria-activedescendant="activeOptionId"
        placeholder="Search icons"
        data-icon-selector-search
        @input="onSearchInput"
        @keydown="onSearchKeydown"
      />

      <ul :id="listboxId" class="trip-editor-icon-selector__options" role="listbox" aria-label="Icon options" data-icon-selector-options>
        <li
          v-for="(icon, index) in filteredIcons"
          :id="optionId(icon)"
          :key="icon"
          class="trip-editor-icon-selector__option"
          :class="{ 'trip-editor-icon-selector__option--active': highlightedIndex === index }"
          role="option"
          :aria-selected="selectedIcon === icon"
          data-icon-selector-option
          @mouseenter="highlightedIndex = index"
          @mousedown.prevent
          @click="selectIcon(icon)"
        >
          <img :src="placeMarkerIconUrl(icon, props.markerColor || 'bg-blue')" width="24" height="39" alt="" data-icon-selector-option-image />
          <span data-icon-selector-option-name>{{ iconLabel(icon) }}</span>
        </li>
        <li v-if="filteredIcons.length === 0" class="trip-editor-icon-selector__empty">No matching icons.</li>
      </ul>
    </div>
  </div>
</template>
