import { expect, test, type Page } from '@playwright/test';
import { absoluteUrl, editorApiPath, editorPath, expectMountedWorkspace, loadEditorStateFixture, pathRegex, signIn } from './tripEditorTestUtils';

const firstSegmentId = '00000000-0000-0000-0000-000000389001';
const loopSegmentId = '00000000-0000-0000-0000-000000389002';

/** Covers the bounded #389 active/inactive, marker-preservation, and draft-recalculation workflow. */
test('keeps independent Segment presentations synchronized without replacing Place markers', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await signIn(page);
  const state = await loadEditorStateFixture(page) as Record<string, any>;
  const places = ensureThreePlaces(state);
  prepareCanonicalSegments(state, places);
  await page.route(pathRegex(editorApiPath), route => {
    if (route.request().method() !== 'GET') throw new Error(`Unexpected mutation: ${route.request().method()}`);
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
  });
  await page.goto(absoluteUrl(editorPath));
  await expectMountedWorkspace(page);

  const markerState = await canonicalMarkerState(page, places.map(place => place.id));
  expect(markerState).toHaveLength(3);
  expect(markerState[0].visitText).toBe('✓');

  const firstRow = page.locator(`[data-segment-id="${firstSegmentId}"]`);
  const loopRow = page.locator(`[data-segment-id="${loopSegmentId}"]`);
  await expect(firstRow.locator('.trip-editor-list-button')).toContainText('A Alpha → B Bravo → C Charlie');
  await expect(loopRow.locator('.trip-editor-list-button')).toContainText('A Alpha → B Bravo → C Alpha');

  await firstRow.locator('.trip-editor-list-button').focus();
  await page.keyboard.press('Enter');
  let snapshot = await presentationSnapshot(page);
  expect(snapshot.segments).toHaveLength(2);
  expect(snapshot.segments.map((segment: any) => segment.anchors.map((anchor: any) => anchor.label))).toEqual([['A', 'B', 'C'], ['A', 'B', 'C']]);
  expect(snapshot.segments.find((segment: any) => segment.id === firstSegmentId)).toMatchObject({ active: true, source: 'D', lineCount: 1, hitLayerCount: 1 });
  expect(snapshot.segments.find((segment: any) => segment.id === firstSegmentId).chevronCount).toBeGreaterThan(0);
  expect(snapshot.routeBadgeCount).toBe(3);

  await loopRow.locator('.trip-editor-list-button').click();
  snapshot = await presentationSnapshot(page);
  expect(snapshot.segments.find((segment: any) => segment.id === firstSegmentId).active).toBe(false);
  expect(snapshot.segments.find((segment: any) => segment.id === loopSegmentId).active).toBe(true);
  expect(snapshot.segments.find((segment: any) => segment.id === loopSegmentId).chevronCount).toBeGreaterThan(0);
  expect(snapshot.routeBadgeCount).toBe(2);
  await expect(page.locator('.segment-route-badge')).toHaveText(['A/C', 'B']);
  expect(await canonicalMarkerState(page, places.map(place => place.id))).toEqual(markerState);

  await loopRow.getByRole('button', { name: 'Hide segment' }).click();
  snapshot = await presentationSnapshot(page);
  expect(snapshot.segments).toHaveLength(1);
  expect(snapshot.routeBadgeCount).toBe(0);
  await loopRow.getByRole('button', { name: 'Show segment' }).click();
  snapshot = await presentationSnapshot(page);
  expect(snapshot.segments).toHaveLength(2);
  expect(snapshot.segments.every((segment: any) => !segment.active)).toBe(true);

  await firstRow.locator('.trip-editor-list-button').click();
  await page.locator('#trip-editor-segment-form').getByLabel('To place').selectOption(places[0].id);
  await expect(firstRow.locator('.trip-editor-list-button')).toContainText('A Alpha → B Bravo → C Alpha');
  snapshot = await presentationSnapshot(page);
  expect(snapshot.routeBadgeCount).toBe(2);
  await expect(page.locator('.segment-route-badge')).toHaveText(['A/C', 'B']);

  await page.evaluate(() => { document.documentElement.style.zoom = '200%'; });
  const containment = await page.locator('.trip-editor-segment-row').evaluateAll(elements =>
    elements.map(element => ({ className: element.className, contained: element.scrollWidth <= element.clientWidth })));
  expect(containment.every(item => item.contained), JSON.stringify(containment)).toBe(true);
  expect(await page.locator('.segment-route-badge').evaluateAll(elements =>
    elements.every(element => getComputedStyle(element).pointerEvents === 'none'))).toBe(true);
});

