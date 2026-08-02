import { expect, test, type Locator, type Page } from '@playwright/test';
import {
  absoluteUrl,
  editorApiPath,
  editorPath,
  expectMountedWorkspace,
  loadEditorStateFixture,
  regionCard,
  signIn,
  uniqueName
} from './tripEditorTestUtils';

type EditorState = Record<string, any>;

// Uses uniquely named disposable records so real endpoint reorder proofs avoid mutating stable fixture order.
test.describe.serial('Trip Editor real endpoint CRUD persistence contracts', () => {
  test.describe.configure({ timeout: 60_000 });

  test('real endpoint region create reorder delete persists in the visible sidebar and API reread', async ({ page }) => {
    await openEditor(page);
    const firstName = uniqueName('PW real region A');
    const secondName = uniqueName('PW real region B');

    try {
      await createRegion(page, firstName);
      await createRegion(page, secondName);
      await expect(regionCard(page, firstName)).toBeVisible();
      await expect(regionCard(page, secondName)).toBeVisible();
      await expectState(page, state => {
        expect(regionByName(state, firstName)).toBeTruthy();
        expect(regionByName(state, secondName)).toBeTruthy();
      });
      const regionOrderPath = /\/editor\/regions\/order$/;
      const initial = await loadEditorStateFixture(page) as EditorState;
      const normalRegionIds = initial.regionOrder.filter((id: string) => !initial.regionsById[id].isShadow);
      await putOrder(page, `${editorApiPath}/regions/order`, {
        regionIds: reorderedAdjacent(normalRegionIds, regionByName(initial, secondName).id, regionByName(initial, firstName).id)
      });
      await page.reload();
      await expectMountedWorkspace(page);
      await expectRegionOrder(page, [secondName, firstName]);
      await expectState(page, state => {
        const ids = state.regionOrder;
        expect(ids.indexOf(regionByName(state, secondName).id)).toBeLessThan(ids.indexOf(regionByName(state, firstName).id));
      });

      let noOpRequests = 0;
      await page.route(regionOrderPath, async route => {
        noOpRequests += 1;
        await route.abort();
      });
      await regionCard(page, secondName).getByRole('button', { name: 'Drag to reorder region' }).dragTo(regionCard(page, secondName));
      await settleCompletedDrag(page);
      expect(noOpRequests, 'A no-op region drop must not issue an order request.').toBe(0);
      await page.unroute(regionOrderPath);

      await deleteRegion(page, secondName);
      await deleteRegion(page, firstName);
      await expect(regionCard(page, firstName)).toHaveCount(0);
      await expect(regionCard(page, secondName)).toHaveCount(0);
      await expectState(page, state => {
        expect(regionByName(state, firstName)).toBeNull();
        expect(regionByName(state, secondName)).toBeNull();
      });
    } finally {
      await cleanupRegions(page, [secondName, firstName]);
    }
  });

  test('real endpoint place create move reorder delete persists in the visible sidebar and API reread', async ({ page }) => {
    await openEditor(page);
    const regionName = uniqueName('PW real place region');
    const firstPlace = `${regionName} A`;
    const secondPlace = `${regionName} B`;

    try {
      await createRegion(page, regionName);
      const region = await requireRegion(page, regionName);
      const shadow = await requireUnassignedRegion(page);
      await createPlace(page, regionName, firstPlace, '37.9838', '23.7275');
      await createPlace(page, regionName, secondPlace, '37.9841', '23.7280');
      await expect(placeRowByName(page, region.id, firstPlace)).toBeVisible();
      await expect(placeRowByName(page, region.id, secondPlace)).toBeVisible();
      await expect(placeLabel(page, region.id, firstPlace)).toHaveText(`1-${firstPlace}`);
      await expect(placeLabel(page, region.id, secondPlace)).toHaveText(`2-${secondPlace}`);

      await movePlace(page, region.id, firstPlace, shadow.id);
      await expect(placeRowByName(page, shadow.id, firstPlace)).toBeVisible();
      await expect(regionLabel(page, shadow.name)).toHaveText('0-Unassigned Places');
      await expect(placeRowByName(page, region.id, firstPlace)).toHaveCount(0);
      await expect(placeLabel(page, region.id, secondPlace)).toHaveText(`1-${secondPlace}`);
      await expectState(page, state => {
        expect(placeByName(state, firstPlace).regionId).toBe(shadow.id);
      });

      await movePlace(page, shadow.id, firstPlace, region.id);
      await expect(placeLabel(page, region.id, secondPlace)).toHaveText(`1-${secondPlace}`);
      await expect(placeLabel(page, region.id, firstPlace)).toHaveText(`2-${firstPlace}`);
      await orderPlacesViaEndpoint(page, region.id, [firstPlace, secondPlace]);
      await page.reload();
      await expectMountedWorkspace(page);
      const reloadedRegion = await requireRegion(page, regionName);
      await expectPlaceOrder(page, reloadedRegion.id, [firstPlace, secondPlace]);
      await expectState(page, state => {
        const order = state.placeOrderByRegionId[reloadedRegion.id];
        expect(order.indexOf(placeByName(state, firstPlace).id)).toBeLessThan(order.indexOf(placeByName(state, secondPlace).id));
      });

      let noOpRequests = 0;
      const noOpPlaceOrderPath = new RegExp(`/editor/regions/${reloadedRegion.id}/places/order$`);
      await page.route(noOpPlaceOrderPath, async route => {
        noOpRequests += 1;
        await route.abort();
      });
      await placeRowByName(page, reloadedRegion.id, firstPlace).getByRole('button', { name: 'Drag to reorder place' }).dragTo(placeRowByName(page, reloadedRegion.id, firstPlace));
      await settleCompletedDrag(page);
      expect(noOpRequests, 'A no-op place drop must not issue an order request.').toBe(0);
      await page.unroute(noOpPlaceOrderPath);

      await deletePlace(page, reloadedRegion.id, secondPlace);
      await expect(placeLabel(page, reloadedRegion.id, firstPlace)).toHaveText(`1-${firstPlace}`);
      await deletePlace(page, reloadedRegion.id, firstPlace);
      await expectState(page, state => {
        expect(placeByName(state, firstPlace)).toBeNull();
        expect(placeByName(state, secondPlace)).toBeNull();
      });
    } finally {
      await cleanupRegions(page, [regionName]);
    }
  });

  test('real endpoint area create reorder delete persists in the visible sidebar and API reread', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 1000 });
    await openEditor(page);
    const regionName = uniqueName('PW real area region');
    const firstArea = `${regionName} A`;
    const secondArea = `${regionName} B`;

    try {
      await createRegion(page, regionName);
      const region = await requireRegion(page, regionName);
      await createArea(page, regionName, firstArea);
      await createArea(page, regionName, secondArea);
      await expect(areaRowByName(page, region.id, firstArea)).toBeVisible();
      await expect(areaRowByName(page, region.id, secondArea)).toBeVisible();
      await expectState(page, state => {
        expect(areaByName(state, firstArea)).toBeTruthy();
        expect(areaByName(state, secondArea)).toBeTruthy();
      });

      await orderAreasViaEndpoint(page, region.id, [secondArea, firstArea]);
      await page.reload();
      await expectMountedWorkspace(page);
      const reloadedRegion = await requireRegion(page, regionName);
      await expectAreaOrder(page, reloadedRegion.id, [secondArea, firstArea]);
      await expectState(page, state => {
        const order = state.areaOrderByRegionId[reloadedRegion.id];
        expect(order.indexOf(areaByName(state, secondArea).id)).toBeLessThan(order.indexOf(areaByName(state, firstArea).id));
      });

      await deleteArea(page, reloadedRegion.id, secondArea);
      await deleteArea(page, reloadedRegion.id, firstArea);
      await expectState(page, state => {
        expect(areaByName(state, firstArea)).toBeNull();
        expect(areaByName(state, secondArea)).toBeNull();
      });
    } finally {
      await cleanupRegions(page, [regionName]);
    }
  });

  test('real endpoint segment create route-save reorder delete persists in the visible sidebar and API reread', async ({ page }) => {
    await openEditor(page);
    const disposableSegmentIds: string[] = [];

    try {
      const firstId = await createSegmentViaEndpoint(page, '1.1', '11');
      disposableSegmentIds.push(firstId);
      const secondId = await createSegmentViaEndpoint(page, '2.2', '22');
      disposableSegmentIds.push(secondId);
      await page.reload();
      await expectMountedWorkspace(page);
      await expect(segmentRow(page, firstId)).toBeVisible();
      await expect(segmentRow(page, secondId)).toBeVisible();

      await updateSegmentRouteViaEndpoint(page, firstId);
      await page.reload();
      await expectMountedWorkspace(page);
      await expectState(page, state => {
        expect(state.segmentsById[firstId].route.coordinates.length).toBeGreaterThan(1);
      });

      await orderSegmentsViaEndpoint(page, [secondId, firstId]);
      await page.reload();
      await expectMountedWorkspace(page);
      await expectSegmentOrder(page, [secondId, firstId]);
      await expectState(page, state => {
        const order = state.segmentOrder;
        expect(order.indexOf(secondId)).toBeLessThan(order.indexOf(firstId));
      });

      await deleteSegmentViaEndpoint(page, secondId);
      removeDisposableId(disposableSegmentIds, secondId);
      await deleteSegmentViaEndpoint(page, firstId);
      removeDisposableId(disposableSegmentIds, firstId);
      await page.reload();
      await expectMountedWorkspace(page);
      await expect(segmentRow(page, secondId)).toHaveCount(0);
      await expect(segmentRow(page, firstId)).toHaveCount(0);
      await expectState(page, state => {
        expect(state.segmentsById[firstId]).toBeUndefined();
        expect(state.segmentsById[secondId]).toBeUndefined();
      });
    } finally {
      await cleanupSegments(page, disposableSegmentIds);
    }
  });
});

