import { expect, test, type Locator, type Page, type Route } from '@playwright/test';
import {
  absoluteUrl,
  editorApiPath,
  editorPath,
  expectMountedWorkspace,
  loadEditorStateFixture,
  signIn,
  uniqueName
} from './tripEditorTestUtils';

type EditorState = Record<string, any>;
type SearchTarget = { id: string; name: string };

const geocodePath = /\/api\/trips\/[^/]+\/editor\/geocode\/search/i;
const resultLatitude = '37.9715';
const resultLongitude = '23.7257';

test.describe.serial('Trip Editor search-add real place persistence contracts', () => {
  test.describe.configure({ timeout: 60_000 });

  test('geosearch Add as place defaults to Unassigned Places and persists through real Save Place', async ({ page }) => {
    await openEditor(page);
    await cleanupDisposableSearchAddPlaces(page);
    const targets = await requireSearchAddTargets(page);
    const placeName = uniqueName('PW search add unassigned');

    try {
      await routeGeocode(page, placeName);
      await runSearch(page, placeName);
      await selectSearchResult(page, placeName);
      await expect(page.getByLabel('Target region')).toHaveValue(targets.unassigned.id);
      const resultView = await mapView(page);

      await page.getByRole('button', { name: 'Add as place' }).click();
      await expectSearchResultsCollapsed(page, placeName);
      await expectSearchAddDraft(page, placeName, targets.unassigned.id);
      await expect(page.locator('[data-search-preview-marker]')).toHaveCount(0);
      await expect(page.locator('[data-place-draft-preview-marker]')).toHaveCount(1);
      await expect.poll(() => mapView(page)).toEqual(resultView);

      const createdPlaceId = await savePlaceThroughRealEndpoint(page, targets.unassigned.id);
      await expectSaved(page);
      await expect(placeRowByName(page, targets.unassigned.id, placeName)).toBeVisible();
      await expectPersistedPlace(page, placeName, targets.unassigned.id, createdPlaceId);

      await page.reload();
      await expectMountedWorkspace(page);
      await expect(placeRowByName(page, targets.unassigned.id, placeName)).toBeVisible();
      await expectPersistedPlace(page, placeName, targets.unassigned.id, createdPlaceId);
    } finally {
      await cleanupPlacesByName(page, [placeName]);
    }
  });

  test('geosearch Add as place persists in a selected normal region and the next fresh add resets to Unassigned Places', async ({ page }) => {
    await openEditor(page);
    await cleanupDisposableSearchAddPlaces(page);
    const targets = await requireSearchAddTargets(page);
    const placeName = uniqueName('PW search add region');
    const resetProbeName = uniqueName('PW search add reset probe');

    try {
      await routeGeocode(page, placeName);
      await runSearch(page, placeName);
      await selectSearchResult(page, placeName);
      const targetSelect = page.getByLabel('Target region');
      await expect(targetSelect).toHaveValue(targets.unassigned.id);
      await targetSelect.selectOption(targets.normal.id);

      await page.getByRole('button', { name: 'Add as place' }).click();
      await expectSearchResultsCollapsed(page, placeName);
      await expectSearchAddDraft(page, placeName, targets.normal.id);

      const createdPlaceId = await savePlaceThroughRealEndpoint(page, targets.normal.id);
      await expectSaved(page);
      await expect(placeRowByName(page, targets.normal.id, placeName)).toBeVisible();
      await expectPersistedPlace(page, placeName, targets.normal.id, createdPlaceId);

      await page.reload();
      await expectMountedWorkspace(page);
      await expect(placeRowByName(page, targets.normal.id, placeName)).toBeVisible();
      await expectPersistedPlace(page, placeName, targets.normal.id, createdPlaceId);

      await closePlaceFormIfOpen(page);
      await routeGeocode(page, resetProbeName);
      await runSearch(page, resetProbeName);
      await selectSearchResult(page, resetProbeName);
      await expect(page.getByLabel('Target region')).toHaveValue(targets.unassigned.id);
    } finally {
      await cleanupPlacesByName(page, [placeName, resetProbeName]);
    }
  });
});

