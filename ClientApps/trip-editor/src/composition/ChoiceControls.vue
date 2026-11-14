<script setup lang="ts">
import { computed } from 'vue';
import { contents, find, label, newChoice, ordered, parentId, sourceSnapshot } from './compositionModel';
import type { Choice, Content, Kind, ReviewDraft, Workspace } from './compositionTypes';
const props = defineProps<{ workspace: Workspace; review: ReviewDraft; choice: Choice; child?: boolean }>();
const source = computed(() => sourceSnapshot(props.workspace, props.review.side));
const captured = computed(() => find(source.value, props.choice.kind, props.choice.sourceId)!);
const current = computed(() => props.review.baseline);
const destinations = computed(() => contents(current.value, props.choice.kind));
const regions = computed(() => ordered(current.value, 'region').filter(r => props.choice.kind !== 'area' || r.name !== 'Unassigned Places'));
const previous = computed(() => props.review.mappings.filter(m => m.source === props.review.side && m.kind === props.choice.kind && m.sourceId === props.choice.sourceId));
const positionItems = computed(() => ordered(current.value, props.choice.kind, props.choice.regionId
  || (props.choice.kind === 'place' ? current.value.graph.regions.find(r => r.name === 'Unassigned Places')?.id : undefined))
  .filter(r => props.choice.kind !== 'region' || r.name !== 'Unassigned Places'));
const canonical = computed(() => props.choice.kind === 'region' && captured.value.name === 'Unassigned Places');
const canRemove = computed(() => previous.value.some(m => m.destinationId === props.choice.destinationId && !['skip', 'destinationRemoved'].includes(m.decision)));
const destinationLabel = (item: Content, kind: Kind): string => {
  const parent = parentId(current.value, kind, item.id);
  return `${parent ? `${find(current.value, 'region', parent)?.name} · ` : ''}${label(item)}`;
};
/** Child decisions are explicit and retain source manual order. No implicit Segments enter this scope. */
function scope(event: Event): void {
  props.choice.withChildren = (event.target as HTMLInputElement).checked;
  if (props.choice.withChildren && !props.choice.children.length) props.choice.children = (['place', 'area'] as const)
    .flatMap(kind => ordered(source.value, kind, props.choice.sourceId).map(item => newChoice({ ...props.workspace, current: current.value, mappings: props.review.mappings }, props.review.side, kind, item.id)));
}
const anchorRole = (id: string): string => [captured.value.fromPlaceId === id ? 'From' : '',
  ...[...captured.value.waypoints ?? []].sort((a, b) => a.position - b.position).filter(w => w.placeId === id).map(w => `Via ${w.position + 1}${w.routeVertexIndex == null ? '' : ` · route vertex ${w.routeVertexIndex}`}`),
  captured.value.toPlaceId === id ? 'To' : ''].filter(Boolean).join(' / ');
</script>

