<script setup lang="ts">
import { computed, ref } from 'vue';
import RichNotesEditor from '../components/RichNotesEditor.vue';
import IconSelector from '../components/IconSelector.vue';
import { placeNotesPreviewHtml } from '../displayHelpers';
import { describePlanningTiming, type PlanningAggregate } from '../planning/planningTypes';
import { copy, fieldKeys, fieldValue, sourceSnapshot } from './compositionModel';
import type { Choice, FieldChoice, ReviewDraft, Workspace } from './compositionTypes';
import type { EditorOptions } from '../types';
const props = defineProps<{ workspace: Workspace; review: ReviewDraft; choice: Choice; editorUrl: string; options: EditorOptions | null }>();
const adoption = ref(0);
const source = computed(() => sourceSnapshot(props.workspace, props.review.side));
const keys = computed(() => fieldKeys(props.choice.kind, Boolean(source.value.planning)));
const titles: Record<string, string> = { name: 'Name', title: 'Segment title', notes: 'Notes', iconName: 'Icon', markerColor: 'Marker colour', fillHex: 'Area fill',
  address: 'Address & enrichment', location: 'Location', geometry: 'Complete polygon', center: 'Region centre', cover: 'Cover', tags: 'Trip tags', viewport: 'Trip viewport', route: 'Complete route', planning: 'Personal planning' };
const original = (key: string): unknown => props.choice.decision === 'reuse'
  ? fieldValue(props.review.baseline, props.choice.kind, props.choice.destinationId, key) : null;
const captured = (key: string): unknown => fieldValue(source.value, props.choice.kind, props.choice.sourceId, key);
const fieldChoice = (key: string): FieldChoice => props.choice.fields[key] ??= { operation: 'keep', value: null };
const notes = computed({ get: () => String(fieldChoice('notes').value ?? ''), set: value => { fieldChoice('notes').value = value; } });
/** An explicit edit adopts a value once; local RichNotes echoes never advance adoptionRevision. */
function select(key: string, operation: FieldChoice['operation']): void {
  const field = fieldChoice(key);
  if (operation === 'edit' && field.operation !== 'edit') {
    const value = props.choice.decision === 'reuse' ? original(key) : captured(key);
    field.value = copy(value);
    if (key === 'notes') { field.value = value ?? ''; adoption.value++; }
    if (['name', 'title', 'cover', 'iconName', 'markerColor', 'fillHex'].includes(key)) field.value = value ?? '';
    if (key === 'tags') field.value = [...source.value.graph.tags ?? []].map(t => t.name).join(', ');
    if (['location', 'center'].includes(key)) field.value ??= [null, null];
    if (key === 'address') field.value = { address: (value as { address?: string } | null)?.address ?? '' };
  }
  field.operation = operation;
}
/** Human descriptions never expose protocol objects or foreign planning diagnostics. */
function describe(key: string, value: unknown): string {
  if (key === 'planning') return value ? (value as PlanningAggregate).entries.map(e => `${e.label ? `${e.label}: ` : ''}${describePlanningTiming(e.when)}`).join('; ') : 'No personal planning';
  if (value === null || value === undefined || value === '') return 'Not set';
  if (key === 'location' || key === 'center') return `${(value as number[])[1]}° latitude, ${(value as number[])[0]}° longitude`;
  if (key === 'geometry') { try { const g = JSON.parse(String(value)); return `Full ${g.type}: outer shell and ${Math.max(0, g.coordinates.length - 1)} hole(s). View on map.`; } catch { return 'Historical geometry unavailable'; } }
  if (key === 'address') { const a = value as Record<string, string | null>; return [a.address, a.resolvedFeatureName, a.resolvedFeatureType, a.addressEnrichmentProvider].filter(Boolean).join(' · ') || 'Not set'; }
  if (key === 'route') { const r = value as Record<string, unknown>; return `${r.mode || 'No mode'} · ${r.routeJson ? 'Saved custom geometry' : 'Anchor fallback'} · ${r.estimatedDistanceKm ?? 'Unknown'} km · ${r.estimatedDurationSource || 'Unknown'} duration. Review all From / Via / To dependencies below.`; }
  if (key === 'viewport') { const v = value as { centerLat: number | null; centerLon: number | null; zoom: number | null }; return `${v.centerLat ?? 'Unset'}°, ${v.centerLon ?? 'Unset'}° · Zoom ${v.zoom ?? 'unset'}`; }
  return Array.isArray(value) ? value.join(', ') || 'None' : String(value);
}
const ordinary = (key: string): boolean => ['geometry', 'route', 'planning'].includes(key);
const canClear = (key: string): boolean => !['name', 'geometry', 'location'].includes(key);
const numericPair = (key: string): number[] => fieldChoice(key).value as number[];
const viewport = (): { centerLat: number | null; centerLon: number | null; zoom: number | null } => fieldChoice('viewport').value as ReturnType<typeof viewport>;
</script>

