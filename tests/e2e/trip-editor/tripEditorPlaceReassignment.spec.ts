import { expect, test, type Locator, type Page } from '@playwright/test';
import {
  absoluteUrl,
  editorApiPath,
  editorPath,
  expectMountedWorkspace,
  signIn
} from './tripEditorTestUtils';

test.describe('Trip Editor place reassignment', () => {
  test('place Region selector sends mocked moves into and out of Unassigned Places', async ({ page }) => {
    await signIn(page);
    const baseState = await loadEditorState(page);
    const fixture = withMovablePlace(baseState);
    const requests: Array<Record<string, any>> = [];
    await routeEditorStateWithPlaceMove(page, fixture.state, requests, fixture.placeId);

    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);
    await page.locator(`[data-place-id="${fixture.placeId}"]`).getByRole('button', { name: 'Edit' }).click();
    await expect(page.getByRole('heading', { name: 'Edit Place - Move Back Place' })).toBeVisible();

    const select = page.locator('#trip-editor-place-form').getByLabel('Region');
    await expect(select).toContainText('Unassigned Places');
    await expect(select).toHaveValue(fixture.normalRegionId);
    await select.selectOption(fixture.unassignedRegionId);
    await page.getByRole('button', { name: 'Save Place' }).click();

    await expect.poll(() => requests.length).toBe(1);
    expect(requests[0].regionId).toBe(fixture.unassignedRegionId);
    await expect(page.locator('.trip-editor-save-state').filter({ hasText: /Place saved/i }).first()).toBeVisible();
    await expect(placeRow(page, fixture.unassignedRegionId, fixture.placeId)).toBeVisible();
    await expect(placeRow(page, fixture.normalRegionId, fixture.placeId)).toHaveCount(0);
    await expect(select).toHaveValue(fixture.unassignedRegionId);

    await select.selectOption(fixture.normalRegionId);
    await page.getByRole('button', { name: 'Save Place' }).click();

    await expect.poll(() => requests.length).toBe(2);
    expect(requests[1].regionId).toBe(fixture.normalRegionId);
    await expect(page.locator('.trip-editor-save-state').filter({ hasText: /Place saved/i }).first()).toBeVisible();
    await expect(placeRow(page, fixture.normalRegionId, fixture.placeId)).toBeVisible();
    await expect(placeRow(page, fixture.unassignedRegionId, fixture.placeId)).toHaveCount(0);
  });
});

async function loadEditorState(page: Page): Promise<any> {
  const response = await page.request.get(absoluteUrl(editorApiPath), { headers: { Accept: 'application/json' } });
  expect(response.ok()).toBeTruthy();
  return await response.json();
}

function withMovablePlace(state: any): { state: any; normalRegionId: string; placeId: string; unassignedRegionId: string } {
  const clone = structuredClone(state);
  clone.permissions.canEditPlaces = true;
  const normalRegionId = firstNormalRegionId(clone);
  const unassignedRegionId = unassignedPlacesRegionId(clone);
  clone.placeOrderByRegionId[normalRegionId] = clone.placeOrderByRegionId[normalRegionId] ?? [];
  clone.placeOrderByRegionId[unassignedRegionId] = clone.placeOrderByRegionId[unassignedRegionId] ?? [];
  const placeId = '00000000-0000-0000-0000-000000295901';
  clone.placesById[placeId] = {
    id: placeId,
    tripId: clone.tripId,
    regionId: normalRegionId,
    name: 'Move Back Place',
    notesHtml: '<p>Move back notes</p>',
    address: 'Athens, Greece',
    location: { latitude: 37.9838, longitude: 23.7275 },
    iconName: clone.options.iconNames[0] ?? 'marker',
    markerColor: clone.options.markerColorClasses[0] ?? 'bg-blue',
    displayOrder: 1,
    visitSummary: { placeId, visitCount: 0, isVisited: false, firstVisitAt: null, lastVisitAt: null },
    capabilities: { canEdit: true, canRename: true, canDelete: true, canReorder: true, canMove: true, canAddChildren: false, canTargetForSearchAdd: false }
  };
  clone.placeOrderByRegionId[normalRegionId] = [placeId, ...clone.placeOrderByRegionId[normalRegionId].filter((id: string) => id !== placeId)];
  return { state: clone, normalRegionId, placeId, unassignedRegionId };
}

async function routeEditorStateWithPlaceMove(page: Page, state: any, requests: Array<Record<string, any>>, placeId: string): Promise<void> {
  const matcher = new RegExp(`${editorApiPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?:/.*)?$`, 'i');
  // Mocked mutation responses prove request shape and frontend affected-slice handling only, not endpoint CRUD persistence.
  await page.route(matcher, async route => {
    const request = route.request();
    if (request.method() === 'GET') {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
      return;
    }

    const body = request.postDataJSON() as Record<string, any>;
    requests.push(body);
    const previousRegionId = state.placesById[placeId].regionId;
    const targetRegionId = body.regionId;
    const place = { ...state.placesById[placeId], ...body, regionId: targetRegionId };
    state.placesById[placeId] = place;
    state.placeOrderByRegionId[previousRegionId] = (state.placeOrderByRegionId[previousRegionId] ?? []).filter((id: string) => id !== placeId);
    state.placeOrderByRegionId[targetRegionId] = [...(state.placeOrderByRegionId[targetRegionId] ?? []).filter((id: string) => id !== placeId), placeId];
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(mutationResult(place, state, previousRegionId, targetRegionId)) });
  });
}

function mutationResult(place: Record<string, any>, state: any, previousRegionId: string, targetRegionId: string): Record<string, any> {
  return {
    success: true,
    data: place,
    affected: {
      metadata: null,
      regions: [],
      regionOrder: null,
      places: [place],
      placeOrdersByRegionId: previousRegionId === targetRegionId ? {} : {
        [previousRegionId]: state.placeOrderByRegionId[previousRegionId],
        [targetRegionId]: state.placeOrderByRegionId[targetRegionId]
      },
      areas: [],
      areaOrdersByRegionId: {},
      segments: [],
      segmentOrder: null,
      tags: [],
      tagOrder: null,
      visitProgress: null,
      options: null
    },
    deletedIds: { regions: [], places: [], areas: [], segments: [], tags: [] },
    warnings: []
  };
}

function unassignedPlacesRegionId(state: any): string {
  const unassigned = Object.values<any>(state.regionsById).find(region => region.isShadow && region.name === 'Unassigned Places');
  expect(unassigned, 'Trip Editor fixture must include the built-in Unassigned Places region.').toBeTruthy();
  return unassigned.id;
}

function firstNormalRegionId(state: any): string {
  const region = Object.values<any>(state.regionsById).find(region => !region.isShadow);
  expect(region, 'Trip Editor fixture must include a normal region.').toBeTruthy();
  return region.id;
}

function placeRow(page: Page, regionId: string, placeId: string): Locator {
  return page.locator(`[data-region-id="${regionId}"] [data-place-id="${placeId}"]`);
}
