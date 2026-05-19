<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import type { EditorPlace, EditorRegion, EditorTripState, EditorVisitHistoryRow, EditorPlaceVisitSummary, Guid } from '../types';
import type { EditorSurfaceController } from '../composables/useEditorSurface';

type VisitFilter = 'all' | 'visited' | 'not-visited';

type VisitPlaceRow = {
  place: EditorPlace | null;
  placeId: Guid;
  placeName: string;
  regionId: Guid;
  summary: EditorPlaceVisitSummary;
  historyRows: EditorVisitHistoryRow[];
};

type VisitRegionGroup = {
  regionId: Guid;
  regionName: string;
  visitedCount: number;
  totalCount: number;
  percentVisited: number;
  rows: VisitPlaceRow[];
};

const props = defineProps<{
  isOpen: boolean;
  state: EditorTripState;
  editorSurface: EditorSurfaceController;
}>();

const emit = defineEmits<{
  close: [];
}>();

const closeButton = ref<HTMLButtonElement | null>(null);
const filter = ref<VisitFilter>('all');
const titleId = 'trip-editor-visit-progress-title';

const totalPlaces = computed(() => props.state.visitProgress.totalPlaces);
const hasVisits = computed(() => props.state.visitProgress.visitedPlaces > 0);
const regionGroups = computed<VisitRegionGroup[]>(() => {
  const groups: VisitRegionGroup[] = [];
  const orderedHistory = [...props.state.visitProgress.historyRows].sort(compareHistoryRows);

  for (const regionId of props.state.regionOrder) {
    const placeIds = props.state.placeOrderByRegionId[regionId] ?? [];
    const allRows = placeIds
      .map(placeId => visitPlaceRow(regionId, placeId, orderedHistory))
      .filter(row => row) as VisitPlaceRow[];
    const rows = allRows.filter(row => filterMatches(row.summary));

    if (rows.length > 0) {
      const visitedCount = allRows.filter(row => row.summary.isVisited).length;
      const totalCount = allRows.length;

      groups.push({
        regionId,
        regionName: regionName(regionId),
        visitedCount,
        totalCount,
        percentVisited: percentVisited(visitedCount, totalCount),
        rows
      });
    }
  }

  return groups;
});

const emptyState = computed(() => {
  if (totalPlaces.value === 0) {
    return 'No places in this trip yet.';
  }

  if (filter.value === 'visited' && regionGroups.value.length === 0) {
    return 'No visited places yet.';
  }

  if (filter.value === 'not-visited' && regionGroups.value.length === 0) {
    return 'All places have visits.';
  }

  return null;
});

watch(
  () => props.isOpen,
  async isOpen => {
    if (!isOpen) {
      filter.value = 'all';
      return;
    }

    await nextTick();
    closeButton.value?.focus();
  }
);

function visitPlaceRow(regionId: Guid, placeId: Guid, orderedHistory: EditorVisitHistoryRow[]): VisitPlaceRow | null {
  const place = props.state.placesById[placeId] ?? null;
  const summary = props.state.visitProgress.placeSummariesByPlaceId[placeId] ?? place?.visitSummary;
  if (!summary) {
    return null;
  }

  return {
    place,
    placeId,
    placeName: place?.name || 'Unknown place',
    regionId,
    summary,
    historyRows: orderedHistory.filter(row => row.placeId === placeId)
  };
}

function filterMatches(summary: EditorPlaceVisitSummary): boolean {
  if (filter.value === 'visited') {
    return summary.isVisited;
  }

  if (filter.value === 'not-visited') {
    return !summary.isVisited;
  }

  return true;
}

function regionName(regionId: Guid): string {
  return props.state.regionsById[regionId]?.name || 'Unknown region';
}

function percentVisited(visitedCount: number, totalCount: number): number {
  return totalCount > 0 ? Math.round((visitedCount / totalCount) * 100) : 0;
}

function historyRegionName(row: EditorVisitHistoryRow, fallbackRegionId: Guid): string {
  return props.state.regionsById[row.regionId]?.name || props.state.regionsById[fallbackRegionId]?.name || 'Unknown region';
}