async function openEditor(page: Page): Promise<void> {
  await page.route(/\/map\/tiles\//i, route => route.fulfill({ status: 204, body: '' }));
  await signIn(page);
  await page.goto(absoluteUrl(editorPath));
  await expectMountedWorkspace(page);
}

async function routeGeocode(page: Page, name: string): Promise<void> {
  await page.unroute(geocodePath).catch(() => undefined);
  await page.route(geocodePath, async route => fulfillGeocode(route, name));
}

async function fulfillGeocode(route: Route, name: string): Promise<void> {
  const query = new URL(route.request().url()).searchParams.get('q') ?? '';
  await route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      query,
      attribution: 'Mocked geocode provider for search-add persistence test',
      results: [{
        id: `batch4:${name}`,
        provider: 'batch4',
        name,
        displayName: `${name}, Athens, Greece`,
        address: 'Athens, Greece',
        category: 'tourism',
        type: 'attraction',
        latitude: Number(resultLatitude),
        longitude: Number(resultLongitude)
      }]
    })
  });
}

async function runSearch(page: Page, query: string): Promise<void> {
  await page.getByRole('searchbox', { name: 'Map search' }).fill(query);
  await page.getByRole('region', { name: 'Map search' }).getByRole('button', { name: 'Search' }).click();
}

async function selectSearchResult(page: Page, name: string): Promise<void> {
  const mapSearch = page.getByRole('region', { name: 'Map search' });
  await expect(mapSearch.getByRole('button', { name })).toBeVisible();
  await mapSearch.getByRole('button', { name }).click();
}

async function expectSearchResultsCollapsed(page: Page, name: string): Promise<void> {
  const mapSearch = page.getByRole('region', { name: 'Map search' });
  await expect(mapSearch.getByRole('button', { name })).toHaveCount(0);
  await expect(page.locator('.trip-editor-map-search__results')).toHaveCount(0);
  await expect(page.getByRole('searchbox', { name: 'Map search' })).toHaveValue('');
}

async function expectSearchAddDraft(page: Page, name: string, regionId: string): Promise<void> {
  const form = page.locator('#trip-editor-place-form');
  await expect(page.getByRole('heading', { name: 'Add Place' })).toBeVisible();
  await expect(form.getByLabel('Region')).toHaveValue(regionId);
  await expect(form.getByLabel('Name')).toHaveValue(name);
  await expect(form.getByLabel('Address')).toHaveValue('Athens, Greece');
  await expect(form.getByLabel('Latitude')).toHaveValue(resultLatitude);
  await expect(form.getByLabel('Longitude')).toHaveValue(resultLongitude);
  await expect(form.getByLabel('Reverse geocode this location on save')).not.toBeChecked();
  await expect(form.locator('[data-selector-kind="icon"] [data-icon-selector-selected-name]')).toHaveText('marker');
  await expect(form.locator('[data-selector-kind="color"] [data-icon-selector-selected-name]')).toHaveText('Blue');
  await expect(page.locator(`img[alt="Pending place location: ${name}"]`)).toBeVisible();
  await expect(page.locator('[data-place-draft-preview-marker]')).toHaveAttribute('src', /\/icons\/wayfarer-map-icons\/dist\/png\/marker\/bg-blue\/marker\.png$/);
}

async function mapView(page: Page): Promise<{ latitude: number; longitude: number; zoom: number }> {
  return await page.getByLabel('Read-only trip map').evaluate(element => ({
    latitude: Number((element as HTMLElement).dataset.tripEditorMapLat),
    longitude: Number((element as HTMLElement).dataset.tripEditorMapLng),
    zoom: Number((element as HTMLElement).dataset.tripEditorMapZoom)
  }));
}

async function savePlaceThroughRealEndpoint(page: Page, regionId: string): Promise<string> {
  const endpoint = new RegExp(`${editorApiPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}/regions/${regionId}/places/?$`, 'i');
  const responsePromise = page.waitForResponse(response => response.request().method() === 'POST' && endpoint.test(new URL(response.url()).pathname));
  await page.getByRole('button', { name: 'Save Place' }).click();
  const response = await responsePromise;
  if (!response.ok()) {
    throw new Error(`POST create-place endpoint returned ${response.status()}: ${await response.text()}`);
  }

  const body = await response.json();
  expect(body.data?.id, 'Create-place endpoint should return the persisted place id.').toBeTruthy();
  return body.data.id;
}

