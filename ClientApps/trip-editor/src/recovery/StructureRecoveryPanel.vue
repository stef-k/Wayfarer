<script setup lang="ts">
import { computed, inject, onMounted, onUnmounted, ref, shallowRef } from 'vue';
import { message } from '../../../../wwwroot/js/localization/messages.js';
import { readCustody, type Custody } from './custody';
import { metadataWorkspaceError, receiptStatus } from './recoverMetadata';
import { discardStructure, emptyStructure, observeStructure, settleStructure, targetRow } from './structureCustody';
import { legacyRegionHold, legacyRegionHoldKey, structureCustodyChanged, structureOwnerLost } from './structureMutation';
import { readStructureOwner, StructureOwnerUnavailable } from './structureOwner';
import { object } from './receiptStructureResults';
import type { EditorTripState } from '../types';

const props = defineProps<{ account: string; tripId: string; endpoint: string; standalone?: boolean }>();
const workspaceError = inject(metadataWorkspaceError, null);
const row = shallowRef<Custody | null>(null);
const graph = shallowRef<EditorTripState | null>(null);
const legacy = ref(false);
const error = ref('');
const busy = ref(false);
let mounted = true;

const custody = computed(() => row.value?.structure ?? emptyStructure());
const histories = computed(() => Object.entries(custody.value.outcomes)
  .filter(([id]) => custody.value.attempts.some(attempt => attempt.permit.operationId === id) || graph.value !== null));
const drafts = computed(() => Object.entries(custody.value.drafts));
const visible = computed(() => !!error.value || legacy.value || histories.value.length || drafts.value.length ||
  custody.value.attempts.length || custody.value.dependents.length);

/** Refreshes browser records without contacting the server or changing generations. */
async function refresh(): Promise<void> {
  try {
    legacy.value = !!legacyRegionHold(props.account, props.tripId);
    row.value = await readCustody(props.account, props.tripId);
  } catch {
    error.value = message('Editor.ReceiptStorage', 'The draft could not be stored. Keep this page open and retry.');
  }
}

/** Clears the former private graph only after a qualified owner-scope denial. */
function hideOwner(): void {
  graph.value = null;
  if (workspaceError && !props.standalone)
    workspaceError.value = message('Editor.LoadUnauthorized', 'Your session ended or you no longer have access. Sign in again or return to My Trips.');
}

/** Fetches historical receipts and current owner state with GETs, then settles only compared generations. */
async function checkServer(): Promise<void> {
  if (busy.value) return;
  busy.value = true;
  error.value = '';
  const account = props.account;
  const tripId = props.tripId;
  const current = (): boolean => mounted && props.account === account && props.tripId === tripId;
  try {
    let before = await readCustody(account, tripId);
    before = await observeStructure(before, props.endpoint);
    if (current()) row.value = before;
    const owner = await readStructureOwner(props.endpoint, account, tripId);
    const settled = await settleStructure(before, owner, current);
    if (!current()) return;
    row.value = settled;
    graph.value = owner;
    if (custody.value.attempts.some(attempt => !custody.value.outcomes[attempt.permit.operationId]))
      error.value = message('Editor.ReceiptHeld', 'The earlier save is still unconfirmed. Keep your draft and check again.');
  } catch (failure) {
    if (!current()) return;
    if (failure instanceof StructureOwnerUnavailable) hideOwner();
    error.value = message('Editor.ReceiptOwnerUnavailable', 'Current Trip values are unavailable. Your draft and save history are retained.');
  } finally {
    busy.value = false;
  }
}

/** Retains the historical attempt and clears only the selected local draft generation. */
async function discard(key: string): Promise<void> {
  if (!row.value) return;
  try { row.value = await discardStructure(row.value, key); }
  catch { await refresh(); }
}

/** A local acknowledgement of an earlier proofless hold cannot authorize another deletion. */
function dismissLegacy(): void {
  if (!graph.value) return;
  localStorage.removeItem(legacyRegionHoldKey(props.account, props.tripId));
  legacy.value = false;
  window.dispatchEvent(new Event('region-deletion-hold'));
}

