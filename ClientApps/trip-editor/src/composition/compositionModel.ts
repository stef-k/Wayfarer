import type { Action, ActionItem, Choice, Content, Kind, OrderOriginal, ReviewDraft, Side, Snapshot, Workspace } from './compositionTypes';

/** Clones plain wire data, including Vue proxies, before an asynchronous boundary. */
export const copy = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T;
export const label = (item: Content | undefined): string => item ? item.name ?? item.title ?? `${item.mode || 'Historical'} route` : 'Unavailable object';
export const sourceSnapshot = (w: Workspace, side: Side): Snapshot => side === 'a' ? w.sourceA : w.sourceB;
/** Historical missing/non-finite coordinates never become a synthetic marker or route vertex. */
export const isCoordinate = (value: unknown): value is number[] => Array.isArray(value) && value.length === 2
  && value.every(Number.isFinite) && Math.abs(value[0]) <= 180 && Math.abs(value[1]) <= 90;
/** Identity/type is authoritative even for a normal Region named Segments. */
export const contents = (snapshot: Snapshot, kind: Kind): Content[] => {
  switch (kind) {
    case 'trip': return [snapshot.graph];
    case 'region': return snapshot.graph.regions;
    case 'place': return snapshot.graph.regions.flatMap(r => r.places ?? []);
    case 'area': return snapshot.graph.regions.flatMap(r => r.areas ?? []);
    case 'segment': return snapshot.graph.segments;
  }
};
export const find = (s: Snapshot, kind: Kind, id: string): Content | undefined => contents(s, kind).find(x => x.id === id);
export const parentId = (s: Snapshot, kind: Kind, id: string): string | undefined =>
  s.graph.regions.find(r => (kind === 'place' ? r.places : r.areas)?.some(x => x.id === id))?.id;
/** Sequence vectors retain server tie/null ordering, independently of hierarchy or names. */
export const ordered = (s: Snapshot, kind: Kind, parent?: string): Content[] => {
  const ids = kind === 'region' ? s.regionOrder : kind === 'place' ? s.placeOrder[parent ?? ''] ?? []
    : kind === 'area' ? s.areaOrder[parent ?? ''] ?? [] : null;
  if (ids) return ids.map(id => find(s, kind, id)).filter((x): x is Content => Boolean(x));
  return [...contents(s, kind)].sort((a, b) => (a.displayOrder ?? 0) - (b.displayOrder ?? 0) || a.id.localeCompare(b.id));
};
export const orderOriginals = (s: Snapshot, kind: Kind, parent?: string): OrderOriginal[] => ordered(s, kind, parent)
  .filter(x => kind !== 'region' || x.name !== 'Unassigned Places').map(x => ({ id: x.id, displayOrder: x.displayOrder ?? null }));

/** Only authorized snapshots have this projection; callers never synthesize foreign absence. */
export const planningValue = (s: Snapshot, kind: Kind, id: string): unknown => {
  if (!s.planning) throw new Error('Planning is unavailable for this source.');
  return kind === 'trip' ? s.planning.trip ?? null : s.planning[`${kind}s`][id] ?? null;
};
/** Exact named originals match the domain owner, including complete route facts and explicit nulls. */
export const fieldValue = (s: Snapshot, kind: Kind, id: string, key: string): unknown => {
  const item = find(s, kind, id);
  if (!item) throw new Error('The selected current counterpart is unavailable. Review current C.');
  if (key === 'planning') return planningValue(s, kind, id);
  if (key === 'cover') return item.coverImageUrl ?? null;
  if (key === 'geometry') return item.geometryGeoJson ?? null;
  if (key === 'tags') return [...s.graph.tags ?? []].map(t => t.slug).sort();
  if (key === 'viewport') return { centerLat: s.graph.centerLat ?? null, centerLon: s.graph.centerLon ?? null, zoom: s.graph.zoom ?? null };
  if (key === 'address') return Object.fromEntries(['address', 'resolvedFeatureName', 'resolvedFeatureType', 'addressEnrichmentProvider',
    'addressEnrichmentStorageMode', 'addressEnrichedAt'].map(k => [k, item[k as keyof Content] ?? null]));
  if (key === 'route') return { ...Object.fromEntries(['fromPlaceId', 'toPlaceId', 'mode', 'transportProfileId', 'routeJson', 'estimatedDistanceKm',
    'estimatedDurationTicks', 'estimatedDurationSource', 'routeInstructionsJson', 'routeProvider', 'routeProviderConfigurationId',
    'routeProviderConfigurationVersion', 'routeTransportProfileId', 'routeGeneratedAt', 'routeAttribution', 'routeStorageMode', 'routeMappingMode']
    .map(k => [k, item[k as keyof Content] ?? null])),
    waypoints: [...item.waypoints ?? []].sort((a, b) => a.position - b.position).map(w => ({ ...w, routeVertexIndex: w.routeVertexIndex ?? null })) };
  return item[key as keyof Content] ?? null;
};

