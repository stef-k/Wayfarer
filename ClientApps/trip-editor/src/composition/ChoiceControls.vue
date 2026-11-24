<script setup lang="ts">
import { message as tr, number } from '../../../../wwwroot/js/localization/messages.js';
import { computed, ref } from 'vue';
import FieldChoices from './FieldChoices.vue';
import { contents, find, foldChildren, label, ordered, parentId, regionChildren, sourceSnapshot } from './compositionModel';
import { compositionDecision, displayLabel, tripKind } from './compositionPresentation';
import type { Choice, Content, Kind, ReviewDraft, Workspace } from './compositionTypes';
import type { EditorOptions } from '../types';
const props = defineProps<{ workspace: Workspace; review: ReviewDraft; choice: Choice; child?: boolean; destinationParent?: string; destinationParentName?: string; existingTrip?: boolean; editorUrl?: string; options?: EditorOptions | null }>();
const scopeError = ref('');
const destinationName = computed(() => props.existingTrip ? tr('Editor.CompositionDestination', 'destination') : 'C');
const source = computed(() => sourceSnapshot(props.workspace, props.review.side));
const captured = computed(() => find(source.value, props.choice.kind, props.choice.sourceId)!);
const current = computed(() => props.review.baseline);
const destinations = computed(() => contents(current.value, props.choice.kind));
const regions = computed(() => ordered(current.value, 'region').filter(r => props.choice.kind !== 'area' || r.name !== 'Unassigned Places'));
const previous = computed(() => props.review.mappings.filter(m => m.source === props.review.side && m.kind === props.choice.kind && m.sourceId === props.choice.sourceId));
const positionItems = computed(() => ordered(current.value, props.choice.kind, props.destinationParent || props.choice.regionId
  || (props.choice.kind === 'place' ? current.value.graph.regions.find(r => r.name === 'Unassigned Places')?.id : undefined))
  .filter(r => props.choice.kind !== 'region' || r.name !== 'Unassigned Places'));
const incompatibleAfter = computed(() => props.choice.position === 'after' && !positionItems.value.some(item => item.id === props.choice.afterId));
const canonical = computed(() => props.choice.kind === 'region' && captured.value.name === 'Unassigned Places');
const canRemove = computed(() => previous.value.some(m => m.destinationId === props.choice.destinationId && !['skip', 'destinationRemoved'].includes(m.decision)));
const destinationLabel = (item: Content, kind: Kind): string => {
  const parent = parentId(current.value, kind, item.id);
  return `${parent ? `${displayLabel(find(current.value, 'region', parent), 'region')} · ` : ''}${displayLabel(item, kind)}`;
};
/** Child decisions are explicit and retain source manual order. No implicit Segments enter this scope. */
function scope(event: Event): void {
  const input = event.target as HTMLInputElement;
  scopeError.value = '';
  if (input.checked) {
    try { foldChildren(props.review, props.choice, regionChildren(props.workspace, props.review, props.choice)); }
    catch (error) {
      scopeError.value = error instanceof Error ? error.message : tr('Editor.CompositionReviewTheConflictingChildChoices', 'Review the conflicting child choices.');
      input.checked = props.choice.withChildren;
      return;
    }
  }
  props.choice.withChildren = input.checked;
}
const anchorRole = (id: string): string => [captured.value.fromPlaceId === id ? tr('Editor.CompositionFrom', 'From') : '',
  ...[...captured.value.waypoints ?? []].sort((a, b) => a.position - b.position).filter(w => w.placeId === id).map(w => w.routeVertexIndex == null ? tr('Editor.RouteVia', 'Via {0}', number(w.position + 1)) : tr('Editor.CompositionVia0RouteVertex1', 'Via {0} · route vertex {1}', number(w.position + 1), number(w.routeVertexIndex))),
  captured.value.toPlaceId === id ? tr('Editor.CompositionTo', 'To') : ''].filter(Boolean).join(' / ');
const selectedCopy = (id: string): boolean => props.review.items.some(c => c.kind === 'place' && c.sourceId === id && c.decision === 'copy'
  || c.withChildren && c.children.some(child => child.kind === 'place' && child.sourceId === id && child.decision === 'copy'));