const desired = (payload: object): unknown => {
  const value = payload as Record<string, unknown>;
  return value.changes ?? value.value ?? Object.fromEntries(Object.entries(value)
    .filter(([key]) => !['expected', 'confirmationToken', 'conditionToken', 'reviewed'].includes(key)));
};

const targetName = (type: string, id: string): string =>
  graph.value ? targetRow(graph.value, type, id)?.name ?? id : id;
const currentParent = (type: string, id: string): string | null => {
  const current = graph.value && targetRow(graph.value, type, id);
  return current && 'regionId' in current ? current.regionId : null;
};
const originalParent = (payload: object): string | null => {
  const original = (payload as { expected?: { regionId?: string; place?: { regionId?: string } } }).expected;
  return original?.regionId ?? original?.place?.regionId ?? null;
};

onMounted(() => {
  void refresh();
  window.addEventListener(structureCustodyChanged, refresh);
  window.addEventListener(structureOwnerLost, hideOwner);
  window.addEventListener('focus', refresh);
});
onUnmounted(() => {
  mounted = false;
  window.removeEventListener(structureCustodyChanged, refresh);
  window.removeEventListener(structureOwnerLost, hideOwner);
  window.removeEventListener('focus', refresh);
});
</script>

<template>
  <section v-if="visible" class="border rounded p-2 mb-2" aria-label="Trip save recovery" data-structure-recovery>
    <p v-if="error" role="status" class="mb-1">{{ error }}</p>
    <p v-for="[id, evidence] in histories" :key="id" role="status" class="mb-1">
      {{ receiptStatus(evidence.state) }}
      <span v-if="object(evidence.result) && typeof evidence.result.id === 'string'">{{ evidence.result.id }}</span>
    </p>
    <p v-if="custody.dependents.length" class="mb-1">
      {{ message('Editor.StructureDependentHeld', 'An earlier Planning or Segment save is still unconfirmed.') }}
    </p>
    <p v-if="legacy" class="mb-1">
      {{ message('Editor.StructureLegacyHold', 'An earlier Region deletion has no recoverable receipt.') }}
      <button v-if="graph" type="button" class="btn btn-sm btn-outline-secondary" @click="dismissLegacy">
        {{ message('Editor.StructureDismiss', 'Dismiss notice') }}
      </button>
    </p>
    <details v-for="[key, draft] in drafts" :key="key" class="mb-2">
      <summary>{{ message('Editor.StructureDraft', 'Retained draft') }} · {{ targetName(draft.route.target.type, draft.route.target.id) }}</summary>
      <p v-if="graph && !draft.route.kind.endsWith('-create') && draft.route.target.type !== 'trip'
        && !targetRow(graph, draft.route.target.type, draft.route.target.id)" class="mb-1">
        {{ message('Editor.StructureTargetUnavailable', 'The original target is no longer available.') }}
      </p>
      <p v-if="originalParent(draft.payload) && currentParent(draft.route.target.type, draft.route.target.id)
        && originalParent(draft.payload) !== currentParent(draft.route.target.type, draft.route.target.id)" class="mb-1">
        {{ message('Editor.StructureParentChanged', 'Review the changed parent before saving.') }}
        {{ originalParent(draft.payload) }} → {{ currentParent(draft.route.target.type, draft.route.target.id) }}
      </p>
      <pre class="text-wrap text-break">{{ JSON.stringify(desired(draft.payload), null, 2) }}</pre>
      <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="busy" @click="discard(key)">
        {{ message('Editor.StructureDiscard', 'Discard local draft') }}
      </button>
    </details>
    <button type="button" class="btn btn-outline-secondary btn-sm" :disabled="busy" @click="checkServer">
      {{ message('Editor.StructureCheckServer', 'Check server') }}
    </button>
  </section>
</template>
