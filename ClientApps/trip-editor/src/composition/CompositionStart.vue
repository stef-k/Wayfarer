<script setup lang="ts">
import { onMounted, ref, watch } from 'vue';
import { compositionApi } from './compositionApi';
import type { SavedComparison, SourceOption } from './compositionTypes';
import { confirm } from '../composables/useConfirmDialog';
const props = defineProps<{ token: string }>();
const api = compositionApi(props.token);
const mode = ref('composition'); const sourceA = ref(''); const sourceB = ref('');
const searchA = ref(''); const searchB = ref(''); const optionsA = ref<SourceOption[]>([]); const optionsB = ref<SourceOption[]>([]);
const name = ref(''); const changeNote = ref(''); const keepPlanning = ref(true);
const existing = ref<SavedComparison[]>([]); const unfinished = ref<SavedComparison[]>([]);
const pending = ref(false); const loading = ref(false); const error = ref(''); const inspect = ref(false);
let generation = 0;
/** Search responses are selection-independent and cannot overwrite a more recent result. */
async function search(): Promise<void> {
  const captured = ++generation; loading.value = true;
  try { const [a, b, all] = await Promise.all([api.sources(searchA.value, true), api.sources(searchB.value, false), api.list()]);
    if (captured !== generation) return;
    optionsA.value = a; optionsB.value = b; unfinished.value = all; error.value = '';
  } catch (e) { if (captured === generation) error.value = e instanceof Error ? e.message : 'Source search unavailable.'; }
  finally { if (captured === generation) loading.value = false; }
}
let pairGeneration = 0;
watch([sourceA, sourceB], async () => {
  const generation = ++pairGeneration;
  existing.value = [];
  if (!sourceA.value || !sourceB.value) return;
  try { const matches = await api.list(sourceA.value, sourceB.value); if (generation === pairGeneration) existing.value = matches; }
  catch (e) { if (generation === pairGeneration) error.value = e instanceof Error ? e.message : 'Existing comparisons unavailable.'; }
});
/** Captures all creation input before waiting and always checks the pair again at activation. */
async function create(): Promise<void> {
  if (pending.value || inspect.value) return;
  const version = mode.value === 'version';
  const submitted = { sourceA: sourceA.value, ...(version ? {} : { sourceB: sourceB.value }), name: name.value.trim() || null,
    changeNote: changeNote.value.trim() || null, keepOwnBasePlanning: keepPlanning.value };
  if (!submitted.sourceA || !version && !submitted.sourceB) { error.value = 'Select the required sources.'; return; }
  pending.value = true; error.value = '';
  try {
    if (!version) {
      const pairs = await api.list(submitted.sourceA, submitted.sourceB);
      existing.value = pairs;
      if (pairs.length && !await confirm({ title: 'Start another independent C?', message: 'Unfinished work already exists for this source pair. Continue Existing resumes the same C. Start New deliberately creates a separate private Trip and comparison.', confirmLabel: 'Start New', cancelLabel: 'Continue Existing', variant: 'warning' })) return;
    }
    const result = await api.create(submitted, version);
    window.location.assign(version ? `/User/Trip/Edit/${result.tripId}` : `/User/Trip/Compose/${result.compositionId}`);
  } catch (e) { error.value = e instanceof Error ? e.message : 'Creation could not be confirmed. Inspect My Trips.'; inspect.value = true; }
  finally { pending.value = false; }
}
onMounted(search);
</script>

<template>
  <section class="composition-start" aria-label="Start or resume Trip composition">
    <h1>Compare &amp; combine Trips</h1>
    <p>Save an independent private C. A and B remain read-only. Every successful action saves immediately.</p>
    <a href="/User/Trip">My Trips</a>
    <p v-if="error" role="alert" class="alert alert-danger">{{ error }}</p>
    <form @submit.prevent="create">
      <label class="composition-field">Journey<select v-model="mode"><option value="composition">Compare A and B in a new C</option><option value="version">Create an independent new version of A</option></select></label>
      <div class="composition-differences">
        <section><h2>A · Your Trip</h2>
          <label class="composition-field">Find owned A<input v-model="searchA" type="search" maxlength="200" /></label>
          <label class="composition-field">Source A<select v-model="sourceA" required><option value="" disabled>Select owned A</option><option v-for="trip in optionsA" :key="trip.id" :value="trip.id">{{ trip.name }}</option></select></label>
        </section>
        <section v-if="mode === 'composition'"><h2>B · Owned or public reusable Trip</h2>
          <label class="composition-field">Find eligible B<input v-model="searchB" type="search" maxlength="200" /></label>
          <label class="composition-field">Source B<select v-model="sourceB" required><option value="" disabled>Select eligible B</option><option v-for="trip in optionsB.filter(t => t.id !== sourceA)" :key="trip.id" :value="trip.id">{{ trip.name }} · {{ trip.owned ? 'Yours' : trip.contributionPolicy }}</option></select></label>
        </section>
      </div>
      <button type="button" :disabled="loading" @click="search">{{ loading ? 'Finding Trips…' : 'Search sources / refresh unfinished work' }}</button>
      <p>Up to 20 source results. Refine the name to find another Trip. Foreign private, unlicensed and public view-only Trips are unavailable.</p>
      <section v-if="existing.length && mode === 'composition'" aria-label="Existing work for this source pair"><h2>Continue Existing</h2>
        <p>This pair already has saved work. Continue resumes that same C.</p>
        <a v-for="item in existing" :key="item.compositionId" :href="`/User/Trip/Compose/${item.compositionId}`" class="btn btn-outline-primary">Continue Existing · {{ item.name }}</a>
      </section>
      <label class="composition-field">New Trip name (optional)<input v-model="name" maxlength="200" /></label>
      <label class="composition-field">Private change note (optional)<textarea v-model="changeNote" maxlength="2000"></textarea></label>
      <label class="composition-check"><input v-model="keepPlanning" type="checkbox" /> Retain my A planning in the new private Trip</label>
      <p v-if="mode === 'version'">New versions are ordinary Trips with no compulsory comparison workspace.</p>
      <button class="btn btn-primary" :disabled="pending || inspect">{{ pending ? 'Creating…' : mode === 'version' ? 'Create new version' : 'Start New composition' }}</button>
      <p v-if="inspect" role="status">Inspect <a href="/User/Trip" target="_blank" rel="noopener">My Trips</a> before any further creation. A communication failure does not prove rollback.</p>
      <button v-if="inspect" type="button" @click="inspect = false; search()">I inspected My Trips · review a new creation</button>
    </form>
    <section aria-label="All unfinished compositions"><h2>Unfinished compositions</h2>
      <p v-if="!unfinished.length && !loading">No unfinished comparisons found.</p>
      <p v-for="item in unfinished" :key="item.compositionId"><strong>{{ item.name }}</strong> · Saved comparison · <a :href="`/User/Trip/Compose/${item.compositionId}`">Continue Composition</a></p>
    </section>
  </section>
</template>
