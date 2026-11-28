<script setup lang="ts">
import { onMounted, ref, shallowRef } from 'vue';
import { message as localize } from '../../../../wwwroot/js/localization/messages.js';
import { adoptedDraft } from '../components/metadataDraft';
import { adoptBases, CustodyRefusal, heldFields, knownHistory, listCustody, type SettingsField } from './custody';
import { receiptStatus, recoverMetadata, type Recovery } from './recoverMetadata';
import { custodyMessage } from './useMetadataCustody';
import ReceiptComparison from './ReceiptComparison.vue';
import StructureRecoveryPanel from './StructureRecoveryPanel.vue';
import { legacyRegionHoldKey } from './structureMutation';
import { isId } from './structureRoutes';

const props = defineProps<{ account: string; editorUrl: string }>();
const entries = shallowRef<Recovery[]>([]);
const busy = ref('');
const legacyTrips = ref<string[]>([]);
const error = ref('');

const endpoint = (tripId: string): string => `/api/trips/${tripId}/editor`;
const editUrl = (tripId: string): string => props.editorUrl.replace('00000000-0000-0000-0000-000000000000', tripId);

onMounted(async () => {
  try {
    entries.value = (await listCustody(props.account)).map(row => ({ row, current: null }));
    const prefix = legacyRegionHoldKey(props.account, '');
    legacyTrips.value = Object.keys(localStorage).filter(key => key.startsWith(prefix)).map(key => key.slice(prefix.length))
      .filter(id => isId(id) && !entries.value.some(entry => entry.row.tripId === id));
  }
  catch { error.value = custodyMessage('storage'); }
});

/** Fetches one historical attempt and current owner values without opening its graph. */
async function recover(tripId: string): Promise<void> {
  busy.value = tripId;
  error.value = '';
  try {
    const result = await recoverMetadata(props.account, tripId, endpoint(tripId));
    entries.value = entries.value.map(entry => entry.row.tripId === tripId ? result : entry);
    if (!result.current && (knownHistory(result.row).length || !heldFields(result.row).length && Object.keys(result.row.fields).length))
      error.value = custodyMessage('owner');
    else if (heldFields(result.row).length) error.value = custodyMessage('held');
  } catch { error.value = custodyMessage('storage'); }
  finally { busy.value = ''; }
}

/** Changes only the explicitly reviewed local base and preserves the saved draft generation on stale interactions. */
async function adopt(entry: Recovery, field: SettingsField): Promise<void> {
  const value = entry.row.fields[field];
  if (!value || !entry.current) return;
  try {
    const row = await adoptBases(entry.row, { [field]: adoptedDraft(field, value, entry.current) });
    entries.value = entries.value.map(prior => prior === entry ? { ...entry, row } : prior);
  } catch (failure) {
    error.value = custodyMessage(failure instanceof CustodyRefusal ? failure.reason : 'storage');
  }
}
</script>

<template>
  <section v-if="entries.length || legacyTrips.length || error" class="mb-3" aria-labelledby="trip-receipt-recovery-heading">
    <h3 id="trip-receipt-recovery-heading">{{ localize('Editor.ReceiptMyTrips', 'Recover Trip saves and drafts') }}</h3>
    <p v-if="error" role="status">{{ error }}</p>
    <article v-for="entry in entries" :key="entry.row.tripId" class="border rounded p-2 mb-2">
      <strong>{{ entry.current?.name ?? entry.row.fields.name?.desired.name ?? entry.row.tripId }}</strong>
      <p v-if="entry.row.fields.notesHtml" class="mb-1">
        {{ localize('Editor.ReceiptDraftNotes', 'Retained Notes draft') }}
      </p>
      <pre v-if="entry.row.fields.notesHtml" class="text-wrap text-break">{{ entry.row.fields.notesHtml.desired.notesHtml }}</pre>
      <p v-if="entry.row.fields.tags">{{ localize('Editor.Tags', 'Tags') }} · {{ entry.row.fields.tags.desired.tags?.join(', ') }}</p>
      <p v-if="entry.row.fields.shareProgressEnabled">
        {{ localize('Editor.ShareProgress', 'Share progress') }} ·
        {{ entry.row.fields.shareProgressEnabled.desired.shareProgressEnabled ? localize('Editor.Enabled', 'Enabled') : localize('Editor.Disabled', 'Disabled') }}
      </p>
      <p v-for="state in new Set([...entry.settled ?? [], ...knownHistory(entry.row)])" :key="state" role="status">{{ receiptStatus(state) }}</p>
      <ReceiptComparison v-if="entry.current" :current="entry.current" :fields="entry.row.review"
        :disabled="!!busy" @adopt="field => adopt(entry, field)" />
      <StructureRecoveryPanel :account="account" :trip-id="entry.row.tripId" :endpoint="endpoint(entry.row.tripId)" standalone />
      <button v-if="entry.row.attempts.length || Object.keys(entry.row.fields).length" class="btn btn-outline-secondary btn-sm me-2" type="button" :disabled="!!busy" @click="recover(entry.row.tripId)">
        {{ localize('Editor.ReceiptRecover', 'Recover Trip save') }}
      </button>
      <a v-if="entry.current" :href="editUrl(entry.row.tripId)">{{ localize('Editor.EditTrip', 'Edit Trip') }}</a>
    </article>
    <StructureRecoveryPanel v-for="tripId in legacyTrips" :key="tripId" :account="account" :trip-id="tripId" :endpoint="endpoint(tripId)" standalone />
  </section>
</template>