<template>
  <fieldset class="composition-choice">
    <legend>{{ choice.kind }} · {{ label(captured) }}</legend>
    <label v-if="previous.length > 1" class="composition-field">Saved counterpart decision
      <select v-model="choice.mappingId" @change="choice.destinationId = choice.mappingId">
        <option v-for="m in previous" :key="m.destinationId" :value="m.destinationId">{{ find(current, choice.kind, m.destinationId) ? label(find(current, choice.kind, m.destinationId)!) : 'Removed counterpart' }} · {{ m.decision }}</option>
      </select>
    </label>
    <label class="composition-field">Action for C
      <select v-model="choice.decision">
        <option v-if="choice.kind !== 'trip' && !canonical" value="copy">Copy / keep separate</option>
        <option value="reuse">Reuse current counterpart / Keep or adopt selected fields</option>
        <option v-if="choice.kind !== 'trip' && !canRemove" value="skip">Skip this source idea</option>
        <option v-if="canRemove && choice.kind !== 'trip'" value="remove">Remove included counterpart from C</option>
      </select>
    </label>
    <p v-if="canonical">Unassigned Places is a reserved category. Select individual Places to copy them into C; it is never duplicated as a normal Region.</p>
    <label v-if="previous.some(m => ['skip', 'destinationRemoved'].includes(m.decision))" class="composition-check"><input v-model="choice.readd" type="checkbox" /> Deliberately re-add this excluded idea</label>
    <template v-if="choice.decision === 'reuse' || choice.decision === 'remove'">
      <label class="composition-field">Current C counterpart
        <select v-model="choice.destinationId" required>
          <option value="" disabled>Select a current counterpart</option>
          <option v-for="item in destinations" :key="item.id" :value="item.id">{{ destinationLabel(item, choice.kind) }}</option>
        </select>
      </label>
      <p>Reuse keeps this identity, its Region and its position. Matched fields default Keep.</p>
    </template>
    <template v-if="choice.decision === 'copy'">
      <label class="composition-check"><input v-model="choice.customName" type="checkbox" /> Use a custom name</label>
      <label v-if="choice.customName" class="composition-field">Custom name<input v-model="choice.name" maxlength="200" required /></label>
      <p v-else>Original name: {{ captured.name ?? captured.title ?? 'Untitled Segment · derived route label' }}</p>
      <label class="composition-check"><input v-model="choice.separate" type="checkbox" /> Keep separate even if the same name or another copy exists</label>
      <p v-if="contents(current, choice.kind).some(x => label(x) === (choice.customName ? choice.name : label(captured)))" class="composition-warning">A same-name item exists in C. Review reuse or explicitly approve a separate copy.</p>
      <template v-if="!child && (choice.kind === 'place' || choice.kind === 'area')">
        <label class="composition-field">Destination Region
          <select v-model="choice.regionId" :disabled="Boolean(choice.newRegionName)" :required="choice.kind === 'area' && !choice.newRegionName">
            <option value="">{{ choice.kind === 'place' ? 'Canonical Unassigned Places (default)' : 'Select a normal Region' }}</option>
            <option v-for="region in regions" :key="region.id" :value="region.id">{{ region.name }}</option>
          </select>
        </label>
        <label v-if="choice.kind === 'area'" class="composition-field">Or create a normal Region with this copy<input v-model="choice.newRegionName" maxlength="200" :disabled="Boolean(choice.regionId)" /></label>
      </template>
      <template v-if="!child">
        <label class="composition-field">{{ choice.kind === 'segment' ? 'Trip-level insertion position' : 'Insertion position (independent of Region)' }}
          <select v-model="choice.position"><option value="append">Append</option><option value="start">At start</option><option value="after" :disabled="Boolean(choice.newRegionName)">After selection</option></select>
        </label>
        <label v-if="choice.position === 'after'" class="composition-field">After this current C {{ choice.kind }}
          <select v-model="choice.afterId" required><option value="" disabled>Select a compatible item</option><option v-for="item in positionItems" :key="item.id" :value="item.id">{{ label(item) }}</option></select>
        </label>
        <p class="composition-result">Result: {{ choice.kind === 'segment' ? 'C · Trip-level Segments' : choice.kind === 'region' ? 'C · Regions' : choice.newRegionName || find(current, 'region', choice.regionId)?.name || 'Unassigned Places' }} · {{ choice.position === 'after' ? `After ${find(current, choice.kind, choice.afterId) ? label(find(current, choice.kind, choice.afterId)!) : '…'}` : choice.position === 'start' ? 'At start' : 'Append' }}</p>
      </template>
    </template>
    <template v-if="choice.decision === 'remove' && choice.kind === 'region'">
      <label class="composition-field">Direct Places<select v-model="choice.places"><option value="keep">Keep in the reviewed fallback</option><option value="delete">Delete with Region</option></select></label>
      <label class="composition-field">Direct Areas<select v-model="choice.areas"><option value="keep">Keep in the reviewed normal Region</option><option value="delete">Delete with Region</option></select></label>
      <p>Current deletion consequences are shown before applying. Region Notes, cover, centre and planning are removed.</p>
    </template>
    <template v-if="choice.kind === 'region' && !canonical && ['copy', 'reuse'].includes(choice.decision)">
      <label class="composition-check"><input :checked="choice.withChildren" type="checkbox" @change="scope" /> Region and direct Places/Areas (otherwise Region only)</label>
      <p>No sub-Regions or automatic Segments. Reused children keep their C container.</p>
      <ChoiceControls v-for="entry in choice.withChildren ? choice.children : []" :key="`${entry.kind}/${entry.sourceId}`" :workspace="workspace" :review="review" :choice="entry" child />
    </template>
    <fieldset v-if="choice.kind === 'segment' && (choice.decision === 'copy' || choice.fields.route?.operation === 'useSource')">
      <legend>Approve complete From / ordered Via / To dependencies</legend>
      <p v-if="!choice.anchors.length">This historical route has no Place anchors. Its saved geometry is shown when available.</p>
      <fieldset v-for="anchor in choice.anchors" :key="anchor.sourceId">
        <legend>{{ anchorRole(anchor.sourceId) }} · {{ label(find(source, 'place', anchor.sourceId)!) }}</legend>
        <p>Source coordinates: {{ find(source, 'place', anchor.sourceId)?.location?.slice().reverse().join(', ') ?? 'Unlocated' }}</p>
        <label class="composition-field">Approved current C Place<select v-model="anchor.destinationId">
          <option value="">Copy a separate source-coordinate Place</option><option v-for="place in contents(current, 'place')" :key="place.id" :value="place.id">{{ destinationLabel(place, 'place') }} · {{ place.location?.slice().reverse().join(', ') ?? 'Unlocated' }}</option>
        </select></label>
        <template v-if="!anchor.destinationId">
          <label class="composition-field">Missing Place destination Region<select v-model="anchor.regionId"><option value="">Canonical Unassigned Places</option><option v-for="r in ordered(current, 'region')" :key="r.id" :value="r.id">{{ r.name }}</option></select></label>
          <label class="composition-check"><input v-model="anchor.approveCopy" type="checkbox" /> Approve a separate Place with the captured location, appended here</label>
        </template>
      </fieldset>
      <p>Close semantic matches can still be incompatible route anchors. No Places move, no captured geometry clears, and no route regenerates without ordinary review.</p>
    </fieldset>
  </fieldset>
</template>