<template>
  <details class="composition-fields" open>
    <summary>Selected fields · unselected fields Keep</summary>
    <p>Names, Notes, planning and route intent are independent. Importing this object does not adopt unrelated Trip details.</p>
    <fieldset v-for="key in keys.filter(k => choice.kind === 'trip' || choice.decision === 'reuse' || k !== 'name' && k !== 'title')" :key="key">
      <legend>{{ titles[key] }}</legend>
      <div class="composition-differences">
        <div><strong>Source {{ review.side.toUpperCase() }}</strong>
          <div v-if="key === 'notes'" class="composition-note" v-html="placeNotesPreviewHtml(String(captured(key) ?? ''), 4000)"></div>
          <p v-else>{{ describe(key, captured(key)) }}</p>
        </div>
        <div v-if="choice.decision === 'reuse' && choice.destinationId"><strong>Current C · {{ JSON.stringify(original(key)) === JSON.stringify(captured(key)) ? 'Same value' : 'Different value' }}</strong>
          <div v-if="key === 'notes'" class="composition-note" v-html="placeNotesPreviewHtml(String(original(key) ?? ''), 4000)"></div>
          <p v-else>{{ describe(key, original(key)) }}</p>
        </div>
      </div>
      <label class="composition-field">{{ titles[key] }} action
        <select :value="choice.fields[key]?.operation ?? 'keep'" @change="select(key, ($event.target as HTMLSelectElement).value as FieldChoice['operation'])">
          <option value="keep">Keep</option><option value="useSource">Use source</option>
          <option v-if="!ordinary(key)" value="edit">Edit value</option><option v-if="canClear(key) && key !== 'route'" value="clear">Clear</option>
        </select>
      </label>
      <template v-if="choice.fields[key]?.operation === 'edit'">
        <RichNotesEditor v-if="key === 'notes'" :editor-id="`composition-notes-${choice.sourceId}`" label="Edited Notes for C" v-model="notes" :adoption-revision="adoption" :validation-messages="[]" />
        <IconSelector v-else-if="key === 'iconName' || key === 'markerColor'" :icons="key === 'iconName' ? options?.iconNames ?? [] : options?.markerColorClasses ?? []"
          :kind="key === 'iconName' ? 'icon' : 'color'" :marker-color="String(choice.fields.markerColor?.value || 'bg-green')" :model-value="String(choice.fields[key].value)" @update:model-value="choice.fields[key].value = $event" />
        <div v-else-if="key === 'location' || key === 'center'" class="composition-differences">
          <label class="composition-field">Latitude<input v-model.number="numericPair(key)[1]" type="number" min="-90" max="90" step="any" required /></label>
          <label class="composition-field">Longitude<input v-model.number="numericPair(key)[0]" type="number" min="-180" max="180" step="any" required /></label>
        </div>
        <label v-else-if="key === 'address'" class="composition-field">Authored address (clears detected provider evidence)<textarea v-model="(choice.fields[key].value as { address: string }).address"></textarea></label>
        <div v-else-if="key === 'viewport'" class="composition-differences">
          <label>Latitude<input v-model.number="viewport().centerLat" type="number" min="-90" max="90" step="any" /></label>
          <label>Longitude<input v-model.number="viewport().centerLon" type="number" min="-180" max="180" step="any" /></label>
          <label>Zoom<input v-model.number="viewport().zoom" type="number" min="0" max="22" step="1" /></label>
        </div>
        <label v-else class="composition-field">{{ key === 'tags' ? 'Tag names, separated by commas' : `Edited ${titles[key]}` }}
          <input v-model="choice.fields[key].value" :type="key === 'cover' ? 'url' : key === 'fillHex' ? 'color' : 'text'" :maxlength="key === 'cover' ? 2000 : 200" :required="key === 'name'" />
        </label>
      </template>
      <a v-if="ordinary(key) && choice.decision === 'reuse'" :href="editorUrl">Edit {{ titles[key].toLowerCase() }} in ordinary C editor, then return</a>
      <p v-if="key === 'route'">Source routes require every anchor below. To clear or repair an incompatible route, use ordinary C route controls and their consequence review, then review again. Captured geometry is never snapped, cleared or regenerated by copying.</p>
    </fieldset>
  </details>
</template>
