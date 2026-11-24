<script setup lang="ts">
import { message as tr, number } from '../../../../wwwroot/js/localization/messages.js';
import { computed, ref, watch } from 'vue';
import RichNotesEditor from '../components/RichNotesEditor.vue';
import IconSelector from '../components/IconSelector.vue';
import { placeNotesPreviewHtml } from '../displayHelpers';
import { describePlanningTiming, type PlanningAggregate } from '../planning/planningTypes';
import { copy, fieldKeys, fieldValue, find, sourceSnapshot } from './compositionModel';
import type { Choice, FieldChoice, ReviewDraft, Workspace } from './compositionTypes';
import type { EditorOptions } from '../types';
const props = defineProps<{ workspace: Workspace; review: ReviewDraft; choice: Choice; editorUrl: string; options: EditorOptions | null; existingTrip?: boolean }>();
const adoption = ref(0);
const destinationName = computed(() => props.existingTrip ? tr('Editor.CompositionDestination', 'destination') : 'C');
// A different choice owns different Notes (item/block/reset); edits within that choice remain local echoes.
watch(() => props.choice, () => { adoption.value++; });
const source = computed(() => sourceSnapshot(props.workspace, props.review.side));
const keys = computed(() => fieldKeys(props.choice.kind, Boolean(source.value.planning)));
const titles: Record<string, string> = { name: tr('Editor.Name', 'Name'), title: tr('Editor.CompositionSegmentTitle', 'Segment title'), notes: tr('Editor.CompositionNotes', 'Notes'), iconName: tr('Editor.Icon', 'Icon'), markerColor: tr('Editor.CompositionMarkerColour', 'Marker colour'), fillHex: tr('Editor.CompositionAreaFill', 'Area fill'),
  address: tr('Editor.CompositionAddressEnrichment', 'Address & enrichment'), location: tr('Editor.CompositionLocation', 'Location'), geometry: tr('Editor.CompositionCompletePolygon', 'Complete polygon'), center: tr('Editor.CompositionRegionCentre', 'Region centre'), cover: tr('Editor.CompositionCover', 'Cover'), tags: tr('Editor.CompositionTripTags', 'Trip tags'), viewport: tr('Editor.CompositionTripViewport', 'Trip viewport'), route: tr('Editor.CompositionCompleteRoute', 'Complete route'), planning: tr('Editor.CompositionPersonalPlanning', 'Personal planning') };
const original = (key: string): unknown => props.choice.decision === 'reuse'
  ? fieldValue(props.review.baseline, props.choice.kind, props.choice.destinationId, key) : null;
const hasCurrent = computed(() => Boolean(find(props.workspace.current, props.choice.kind, props.choice.destinationId)));
const currentValue = (key: string): unknown => hasCurrent.value
  ? fieldValue(props.workspace.current, props.choice.kind, props.choice.destinationId, key) : null;