const excludedAnchor = (id: string): boolean => props.review.mappings.some(m => m.source === props.review.side && m.kind === 'place'
  && m.sourceId === id && ['skip', 'destinationRemoved'].includes(m.decision));
</script>

<template>
  <fieldset class="composition-choice">
    <legend>{{ tripKind(choice.kind) }} · {{ displayLabel(captured, choice.kind) }}</legend>
    <label v-if="previous.length > 1" class="composition-field">{{ tr('Editor.CompositionSavedCounterpartDecision', 'Saved counterpart decision') }}
      <select v-model="choice.mappingId" @change="choice.destinationId = choice.mappingId">
        <option v-for="m in previous" :key="m.destinationId" :value="m.destinationId">{{ find(current, choice.kind, m.destinationId) ? displayLabel(find(current, choice.kind, m.destinationId), choice.kind) : tr('Editor.CompositionRemovedCounterpart', 'Removed counterpart') }} · {{ compositionDecision(m.decision) }}</option>
      </select>
    </label>
    <label class="composition-field">{{ tr('Editor.CompositionActionFor0', 'Action for {0}', destinationName) }}
      <select v-model="choice.decision" :aria-label="tr('Editor.CompositionActionFor0', 'Action for {0}', destinationName)">
        <option v-if="choice.kind !== 'trip' && !canonical" value="copy">{{ tr('Editor.CompositionCopyKeepSeparate', 'Copy / keep separate') }}</option>
        <option value="reuse">{{ tr('Editor.CompositionReuseCurrentCounterpartKeepOrAdopt', 'Reuse current counterpart / Keep or adopt selected fields') }}</option>
        <option v-if="choice.kind !== 'trip' && !canRemove" value="skip">{{ tr('Editor.CompositionSkipThisSourceIdea', 'Skip this source idea') }}</option>
        <option v-if="canRemove && choice.kind !== 'trip'" value="remove">{{ tr('Editor.CompositionRemoveIncludedCounterpartFromC', 'Remove included counterpart from C') }}</option>
      </select>
    </label>
    <p v-if="canonical">{{ tr('Editor.CompositionUnassignedPlacesIsAReservedCategory', 'Unassigned Places is a reserved category. Select individual Places to copy them into {0}; it is never duplicated as a normal Region.', destinationName) }}</p>
    <label v-if="previous.some(m => ['skip', 'destinationRemoved'].includes(m.decision))" class="composition-check"><input v-model="choice.readd" type="checkbox" /> {{ tr('Editor.CompositionDeliberatelyReAddThisExcludedIdea', 'Deliberately re-add this excluded idea') }}</label>
    <template v-if="choice.decision === 'reuse' || choice.decision === 'remove'">
      <label class="composition-field">{{ tr('Editor.CompositionCurrent0Counterpart', 'Current {0} counterpart', destinationName) }}
        <select v-model="choice.destinationId" :aria-label="tr('Editor.CompositionCurrent0Counterpart', 'Current {0} counterpart', destinationName)" required>
          <option value="" disabled>{{ tr('Editor.CompositionSelectACurrentCounterpart', 'Select a current counterpart') }}</option>
          <option v-for="item in destinations" :key="item.id" :value="item.id">{{ destinationLabel(item, choice.kind) }}</option>
        </select>
      </label>
      <p>{{ tr('Editor.CompositionReuseKeepsThisIdentityItsRegion', 'Reuse keeps this identity, its Region and its position. Matched fields default Keep.') }}</p>
      <p v-if="choice.destinationId && !find(current, choice.kind, choice.destinationId)" role="status">{{ tr('Editor.CompositionTheSelectedCounterpartIsUnavailableIn', 'The selected counterpart is unavailable in these originals. Review current {0}, then choose a current counterpart. Your field choices remain retained.', destinationName) }}</p>
    </template>
    <template v-if="choice.decision === 'copy'">
      <label class="composition-check"><input v-model="choice.customName" type="checkbox" /> {{ tr('Editor.CompositionUseACustomName', 'Use a custom name') }}</label>
      <label v-if="choice.customName" class="composition-field">{{ tr('Editor.CompositionCustomName', 'Custom name') }}<input v-model="choice.name" maxlength="200" required /></label>
      <p v-else>{{ tr('Editor.CompositionOriginalName0', 'Original name: {0}', captured.name ?? captured.title ?? tr('Editor.CompositionUntitledSegmentDerivedRouteLabel', 'Untitled Segment · derived route label')) }}</p>
      <label class="composition-check"><input v-model="choice.separate" type="checkbox" /> {{ tr('Editor.CompositionKeepSeparateEvenIfTheSame', 'Keep separate even if the same name or another copy exists') }}</label>
      <p v-if="contents(current, choice.kind).some(x => label(x) === (choice.customName ? choice.name : label(captured)))" class="composition-warning">{{ tr('Editor.CompositionASameNameItemExistsIn', 'A same-name item exists in {0}. Review reuse or explicitly approve a separate copy.', destinationName) }}</p>
      <template v-if="!child && (choice.kind === 'place' || choice.kind === 'area')">
        <label class="composition-field">{{ tr('Editor.CompositionDestinationRegion', 'Destination Region') }}
          <select v-model="choice.regionId" :aria-label="tr('Editor.CompositionDestinationRegion', 'Destination Region')" :disabled="Boolean(choice.newRegionName)" :required="choice.kind === 'area' && !choice.newRegionName">
            <option value="">{{ choice.kind === 'place' ? tr('Editor.CompositionCanonicalUnassignedPlacesDefault', 'Canonical Unassigned Places (default)') : tr('Editor.CompositionSelectANormalRegion', 'Select a normal Region') }}</option>
            <option v-for="region in regions" :key="region.id" :value="region.id">{{ displayLabel(region, 'region') }}</option>
          </select>
        </label>
        <label v-if="choice.kind === 'area'" class="composition-field">{{ tr('Editor.CompositionOrCreateANormalRegionWith', 'Or create a normal Region with this copy') }}<input v-model="choice.newRegionName" maxlength="200" :disabled="Boolean(choice.regionId)" /></label>
      </template>
      <template v-if="!child || destinationParent">
        <p v-if="child">{{ tr('Editor.CompositionEffectiveDestinationRegion0NewChildren', 'Effective destination Region: {0}. New children follow this Region; reused children keep their current container and position.', destinationParentName || (destinationParent && find(current, 'region', destinationParent) ? displayLabel(find(current, 'region', destinationParent), 'region') : tr('Editor.CompositionSelectACurrentRegionCounterpart', 'Select a current Region counterpart'))) }}</p>
        <label class="composition-field">{{ choice.kind === 'segment' ? tr('Editor.CompositionTripLevelInsertionPosition', 'Trip-level insertion position') : tr('Editor.CompositionInsertionPositionIndependentOfRegion', 'Insertion position (independent of Region)') }}
          <select v-model="choice.position" :aria-label="tr('Editor.CompositionInsertionPosition', 'Insertion position')"><option value="append">{{ tr('Editor.CompositionAppend', 'Append') }}</option><option value="start">{{ tr('Editor.CompositionAtStart', 'At start') }}</option><option value="after" :disabled="Boolean(choice.newRegionName) || destinationParent === '__new__'">{{ tr('Editor.CompositionAfterSelection', 'After selection') }}</option></select>
        </label>
        <label v-if="choice.position === 'after'" class="composition-field">{{ tr('Editor.CompositionAfterThisCurrent01', 'After this current {0} {1}', destinationName, tripKind(choice.kind)) }}
          <select v-model="choice.afterId" required><option value="" disabled>{{ tr('Editor.CompositionSelectACompatibleItem', 'Select a compatible item') }}</option><option v-if="incompatibleAfter && choice.afterId" :value="choice.afterId" disabled>{{ tr('Editor.CompositionPreviousReference0OutsideThisList', 'Previous reference · {0} (outside this list)', displayLabel(find(current, choice.kind, choice.afterId), choice.kind)) }}</option><option v-for="item in positionItems" :key="item.id" :value="item.id">{{ displayLabel(item, choice.kind) }}</option></select>
        </label>
        <p v-if="incompatibleAfter" role="alert">{{ child
          ? tr('Editor.CompositionThePreviousAfterReferenceIsUnavailable', 'The previous After reference is unavailable in this destination list. Choose Append or At start, select a compatible reference, or turn off Region children to cancel folding. Your field choices are retained.')
          : tr('Editor.CompositionThePreviousAfterReferenceIsUnavailable2', 'The previous After reference is unavailable in this destination list. Choose Append or At start, select a compatible reference. Your field choices are retained.') }}</p>
        <p class="composition-result">{{ tr('Editor.CompositionResult01', 'Result: {0} · {1}', choice.kind === 'segment' ? tr('Editor.Composition0TripLevelSegments', '{0} · Trip-level Segments', destinationName) : choice.kind === 'region' ? tr('Editor.Composition0Regions', '{0} · Regions', destinationName) : destinationParentName || (choice.newRegionName ? tr('Editor.CompositionNewNormalRegion0CreatedFor', 'New normal Region “{0}” (created for this item)', choice.newRegionName) : (find(current, 'region', destinationParent || choice.regionId) ? displayLabel(find(current, 'region', destinationParent || choice.regionId), 'region') : tr('Editor.CompositionCanonicalUnassignedPlaces', 'Canonical Unassigned Places'))), choice.position === 'after' ? tr('Editor.CompositionAfter0', 'After {0}', find(current, choice.kind, choice.afterId) ? displayLabel(find(current, choice.kind, choice.afterId), choice.kind) : '…') : choice.position === 'start' ? tr('Editor.CompositionAtStart', 'At start') : tr('Editor.CompositionAppend', 'Append')) }}</p>
      </template>
    </template>
    <template v-if="choice.decision === 'remove' && choice.kind === 'region'">
      <label class="composition-field">{{ tr('Editor.CompositionDirectPlaces', 'Direct Places') }}<select v-model="choice.places"><option value="keep">{{ tr('Editor.CompositionKeepInTheReviewedFallback', 'Keep in the reviewed fallback') }}</option><option value="delete">{{ tr('Editor.CompositionDeleteWithRegion', 'Delete with Region') }}</option></select></label>
      <label class="composition-field">{{ tr('Editor.CompositionDirectAreas', 'Direct Areas') }}<select v-model="choice.areas"><option value="keep">{{ tr('Editor.CompositionKeepInTheReviewedNormalRegion', 'Keep in the reviewed normal Region') }}</option><option value="delete">{{ tr('Editor.CompositionDeleteWithRegion', 'Delete with Region') }}</option></select></label>
      <p>{{ tr('Editor.CompositionCurrentDeletionConsequencesAreShownBefore', 'Current deletion consequences are shown before applying. Region Notes, cover, centre and planning are removed.') }}</p>
    </template>
    <template v-if="choice.kind === 'region' && !canonical && ['copy', 'reuse'].includes(choice.decision)">
      <label class="composition-check"><input :checked="choice.withChildren" type="checkbox" @change="scope" /> {{ tr('Editor.CompositionRegionAndDirectPlacesAreasOtherwise', 'Region and direct Places/Areas (otherwise Region only)') }}</label>
      <p v-if="scopeError" role="alert">{{ scopeError }}</p>
      <p>{{ tr('Editor.CompositionNoSubRegionsOrAutomaticSegments', 'No sub-Regions or automatic Segments. Reused children keep their {0} container.', destinationName) }}</p>
      <ChoiceControls v-for="entry in choice.withChildren ? choice.children : []" :key="`${entry.kind}/${entry.sourceId}`" :workspace="workspace" :review="review" :choice="entry" :existing-trip="existingTrip" :destination-parent="choice.decision === 'copy' ? '__new__' : choice.destinationId" :destination-parent-name="choice.decision === 'copy' ? tr('Editor.CompositionNewNormalRegion0', 'New normal Region “{0}”', choice.customName ? choice.name.trim() : label(captured)) : undefined" :editor-url="editorUrl" :options="options" child />
    </template>
    <fieldset v-if="choice.kind === 'segment' && (choice.decision === 'copy' || choice.fields.route?.operation === 'useSource')">
      <legend>{{ tr('Editor.CompositionApproveCompleteFromOrderedViaTo', 'Approve complete From / ordered Via / To dependencies') }}</legend>
      <p>{{ tr('Editor.CompositionFrom0To1', 'From: {0} · To: {1}', captured.fromPlaceId ? displayLabel(find(source, 'place', captured.fromPlaceId)) : tr('Editor.CompositionNotSet', 'Not set'), captured.toPlaceId ? displayLabel(find(source, 'place', captured.toPlaceId)) : tr('Editor.CompositionNotSet', 'Not set')) }} · {{ tr('Editor.CompositionOrderedViaPoints0', 'Ordered Via points: {0}', number(captured.waypoints?.length ?? 0)) }}</p>
      <p v-if="!choice.anchors.length">{{ tr('Editor.CompositionThisHistoricalRouteHasNoPlace', 'This historical route has no Place anchors. Its saved geometry is shown when available.') }}</p>
      <fieldset v-for="anchor in choice.anchors" :key="anchor.sourceId">
        <legend>{{ anchorRole(anchor.sourceId) }} · {{ displayLabel(find(source, 'place', anchor.sourceId)) }}</legend>
        <p>{{ tr('Editor.CompositionSourceCoordinates0', 'Source coordinates: {0}', find(source, 'place', anchor.sourceId)?.location?.slice().reverse().map(value => number(value, { maximumFractionDigits: 9 })).join(' / ') ?? tr('Editor.CompositionUnlocated', 'Unlocated')) }}</p>
        <label class="composition-field">{{ tr('Editor.CompositionApprovedCurrent0Place', 'Approved current {0} Place', destinationName) }}<select v-model="anchor.destinationId" :aria-label="tr('Editor.CompositionApprovedCurrent0Place', 'Approved current {0} Place', destinationName)">
          <option value="">{{ tr('Editor.CompositionCopyASeparateSourceCoordinatePlace', 'Copy a separate source-coordinate Place') }}</option><option v-for="place in contents(current, 'place')" :key="place.id" :value="place.id">{{ destinationLabel(place, 'place') }} · {{ place.location?.slice().reverse().join(', ') ?? tr('Editor.CompositionUnlocated', 'Unlocated') }}</option>
        </select></label>
        <template v-if="!anchor.destinationId">
          <label v-if="selectedCopy(anchor.sourceId)" class="composition-check"><input v-model="anchor.useSelectedCopy" type="checkbox" /> {{ tr('Editor.CompositionUseThePlaceAlreadySelectedIn', 'Use the Place already selected in this block, in its reviewed container') }}</label>
          <label v-if="!anchor.useSelectedCopy" class="composition-field">{{ tr('Editor.CompositionMissingPlaceDestinationRegion', 'Missing Place destination Region') }}<select v-model="anchor.regionId"><option value="">{{ tr('Editor.CompositionCanonicalUnassignedPlaces', 'Canonical Unassigned Places') }}</option><option v-for="r in ordered(current, 'region')" :key="r.id" :value="r.id">{{ displayLabel(r, 'region') }}</option></select></label>
          <label class="composition-check"><input v-model="anchor.approveCopy" type="checkbox" /> {{ tr('Editor.CompositionApproveASeparatePlaceWithThe', 'Approve a separate Place with the captured location, appended here') }}{{ excludedAnchor(anchor.sourceId) ? (' · ' + tr('Editor.CompositionDeliberatelyReAddThisExcludedDependency', 'deliberately re-add this excluded dependency')) : '' }}</label>
        </template>
      </fieldset>
      <p>{{ tr('Editor.CompositionCloseSemanticMatchesCanStillBe', 'Close semantic matches can still be incompatible route anchors. No Places move, no captured geometry clears, and no route regenerates without ordinary review.') }}</p>
      <p>{{ tr('Editor.CompositionSharedApprovedDependenciesAndAPlace', 'Shared approved dependencies and a Place copied elsewhere in this block use one new Place when their reviewed destinations agree.') }}</p>
    </fieldset>
    <FieldChoices v-if="child && (choice.decision === 'copy' || choice.decision === 'reuse' && find(current, choice.kind, choice.destinationId))"
      :workspace="workspace" :review="review" :choice="choice" :editor-url="editorUrl || `/User/Trip/Edit/${workspace.tripId}`" :options="options ?? null" :existing-trip="existingTrip" />
  </fieldset>
</template>