export const fieldKeys = (kind: Kind, own: boolean): string[] => {
  const keys: Record<Kind, string[]> = {
    trip: ['name', 'notes', 'tags', 'cover', 'viewport'], region: ['name', 'notes', 'center', 'cover'],
    place: ['name', 'notes', 'iconName', 'markerColor', 'address', 'location'], area: ['name', 'notes', 'fillHex', 'geometry'],
    segment: ['title', 'notes', 'route']
  };
  return [...keys[kind], ...(own ? ['planning'] : [])];
};

/** Fresh review never renews retained consent; the UI asks explicitly before replacing a draft baseline. */
export const newReview = (w: Workspace, side: Side): ReviewDraft => ({ side, items: [], baseline: copy(w.current), mappings: copy(w.mappings) });
/** Explicit review renews comparison evidence only; authored operations, names, values and destinations stay intact. */
export const reviewAgainstCurrent = (w: Workspace, draft: ReviewDraft): void => {
  draft.baseline = copy(w.current); draft.mappings = copy(w.mappings);
  const decisions = (items: Choice[]): void => {
    for (const item of items) {
      const matches = draft.mappings.filter(m => m.source === draft.side && m.kind === item.kind && m.sourceId === item.sourceId);
      if (!matches.some(m => m.destinationId === item.mappingId)) item.mappingId = matches[0]?.destinationId ?? '';
      decisions(item.children);
    }
  };
  decisions(draft.items);
};
export const newChoice = (w: Workspace, side: Side, kind: Kind, id: string): Choice => {
  const s = sourceSnapshot(w, side); const item = find(s, kind, id);
  if (!item) throw new Error('The captured source object is unavailable.');
  const mapping = w.mappings.find(m => m.source === side && m.kind === kind && m.sourceId === id);
  const active = mapping && !['skip', 'destinationRemoved'].includes(mapping.decision) && find(w.current, kind, mapping.destinationId);
  const choice: Choice = { kind, sourceId: id, decision: active || kind === 'trip' ? 'reuse' : 'copy',
    destinationId: active ? mapping!.destinationId : kind === 'trip' ? w.tripId : '', mappingId: mapping?.destinationId ?? '',
    customName: false, name: label(item), separate: false, readd: false, regionId: '', newRegionName: '', position: 'append', afterId: '',
    withChildren: false, children: [], anchors: [], fields: Object.fromEntries(fieldKeys(kind, Boolean(s.planning)).map(k => [k, { operation: 'keep', value: null }])),
    places: 'keep', areas: 'keep' };
  if (kind === 'segment') choice.anchors = anchorIds(item).map(sourceId => {
    const matched = w.mappings.find(m => m.source === side && m.kind === 'place' && m.sourceId === sourceId
      && !['skip', 'destinationRemoved'].includes(m.decision) && find(w.current, 'place', m.destinationId));
    return { sourceId, destinationId: matched?.destinationId ?? '', regionId: '', approveCopy: false };
  });
  return choice;
};
/** Travel order is preserved; shared endpoints/Via require one explicit approval per distinct identity. */
export const anchorIds = (item: Content): string[] => [...new Set([item.fromPlaceId, ...[...item.waypoints ?? []]
  .sort((a, b) => a.position - b.position).map(w => w.placeId), item.toPlaceId].filter((id): id is string => Boolean(id)))];