const changedSinceReview = (key: string): boolean => !hasCurrent.value || JSON.stringify(original(key)) !== JSON.stringify(currentValue(key));
const captured = (key: string): unknown => fieldValue(source.value, props.choice.kind, props.choice.sourceId, key);
const fieldChoice = (key: string): FieldChoice => props.choice.fields[key] ??= { operation: 'keep', value: null };
const notes = computed({ get: () => String(fieldChoice('notes').value ?? ''), set: value => { fieldChoice('notes').value = value; } });
/** An explicit edit adopts a value once; local RichNotes echoes never advance adoptionRevision. */
function select(key: string, operation: FieldChoice['operation']): void {
  const field = fieldChoice(key);
  if (operation === 'edit' && field.operation !== 'edit') {
    const value = field.operation === 'useSource' ? captured(key) : field.operation === 'clear' ? null
      : props.choice.decision === 'reuse' ? original(key) : captured(key);
    field.value = copy(value);
    if (key === 'notes') { field.value = value ?? ''; adoption.value++; }
    if (['name', 'title', 'cover', 'iconName', 'markerColor', 'fillHex'].includes(key)) field.value = value ?? '';
    if (key === 'tags') field.value = [...(props.choice.decision === 'reuse' && field.operation !== 'useSource'
      ? props.review.baseline.graph.tags : source.value.graph.tags) ?? []].map(t => t.name).join(', ');
    if (['location', 'center'].includes(key)) field.value ??= [null, null];
    if (key === 'viewport') field.value ??= { centerLat: null, centerLon: null, zoom: null };
    if (key === 'address') field.value = { address: (value as { address?: string } | null)?.address ?? '' };
  }
  field.operation = operation;
}
/** Uses the established hr/min labels, retaining fractional seconds from saved ticks without estimating. */
function savedDuration(ticks: unknown): string {
  if (typeof ticks !== 'number' || !Number.isFinite(ticks)) return tr('Editor.DurationUnavailable', 'Duration unavailable');
  if (ticks === 0) return tr('Editor.Composition0Min', '0 min');
  const hours = Math.floor(ticks / 36000000000);
  const minutes = Math.floor(ticks % 36000000000 / 600000000);
  const seconds = ticks % 600000000 / 10000000;
  return [hours ? tr('Editor.VisitDurationHours', '{0} hr', number(hours)) : '', minutes ? tr('Editor.VisitDurationMinutes', '{0} min', number(minutes)) : '', seconds ? tr('Editor.Composition0Sec', '{0} sec', number(seconds, { maximumFractionDigits: 7 })) : ''].filter(Boolean).join(' ');
}
/** Resolves built-in duration sources for display and preserves unfamiliar saved values. */
function durationSource(value: unknown): string {
  if (value === 'Manual') return tr('Editor.Manual', 'Manual');
  if (value === 'Automatic') return tr('Editor.Automatic', 'Automatic');
  return value ? String(value) : tr('Editor.CheckStatus.unknown', 'Unknown');
}
/** Complete saved facts stay readable; internal profile/configuration IDs are represented by presence and comparison labels. */
function describeRoute(value: unknown): string {
  const r = value as Record<string, unknown>;
  const mode = props.options?.transportModes.find(m => m.value === r.mode)?.label ?? r.mode ?? tr('Editor.CompositionNoMode', 'No mode');
  return `${mode} · ${r.routeJson ? tr('Editor.CompositionSavedCustomGeometry', 'Saved custom geometry') : tr('Editor.CompositionAnchorFallback', 'Anchor fallback')} · ${r.estimatedDistanceKm == null ? tr('Editor.DistanceUnavailable', 'Distance unavailable') : tr('Editor.EstimateDistance', '{0} km', number(Number(r.estimatedDistanceKm), { maximumFractionDigits: 9 }))} · ${savedDuration(r.estimatedDurationTicks)} · ${tr('Editor.Composition0Duration', '{0} duration', durationSource(r.estimatedDurationSource))}`;
}
/** Secondary retained evidence is available without crowding the measurement comparison. */
function routeDetails(value: unknown): string {
  const r = value as Record<string, unknown>;
  return [tr('Editor.CompositionTransportProfile0', 'Transport profile: {0}', r.transportProfileId ? tr('Editor.CompositionSavedProfile', 'Saved profile') : tr('Editor.CompositionNotSet', 'Not set')),
    tr('Editor.CompositionProvider0Settings1Version2', 'Provider: {0}; settings: {1}; version: {2}', r.routeProvider || tr('Editor.CompositionNotSet', 'Not set'), r.routeProviderConfigurationId ? tr('Editor.CompositionRecorded', 'Recorded') : tr('Editor.CompositionNotSet', 'Not set'), r.routeProviderConfigurationVersion ?? tr('Editor.CompositionNotSet', 'Not set')),
    tr('Editor.CompositionRoutedProfile0RoutingMode1', 'Routed profile: {0}; routing mode: {1}', r.routeTransportProfileId ? tr('Editor.CompositionRecorded', 'Recorded') : tr('Editor.CompositionNotSet', 'Not set'), r.routeMappingMode || tr('Editor.CompositionNotSet', 'Not set')),
    tr('Editor.CompositionGenerated0Storage1', 'Generated: {0}; storage: {1}', r.routeGeneratedAt || tr('Editor.CompositionNotSet', 'Not set'), r.routeStorageMode || tr('Editor.CompositionNotSet', 'Not set')),
    tr('Editor.CompositionInstructions0Attribution1', 'Instructions: {0}; Attribution: {1}', r.routeInstructionsJson ? tr('Editor.Saved', 'Saved') : tr('Editor.CompositionNotSet', 'Not set'), r.routeAttribution || tr('Editor.CompositionNotSet', 'Not set'))].join('. ');
}
/** Human descriptions never expose protocol objects or foreign planning diagnostics. */
function describe(key: string, value: unknown): string {
  if (key === 'planning') return value ? (value as PlanningAggregate).entries.map(e => `${e.label ? `${e.label}: ` : ''}${describePlanningTiming(e.when)}`).join('; ') : tr('Editor.CompositionNoPersonalPlanning', 'No personal planning');
  if (value === null || value === undefined || value === '') return tr('Editor.CompositionNotSet', 'Not set');
  if (key === 'location' || key === 'center') return tr('Editor.Composition0Latitude1Longitude', '{0}° latitude, {1}° longitude', ...[...(value as number[])].reverse().map(n => number(n, { maximumFractionDigits: 9 })));
  if (key === 'geometry') { try { const g = JSON.parse(String(value)); return tr('Editor.CompositionFull0OuterShellAnd1', 'Full {0}: outer shell and {1} hole(s). View on map.', g.type, Math.max(0, g.coordinates.length - 1)); } catch { return tr('Editor.CompositionHistoricalGeometryUnavailable', 'Historical geometry unavailable'); } }
  if (key === 'address') { const a = value as Record<string, string | null>; return [a.address, a.resolvedFeatureName, a.resolvedFeatureType, a.addressEnrichmentProvider].filter(Boolean).join(' · ') || tr('Editor.CompositionNotSet', 'Not set'); }
  if (key === 'route') return describeRoute(value);
  if (key === 'viewport') { const v = value as { centerLat: number | null; centerLon: number | null; zoom: number | null }; return tr('Editor.Composition01Zoom2', '{0}°, {1}° · Zoom {2}', v.centerLat ?? tr('Editor.Unset', 'Unset'), v.centerLon ?? tr('Editor.Unset', 'Unset'), v.zoom ?? tr('Editor.CompositionUnset', 'unset')); }
  return Array.isArray(value) ? value.join(', ') || tr('Editor.CompositionNone', 'None') : String(value);
}
const ordinary = (key: string): boolean => ['geometry', 'route', 'planning'].includes(key);
const canClear = (key: string): boolean => !['name', 'geometry', 'location'].includes(key);
const numericPair = (key: string): number[] => fieldChoice(key).value as number[];
const viewport = (): { centerLat: number | null; centerLon: number | null; zoom: number | null } => fieldChoice('viewport').value as ReturnType<typeof viewport>;
const markerColor = computed(() => {
  const field = fieldChoice('markerColor');
  if (field.operation === 'edit') return String(field.value || 'bg-blue');
  if (field.operation === 'clear') return 'bg-blue';
  return String((field.operation === 'useSource' || props.choice.decision === 'copy' ? captured('markerColor') : original('markerColor')) || 'bg-blue');
});
/** Compare retained facts exactly, separately from remapped Place identities and their mandatory dependency review. */
function routeComparison(value: unknown): string {
  if (!value) return tr('Editor.CompositionRouteUnavailableReviewFromViaTo', 'Route unavailable · Review From / Via / To dependencies');
  const route = value as Record<string, unknown>; const sourceRoute = captured('route') as Record<string, unknown>;
  const facts: Record<string, string> = {
    mode: tr('Editor.CompositionRouteDifferenceMode', 'mode'),
    routeJson: tr('Editor.CompositionSavedGeometry', 'saved geometry'),
    estimatedDistanceKm: tr('Editor.CompositionRouteDifferenceDistance', 'distance'),
    estimatedDurationTicks: tr('Editor.CompositionRouteDifferenceDuration', 'duration'),
    estimatedDurationSource: tr('Editor.CompositionDurationSource', 'duration source'),
    transportProfileId: tr('Editor.CompositionTransportProfile', 'transport profile'),
    routeInstructionsJson: tr('Editor.CompositionRouteDifferenceInstructions', 'instructions'),
    routeProvider: tr('Editor.CompositionRouteDifferenceProvider', 'provider'),
    routeProviderConfigurationId: tr('Editor.CompositionProviderSettings', 'provider settings'),
    routeProviderConfigurationVersion: tr('Editor.CompositionProviderVersion', 'provider version'),
    routeTransportProfileId: tr('Editor.CompositionRoutedProfile', 'routed profile'),
    routeGeneratedAt: tr('Editor.CompositionGenerationTime', 'generation time'),
    routeAttribution: tr('Editor.CompositionRouteDifferenceAttribution', 'attribution'),
    routeStorageMode: tr('Editor.CompositionRouteDifferenceStorage', 'storage'),
    routeMappingMode: tr('Editor.CompositionRoutingMode', 'routing mode')
  };
  const differences = Object.entries(facts).filter(([key]) => JSON.stringify(route[key]) !== JSON.stringify(sourceRoute[key])).map(([, title]) => title);
  return [(differences.length ? tr('Editor.CompositionDifferentSavedRouteFacts0', 'Different saved route facts: {0}', differences.join(', ')) : tr('Editor.CompositionSameSavedRouteFacts', 'Same saved route facts')), tr('Editor.CompositionReviewFromViaToDependencies', 'Review From / Via / To dependencies')].join(' · ');
}
/** Fresh planning entry IDs alone do not make the intentions different. */
function comparison(key: string, value: unknown): string {
  if (key === 'route') return routeComparison(value);
  const intentions = (value: unknown) => key === 'planning' && value
    ? (value as PlanningAggregate).entries.map(({ id: _id, ...entry }) => entry) : value;
  return JSON.stringify(intentions(value)) === JSON.stringify(intentions(captured(key))) ? tr('Editor.CompositionSameValue', 'Same value') : tr('Editor.CompositionDifferentValue', 'Different value');
}
</script>

