<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import SourceTree from './SourceTree.vue';
import CompositionMap from './CompositionMap.vue';
import ChoiceControls from './ChoiceControls.vue';
import FieldChoices from './FieldChoices.vue';
import ConfirmDialog from '../components/ConfirmDialog.vue';
import { confirm, disposeConfirmDialogHost } from '../composables/useConfirmDialog';
import { compositionApi } from './compositionApi';
import { createCompositionState } from './compositionState';
import { buildAction, copy, find, label, newChoice, newReview } from './compositionModel';
import type { Selection, SourceOption, Workspace } from './compositionTypes';
import type { EditorOptions } from '../types';
import './composition.css';

const props = defineProps<{ account: string; token: string; source: string; destination: string }>();
const api = compositionApi(props.token);
let store = createCompositionState(props.account, 'copy', 'destination Trip');
const state = ref(store.state);
const sourceId = ref(props.source); const destinationId = ref(props.destination);
const sourceSearch = ref(''); const destinationSearch = ref('');
const sources = ref<SourceOption[]>([]); const destinations = ref<SourceOption[]>([]);
const preview = ref<CopyPreview | null>(null); const options = ref<EditorOptions | null>(null);
const map = ref<InstanceType<typeof CompositionMap> | null>(null); const mapStatus = ref('');
const visible = ref({ a: false, b: true, c: true });
const loading = ref(false); const error = ref(''); const expanded = ref<string[]>([]);
let generation = 0; let disposed = false;
interface Original { kind: string; id: string; value: string; children?: string }
interface CopyPreview { sourceTripId: string; tripId: string; source: Workspace['sourceB']; current: Workspace['current']; context: string; originals: Original[] }
const draft = computed(() => state.value.view.block);
const editUrl = computed(() => preview.value ? `/User/Trip/Edit/${preview.value.tripId}` : '/User/Trip');
const pairMatches = computed(() => preview.value?.sourceTripId === sourceId.value && preview.value?.tripId === destinationId.value);
const selected = computed(() => draft.value?.items.flatMap(c => [c, ...c.withChildren ? c.children : []])
  .find(c => c.kind === state.value.view.selection?.kind && c.sourceId === state.value.view.selection?.id));