/** Freezes a complete selected intent without granting authority from any captured ID. */
export const buildAction = (w: Workspace, draft: ReviewDraft): Action => {
  const snapshot = sourceSnapshot(w, draft.side); const current = draft.baseline;
  const dependencyCopies: ActionItem[] = [];
  const copiedPlaces = new Map<string, string>();
  const effectiveRegion = (id: string): string => id || current.graph.regions.find(r => r.name === 'Unassigned Places')?.id || '';
  const build = (c: Choice, forcedParent?: string): ActionItem => {
    const item: ActionItem = { kind: c.kind, sourceId: c.sourceId, decision: c.decision };
    const previous = draft.mappings.find(m => m.source === draft.side && m.kind === c.kind && m.sourceId === c.sourceId && m.destinationId === c.mappingId);
    if (previous) item.expectedDecision = { destinationId: previous.destinationId, decision: previous.decision };
    if (c.readd) item.readd = true;
    if (c.decision === 'skip') return item;
    if (c.decision !== 'copy') {
      if (!c.destinationId || !find(current, c.kind, c.destinationId)) throw new Error('Select a current C counterpart.');
      item.destinationId = c.destinationId;
      if (c.kind === 'place' || c.kind === 'area') item.expectedParentId = parentId(current, c.kind, c.destinationId);
    }
    if (c.decision === 'remove') {
      if (c.kind === 'trip') throw new Error('Use normal Delete C.');
      if (c.kind === 'region') { item.places = c.places; item.areas = c.areas; }
      if (c.kind === 'area' || c.kind === 'segment') item.original = {
        content: c.kind === 'area' ? find(current, c.kind, c.destinationId) : fieldValue(current, c.kind, c.destinationId, 'route'),
        planning: planningValue(current, c.kind, c.destinationId), notes: fieldValue(current, c.kind, c.destinationId, 'notes'),
        title: c.kind === 'segment' ? fieldValue(current, c.kind, c.destinationId, 'title') : null
      };
      return item;
    }
    if (c.decision === 'copy') {
      if (c.kind === 'trip') throw new Error('Trip metadata is adopted into current C.');
      if (c.customName) { if (!c.name.trim()) throw new Error('Enter the custom name.'); item.name = c.name.trim(); }
      item.separate = c.separate;
      let parent = forcedParent ?? c.regionId;
      if (c.kind === 'place' || c.kind === 'area') {
        if (c.newRegionName && !forcedParent) {
          if (c.regionId) throw new Error('Choose a Region or create a normal Region.');
          item.newRegionName = c.newRegionName.trim(); item.expectedRegions = ordered(current, 'region').map(r => ({ id: r.id, displayOrder: r.displayOrder ?? null }));
          item.regionPlacement = { expected: orderOriginals(current, 'region'), position: 'append' }; parent = '__new__';
        } else if (parent && parent !== '__new__') {
          const region = find(current, 'region', parent);
          if (!region || c.kind === 'area' && region.name === 'Unassigned Places') throw new Error('Select a normal Region for an Area.');
          if (!forcedParent) item.regionId = parent;
        } else if (!parent && c.kind === 'area') throw new Error('Select or create a normal Region for the Area.');
        if (!parent && c.kind === 'place') {
          item.expectedRegions = ordered(current, 'region').map(r => ({ id: r.id, displayOrder: r.displayOrder ?? null }));
          parent = current.graph.regions.find(r => r.name === 'Unassigned Places')?.id ?? '__new__';
        }
      }
      const expected = parent === '__new__' ? [] : orderOriginals(current, c.kind, parent);
      // Index staged Place copies, including Region children, using their reviewed effective parent.
      // A newly created normal Region has no existing ID an anchor can approve as its destination.
      if (c.kind === 'place') copiedPlaces.set(c.sourceId, c.newRegionName && !forcedParent ? '__new__' : forcedParent ?? c.regionId);
      if (c.position === 'after' && !expected.some(x => x.id === c.afterId)) throw new Error('Select a compatible current C item for After.');
      item.placement = { expected, position: c.position, ...(c.position === 'after' ? { afterId: c.afterId } : {}) };
    }
    const fields: NonNullable<ActionItem['fields']> = {};
    for (const [key, field] of Object.entries(c.fields)) {
      if (field.operation === 'keep') continue;
      if (!fieldKeys(c.kind, Boolean(snapshot.planning)).includes(key)) throw new Error('This selected field is unavailable.');
      fields[key] = { operation: field.operation, expected: c.decision === 'reuse' ? fieldValue(current, c.kind, c.destinationId, key) : null,
        ...(field.operation === 'edit' ? { value: key === 'tags' ? String(field.value).split(',').map(t => t.trim()).filter(Boolean) : copy(field.value) } : {}) };
    }
    if (Object.keys(fields).length) item.fields = fields;
    if (c.kind === 'region' && c.withChildren) item.children = c.children.map(child => build(child, c.decision === 'copy' ? '__new__' : c.destinationId));
    if (c.kind === 'segment' && (c.decision === 'copy' || fields.route?.operation === 'useSource')) {
      item.anchors = c.anchors.map(a => {
        if (a.destinationId) {
          const place = find(current, 'place', a.destinationId);
          if (!place) throw new Error('Review the current route anchor.');
          return { sourceId: a.sourceId, destinationId: a.destinationId, regionId: parentId(current, 'place', a.destinationId) ?? null, location: place.location ?? null };
        }
        if (!a.approveCopy) throw new Error('Explicitly approve every missing From / To / Via Place.');
        if (copiedPlaces.has(a.sourceId)) {
          if (effectiveRegion(copiedPlaces.get(a.sourceId)!) !== effectiveRegion(a.regionId)) throw new Error('The selected Place and route dependency need the same reviewed destination Region.');
          return { sourceId: a.sourceId, destinationId: null, regionId: null, location: null };
        }
        const dep = newChoice({ ...w, current, mappings: draft.mappings }, draft.side, 'place', a.sourceId);
        dep.decision = 'copy'; dep.separate = true;
        dep.readd = draft.mappings.some(m => m.source === draft.side && m.kind === 'place' && m.sourceId === a.sourceId && ['skip', 'destinationRemoved'].includes(m.decision));
        dep.regionId = a.regionId;
        dependencyCopies.push(build(dep));
        return { sourceId: a.sourceId, destinationId: null, regionId: null, location: null };
      });
    }
    return item;
  };
  // Dependencies stage before Trip-level routes; relative order inside each destination list is unchanged.
  const items = [...draft.items.filter(c => c.kind !== 'segment'), ...draft.items.filter(c => c.kind === 'segment')].map(c => build(c));
  if (items.length > 1 && items.some(i => i.decision === 'remove')) throw new Error('Review removal as a separate action from a block.');
  return copy({ source: draft.side, items: [...dependencyCopies, ...items] });
};