async function openEditor(page: Page): Promise<void> {
  await signIn(page);
  await page.goto(absoluteUrl(editorPath));
  await expectMountedWorkspace(page);
}

async function createRegion(page: Page, name: string): Promise<void> {
  await page.getByRole('button', { name: 'Add Region' }).click();
  await page.locator('#trip-editor-region-form').getByLabel('Name').fill(name);
  await page.getByRole('button', { name: 'Save Region' }).click();
  await expectSaved(page);
  await expect(regionCard(page, name)).toBeVisible();
  await closeFormSurface(page, '#trip-editor-region-form');
}

async function deleteRegion(page: Page, name: string): Promise<void> {
  const card = regionCard(page, name);
  if ((await card.count()) === 0) {
    return;
  }

  await card.locator('.trip-editor-region-card__header').getByRole('button', { name: 'Edit' }).click();
  await page.getByRole('button', { name: 'Delete', exact: true }).click();
  await page.getByRole('dialog', { name: 'Delete region?' }).getByRole('button', { name: 'Delete' }).click();
  await expectSaved(page);
  await expect(card).toHaveCount(0);
}

async function createPlace(page: Page, regionName: string, name: string, latitude: string, longitude: string): Promise<void> {
  const card = regionCard(page, regionName);
  await card.getByRole('button', { name: 'Add Place' }).click();
  const form = page.locator('#trip-editor-place-form');
  await form.getByLabel('Name').fill(name);
  await form.getByLabel('Address').fill(`${name} address`);
  await form.getByLabel('Latitude').fill(latitude);
  await form.getByLabel('Longitude').fill(longitude);
  await form.getByLabel('Reverse geocode this location on save').uncheck();
  await page.getByRole('button', { name: 'Save Place', exact: true }).click();
  await expectSaved(page);
  await closeFormSurface(page, '#trip-editor-place-form');
}

