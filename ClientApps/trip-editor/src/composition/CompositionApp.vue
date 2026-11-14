<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import CompositionStart from './CompositionStart.vue';
import CompositionMap from './CompositionMap.vue';
import SourceTree from './SourceTree.vue';
import ChoiceControls from './ChoiceControls.vue';
import FieldChoices from './FieldChoices.vue';
import ConfirmDialog from '../components/ConfirmDialog.vue';
import { confirm, disposeConfirmDialogHost } from '../composables/useConfirmDialog';
import { compositionApi, editorUrl } from './compositionApi';
import { createCompositionState, draftKey, viewKey } from './compositionState';
import { buildAction, copy, find, label, newChoice, newReview, reviewAgainstCurrent, sourceSnapshot } from './compositionModel';
import type { Candidate, Selection } from './compositionTypes';
import type { EditorOptions } from '../types';
import './composition.css';

const props = defineProps<{ account: string; compositionId: string; token: string }>();
const api = compositionApi(props.token);
const store = createCompositionState(props.account, props.compositionId);
const state = store.state;
const visible = ref({ a: true, b: true, c: true });
const mapStatus = ref(''); const map = ref<InstanceType<typeof CompositionMap> | null>(null);
const workspaceElement = ref<HTMLElement | null>(null);
const completionElement = ref<HTMLElement | null>(null);
const options = ref<EditorOptions | null>(null);
const candidates = ref<Candidate[]>([]); const candidateSearch = ref(''); const candidatePending = ref(false);
const candidateResultSearch = ref('');
const finishing = ref(false); const finished = ref(false); const finishedTripId = ref(''); const lifecycleError = ref('');
const storageError = ref(''); let candidateGeneration = 0; let disposed = false;
const key = draftKey(window.location.origin, props.account, props.compositionId);
const selectedDraft = computed(() => state.view.selection ? state.view.drafts[viewKey(state.view.selection)] ?? null : null);
const draft = computed(() => state.view.blockMode ? state.view.block : selectedDraft.value);
const choice = computed(() => selectedDraft.value?.items[0]);
const counterpart = computed(() => choice.value?.decision === 'reuse' || choice.value?.decision === 'remove' ? choice.value.destinationId : '');
const editUrl = computed(() => state.workspace ? editorUrl(state.workspace.tripId, props.compositionId) : '#');
const source = computed(() => state.workspace && state.view.selection ? sourceSnapshot(state.workspace, state.view.selection.side) : null);
const selectionTitle = computed(() => source.value && state.view.selection ? label(find(source.value, state.view.selection.kind, state.view.selection.id)!) : 'Select a source idea');
const credits = computed(() => state.workspace?.current.graph.credits ?? []);