function compareHistoryRows(left: EditorVisitHistoryRow, right: EditorVisitHistoryRow): number {
  const startedAt = Date.parse(right.startedAt) - Date.parse(left.startedAt);
  if (startedAt !== 0) {
    return startedAt;
  }

  return left.visitId.localeCompare(right.visitId);
}

function formatTimestamp(value: string | null): string {
  if (!value) {
    return 'Not available';
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return 'Not available';
  }

  return `${date.toISOString().slice(0, 16).replace('T', ' ')} UTC`;
}

function formatEndedAt(value: string | null): string {
  return value ? formatTimestamp(value) : 'Open';
}

function formatDuration(minutes: number | null): string {
  if (minutes === null) {
    return 'Duration unavailable';
  }

  if (minutes < 60) {
    return `${minutes} min`;
  }

  const hours = Math.floor(minutes / 60);
  const remainingMinutes = minutes % 60;
  return remainingMinutes === 0 ? `${hours} hr` : `${hours} hr ${remainingMinutes} min`;
}

function manageVisitHref(visitId: Guid): string {
  return `/User/Visit/Edit/${visitId}`;
}

async function manageVisit(event: MouseEvent, visitId: Guid): Promise<void> {
  event.preventDefault();
  const canNavigate = await props.editorSurface.closeActiveTarget('Discard unsaved editor changes before managing this visit?');
  if (!canNavigate) {
    return;
  }

  const returnUrl = `${window.location.pathname}${window.location.search}${window.location.hash}`;
  window.location.href = `${manageVisitHref(visitId)}?returnUrl=${encodeURIComponent(returnUrl)}`;
}
</script>