async function movePlace(page: Page, fromRegionId: string, name: string, toRegionId: string): Promise<void> {
  await placeRowByName(page, fromRegionId, name).getByRole('button', { name: 'Edit', exact: true }).click();
  await page.locator('#trip-editor-place-form').getByLabel('Region').selectOption(toRegionId);
  await page.getByRole('button', { name: 'Save Place', exact: true }).click();
  await expectSaved(page);
  await closeFormSurface(page, '#trip-editor-place-form');
}

async function deletePlace(page: Page, regionId: string, name: string): Promise<void> {
  const row = placeRowByName(page, regionId, name);
  if ((await row.count()) === 0) {
    return;
  }

  await row.getByRole('button', { name: 'Edit', exact: true }).click();
  await page.getByRole('button', { name: 'Delete', exact: true }).click();
  await page.getByRole('dialog', { name: 'Delete place?' }).getByRole('button', { name: 'Delete' }).click();
  await expectSaved(page);
  await expect(row).toHaveCount(0);
}

async function createArea(page: Page, regionName: string, name: string): Promise<void> {
  const card = regionCard(page, regionName);
  await card.getByRole('button', { name: 'Add Area' }).click();
  await page.getByRole('button', { name: 'Draw/Edit Area' }).click();
  await drawTriangle(page);
  await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Done' }).click();
  await page.locator('#trip-editor-area-form').getByLabel('Name').fill(name);
  await page.getByRole('button', { name: 'Save Area' }).click();
  await expectSaved(page);
  await closeFormSurface(page, '#trip-editor-area-form');
}

