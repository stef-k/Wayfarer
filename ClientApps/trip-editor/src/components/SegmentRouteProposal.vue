<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue';
import { ExternalRouteProposalError, generateExternalRouteProposal } from '../api/tripEditorApi';
import { confirm } from '../composables/useConfirmDialog';
import type { EditorSegment, ExternalRouteProposal, Guid } from '../types';
import { createSegmentRouteProposalStore } from './segmentRouteProposalState';

const props = defineProps<{
  antiforgeryToken: string;
  draftHasRoute: boolean;
  isSaving: boolean;
  manualDurationOverride: boolean;
  draftMode: string;
  draftTransportProfileId: Guid | null;
  draftContextKey: string;
  segment: EditorSegment;
  tripId: Guid;
}>();

const emit = defineEmits<{
  generatingChanged: [generating: boolean];
  previewChanged: [proposal: ExternalRouteProposal | null];
}>();

const proposalStore = createSegmentRouteProposalStore();
const states = proposalStore.states;
const selectedProviderMode = ref('');
const proposalContextKey = computed(() => `${props.draftTransportProfileId ?? ''}:${props.draftMode}:${selectedProviderMode.value}`);
const state = computed(() => states[props.segment.id] ??= proposalStore.get(props.segment.id, proposalContextKey.value));
const capability = computed(() => props.segment.externalRouting ?? null);
const actionLabel = computed(() => props.draftHasRoute ? 'Replace with routed path' : 'Generate routed path');
const profileChanged = computed(() => props.draftMode !== props.segment.mode);

watch(() => props.segment.id, (segmentId, previousId) => {
  if (previousId) {
    proposalStore.discard(previousId);
    selectedProviderMode.value = '';
  }
  emit('previewChanged', proposalStore.get(segmentId, proposalContextKey.value).proposal);
}, { immediate: true });
watch(() => state.value.generating, value => emit('generatingChanged', value), { flush: 'sync' });
watch(() => props.draftContextKey, (value, previous) => {
  if (value === previous) return;
  const hadProposal = state.value.proposal !== null;
  proposalStore.invalidate(props.segment.id, 'draft-context-changed');
  if (hadProposal && !props.isSaving) state.value.error = 'Route context changed. The proposal was discarded; your draft route is unchanged.';
  emit('previewChanged', null);
  emit('generatingChanged', false);
}, { flush: 'sync' });
watch(proposalContextKey, profileKey => {
  const hadProposal = state.value.proposal !== null;
  if (!proposalStore.invalidateProfile(props.segment.id, profileKey)) return;
  if (hadProposal) state.value.error = 'Directions mode changed. Generate a new proposal to use this mode.';
  emit('previewChanged', null);
  emit('generatingChanged', false);
});

/** Starts only an explicit user-authorized provider request for this Segment. */
async function generate(): Promise<void> {
  if (!capability.value?.available || !selectedProviderMode.value || profileChanged.value || state.value.generating || props.isSaving) return;
  if (props.draftHasRoute && !(await confirm({
    title: 'Replace current route?', message: 'Generate a proposal without changing the current draft until you save the Segment.',
    confirmLabel: 'Generate replacement', cancelLabel: 'Keep current route', variant: 'warning'
  }))) return;
  state.value.controller?.abort();
  const controller = new AbortController();
  const segmentId = props.segment.id;
  const requestId = proposalStore.begin(segmentId, proposalContextKey.value, controller);
  emit('previewChanged', null);
  try {
    const proposal = await generateExternalRouteProposal(
      props.tripId, segmentId, props.antiforgeryToken, props.segment.aggregateConcurrencyToken,
      selectedProviderMode.value, controller.signal);
    if (proposalStore.complete(segmentId, requestId, proposal)) emit('previewChanged', proposal);
  } catch (error) {
    const message = controller.signal.aborted
      ? 'Route generation cancelled. The draft is unchanged.'
      : boundedMessage(error);
    proposalStore.fail(segmentId, requestId, message);
  }
}

/** Discards this Segment's proposal without changing its draft. */
function discard(): void {
  proposalStore.discard(props.segment.id);
  emit('previewChanged', null);
  selectedProviderMode.value = '';
}

function boundedMessage(error: unknown): string {
  if (!(error instanceof ExternalRouteProposalError)) return 'Route generation is unavailable. The draft is unchanged.';
  if (error.code === 'unmapped-transport-profile') return 'Route suggestions are not configured for this transport profile.';
  if (error.code === 'unsupported-transport-profile') return 'This routing provider does not support the mapped transport mode.';
  if (error.code.includes('unavailable') || error.code.includes('configuration')) return 'Route suggestions are temporarily unavailable.';
  if (error.code.includes('stale') || error.code.includes('expired')) return 'This proposal is stale or expired. Generate it again.';
  if (error.code.includes('rate') || error.code.includes('budget')) return 'The routing request limit was reached. Try again later.';
  return 'The routing provider could not produce a safe route. The draft is unchanged.';
}

onUnmounted(() => {
  proposalStore.dispose();
  emit('previewChanged', null);
  emit('generatingChanged', false);
});
</script>

<template>
  <section v-if="capability?.available" class="border rounded p-2 mt-3" aria-labelledby="external-route-heading">
    <h3 id="external-route-heading" class="fs-6">External routed path</h3>
    <p class="small mb-1"><strong>{{ capability.providerDisplayName }}</strong></p>
    <p class="small mb-1">{{ capability.disclosure }}</p>
    <p v-if="capability.attribution?.includes('Powered by Geoapify')" class="small text-muted mb-2">
      <a href="https://www.geoapify.com/" rel="follow">Powered by Geoapify</a> ·
      <a href="https://www.openstreetmap.org/copyright">© OpenStreetMap contributors</a>
    </p>
    <p v-else-if="capability.attribution" class="small text-muted mb-2">{{ capability.attribution }}</p>
    <p v-if="profileChanged" class="small text-warning">Save the transport-profile change before generating a new proposal.</p>
    <label :for="`provider-mode-${segment.id}`" class="form-label small">Directions mode</label>
    <select :id="`provider-mode-${segment.id}`" v-model="selectedProviderMode" :disabled="isSaving" class="form-select form-select-sm mb-2">
      <option value="">Choose a mode</option>
      <option v-for="mode in capability.modes" :key="mode.key" :value="mode.key">{{ mode.label }}</option>
    </select>
    <p class="small text-muted">Used only to calculate this route. The Segment's transport profile stays unchanged.</p>
    <button v-if="!state.proposal" type="button" class="btn btn-outline-info btn-sm" :disabled="isSaving || state.generating || profileChanged || !selectedProviderMode" @click="generate">
      {{ state.generating ? 'Generating…' : actionLabel }}
    </button>
    <button v-if="state.generating" type="button" class="btn btn-outline-secondary btn-sm ms-2" @click="discard">Cancel generation</button>
    <div v-if="state.proposal" role="status" class="mt-2">
      <p class="small mb-2">Review the proposed route and estimates. Save Segment uses this proposal and saves your other Segment changes. Discard proposal keeps your previous route.</p>
      <p v-if="manualDurationOverride" class="small">Save keeps your manual duration instead of the proposed duration estimate.</p>
      <button type="button" class="btn btn-outline-secondary btn-sm ms-2" :disabled="isSaving" @click="discard">Discard proposal</button>
    </div>
    <p v-if="state.error" class="trip-editor-form-error mt-2 mb-0" role="alert" tabindex="-1">{{ state.error }}</p>
  </section>
</template>
