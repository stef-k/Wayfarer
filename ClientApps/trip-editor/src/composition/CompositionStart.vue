<script setup lang="ts">
import { message as tr } from '../../../../wwwroot/js/localization/messages.js';
import { onMounted, onUnmounted, ref, watch } from 'vue';
import { compositionApi } from './compositionApi';
import type { SavedComparison, SourceOption } from './compositionTypes';
import { useCompositionLanguage } from './useCompositionLanguage';
import { confirm } from '../composables/useConfirmDialog';
const props = defineProps<{ token: string }>();
const api = compositionApi(props.token);
const mode = ref('composition'); const sourceA = ref(''); const sourceB = ref('');
const searchA = ref(''); const searchB = ref(''); const optionsA = ref<SourceOption[]>([]); const optionsB = ref<SourceOption[]>([]);
const name = ref(''); const changeNote = ref(''); const keepPlanning = ref(true);
const existing = ref<SavedComparison[]>([]); const unfinished = ref<SavedComparison[]>([]);
const pending = ref(false); const loading = ref(false); const error = ref(''); const inspect = ref(false);
const savedResultUrl = ref('');
useCompositionLanguage(
  () => Boolean(sourceA.value || sourceB.value || name.value || changeNote.value || mode.value !== 'composition' || !keepPlanning.value),
  () => pending.value || inspect.value
);
let active = true;
let generation = 0;
/** Search responses are selection-independent and cannot overwrite a more recent result. */
async function search(): Promise<void> {
  const captured = ++generation; loading.value = true;
  const terms = [searchA.value, searchB.value];
  try { const [a, b, all] = await Promise.all([api.sources(terms[0], true), api.sources(terms[1], false), api.list()]);
    if (captured !== generation) return;
    unfinished.value = all;
    if (terms[0] !== searchA.value || terms[1] !== searchB.value) return;
    const retainSelection = (results: SourceOption[], prior: SourceOption[], selected: string): SourceOption[] => {
      const retained = prior.find(t => t.id === selected);
      return retained && !results.some(t => t.id === selected) ? [retained, ...results] : results;
    };
    optionsA.value = retainSelection(a, optionsA.value, sourceA.value);
    optionsB.value = retainSelection(b, optionsB.value, sourceB.value); error.value = '';
  } catch (e) { if (captured === generation) error.value = e instanceof Error ? e.message : tr('Editor.CompositionSourceSearchUnavailable', 'Source search unavailable.'); }
  finally { if (captured === generation) loading.value = false; }
}
let pairGeneration = 0;
watch([sourceA, sourceB], async () => {
  const generation = ++pairGeneration;
  existing.value = [];
  if (!sourceA.value || !sourceB.value) return;
  try { const matches = await api.list(sourceA.value, sourceB.value); if (generation === pairGeneration) existing.value = matches; }
  catch (e) { if (generation === pairGeneration) error.value = e instanceof Error ? e.message : tr('Editor.CompositionExistingComparisonsUnavailable', 'Existing comparisons unavailable.'); }
});
/** Captures all creation input before waiting and always checks the pair again at activation. */
const creationInput = () => ({ version: mode.value === 'version', body: { sourceA: sourceA.value,
  ...(mode.value === 'version' ? {} : { sourceB: sourceB.value }), name: name.value.trim() || null,
  changeNote: changeNote.value.trim() || null, keepOwnBasePlanning: keepPlanning.value },
  view: { searchA: searchA.value, searchB: searchB.value, name: name.value, changeNote: changeNote.value } });
