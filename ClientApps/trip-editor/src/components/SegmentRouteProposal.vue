<script setup lang="ts">
import { computed, onUnmounted, watch } from 'vue';
import { acceptExternalRouteProposal, ExternalRouteProposalError, generateExternalRouteProposal } from '../api/tripEditorApi';
import { confirm } from '../composables/useConfirmDialog';
import type { AcceptedExternalRouteProposal, EditorSegment, ExternalRouteProposal, Guid } from '../types';
import { createSegmentRouteProposalStore } from './segmentRouteProposalState';

const props = defineProps<{
  antiforgeryToken: string;
  draftHasRoute: boolean;
  draftMode: string;
  draftTransportProfileId: Guid | null;
  segment: EditorSegment;
  tripId: Guid;
}>();

const emit = defineEmits<{
  accepted: [proposal: AcceptedExternalRouteProposal];
  previewChanged: [proposal: ExternalRouteProposal | null];
}>();

const proposalStore = createSegmentRouteProposalStore();
const states = proposalStore.states;
const proposalContextKey = computed(() => `${props.draftTransportProfileId ?? ''}:${props.draftMode}`);
const state = computed(() => states[props.segment.id] ??= proposalStore.get(props.segment.id, proposalContextKey.value));
const capability = computed(() => props.segment.externalRouting ?? null);
const actionLabel = computed(() => props.draftHasRoute ? 'Replace with routed path' : 'Generate routed path');
const profileChanged = computed(() => props.draftMode !== props.segment.mode);

watch(() => props.segment.id, () => emit('previewChanged', state.value.proposal), { immediate: true });
watch(proposalContextKey, profileKey => {
  if (!proposalStore.invalidateProfile(props.segment.id, profileKey)) return;
  emit('previewChanged', null);
});

/** Starts only an explicit user-authorized provider request for this Segment. */
async function generate(): Promise<void> {
  if (!capability.value?.available || profileChanged.value || state.value.generating) return;
  if (props.draftHasRoute && !(await confirm({
    title: 'Replace current route?', message: 'Generate a proposal without changing the current draft until you accept it.',
    confirmLabel: 'Generate replacement', cancelLabel: 'Keep current route', variant: 'warning'
  }))) return;
  state.value.controller?.abort();
  const controller = new AbortController();
  const segmentId = props.segment.id;
  const requestId = proposalStore.begin(segmentId, proposalContextKey.value, controller);
  emit('previewChanged', null);
  try {
    const proposal = await generateExternalRouteProposal(
      props.tripId, segmentId, props.antiforgeryToken, props.segment.aggregateConcurrencyToken, controller.signal);
    if (proposalStore.complete(segmentId, requestId, proposal)) emit('previewChanged', proposal);
  } catch (error) {
    const message = controller.signal.aborted
      ? 'Route generation cancelled. The draft is unchanged.'
      : boundedMessage(error);
    proposalStore.fail(segmentId, requestId, message);
  }
}

/** Accepts only after the server revalidates the protected context. */
async function accept(): Promise<void> {
  if (!state.value.proposal) return;
  try {
    const accepted = await acceptExternalRouteProposal(props.tripId, state.value.proposal, props.antiforgeryToken);
    emit('accepted', accepted);
    discard();
  } catch (error) { state.value.error = boundedMessage(error); }
}

/** Discards this Segment's proposal without changing its draft. */
function discard(): void {
  proposalStore.discard(props.segment.id);
  emit('previewChanged', null);
}

function boundedMessage(error: unknown): string {
  if (!(error instanceof ExternalRouteProposalError)) return 'Route generation is unavailable. The draft is unchanged.';
  if (error.code.includes('stale') || error.code.includes('expired')) return 'This proposal is stale or expired. Generate it again.';
  if (error.code.includes('rate') || error.code.includes('budget')) return 'The routing request limit was reached. Try again later.';
  return 'The routing provider could not produce a safe route. The draft is unchanged.';
}

onUnmounted(() => {
  proposalStore.dispose();
  emit('previewChanged', null);
});
</script>

<template>
  <section v-if="capability?.available" class="border rounded p-2 mt-3" aria-labelledby="external-route-heading">
    <h3 id="external-route-heading" class="fs-6">External routed path</h3>
    <p class="small mb-1"><strong>{{ capability.providerDisplayName }}</strong> · {{ capability.mappedProfileLabel }}</p>
    <p class="small mb-1">{{ capability.disclosure }}</p>
    <p v-if="capability.attribution" class="small text-muted mb-2">{{ capability.attribution }}</p>
    <p v-if="profileChanged" class="small text-warning">Save the transport-profile change before generating a new proposal.</p>
    <button v-if="!state.proposal" type="button" class="btn btn-outline-info btn-sm" :disabled="state.generating || profileChanged" @click="generate">
      {{ state.generating ? 'Generating…' : actionLabel }}
    </button>
    <button v-if="state.generating" type="button" class="btn btn-outline-secondary btn-sm ms-2" @click="discard">Cancel generation</button>
    <div v-if="state.proposal" role="status" class="mt-2">
      <p class="small mb-2">Proposal ready for preview. Accepting changes only this unsaved Segment draft.</p>
      <button type="button" class="btn btn-success btn-sm" @click="accept">Accept proposal</button>
      <button type="button" class="btn btn-outline-secondary btn-sm ms-2" @click="discard">Discard proposal</button>
    </div>
    <p v-if="state.error" class="trip-editor-form-error mt-2 mb-0" role="alert" tabindex="-1">{{ state.error }}</p>
  </section>
</template>