<template>
  <Teleport to="body">
    <div v-if="isOpen" class="trip-editor-expanded trip-editor-visit-progress">
      <div class="trip-editor-expanded__backdrop" aria-hidden="true"></div>
      <section class="trip-editor-expanded__dialog" role="dialog" aria-modal="true" :aria-labelledby="titleId">
        <header class="trip-editor-surface__header trip-editor-expanded__header">
          <div>
            <p class="trip-editor-surface__eyebrow">Read-only visits</p>
            <h2 :id="titleId">Visit progress and history</h2>
            <small>{{ state.visitProgress.visitedPlaces }} / {{ state.visitProgress.totalPlaces }} places visited</small>
          </div>
          <div class="trip-editor-surface__controls">
            <button ref="closeButton" type="button" class="btn btn-outline-secondary btn-sm" @click="emit('close')">Close</button>
          </div>
        </header>

        <div class="trip-editor-expanded__body trip-editor-visit-progress__body">
          <section class="trip-editor-visit-progress__summary" aria-label="Trip visit progress summary">
            <div class="trip-editor-visit-progress__summary-heading">
              <div>
                <span>Visit progress</span>
                <strong>Your overall trip progress</strong>
              </div>
              <strong class="trip-editor-visit-progress__percent">{{ state.visitProgress.percentVisited }}%</strong>
            </div>
            <div
              class="trip-editor-progress trip-editor-visit-progress__bar"
              role="progressbar"
              aria-label="Overall visit progress"
              :aria-valuenow="state.visitProgress.percentVisited"
              aria-valuemin="0"
              aria-valuemax="100"
            >
              <span :style="{ width: `${state.visitProgress.percentVisited}%` }"></span>
            </div>
            <p class="trip-editor-visit-progress__summary-count">
              <span class="trip-editor-visit-status trip-editor-visit-status--visited" aria-hidden="true">&check;</span>
              {{ state.visitProgress.visitedPlaces }} / {{ state.visitProgress.totalPlaces }} places visited
            </p>
            <p v-if="totalPlaces > 0 && !hasVisits">No visit history yet.</p>
          </section>

          <fieldset class="trip-editor-visit-progress__filters" aria-label="Visit filters">
            <legend>Filter places</legend>
            <label class="trip-editor-visit-filter-option">
              <input v-model="filter" type="radio" name="trip-editor-visit-filter" value="all" />
              <span>All</span>
            </label>
            <label class="trip-editor-visit-filter-option">
              <input v-model="filter" type="radio" name="trip-editor-visit-filter" value="visited" />
              <span><span class="trip-editor-visit-status trip-editor-visit-status--visited" aria-hidden="true">&check;</span>Visited</span>
            </label>
            <label class="trip-editor-visit-filter-option">
              <input v-model="filter" type="radio" name="trip-editor-visit-filter" value="not-visited" />
              <span><span class="trip-editor-visit-status trip-editor-visit-status--pending" aria-hidden="true"></span>Not visited</span>
            </label>
          </fieldset>

          <p v-if="emptyState" class="trip-editor-empty-state">{{ emptyState }}</p>

          <div v-else class="trip-editor-visit-region-list">
            <section v-for="group in regionGroups" :key="group.regionId" class="trip-editor-visit-region" :aria-label="group.regionName">
              <header class="trip-editor-visit-region__header">
                <h3>{{ group.regionName }}</h3>
                <span>{{ group.visitedCount }} / {{ group.totalCount }} visited</span>
              </header>
              <div
                v-if="group.totalCount > 0"
                class="trip-editor-progress trip-editor-visit-region__bar"
                role="progressbar"
                :aria-label="`${group.regionName} visit progress`"
                :aria-valuenow="group.percentVisited"
                aria-valuemin="0"
                aria-valuemax="100"
              >
                <span :style="{ width: `${group.percentVisited}%` }"></span>
              </div>
              <article v-for="row in group.rows" :key="row.placeId" class="trip-editor-visit-place-row" :data-visit-place-id="row.placeId">
                <header>
                  <div class="trip-editor-visit-place-row__title">
                    <span
                      class="trip-editor-visit-status"
                      :class="row.summary.isVisited ? 'trip-editor-visit-status--visited' : 'trip-editor-visit-status--pending'"
                      :aria-label="row.summary.isVisited ? 'Visited' : 'Not visited'"
                      role="img"
                    >
                      <span v-if="row.summary.isVisited" aria-hidden="true">&check;</span>
                    </span>
                    <div>
                      <h4>{{ row.placeName }}</h4>
                      <small>{{ row.summary.isVisited ? 'Visited' : 'Not visited' }}</small>
                    </div>
                  </div>
                  <strong class="trip-editor-visit-count-pill">{{ row.summary.visitCount }} visit{{ row.summary.visitCount === 1 ? '' : 's' }}</strong>
                </header>
                <dl class="trip-editor-visit-place-row__summary">
                  <div>
                    <dt>First visit</dt>
                    <dd>{{ formatTimestamp(row.summary.firstVisitAt) }}</dd>
                  </div>
                  <div>
                    <dt>Last visit</dt>
                    <dd>{{ formatTimestamp(row.summary.lastVisitAt) }}</dd>
                  </div>
                </dl>

                <div v-if="row.summary.isVisited" class="trip-editor-visit-history">
                  <p v-if="row.historyRows.length === 0" class="trip-editor-empty-state">No visit history rows available for this place.</p>
                  <div v-for="history in row.historyRows" :key="history.visitId" class="trip-editor-visit-history-row" :data-visit-id="history.visitId">
                    <div class="trip-editor-visit-history-row__title">
                      <strong>{{ row.placeName }}</strong>
                      <span>{{ historyRegionName(history, row.regionId) }}</span>
                    </div>
                    <dl>
                      <div>
                        <dt>Start</dt>
                        <dd>{{ formatTimestamp(history.startedAt) }}</dd>
                      </div>
                      <div>
                        <dt>End</dt>
                        <dd>{{ formatEndedAt(history.endedAt) }}</dd>
                      </div>
                      <div>
                        <dt>Duration</dt>
                        <dd>{{ formatDuration(history.durationMinutes) }}</dd>
                      </div>
                    </dl>
                    <a class="btn btn-outline-light btn-sm" :href="manageVisitHref(history.visitId)" @click="event => manageVisit(event, history.visitId)">
                      Manage visit
                    </a>
                  </div>
                </div>
              </article>
            </section>
          </div>
        </div>
      </section>
    </div>
  </Teleport>
</template>