async function create(): Promise<void> {
  if (pending.value || inspect.value) return;
  const captured = creationInput(); const { version, body: submitted } = captured;
  if (!submitted.sourceA || !version && !submitted.sourceB) { error.value = tr('Editor.CompositionSelectTheRequiredSources', 'Select the required sources.'); return; }
  pending.value = true; error.value = '';
  try {
    if (!version) {
      const pairs = await api.list(submitted.sourceA, submitted.sourceB);
      if (!active) return;
      existing.value = pairs;
      if (pairs.length && !await confirm({ title: tr('Editor.CompositionStartAnotherIndependentC', 'Start another independent C?'), message: tr('Editor.CompositionUnfinishedWorkAlreadyExistsForThis', 'Unfinished work already exists for this source pair. Continue Existing resumes the same C. Start New deliberately creates a separate private Trip and comparison.'), confirmLabel: tr('Editor.CompositionStartNew', 'Start New'), cancelLabel: tr('Editor.CompositionContinueExisting', 'Continue Existing'), variant: 'warning' })) return;
    }
    if (!active) return;
    const result = await api.create(submitted, version);
    if (!active) return;
    const url = version ? `/User/Trip/Edit/${result.tripId}` : `/User/Trip/Compose/${result.compositionId}`;
    if (JSON.stringify(creationInput()) === JSON.stringify(captured)) window.location.assign(url);
    else savedResultUrl.value = url;
  } catch (e) { if (active) { error.value = e instanceof Error ? e.message : tr('Editor.CompositionCreationCouldNotBeConfirmedInspect', 'Creation could not be confirmed. Inspect My Trips.'); inspect.value = true; } }
  finally { if (active) pending.value = false; }
}
onMounted(search);
onUnmounted(() => { active = false; ++generation; ++pairGeneration; });
</script>