/** Source discovery and destination ownership use the existing bounded source catalogue. */
async function search(owned: boolean): Promise<void> {
  const field = owned ? destinationSearch.value : sourceSearch.value;
  try {
    const values = await api.sources(field, owned);
    if (disposed || field !== (owned ? destinationSearch.value : sourceSearch.value)) return;
    if (owned) destinations.value = values; else sources.value = values;
  } catch (e) { error.value = e instanceof Error ? e.message : 'Trip search unavailable.'; }
}
/** Explicit preview replacement renews source originals; neither refusal nor acknowledgement silently renews them. */
async function load(): Promise<void> {
  if (state.value.pending || loading.value || !sourceId.value || !destinationId.value) return;
  const source = sourceId.value; const destination = destinationId.value; const owner = store;
  const same = pairMatches.value;
  // Replacement consent belongs to this pair and these choices, including selection and expansion.
  const reviewState = () => JSON.stringify({ view: state.value.view, expanded: expanded.value });
  const confirmed = reviewState();
  if (draft.value?.items.length && !await confirm({ title: same ? 'Review current source and destination?' : 'Change the copy context?',
    message: same ? 'Replace the reviewed originals with current source and destination values. Keep your choices for inspection; review changed values before applying.' : 'Clear these unapplied choices and preview the selected source and destination?',
    confirmLabel: 'Review current values', cancelLabel: 'Keep choices' })) return;
  if (disposed || store !== owner || sourceId.value !== source || destinationId.value !== destination) return;
  if (!same && reviewState() !== confirmed) {
    error.value = 'Copy choices changed during confirmation. They remain in the previous context. Preview again to confirm replacement.';
    return;
  }
  const attempt = ++generation;
  loading.value = true; error.value = '';
  try {
    const value = await api.request<CopyPreview>(`/api/trip-compositions/copy-preview?${new URLSearchParams({ sourceTripId: source, destinationTripId: destination })}`);
    if (disposed || attempt !== generation || sourceId.value !== source || destinationId.value !== destination) return;
    if (value.sourceTripId !== source || value.tripId !== destination || !Array.isArray(value.originals) || !value.context)
      throw new Error('The preview does not match the selected Trips. Review again.');
    if (same && draft.value?.items.flatMap(c => [c, ...c.children]).some(c => !find(value.source, c.kind, c.sourceId)))
      throw new Error('A selected source object was removed. Keep the old review or remove that local choice before refreshing.');
    const retained = same ? copy(draft.value?.items ?? []) : [];
    const selection = same ? copy(state.value.view.selection) : null;
    const next = createCompositionState(props.account, 'copy', 'destination Trip');
    const workspace: Workspace = { compositionId: 'copy', tripId: value.tripId, sourceA: value.source, sourceB: value.source, current: value.current, mappings: [] };
    if (!await next.read(async () => workspace)) throw new Error(next.state.error);
    if (disposed || store !== owner || attempt !== generation || sourceId.value !== source || destinationId.value !== destination) { next.dispose(); return; }
    if (!same && reviewState() !== confirmed) {
      next.dispose();
      throw new Error('Copy choices changed while the preview loaded. They remain in the previous context. Preview again to confirm replacement.');
    }
    store.dispose(); store = next; state.value = store.state; preview.value = value;
    const review = newReview(workspace, 'b'); review.items = retained; state.value.view.block = review; state.value.view.blockMode = true;
    state.value.view.selection = selection;
    state.value.status = 'Preview ready. Choose objects, destinations, dependencies and selected fields.';
    if (!same) expanded.value = value.source.regionOrder.map(id => `b/${id}`);
    options.value = null;
    try { const editor = await api.editorState(value.tripId); if (!disposed && attempt === generation) options.value = editor.options; }
    catch { /* Existing source choices and review remain usable; ordinary editing can repair unavailable route options. */ }
  } catch (e) { if (!disposed && attempt === generation) error.value = e instanceof Error ? e.message : 'Preview unavailable.'; }
  finally { if (!disposed && attempt === generation) loading.value = false; }
}
/** Selection stays editable during waits; submitted actions retain their own immutable draft and ownership. */
function select(selection: Selection): void {
  if (!state.value.workspace || selection.kind === 'trip' || !draft.value) return;
  state.value.view.selection = selection;
  if (!selected.value) {
    // A scope-off child remains the same owner when selected independently, then folds back without replacement.
    const cached = draft.value.items.flatMap(c => c.children).find(c => c.kind === selection.kind && c.sourceId === selection.id);
    draft.value.items.push(cached ?? newChoice(state.value.workspace, 'b', selection.kind, selection.id));
  }
}
function expand(key: string, open: boolean): void {
  expanded.value = open ? [...new Set([...expanded.value, key])] : expanded.value.filter(x => x !== key);
}
/** One apply sends reviewed source originals plus the existing composition intent; never retries an unconfirmed copy. */
async function apply(): Promise<void> {
  if (!draft.value?.items.length || !preview.value || !state.value.workspace || !pairMatches.value) return;
  const owned = store; const submittedPreview = copy(preview.value); const submittedDraft = draft.value;
  const workspace = state.value.workspace;
  try {
    const action = buildAction(workspace, submittedDraft);
    await owned.submit(submittedDraft, action, async frozen => {
      if (!await api.prepare(workspace, frozen, message => confirm({ title: 'Review destination consequences', message,
        confirmLabel: 'Apply reviewed copy', cancelLabel: 'Keep choices', variant: 'warning' }))) throw new Error('Copy not applied. Choices retained.');
      const selected = frozen.items.flatMap(i => [i, ...i.children ?? []]);
      const identities = new Set(selected.map(i => `${i.kind}/${i.sourceId}`).concat(selected.flatMap(i => (i.anchors ?? []).map(a => `place/${a.sourceId}`))));
      const originals = submittedPreview.originals.filter(o => identities.has(`${o.kind}/${o.id}`));
      const result = await api.request<{ tripId: string; compositionId: string; status: string; current: Workspace['current']; mappings: Workspace['mappings'] }>(
        '/api/trip-compositions/copy', { sourceTripId: submittedPreview.sourceTripId, destinationTripId: submittedPreview.tripId,
          context: submittedPreview.context, originals, items: frozen.items });
      if (result.status !== 'copied') throw new Error('Save could not be confirmed. Inspect the destination before another copy.');
      return { ...result, compositionId: 'copy' };
    }, () => {
      if (owned.state.workspace) owned.state.view.block = newReview(owned.state.workspace, 'b');
    }, () => sourceId.value === submittedPreview.sourceTripId && destinationId.value === submittedPreview.tripId);
  } catch (e) { error.value = e instanceof Error ? e.message : 'Review the selected choices.'; }
}
onMounted(async () => { await Promise.all([search(false), search(true)]); if (props.source && props.destination) await load(); });
onUnmounted(() => { disposed = true; ++generation; store.dispose(); disposeConfirmDialogHost(); });
</script>

