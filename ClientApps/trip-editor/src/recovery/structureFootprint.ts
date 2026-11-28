import { isId, type StructureRoute, type Target } from './structureRoutes';

export type Footprint = { target: Target; effects: string[] };

/** Checks field, membership and material scopes without treating Trip freshness as an effect. */
export const footprintsOverlap = (left: Footprint, right: Footprint): boolean =>
  left.effects.some(first => right.effects.some(second => {
    const [family, target] = first.split('/');
    const [otherFamily, otherTarget] = second.split('/');
    return family === otherFamily && (target === otherTarget || target === '*' || otherTarget === '*');
  }));

const record = (value: unknown): Record<string, unknown> =>
  value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {};

const members = (rows: unknown, kind: string): string[] =>
  Array.isArray(rows) && rows.every(row => isId(record(row).id)) ? rows.map(row => `${kind}/${record(row).id}`) : [`${kind}/*`];

/** Builds compare and write overlap from frozen intent, using kind-wide scope where the preview is sampled. */
export function structureFootprint(route: StructureRoute, request: unknown): Footprint {
  const { kind, target } = route;
  const root = record(request);
  const expected = record(root.expected);
  const changes = record(root.changes);
  const place = record(expected.place);
  const parent = kind === 'place-save' && isId(place.regionId) ? place.regionId : expected.regionId;
  const leaf = `${target.type}/${target.id}`;
  const effects = new Set<string>();
  switch (kind) {
    case 'region-fields': effects.add(leaf); break;
    case 'region-create': effects.add('region-list/*'); break;
    case 'region-order':
      ['region-list/*', ...members(expected.regions, 'region'), ...members(expected.regions, 'planning-region')].forEach(key => effects.add(key));
      break;
    case 'region-delete':
      [leaf, 'region-list/*', 'place-list/*', 'area-list/*', 'place/*', 'area/*', 'segments/*',
        `planning-region/${target.id}`, 'planning-place/*', 'planning-area/*', 'planning-segment/*'].forEach(key => effects.add(key));
      break;
    case 'place-create': effects.add(`place-list/${target.id}`); break;
    case 'place-fields': effects.add(leaf); break;
    case 'place-save':
      effects.add(leaf);
      if (Object.hasOwn(changes, 'regionId')) {
        effects.add(`place-list/${isId(parent) ? parent : '*'}`);
        effects.add(`place-list/${isId(changes.regionId) ? changes.regionId : '*'}`);
        effects.add(`planning-place/${target.id}`);
      }
      if (Object.hasOwn(changes, 'location') || Object.hasOwn(changes, 'regionId')) effects.add('segments/*');
      break;
    case 'place-delete':
      [leaf, 'place-list/*', 'segments/*', `planning-place/${target.id}`,
        'planning-region/*', 'planning-segment/*'].forEach(key => effects.add(key));
      break;
    case 'place-order':
      [`place-list/${target.id}`, ...members(expected.places, 'place'), ...members(expected.places, 'planning-place')]
        .forEach(key => effects.add(key));
      break;
    case 'area-fields': case 'area-geometry': effects.add(leaf); break;
    case 'area-create': effects.add(`area-list/${target.id}`); break;
    case 'area-order':
      [`area-list/${target.id}`, ...members(expected.areas, 'area'), ...members(expected.areas, 'planning-area')]
        .forEach(key => effects.add(key));
      break;
    case 'area-delete':
      [leaf, 'area-list/*', `planning-area/${target.id}`].forEach(key => effects.add(key));
      break;
  }
  return { target, effects: [...effects] };
}

/** Assigns the exact Planning target or the Trip-wide Segment mutation footprint. */
export const dependentFootprint = (target: Target, planning: boolean): Footprint => ({
  target, effects: [planning ? `planning-${target.type}/${target.id}` : 'segments/*']
});