<template>
  <section class="composition-start" :aria-label="tr('Editor.CompositionStartOrResumeTripComposition', 'Start or resume Trip composition')">
    <h1>{{ tr('Editor.CompositionCompareCombineTrips', 'Compare & combine Trips') }}</h1>
    <p>{{ tr('Editor.CompositionSaveAnIndependentPrivateCA', 'Save an independent private C. A and B remain read-only. Every successful action saves immediately.') }}</p>
    <a href="/User/Trip">{{ tr('Editor.MyTrips', 'My Trips') }}</a>
    <p v-if="error" role="alert" class="alert alert-danger">{{ error }}</p>
    <p v-if="savedResultUrl" role="status">{{ tr('Editor.CompositionTheEarlierSubmittedCreationWasSaved', 'The earlier submitted creation was saved. Newer choices remain here and would create another Trip.') }} <a :href="savedResultUrl">{{ tr('Editor.CompositionOpenTheSavedResult', 'Open the saved result') }}</a></p>
    <form @submit.prevent="create">
      <label class="composition-field">{{ tr('Editor.Journey', 'Journey') }}<select v-model="mode"><option value="composition">{{ tr('Editor.CompositionCompareAAndBInA', 'Compare A and B in a new C') }}</option><option value="version">{{ tr('Editor.CompositionCreateAnIndependentNewVersionOf', 'Create an independent new version of A') }}</option></select></label>
      <div class="composition-differences">
        <section><h2>{{ tr('Editor.CompositionAYourTrip', 'A · Your Trip') }}</h2>
          <label class="composition-field">{{ tr('Editor.CompositionFindOwnedA', 'Find owned A') }}<input v-model="searchA" type="search" maxlength="200" /></label>
          <label class="composition-field">{{ tr('Editor.CompositionSourceA', 'Source A') }}<select v-model="sourceA" :aria-label="tr('Editor.CompositionSourceA', 'Source A')" required><option value="" disabled>{{ tr('Editor.CompositionSelectOwnedA', 'Select owned A') }}</option><option v-for="trip in optionsA" :key="trip.id" :value="trip.id">{{ trip.name }}</option></select></label>
        </section>
        <section v-if="mode === 'composition'"><h2>{{ tr('Editor.CompositionBOwnedOrPublicReusableTrip', 'B · Owned or public reusable Trip') }}</h2>
          <label class="composition-field">{{ tr('Editor.CompositionFindEligibleB', 'Find eligible B') }}<input v-model="searchB" type="search" maxlength="200" /></label>
          <label class="composition-field">{{ tr('Editor.CompositionSourceB', 'Source B') }}<select v-model="sourceB" :aria-label="tr('Editor.CompositionSourceB', 'Source B')" required><option value="" disabled>{{ tr('Editor.CompositionSelectEligibleB', 'Select eligible B') }}</option><option v-for="trip in optionsB.filter(t => t.id !== sourceA)" :key="trip.id" :value="trip.id">{{ trip.name }} · {{ trip.owned ? tr('Editor.CompositionYours', 'Yours') : trip.contributionPolicy }}</option></select></label>
        </section>
      </div>
      <button type="button" :disabled="loading" @click="search">{{ loading ? tr('Editor.CompositionFindingTrips', 'Finding Trips…') : tr('Editor.CompositionSearchSourcesRefreshUnfinishedWork', 'Search sources / refresh unfinished work') }}</button>
      <p>{{ tr('Editor.CompositionUpTo20MatchesPlusYour', 'Up to 20 matches plus your retained selection. Refine the name to find another Trip. Foreign private, unlicensed and public view-only Trips are unavailable.') }}</p>
      <section v-if="existing.length && mode === 'composition'" :aria-label="tr('Editor.CompositionExistingWorkForThisSourcePair', 'Existing work for this source pair')"><h2>{{ tr('Editor.CompositionContinueExisting', 'Continue Existing') }}</h2>
        <p>{{ tr('Editor.CompositionThisPairAlreadyHasSavedWork', 'This pair already has saved work. Continue resumes that same C.') }}</p>
        <a v-for="item in existing" :key="item.compositionId" :href="`/User/Trip/Compose/${item.compositionId}`" class="btn btn-outline-primary"> {{ tr('Editor.CompositionContinueExisting', 'Continue Existing') }} · {{ item.name }}</a>
      </section>
      <label class="composition-field">{{ tr('Editor.CompositionNewTripNameOptional', 'New Trip name (optional)') }}<input v-model="name" maxlength="200" /></label>
      <label class="composition-field">{{ tr('Editor.CompositionPrivateChangeNoteOptional', 'Private change note (optional)') }}<textarea v-model="changeNote" maxlength="2000"></textarea></label>
      <label class="composition-check"><input v-model="keepPlanning" type="checkbox" /> {{ tr('Editor.CompositionRetainMyAPlanningInThe', 'Retain my A planning in the new private Trip') }}</label>
      <p v-if="mode === 'version'">{{ tr('Editor.CompositionNewVersionsAreOrdinaryTripsWith', 'New versions are ordinary Trips with no compulsory comparison workspace.') }}</p>
      <button class="btn btn-primary" :disabled="pending || inspect">{{ pending ? tr('Editor.CompositionCreating', 'Creating…') : mode === 'version' ? tr('Editor.CompositionCreateNewVersion', 'Create new version') : tr('Editor.CompositionStartNewComposition', 'Start New composition') }}</button>
      <p v-if="inspect" role="status">{{ tr('Editor.CompositionInspectYourTripsBeforeAnyFurther', 'Inspect your trips before any further creation. The submitted action may still finish.') }} <a href="/User/Trip" target="_blank" rel="noopener">{{ tr('Editor.MyTrips', 'My Trips') }}</a></p>
      <button v-if="inspect" type="button" @click="inspect = false; search()">{{ tr('Editor.CompositionIInspectedMyTripsReviewA', 'I inspected My Trips · review a new creation') }}</button>
    </form>
    <section :aria-label="tr('Editor.CompositionAllUnfinishedCompositions', 'All unfinished compositions')"><h2>{{ tr('Editor.CompositionUnfinishedCompositions', 'Unfinished compositions') }}</h2>
      <p v-if="!unfinished.length && !loading">{{ tr('Editor.CompositionNoUnfinishedComparisonsFound', 'No unfinished comparisons found.') }}</p>
      <p v-for="item in unfinished" :key="item.compositionId"><strong>{{ item.name }}</strong> · {{ tr('Editor.CompositionSavedComparison', 'Saved comparison') }} · <a :href="`/User/Trip/Compose/${item.compositionId}`">{{ tr('Editor.CompositionContinueComposition', 'Continue Composition') }}</a></p>
    </section>
  </section>
</template>