/** Authoritative C refresh preserves all local drafts. A refresh itself never renews selected originals. */
async function refresh(): Promise<boolean> {
  if (state.pending) return false;
  const read = await store.read(() => api.read(props.compositionId));
  if (read && state.workspace) {
    try { const current = await api.editorState(state.workspace.tripId); if (!disposed && current.tripId === state.workspace.tripId) options.value = current.options; }
    catch { /* Source comparison and recovery remain usable if ordinary editor options are temporarily unavailable. */ }
  }
  return read;
}
/** Selection reads current C before creating a new review; an existing draft keeps its own original baseline. */
async function select(selection: Selection): Promise<void> {
  if (!state.workspace || finished.value) return;
  state.view.selection = selection; state.view.blockMode = false; state.view.tab = 'inspector';
  ++candidateGeneration; candidatePending.value = false;
  state.view.collapsed = false; candidateSearch.value = ''; candidates.value = [];
  const selected = viewKey(selection);
  if (!state.view.drafts[selected]) {
    if (!state.pending && !await refresh()) return;
    if (!state.workspace || !state.view.selection || viewKey(state.view.selection) !== selected) return;
    const review = newReview(state.workspace, selection.side);
    review.items = [newChoice(state.workspace, selection.side, selection.kind, selection.id)]; state.view.drafts[selected] = review;
  }
  await searchCandidates();
}
/** A late candidate reply belongs only to its captured source/selection and query generation. */
async function searchCandidates(): Promise<void> {
  const selection = state.view.selection; if (!selection || selection.kind === 'trip') return;
  const selected = viewKey(selection); const generation = ++candidateGeneration; candidatePending.value = true;
  const submittedSearch = candidateSearch.value;
  try {
    const result = await api.request<{ candidates: Candidate[] }>(`/api/trip-compositions/${props.compositionId}/candidates?${new URLSearchParams({ source: selection.side, kind: selection.kind, sourceId: selection.id, search: submittedSearch })}`);
    if (!disposed && generation === candidateGeneration && state.view.selection && viewKey(state.view.selection) === selected) {
      candidates.value = result.candidates; candidateResultSearch.value = submittedSearch;
    }
  } catch (error) { if (!disposed && generation === candidateGeneration) state.error = error instanceof Error ? error.message : 'Candidate search unavailable.'; }
  finally { if (!disposed && generation === candidateGeneration) candidatePending.value = false; }
}
/** Explicit review replaces only comparison originals. Names, typed Notes, destinations and selected actions remain. */
async function reviewCurrent(): Promise<void> {
  const retained = draft.value;
  if (!retained || state.pending) return;
  if (!await refresh() || !state.workspace) return;
  if (!await confirm({ title: 'Review current C?', message: 'Keep your choices and use current C as the new comparison. Inspect the updated differences and placement before applying; this does not save the choices.', confirmLabel: 'Review current originals', cancelLabel: 'Keep prior originals' })) return;
  reviewAgainstCurrent(state.workspace, retained);
  state.status = 'Current originals loaded. Review the retained choices before applying.';
}
/** Refreshes C, freezes the submitted review, prepares ordinary consent, then commits exactly one domain action. */
async function apply(): Promise<void> {
  const retained = draft.value; if (!retained || !state.workspace || state.pending || finishing.value) return;
  let action;
  try { action = buildAction(state.workspace, copy(retained)); }
  catch (error) { state.error = error instanceof Error ? error.message : 'Review your selected fields and destination.'; return; }
  await store.submit(retained, action, async frozen => {
    const current = await api.read(props.compositionId);
    if (disposed) throw new Error('Comparison context changed. Inspect saved C.');
    store.validateWorkspace(current);
    state.workspace = current;
    if (!await api.prepare(current, frozen, message => confirm({ title: 'Review consequences in C', message, confirmLabel: 'Apply reviewed action', cancelLabel: 'Keep choices', variant: 'warning' })))
      throw new Error('Action not applied. Choices retained for review.');
    if (disposed) throw new Error('Comparison context changed.');
    return await api.apply(props.compositionId, frozen);
  }, () => {
    if (state.view.blockMode) { state.view.block = null; state.view.blockMode = false; return; }
    const selection = state.view.selection;
    if (selection && state.workspace) {
      const review = newReview(state.workspace, selection.side);
      review.items = [newChoice(state.workspace, selection.side, selection.kind, selection.id)]; state.view.drafts[viewKey(selection)] = review;
    }
  });
}
/** Blocks keep each list/destination independent; the inspector shows the resulting placement for every item. */
function addToBlock(): void {
  if (!selectedDraft.value || !state.workspace) return;
  const review = selectedDraft.value;
  if (!state.view.block?.items.length) state.view.block = null;
  if (state.view.block && state.view.block.side !== review.side) { state.error = 'One action block uses a single source. Apply or clear the existing block first.'; return; }
  state.view.block ??= { ...copy(review), items: [] };
  const item = review.items[0];
  if (state.view.block.items.some(c => c.kind === item.kind && c.sourceId === item.sourceId)) { state.error = 'This item is already in the block. Open the block to edit it.'; return; }
  state.view.block.items.push(copy(item)); state.status = 'Added to reviewed block. Open it to check destinations and position.';
}
async function clearBlock(): Promise<void> {
  if (state.pending || !await confirm({ title: 'Clear local block?', message: 'Clear these unapplied block choices? Saved C and its comparison remain unchanged.', confirmLabel: 'Clear local block', cancelLabel: 'Keep block' })) return;
  state.view.block = null; state.view.blockMode = false;
}
function syncBlock(): void {
  const items = state.view.block?.items; if (!items?.length) return;
  const first = items[0];
  if (first.newRegionName) { state.error = 'A block shares an existing destination Region. To create one Region with several direct children atomically, use the Region-with-children scope.'; return; }
  for (const item of items.slice(1).filter(c => c.kind === first.kind && c.decision === 'copy')) {
    item.regionId = first.regionId; item.newRegionName = first.newRegionName; item.position = first.position; item.afterId = first.afterId;
  }
}
/** Finishing removes historical comparison payload only; neither publication nor pending local choices are implied. */
async function finish(): Promise<void> {
  if (finishing.value || state.pending || !state.workspace) return;
  if (!await confirm({ title: 'Finish comparison?', message: 'Keep C, its current visibility, saved content and credits. Remove this comparison’s source snapshots and workspace decisions permanently. The historical comparison cannot reopen. Unapplied local choices will not be applied. You can continue ordinary editing and publication.', confirmLabel: 'Finish comparison', cancelLabel: 'Continue comparison', variant: 'warning' })) return;
  finishing.value = true; lifecycleError.value = '';
  const tripId = state.workspace.tripId;
  try { const response = await api.finish(props.compositionId);
    if (disposed) return;
    if (response?.tripId !== tripId || response.status !== 'finished')
      throw new Error('The reply is incomplete or belongs to another Trip.');
    finishedTripId.value = response.tripId; finished.value = true;
    // Retire pending reads and release captured inputs/drafts after confirmed server cleanup.
    store.dispose(); state.workspace = null; state.view.drafts = {}; state.view.block = null; state.view.selection = null;
    try { localStorage.removeItem(key); storageError.value = ''; }
    catch { storageError.value = 'Comparison finished on the server, but this browser could not clear its retained local choices.'; }
    await nextTick(); completionElement.value?.focus();
  } catch (error) { lifecycleError.value = `Finish could not be confirmed. Inspect or refresh saved C. ${error instanceof Error ? error.message : ''}`; }
  finally { finishing.value = false; }
}
/** Uses the ordinary Trip delete route and confirmation convention; source Trips never enter the request. */
async function remove(): Promise<void> {
  if (state.pending || finishing.value || !state.workspace) return;
  if (!await confirm({ title: 'Delete Trip C?', message: `Permanently delete “${state.workspace.current.graph.name}”, all its content and this comparison. A, B and independent copies remain unchanged. This cannot be undone.`, confirmLabel: 'Delete C', cancelLabel: 'Keep C', variant: 'danger' })) return;
  finishing.value = true;
  // Native submission retains the ordinary redirect/alert behavior, including a failed deletion's recovery.
  const form = document.createElement('form'); form.method = 'POST'; form.action = '/User/Trip/Delete'; form.hidden = true;
  for (const [name, value] of Object.entries({ id: state.workspace.tripId, __RequestVerificationToken: props.token })) {
    const input = document.createElement('input'); input.name = name; input.value = value; form.append(input);
  }
  document.body.append(form); form.submit();
}
function expand(value: string, open: boolean): void {
  const current = state.view.expanded;
  if (open && !current.includes(value)) current.push(value);
  else if (!open) state.view.expanded = current.filter(x => x !== value);
}
/** Drawer uses the editor's map/navigation height budget and a keyboard/pointer equivalent with cancellation. */
let drag: { id: number; element: HTMLElement; before: number } | null = null;
function resize(event: KeyboardEvent): void {
  const amount = event.shiftKey ? 10 : 3;
  if (event.key === 'ArrowUp') state.view.drawer = Math.min(65, state.view.drawer + amount);
  else if (event.key === 'ArrowDown') state.view.drawer = Math.max(25, state.view.drawer - amount);
  else if (event.key === 'Home') state.view.drawer = 25;
  else if (event.key === 'End') state.view.drawer = 65;
  else if (event.key === 'Escape' && drag) { state.view.drawer = drag.before; stopResize(); }
  else return;
  event.preventDefault();
}
function startResize(event: PointerEvent): void {
  if (event.button !== 0) return;
  const element = event.currentTarget as HTMLElement;
  drag = { id: event.pointerId, element, before: state.view.drawer }; element.setPointerCapture(event.pointerId); element.focus();
}
function moveResize(event: PointerEvent): void {
  if (!drag || event.pointerId !== drag.id || !workspaceElement.value) return;
  const rect = workspaceElement.value.getBoundingClientRect();
  state.view.drawer = Math.max(25, Math.min(65, (rect.bottom - event.clientY) / rect.height * 100));
}
function stopResize(): void { if (drag?.element.hasPointerCapture(drag.id)) drag.element.releasePointerCapture(drag.id); drag = null; }
function cancelResize(): void { if (drag) state.view.drawer = drag.before; stopResize(); }
onMounted(async () => {
  window.addEventListener('blur', cancelResize); window.addEventListener('resize', cancelResize);
  if (!props.compositionId) return;
  try { store.restore(localStorage.getItem(key)); } catch { storageError.value = 'Local view storage is unavailable; saved C still resumes from My Trips.'; }
  await refresh();
  if (state.view.selection && state.workspace && !find(sourceSnapshot(state.workspace, state.view.selection.side), state.view.selection.kind, state.view.selection.id)) state.view.selection = null;
  if (state.view.selection) await searchCandidates();
});
watch(() => state.view, () => {
  if (!props.compositionId || finished.value) return;
  try { localStorage.setItem(key, store.retain()); } catch { storageError.value = 'Local choices could not be retained. Keep this page open; server-saved actions are unaffected.'; }
}, { deep: true });
onUnmounted(() => {
  disposed = true; ++candidateGeneration; store.dispose(); stopResize(); disposeConfirmDialogHost();
  window.removeEventListener('blur', cancelResize); window.removeEventListener('resize', cancelResize);
});
watch(() => state.view.collapsed, cancelResize);
</script>