async function deleteArea(page: Page, regionId: string, name: string): Promise<void> {
  const row = areaRowByName(page, regionId, name);
  if ((await row.count()) === 0) {
    return;
  }

  await row.getByRole('button', { name: 'Edit', exact: true }).click();
  await page.getByRole('button', { name: 'Delete', exact: true }).click();
  await page.getByRole('dialog', { name: 'Delete area?' }).getByRole('button', { name: 'Delete' }).click();
  await expectSaved(page);
  await expect(row).toHaveCount(0);
}

async function cleanupRegions(page: Page, names: string[]): Promise<void> {
  await page.goto(absoluteUrl(editorPath));
  await expectMountedWorkspace(page);
  for (const name of names) {
    await deleteRegion(page, name);
  }
}

async function cleanupSegments(page: Page, segmentIds: string[]): Promise<void> {
  if (segmentIds.length === 0 || page.isClosed()) {
    return;
  }

  await page.goto(absoluteUrl(editorPath));
  await expectMountedWorkspace(page);
  for (const segmentId of segmentIds) {
    if ((await loadEditorStateFixture(page)).segmentsById[segmentId]) {
      await deleteSegmentViaEndpoint(page, segmentId);
      await page.reload();
      await expectMountedWorkspace(page);
    }
  }
}

function removeDisposableId(ids: string[], id: string): void {
  const index = ids.indexOf(id);
  if (index >= 0) {
    ids.splice(index, 1);
  }
}

async function expectSaved(page: Page): Promise<void> {
  await expect(page.locator('.trip-editor-save-state').filter({ hasText: /saved/i }).first()).toBeVisible();
}

async function closeFormSurface(page: Page, formSelector: string): Promise<void> {
  const surface = page.locator('.trip-editor-surface--docked').filter({ has: page.locator(formSelector) });
  if ((await surface.count()) === 0) {
    return;
  }

  await surface.getByRole('button', { name: 'Cancel' }).click();
  await expect(page.locator(formSelector)).toHaveCount(0);
}

async function expectState(page: Page, assertion: (state: EditorState) => void): Promise<void> {
  assertion(await loadEditorStateFixture(page));
}

async function requireRegion(page: Page, name: string): Promise<any> {
  const region = regionByName(await loadEditorStateFixture(page), name);
  expect(region, `Expected region ${name} to exist.`).toBeTruthy();
  return region;
}

async function requireUnassignedRegion(page: Page): Promise<any> {
  const region = Object.values<any>((await loadEditorStateFixture(page)).regionsById).find(item => item.isShadow && item.name === 'Unassigned Places');
  expect(region, 'Expected the built-in Unassigned Places region to exist.').toBeTruthy();
  return region;
}

async function dragRegion(page: Page, fromName: string, toName: string): Promise<void> {
  await regionCard(page, fromName).getByRole('button', { name: 'Drag to reorder region' }).dragTo(regionCard(page, toName));
  await expectSaved(page);
}

/** Locates the visible derived region label while retaining raw-name lookup. */
function regionLabel(page: Page, name: string): Locator {
  return regionCard(page, name).locator('.itinerary-region-label, h3').first();
}

/** Locates the visible derived place label under its authoritative parent region. */
function placeLabel(page: Page, regionId: string, name: string): Locator {
  return placeRowByName(page, regionId, name).locator('.trip-editor-place-row__name');
}

/** Reads the numeric prefix from a rendered itinerary label. */
function itineraryOrdinal(label: string): number {
  return Number(label.slice(0, label.indexOf('-')));
}

async function expectRegionOrder(page: Page, adjacentNames: [string, string]): Promise<void> {
  await expect.poll(async () => {
    const names = await page.locator('.trip-editor-region-card--normal').evaluateAll(cards => cards.map(card => (card as HTMLElement).dataset.regionName));
    return adjacentInOrder(names, adjacentNames);
  }).toBeTruthy();
}

async function expectPlaceOrder(page: Page, regionId: string, adjacentNames: [string, string]): Promise<void> {
  await expect.poll(async () => {
    const names = await page.locator(`[data-place-list-region-id="${regionId}"] [data-place-id]`).evaluateAll(rows => rows.map(row => (row as HTMLElement).dataset.placeName));
    return adjacentInOrder(names, adjacentNames);
  }).toBeTruthy();
}