/** Returns the issue-approved serializable map registry snapshot. */
async function presentationSnapshot(page: Page): Promise<any> {
  return await expect.poll(() => page.evaluate(() => (window as any).__segmentPresentationSnapshot)).not.toBeNull()
    .then(() => page.evaluate(() => (window as any).__segmentPresentationSnapshot));
}

/** Preserves and observes the canonical marker image, icon/color URL, and visit channel. */
async function canonicalMarkerState(page: Page, ids: string[]): Promise<any[]> {
  return await page.evaluate(placeIds => placeIds.map(id => {
    const image = document.querySelector<HTMLImageElement>(`[data-place-marker-icon="${id}"]`);
    return { id, count: document.querySelectorAll(`[data-place-marker-icon="${id}"]`).length, src: image?.getAttribute('src'),
      visitText: image?.parentElement?.querySelector('.trip-editor-map-marker__badge')?.textContent?.trim() ?? null };
  }), ids);
}

/** Reuses persisted fixture Places while making route geometry deterministic and long enough for cues. */
function ensureThreePlaces(state: Record<string, any>): any[] {
  const region = Object.values(state.regionsById).find((candidate: any) => !candidate.isShadow) as any;
  const places = (Object.values(state.placesById) as any[]).filter(place => place.regionId === region?.id);
  if (places.length < 2) throw new Error('The configured fixture needs two canonical Places.');
  while (places.length < 3) {
    const source = places[0];
    const clone = { ...structuredClone(source), id: '00000000-0000-0000-0000-000000389099', name: 'Charlie' };
    state.placesById[clone.id] = clone;
    state.placeOrderByRegionId[clone.regionId] = [...(state.placeOrderByRegionId[clone.regionId] ?? []), clone.id];
    places.push(clone);
  }
  ['Alpha', 'Bravo', 'Charlie'].forEach((name, index) => Object.assign(places[index], {
    name, location: { longitude: index * 10, latitude: 0 },
    visitSummary: { placeId: places[index].id, isVisited: index === 0, visitCount: index === 0 ? 1 : 0 }
  }));
  return places.slice(0, 3);
}

/** Creates one A-B-C route and one A-B-A loop without persisting the browser fixture. */
function prepareCanonicalSegments(state: Record<string, any>, places: any[]): void {
  state.permissions.canEditSegments = true;
  const segment = (id: string, endId: string, order: number) => ({
    id, tripId: state.tripId, fromPlaceId: places[0].id, toPlaceId: endId,
    waypointPlaceIds: [places[1].id], waypointRouteVertexIndices: [null], mode: 'walk', transportProfileId: null,
    hasCustomRoute: false, estimatedDistanceKm: 10, estimatedDurationMinutes: 60, estimatedDurationSource: 'Manual', notesHtml: '',
    route: null, effectiveRoute: { type: 'LineString', coordinates: [[0, 0], [10, 0], endId === places[0].id ? [0, 0] : [20, 0]] },
    aggregateConcurrencyToken: `opaque-${id}`, displayOrder: order,
    capabilities: { canEdit: true, canRename: false, canDelete: true, canReorder: true, canMove: false, canAddChildren: false, canTargetForSearchAdd: false }
  });
  state.segmentsById = { [firstSegmentId]: segment(firstSegmentId, places[2].id, 1), [loopSegmentId]: segment(loopSegmentId, places[0].id, 2) };
  state.segmentOrder = [firstSegmentId, loopSegmentId];
}