<template>
  <section class="copy-to-trip" aria-label="Copy to existing Trip">
    <header><h1>Copy to existing Trip</h1><p>Choose an owned or publicly reusable source and an existing Trip you own. Source Trips stay unchanged.</p><a href="/User/Trip">My Trips</a></header>
    <div class="copy-context">
      <fieldset><legend>Read-only source</legend><form @submit.prevent="search(false)"><label class="composition-field">Find source Trip<input v-model="sourceSearch" type="search" maxlength="200" /></label><button>Search sources</button></form>
        <label class="composition-field">Source Trip<select v-model="sourceId" aria-label="Source Trip"><option value="">Select source</option><option v-if="sourceId && !sources.some(x => x.id === sourceId)" :value="sourceId">Selected source</option><option v-for="item in sources" :key="item.id" :value="item.id">{{ item.name }} · {{ item.owned ? 'Owned' : item.contributionPolicy }}</option></select></label></fieldset>
      <fieldset><legend>Existing owned destination</legend><form @submit.prevent="search(true)"><label class="composition-field">Find destination Trip<input v-model="destinationSearch" type="search" maxlength="200" /></label><button>Search destinations</button></form>
        <label class="composition-field">Destination Trip<select v-model="destinationId" aria-label="Destination Trip"><option value="">Select destination</option><option v-if="destinationId && !destinations.some(x => x.id === destinationId)" :value="destinationId">Selected destination</option><option v-for="item in destinations" :key="item.id" :value="item.id">{{ item.name }}</option></select></label></fieldset>
    </div>
    <button type="button" :disabled="loading || state.pending || !sourceId || !destinationId" @click="load">{{ loading ? 'Loading preview…' : preview && pairMatches ? 'Refresh and review current values' : 'Preview copy choices' }}</button>
    <div aria-live="polite"><p v-if="error || state.error" role="alert">{{ error || state.error }} <a :href="editUrl">Inspect destination Trip</a></p><p>{{ state.status }}</p>
      <p v-if="state.pending">Saving the submitted choices…</p><p v-if="preview && !pairMatches">Trip selection changed. Preview the selected pair before applying choices.</p></div>
    <template v-if="preview && state.workspace && draft">
      <h2>Destination · {{ state.workspace.current.graph.name }}</h2><p>{{ state.workspace.current.graph.isPublic ? 'Public · saved content updates this Trip' : 'Private' }} · <a :href="editUrl">Edit / inspect destination Trip</a></p>
      <p v-if="preview.source.mediaOmitted" role="status">Unqualified source covers and Notes images were omitted. Copying does not grant media permission; reselect eligible images in the destination editor.</p>
      <div class="copy-map">
        <CompositionMap ref="map" :workspace="state.workspace" :selection="state.view.selection" :counterpart="selected?.destinationId || ''" :visible="visible" map-label="Source and existing destination Trip map" @select="select" @status="mapStatus = $event" />
      </div>
      <fieldset><legend>Preview map</legend><label><input v-model="visible.b" type="checkbox" /> Source (orange)</label> · <label><input v-model="visible.c" type="checkbox" /> Destination (green)</label><button type="button" @click="map?.focus()">Focus selected geography</button></fieldset><p role="status">{{ mapStatus }}</p>
      <div class="copy-review">
        <SourceTree :snapshot="preview.source" side="b" :selection="state.view.selection" :mappings="[]" :expanded="expanded" objects-only caption="Source" @select="select" @expand="expand" />
        <section aria-label="Reviewed copy choices">
          <h2>Review proposed effect · {{ draft.items.length }} selected</h2>
          <p>Each list receives a contiguous block in the displayed order. Reused objects keep their identity, container and position. Trip metadata is unchanged; own planning is copied only when explicitly selected.</p>
          <p v-if="!draft.items.length">Select a Region, Place, Area or Trip-level Segment from the source.</p>
          <details v-for="(item, index) in draft.items" :key="`${item.kind}/${item.sourceId}`" :open="item === selected || item.withChildren && item.children.includes(selected!)">
            <summary>{{ index + 1 }} · {{ item.kind }} · {{ label(find(preview.source, item.kind, item.sourceId)) }}</summary>
            <button type="button" :disabled="index === 0" @click="[draft.items[index - 1], draft.items[index]] = [draft.items[index], draft.items[index - 1]]">Move up</button>
            <button type="button" :disabled="index === draft.items.length - 1" @click="[draft.items[index], draft.items[index + 1]] = [draft.items[index + 1], draft.items[index]]">Move down</button>
            <button type="button" @click="draft.items.splice(index, 1)">Remove local choice</button>
            <ChoiceControls :workspace="state.workspace" :review="draft" :choice="item" :editor-url="editUrl" :options="options" existing-trip />
            <FieldChoices v-if="item.decision === 'copy' || item.decision === 'reuse' && find(draft.baseline, item.kind, item.destinationId)" :workspace="state.workspace" :review="draft" :choice="item" :editor-url="editUrl" :options="options" existing-trip />
          </details>
          <details class="composition-credits"><summary>Applicable source credits &amp; notices</summary>
            <p>Destination owner’s contribution policy stays {{ state.workspace.current.graph.contributionPolicy || 'unselected' }}. Inherited notices remain separate.</p>
            <p v-if="!preview.source.planning">{{ preview.source.graph.name }} · {{ preview.source.graph.contributionPolicy }} · Selected content adapted.</p>
            <p v-for="(credit, index) in preview.source.graph.credits" :key="index">{{ credit.contributor }} · {{ credit.sourceTitle }} · {{ credit.licence }} · {{ credit.notices }} {{ credit.modifications }}</p>
            <p>Applicable notices accompany copied content or selected source fields. Existing destination notices stay.</p>
          </details>
          <button type="button" :disabled="state.pending || state.needsInspection || loading || !pairMatches || !draft.items.length" @click="apply">{{ state.pending ? 'Saving submitted copy…' : 'Apply reviewed copy to destination' }}</button>
        </section>
      </div>
    </template>
  </section>
  <ConfirmDialog />
</template>