<template>
  <CompositionStart v-if="!compositionId" :token="token" />
  <section v-else-if="finished" ref="completionElement" class="composition-start" role="status" tabindex="-1">
    <h1>Comparison finished</h1>
    <p v-if="finished">C keeps its saved content, visibility and credits. This historical comparison cannot reopen.</p>
    <p v-if="storageError" role="status">{{ storageError }}</p>
    <a :href="`/User/Trip/Edit/${finishedTripId}`">Continue ordinary editing of C</a> · <a href="/User/Trip">My Trips</a>
  </section>
  <section v-else ref="workspaceElement" class="composition-workspace" :class="{ 'drawer-collapsed': state.view.collapsed }" :style="{ '--composition-drawer': `${state.view.drawer}%` }" aria-label="Trip comparison workspace">
    <header class="composition-header">
      <div><h1>Compare &amp; combine</h1><p v-if="state.workspace"><strong class="source-c">C · {{ state.workspace.current.graph.name }}</strong> · {{ state.workspace.current.graph.isPublic ? 'Public · saved changes update the same Trip' : 'Private Trip' }} · {{ state.status || 'Saved / editable' }}</p></div>
      <nav aria-label="Composition actions"><a href="/User/Trip">My Trips</a><a v-if="state.workspace" :href="editUrl">Edit C / publish</a>
        <button type="button" :disabled="state.loading || state.pending || finishing" @click="refresh">Refresh saved C</button>
        <button type="button" :disabled="!state.workspace || state.pending || finishing" @click="finish">Finish comparison</button>
        <button type="button" class="composition-delete" :disabled="!state.workspace || state.pending || finishing" @click="remove">Delete C</button>
      </nav>
    </header>
    <div class="composition-feedback" aria-live="polite">
      <p v-if="state.error || lifecycleError" role="alert">{{ state.error || lifecycleError }} <a :href="editUrl">Inspect saved C</a></p>
      <p v-if="storageError">{{ storageError }}</p>
      <span v-if="state.loading">Loading current saved C…</span><span v-else>{{ state.status }}</span>
    </div>
    <template v-if="state.workspace">
      <div class="composition-map-frame">
        <CompositionMap ref="map" :workspace="state.workspace" :selection="state.view.selection" :counterpart="counterpart" :visible="visible" @select="select" @status="mapStatus = $event" />
        <fieldset class="composition-layers"><legend>Map layers</legend><label v-for="side in (['a', 'b', 'c'] as const)" :key="side" :class="`source-${side}`"><input v-model="visible[side]" type="checkbox" /> {{ side.toUpperCase() }} · {{ side === 'c' ? 'Editable result' : 'Read-only source' }}</label><button type="button" @click="map?.focus()">Focus selection</button></fieldset>
        <p class="composition-map-status" role="status">{{ mapStatus }}</p>
      </div>
      <div class="composition-drawer-bar">
        <div v-if="!state.view.collapsed" class="composition-resize" role="separator" tabindex="0" aria-label="Resize comparison drawer" aria-orientation="horizontal" :aria-valuenow="Math.round(state.view.drawer)" aria-valuemin="25" aria-valuemax="65"
          title="Drag or use Up/Down arrows; Shift for larger steps; Home/End; Escape cancels drag" @keydown="resize" @pointerdown="startResize" @pointermove="moveResize" @pointerup="stopResize" @pointercancel="cancelResize" @lostpointercapture="stopResize"></div>
        <button type="button" :aria-expanded="!state.view.collapsed" aria-controls="comparison-drawer" @click="state.view.collapsed = !state.view.collapsed">{{ state.view.collapsed ? 'Show comparison' : 'Collapse comparison' }}</button>
      </div>
      <div v-show="!state.view.collapsed" id="comparison-drawer" class="composition-drawer" :inert="finishing">
        <nav class="composition-tabs" aria-label="Comparison drawer sections"><button v-for="tab in ['a', 'b', 'inspector']" :key="tab" type="button" :aria-pressed="state.view.tab === tab" @click="state.view.tab = tab">{{ tab === 'inspector' ? 'Inspector' : `Source ${tab.toUpperCase()}` }}</button></nav>
        <SourceTree v-for="side in (['a', 'b'] as const)" v-show="state.view.tab === side" :key="side" :class="`composition-tree-${side}`" :snapshot="sourceSnapshot(state.workspace, side)" :side="side" :selection="state.view.selection" :mappings="state.workspace.mappings" :expanded="state.view.expanded" :counterpart="state.view.selection && counterpart ? { kind: state.view.selection.kind, id: counterpart } : null" @select="select" @expand="expand" />
        <section v-show="state.view.tab === 'inspector'" class="composition-inspector" aria-label="Contextual comparison inspector">
          <h2>{{ state.view.blockMode ? 'Reviewed item block' : selectionTitle }}</h2>
          <p v-if="source?.mediaOmitted" role="status">Some source media was omitted by its permissions/storage policy. Reselect eligible images explicitly in C’s ordinary editor; copying does not grant media permission.</p>
          <template v-if="choice && !state.view.blockMode && state.view.selection?.kind !== 'trip'">
            <form @submit.prevent="searchCandidates"><label class="composition-field">Manual counterpart search<input v-model="candidateSearch" type="search" maxlength="200" /></label><button :disabled="candidatePending">{{ candidatePending ? 'Searching…' : 'Find possible counterparts' }}</button></form>
            <details><summary>Possible match—review needed · {{ candidates.length }} suggestions</summary>
              <p>Evidence helps review; it does not prove identity. Nearby Places and overlapping Areas may be distinct.</p>
              <p>Results for {{ candidateResultSearch ? `“${candidateResultSearch}”` : 'the unfiltered source selection' }}.</p>
              <div v-for="candidate in candidates" :key="candidate.destinationId" class="composition-candidate">
                <strong>{{ candidate.name }}</strong> · Possible match—review needed
                <p>{{ candidate.sameName ? 'Same name' : 'Different name' }}<span v-if="candidate.distanceKm !== null"> · {{ Math.round(candidate.distanceKm * 1000) }} metres apart</span><span v-if="candidate.geometry"> · Geometry: {{ candidate.geometry }}</span><span v-if="candidate.mappedChildren"> · {{ candidate.mappedChildren }} confirmed child counterparts</span><span v-if="state.view.selection?.kind === 'segment'"> · {{ candidate.mappedAnchors ? 'Confirmed mapped anchors' : 'Different or unconfirmed anchors' }} · Mode {{ candidate.sameMode ? 'same' : 'different' }} · Profile {{ candidate.sameProfile ? 'same' : 'different' }}</span></p>
                <button type="button" @click="choice.destinationId = candidate.destinationId; choice.decision = 'reuse'">Review this C counterpart</button>
              </div>
            </details>
          </template>
          <div v-if="draft" :aria-busy="state.pending">
            <p>These review choices are local until Apply. All earlier successful actions are already saved in C.</p>
            <template v-for="(item, index) in draft.items" :key="`${item.kind}/${item.sourceId}`">
              <p v-if="state.view.blockMode"><strong>Block item {{ index + 1 }}</strong>
                <button type="button" :disabled="index === 0" @click="[draft.items[index - 1], draft.items[index]] = [draft.items[index], draft.items[index - 1]]">Move up in block</button>
                <button type="button" :disabled="index === draft.items.length - 1" @click="[draft.items[index], draft.items[index + 1]] = [draft.items[index + 1], draft.items[index]]">Move down in block</button>
              </p>
              <ChoiceControls :workspace="state.workspace" :review="draft" :choice="item" />
              <FieldChoices v-if="item.decision === 'copy' || item.decision === 'reuse' && find(draft.baseline, item.kind, item.destinationId)" :workspace="state.workspace" :review="draft" :choice="item" :editor-url="editUrl" :options="options" />
              <button v-if="state.view.blockMode" type="button" @click="draft.items.splice(index, 1)">Remove from local block</button>
            </template>
            <button v-if="state.view.blockMode && draft.items.length > 1" type="button" @click="syncBlock">Use first item’s destination / position for same-type copies</button>
            <button v-if="state.view.blockMode" type="button" :disabled="state.pending" @click="clearBlock">Clear local block</button>
            <p v-if="state.view.blockMode">New siblings in each destination list enter one contiguous block in the displayed order. Reused identities stay at their current positions.</p>
            <div class="composition-actions"><button type="button" :disabled="state.pending || state.needsInspection || finishing || !draft.items.length" @click="apply">{{ state.pending ? 'Saving submitted action…' : 'Apply reviewed action to C' }}</button>
              <button type="button" :disabled="state.pending" @click="reviewCurrent">Review current C / refresh originals</button>
              <button v-if="!state.view.blockMode" type="button" @click="addToBlock">Add this reviewed item to block</button>
            </div>
          </div>
          <p v-else>Select a source Region, Place, Area, Trip details or Trip-level Segment. The map focuses its actual geography.</p>
          <p v-if="state.view.block?.items.length"><button type="button" @click="state.view.blockMode = !state.view.blockMode">{{ state.view.blockMode ? 'Return to selected item' : `Review block · ${state.view.block.items.length} items` }}</button></p>
          <details class="composition-credits"><summary>Sources &amp; credits</summary><p>Owner’s contributions: {{ state.workspace.current.graph.contributionPolicy || 'No publication grant selected' }}. Inherited terms stay separate. Copy/adoption carries applicable source notices.</p>
            <p v-for="(credit, index) in credits" :key="index">{{ credit.contributor }} · {{ credit.sourceTitle }} · {{ credit.licence }}<br />{{ credit.notices }} {{ credit.modifications }}</p>
            <p v-if="source && source.graph.id !== state.workspace.current.graph.id">Review source terms: {{ source.graph.contributionPolicy || 'Owned source' }}<span v-for="(credit, index) in source.graph.credits" :key="index"> · {{ credit.contributor }} · {{ credit.sourceTitle }} · {{ credit.licence }} · {{ credit.notices }} {{ credit.modifications }}</span></p>
            <a :href="editUrl">Inspect/correct publishable credits in Trip settings</a>
          </details>
        </section>
      </div>
    </template>
    <p v-else class="composition-start">Loading or comparison unavailable. <button type="button" :disabled="state.loading" @click="refresh">Try reading again</button> · <a href="/User/Trip">My Trips</a></p>
  </section>
  <ConfirmDialog />
</template>