async function expectAreaOrder(page: Page, regionId: string, adjacentNames: [string, string]): Promise<void> {
  await expect.poll(async () => {
    const names = await page.locator(`[data-area-list-region-id="${regionId}"] [data-area-id] > span`).allInnerTexts();
    return adjacentInOrder(names.map(name => name.trim()), adjacentNames);
  }).toBeTruthy();
}

async function expectSegmentOrder(page: Page, adjacentIds: [string, string]): Promise<void> {
  await expect.poll(async () => {
    const ids = await page.locator('[data-segment-id]').evaluateAll(rows => rows.map(row => (row as HTMLElement).dataset.segmentId));
    return adjacentInOrder(ids, adjacentIds);
  }).toBeTruthy();
}

async function orderPlacesViaEndpoint(page: Page, regionId: string, adjacentNames: [string, string]): Promise<void> {
  const state = await loadEditorStateFixture(page);
  const first = placeByName(state, adjacentNames[0]);
  const second = placeByName(state, adjacentNames[1]);
  const current = state.placeOrderByRegionId[regionId] as string[];
  await putOrder(page, `${editorApiPath}/regions/${regionId}/places/order`, {
    placeIds: reorderedAdjacent(current, first.id, second.id)
  });
}

async function orderAreasViaEndpoint(page: Page, regionId: string, adjacentNames: [string, string]): Promise<void> {
  const state = await loadEditorStateFixture(page);
  const first = areaByName(state, adjacentNames[0]);
  const second = areaByName(state, adjacentNames[1]);
  const current = state.areaOrderByRegionId[regionId] as string[];
  await putOrder(page, `${editorApiPath}/regions/${regionId}/areas/order`, {
    areaIds: reorderedAdjacent(current, first.id, second.id)
  });
}

async function orderSegmentsViaEndpoint(page: Page, adjacentIds: [string, string]): Promise<void> {
  const state = await loadEditorStateFixture(page);
  await putOrder(page, `${editorApiPath}/segments/order`, {
    segmentIds: reorderedAdjacent(state.segmentOrder as string[], adjacentIds[0], adjacentIds[1])
  });
}

async function createSegmentViaEndpoint(page: Page, distance: string, duration: string): Promise<string> {
  const result = await postMutation(page, `${editorApiPath}/segments`, {
    fromPlaceId: null,
    toPlaceId: null,
    mode: 'walk',
    estimatedDistanceKm: Number(distance),
    estimatedDurationMinutes: Number(duration),
    estimatedDurationSource: 'Manual',
    notesHtml: '',
    route: null
  });
  expect(result.data?.id, 'Expected segment create endpoint to return a real segment ID.').toBeTruthy();
  return result.data.id;
}

async function updateSegmentRouteViaEndpoint(page: Page, segmentId: string): Promise<void> {
  const state = await loadEditorStateFixture(page);
  const segment = state.segmentsById[segmentId];
  await putMutation(page, `${editorApiPath}/segments/${segmentId}`, {
    fromPlaceId: segment.fromPlaceId,
    toPlaceId: segment.toPlaceId,
    mode: segment.mode,
    estimatedDistanceKm: segment.estimatedDistanceKm,
    estimatedDurationMinutes: segment.estimatedDurationMinutes,
    estimatedDurationSource: segment.estimatedDurationSource,
    notesHtml: segment.notesHtml,
    route: { type: 'LineString', coordinates: [[23, 37], [24, 38]] }
  });
}

async function deleteSegmentViaEndpoint(page: Page, segmentId: string): Promise<void> {
  await deleteMutation(page, `${editorApiPath}/segments/${segmentId}`);
}

async function postMutation(page: Page, path: string, data: Record<string, unknown>): Promise<any> {
  const token = await antiforgeryToken(page);
  const response = await page.request.post(absoluteUrl(path), {
    data,
    headers: { RequestVerificationToken: token }
  });
  expect(response.ok(), `POST ${path} returned ${response.status()}: ${await response.text()}`).toBeTruthy();
  return await response.json();
}

async function putOrder(page: Page, path: string, data: Record<string, string[]>): Promise<void> {
  await putMutation(page, path, data);
}

async function putMutation(page: Page, path: string, data: Record<string, unknown>): Promise<void> {
  const token = await antiforgeryToken(page);
  const response = await page.request.put(absoluteUrl(path), {
    data,
    headers: { RequestVerificationToken: token }
  });
  expect(response.ok(), `PUT ${path} returned ${response.status()}: ${await response.text()}`).toBeTruthy();
}