/** Real spatial vertices only: all polygon rings, saved routes and available anchors, with no synthetic placeholders. */
export const spatialContent = (s: Snapshot, kind: Kind, id: string): number[][] => {
  const item = find(s, kind, id); if (!item) return [];
  const pair = (p: number[] | null | undefined): number[][] => isCoordinate(p) ? [p] : [];
  const geometry = (json: string | null | undefined, type: string): number[][] => {
    try { const g = JSON.parse(json ?? 'null'); if (g?.type !== type) return [];
      return (type === 'Polygon' ? g.coordinates.flat() : g.coordinates).flatMap(pair);
    } catch { return []; }
  };
  if (kind === 'trip') return s.regionOrder.flatMap(r => spatialContent(s, 'region', r)).concat(s.graph.segments.flatMap(r => spatialContent(s, 'segment', r.id)));
  if (kind === 'region') return pair(item.center).concat((s.placeOrder[id] ?? []).flatMap(p => spatialContent(s, 'place', p)),
    (s.areaOrder[id] ?? []).flatMap(a => spatialContent(s, 'area', a)));
  if (kind === 'place') return pair(item.location);
  if (kind === 'area') return geometry(item.geometryGeoJson, 'Polygon');
  return geometry(item.routeJson, 'LineString').concat(anchorIds(item).flatMap(p => pair(find(s, 'place', p)?.location)));
};