<template>
  <details class="composition-fields" open>
    <summary>{{ tr('Editor.CompositionSelectedFieldsUnselectedFieldsKeep', 'Selected fields · unselected fields Keep') }}</summary>
    <p>{{ tr('Editor.CompositionNamesNotesPlanningAndRouteIntent', 'Names, Notes, planning and route intent are independent. Importing this object does not adopt unrelated Trip details.') }}</p>
    <fieldset v-for="key in keys.filter(k => choice.kind === 'trip' || choice.decision === 'reuse' || k !== 'name' && k !== 'title')" :key="key">
      <legend>{{ titles[key] }}</legend>
      <div class="composition-differences">
        <div><strong>{{ tr('Editor.CompositionSource0', 'Source {0}', review.side.toUpperCase()) }}</strong>
          <div v-if="key === 'notes'" class="composition-note" v-html="placeNotesPreviewHtml(String(captured(key) ?? ''), Number.MAX_SAFE_INTEGER)"></div>
          <p v-else>{{ describe(key, captured(key)) }}</p>
          <details v-if="key === 'route'"><summary>{{ tr('Editor.CompositionSavedProfileProvenance', 'Saved profile & provenance') }}</summary><p>{{ routeDetails(captured(key)) }}</p></details>
        </div>
        <div v-if="choice.decision === 'reuse' && choice.destinationId"><strong>{{ tr('Editor.CompositionReviewedOriginal01', 'Reviewed original {0} · {1}', destinationName, comparison(key, original(key))) }}</strong>
          <div v-if="key === 'notes'" class="composition-note" v-html="placeNotesPreviewHtml(String(original(key) ?? ''), Number.MAX_SAFE_INTEGER)"></div>
          <p v-else>{{ describe(key, original(key)) }}</p>
          <details v-if="key === 'route' && original(key)"><summary>{{ tr('Editor.CompositionSavedProfileProvenance', 'Saved profile & provenance') }}</summary><p>{{ routeDetails(original(key)) }}</p></details>
        </div>
        <div v-if="choice.decision === 'reuse' && choice.destinationId"><strong>{{ tr('Editor.CompositionCurrent01', 'Current {0} · {1}', destinationName, hasCurrent ? comparison(key, currentValue(key)) : tr('Editor.CompositionCounterpartUnavailable', 'Counterpart unavailable')) }}</strong>
          <template v-if="hasCurrent">
            <div v-if="key === 'notes'" class="composition-note" v-html="placeNotesPreviewHtml(String(currentValue(key) ?? ''), Number.MAX_SAFE_INTEGER)"></div>
            <p v-else>{{ describe(key, currentValue(key)) }}</p>
            <details v-if="key === 'route'"><summary>{{ tr('Editor.CompositionSavedProfileProvenance', 'Saved profile & provenance') }}</summary><p>{{ routeDetails(currentValue(key)) }}</p></details>
          </template>
          <p v-if="changedSinceReview(key)">{{ tr('Editor.CompositionCurrent0ChangedSinceThisReview', 'Current {0} changed since this review. Your choices and reviewed originals are retained. Use the refresh / review control to review the change explicitly.', destinationName) }}</p>
        </div>
      </div>
      <label class="composition-field">{{ tr('Editor.Composition0Action', '{0} action', titles[key]) }}
        <select :aria-label="tr('Editor.Composition0Action', '{0} action', titles[key])" :value="choice.fields[key]?.operation ?? 'keep'" @change="select(key, ($event.target as HTMLSelectElement).value as FieldChoice['operation'])">
          <option value="keep">{{ tr('Editor.CompositionKeep', 'Keep') }}</option><option value="useSource" :disabled="key === 'location' && captured(key) === null">{{ tr('Editor.CompositionUseSource', 'Use source') }}</option>
          <option v-if="!ordinary(key)" value="edit">{{ tr('Editor.CompositionEditValue', 'Edit value') }}</option><option v-if="canClear(key) && key !== 'route'" value="clear">{{ tr('Editor.CompositionClear', 'Clear') }}</option>
        </select>
      </label>
      <template v-if="choice.fields[key]?.operation === 'edit'">
        <RichNotesEditor v-if="key === 'notes'" :editor-id="`composition-notes-${choice.sourceId}`" :label="tr('Editor.CompositionEditedNotesFor0', 'Edited Notes for {0}', destinationName)" v-model="notes" :adoption-revision="adoption" :validation-messages="[]" />
        <IconSelector v-else-if="key === 'iconName' || key === 'markerColor'" :icons="key === 'iconName' ? options?.iconNames ?? [] : options?.markerColorClasses ?? []"
          :kind="key === 'iconName' ? 'icon' : 'color'" :marker-color="markerColor" :model-value="String(choice.fields[key].value)" @update:model-value="choice.fields[key].value = $event" />
        <div v-else-if="key === 'location' || key === 'center'" class="composition-differences">
          <label class="composition-field">{{ tr('Editor.Latitude', 'Latitude') }}<input v-model.number="numericPair(key)[1]" type="number" min="-90" max="90" step="any" required /></label>
          <label class="composition-field">{{ tr('Editor.Longitude', 'Longitude') }}<input v-model.number="numericPair(key)[0]" type="number" min="-180" max="180" step="any" required /></label>
        </div>
        <label v-else-if="key === 'address'" class="composition-field">{{ tr('Editor.CompositionAuthoredAddressClearsDetectedProviderEvidence', 'Authored address (clears detected provider evidence)') }}<textarea v-model="(choice.fields[key].value as { address: string }).address"></textarea></label>
        <div v-else-if="key === 'viewport'" class="composition-differences">
          <label>{{ tr('Editor.Latitude', 'Latitude') }}<input v-model.number="viewport().centerLat" type="number" min="-90" max="90" step="any" /></label>
          <label>{{ tr('Editor.Longitude', 'Longitude') }}<input v-model.number="viewport().centerLon" type="number" min="-180" max="180" step="any" /></label>
          <label>{{ tr('Editor.Zoom', 'Zoom') }}<input v-model.number="viewport().zoom" type="number" min="0" max="22" step="1" /></label>
        </div>
        <label v-else class="composition-field">{{ key === 'tags' ? tr('Editor.CompositionTagNamesSeparatedByCommas', 'Tag names, separated by commas') : tr('Editor.CompositionEdited0', 'Edited {0}', titles[key]) }}
          <input v-model="choice.fields[key].value" :type="key === 'cover' ? 'url' : key === 'fillHex' ? 'color' : 'text'" :maxlength="key === 'cover' ? 2000 : key === 'tags' ? undefined : 200" :required="key === 'name'" />
        </label>
      </template>
      <a v-if="ordinary(key) && choice.decision === 'reuse'" :href="editorUrl">{{ tr('Editor.CompositionEdit0InOrdinary1Editor', 'Edit {0} in ordinary {1} editor, then return', titles[key].toLowerCase(), destinationName) }}</a>
      <p v-if="key === 'location' && captured(key) === null">{{ tr('Editor.CompositionTheCapturedPlaceIsUnlocatedAn', 'The captured Place is unlocated. An independent copy retains that absence; adopting it cannot silently erase the {0}’s required location.', destinationName) }}</p>
      <p v-if="key === 'route'">{{ tr('Editor.CompositionSourceRoutesRequireEveryAnchorBelow', 'Source routes require every anchor below. To clear or repair an incompatible route, use ordinary {0} route controls and their consequence review, then review again. Captured geometry is never snapped, cleared or regenerated by copying.', destinationName) }}</p>
    </fieldset>
  </details>
</template>