async function deleteMutation(page: Page, path: string): Promise<void> {
  const token = await antiforgeryToken(page);
  const response = await page.request.delete(absoluteUrl(path), {
    headers: { RequestVerificationToken: token }
  });
  expect(response.ok(), `DELETE ${path} returned ${response.status()}: ${await response.text()}`).toBeTruthy();
}

async function antiforgeryToken(page: Page): Promise<string> {
  return await page.locator('#trip-editor-antiforgery input[name="__RequestVerificationToken"]').inputValue();
}

function reorderedAdjacent(current: string[], firstId: string, secondId: string): string[] {
  return [...current.filter(id => id !== firstId && id !== secondId), firstId, secondId];
}

function adjacentInOrder(values: Array<string | undefined>, adjacentValues: [string, string]): boolean {
  const first = values.indexOf(adjacentValues[0]);
  const second = values.indexOf(adjacentValues[1]);
  return first >= 0 && second === first + 1;
}

function regionByName(state: EditorState, name: string): any | null {
  return Object.values<any>(state.regionsById).find(region => region.name === name) ?? null;
}

function placeByName(state: EditorState, name: string): any | null {
  return Object.values<any>(state.placesById).find(place => place.name === name) ?? null;
}

function areaByName(state: EditorState, name: string): any | null {
  return Object.values<any>(state.areasById).find(area => area.name === name) ?? null;
}

function placeRowByName(page: Page, regionId: string, name: string): Locator {
  return page.locator(`[data-region-id="${regionId}"] [data-place-id]`).filter({ hasText: name });
}

/** Attempts a pointer drag without Playwright's enabled-control precondition. */
async function dragByMouse(page: Page, source: Locator, target: Locator): Promise<void> {
  await source.scrollIntoViewIfNeeded();
  const sourceBox = await source.boundingBox();
  const targetBox = await target.boundingBox();
  expect(sourceBox, 'Drag source must have a rendered box.').not.toBeNull();
  expect(targetBox, 'Drag target must have a rendered box.').not.toBeNull();
  await page.mouse.move(sourceBox!.x + sourceBox!.width / 2, sourceBox!.y + sourceBox!.height / 2);
  await page.mouse.down();
  await page.mouse.move(targetBox!.x + targetBox!.width / 2, targetBox!.y + targetBox!.height / 2, { steps: 8 });
  await page.mouse.up();
}

/** Waits for the completed drag event and its queued browser work without a timing window. */
async function settleCompletedDrag(page: Page): Promise<void> {
  await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));
}

function areaRowByName(page: Page, regionId: string, name: string): Locator {
  return page.locator(`[data-region-id="${regionId}"] [data-area-id]`).filter({ has: page.getByText(name, { exact: true }) });
}

function segmentRow(page: Page, segmentId: string): Locator {
  return page.locator(`[data-segment-id="${segmentId}"]`);
}

async function drawTriangle(page: Page): Promise<void> {
  await clickMap(page, { xRatio: 0.35, yRatio: 0.35 });
  await clickMap(page, { xRatio: 0.45, yRatio: 0.35 });
  await clickMap(page, { xRatio: 0.40, yRatio: 0.45 });
  await page.locator('.leaflet-editing-icon').first().click();
}

async function clickMap(page: Page, position: { xRatio: number; yRatio: number }): Promise<void> {
  await page.evaluate(() => window.scrollTo(0, 0));
  const map = page.locator('.trip-editor-map.leaflet-container');
  const box = await map.boundingBox();
  expect(box, 'Trip Editor map should be visible before clicking it.').not.toBeNull();
  const viewport = page.viewportSize();
  expect(viewport, 'Playwright should provide a viewport for visible map clicks.').not.toBeNull();
  const footerBox = await page.locator('body > footer').boundingBox();
  const visibleLeft = Math.max(box!.x, 0) + 16;
  const visibleRight = Math.min(box!.x + box!.width, viewport!.width) - 16;
  const visibleTop = Math.max(box!.y, 0) + 16;
  const visibleBottom = Math.min(box!.y + box!.height, footerBox?.y ?? viewport!.height, viewport!.height) - 16;
  expect(visibleRight, 'Trip Editor map should have a visible clickable width.').toBeGreaterThan(visibleLeft);
  expect(visibleBottom, 'Trip Editor map should have a visible clickable height above the footer.').toBeGreaterThan(visibleTop);
  await page.mouse.click(visibleLeft + (visibleRight - visibleLeft) * position.xRatio, visibleTop + (visibleBottom - visibleTop) * position.yRatio);
  await page.waitForTimeout(75);
}