async function expectSaved(page: Page): Promise<void> {
  await expect(page.locator('.trip-editor-save-state').filter({ hasText: /Place saved|saved/i }).first()).toBeVisible();
}

async function expectPersistedPlace(page: Page, name: string, regionId: string, expectedId: string): Promise<void> {
  const place = placeByName(await loadEditorStateFixture(page), name);
  expect(place, `Expected ${name} to exist in the editor API reread.`).toBeTruthy();
  expect(place.id).toBe(expectedId);
  expect(place.regionId).toBe(regionId);
  expect(place.address).toBe('Athens, Greece');
  expect(String(place.location?.latitude)).toBe(resultLatitude);
  expect(String(place.location?.longitude)).toBe(resultLongitude);
  expect(place.iconName).toBe('marker');
  expect(place.markerColor).toBe('bg-blue');
}

async function requireSearchAddTargets(page: Page): Promise<{ unassigned: SearchTarget; normal: SearchTarget }> {
  const state = await loadEditorStateFixture(page);
  const eligible = state.regionOrder
    .map((id: string) => state.regionsById[id])
    .filter((region: any) => region?.capabilities?.canTargetForSearchAdd);
  const unassigned = eligible.find((region: any) => region.isShadow && region.name === 'Unassigned Places');
  const normal = eligible.find((region: any) => !region.isShadow);
  expect(unassigned, 'Batch 4 requires Unassigned Places to be an eligible search-add target.').toBeTruthy();
  expect(normal, 'Batch 4 requires at least one normal region eligible for search-add.').toBeTruthy();
  return {
    unassigned: { id: unassigned.id, name: unassigned.name },
    normal: { id: normal.id, name: normal.name }
  };
}

async function closePlaceFormIfOpen(page: Page): Promise<void> {
  const form = page.locator('#trip-editor-place-form');
  if ((await form.count()) === 0) {
    return;
  }

  await page.locator('.trip-editor-surface--docked').filter({ has: form }).getByRole('button', { name: 'Cancel' }).click();
  await expect(form).toHaveCount(0);
}

async function cleanupPlacesByName(page: Page, names: string[]): Promise<void> {
  if (page.isClosed()) {
    return;
  }

  await page.goto(absoluteUrl(editorPath));
  await expectMountedWorkspace(page);
  for (const name of names) {
    const state = await loadEditorStateFixture(page);
    const place = placeByName(state, name);
    if (!place) {
      continue;
    }

    await deletePlaceViaEndpoint(page, place.id);
  }
}

async function cleanupDisposableSearchAddPlaces(page: Page): Promise<void> {
  await cleanupPlacesByPredicate(page, place => place.name.startsWith('PW search add unassigned ') || place.name.startsWith('PW search add region '));
}

async function cleanupPlacesByPredicate(page: Page, predicate: (place: any) => boolean): Promise<void> {
  const state = await loadEditorStateFixture(page);
  const places = Object.values<any>(state.placesById).filter(predicate);
  for (const place of places) {
    await deletePlaceViaEndpoint(page, place.id);
  }
}

async function deletePlaceViaEndpoint(page: Page, placeId: string): Promise<void> {
  const response = await page.request.delete(absoluteUrl(`${editorApiPath}/places/${placeId}`), {
    headers: { RequestVerificationToken: await antiforgeryToken(page) }
  });
  if (!response.ok()) {
    throw new Error(`DELETE place cleanup returned ${response.status()}: ${await response.text()}`);
  }
}

async function antiforgeryToken(page: Page): Promise<string> {
  return await page.locator('#trip-editor-antiforgery input[name="__RequestVerificationToken"]').inputValue();
}

function placeByName(state: EditorState, name: string): any | null {
  return Object.values<any>(state.placesById).find(place => place.name === name) ?? null;
}

function placeRowByName(page: Page, regionId: string, name: string): Locator {
  return page.locator(`[data-place-list-region-id="${regionId}"] [data-place-id]`).filter({ hasText: name });
}
