import { expect, test, type Locator, type Page, type Route, type TestInfo } from '@playwright/test';
import {
  absoluteUrl,
  editorApiPath,
  editorPath,
  expectMountedWorkspace,
  loadEditorStateFixture,
  signIn
} from './tripEditorTestUtils';

type MutableEditorState = Record<string, any>;

const regionId = '00000000-0000-0000-0000-000000283101';
const firstPlaceId = '00000000-0000-0000-0000-000000283201';
const secondPlaceId = '00000000-0000-0000-0000-000000283202';
const firstPlaceName = 'PW marker parity place with a deliberately long sidebar name that must wrap without covering the Edit action';
const secondPlaceName = 'PW marker parity second place';
const externalImageUrl = 'https://images.example.test/pw-rich-note.png';
const editorApiMatcher = new RegExp(`${editorApiPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?:/.*)?$`, 'i');
const tinyPng = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=', 'base64');

test.describe.serial('Trip Editor marker and notes parity', () => {
  test('keeps map markers, sidebar rows, popups, and selection synchronized both directions', async ({ page }, testInfo) => {
    await signIn(page);
    await loadWorkspaceWithMarkerFixture(page);

    await expectLoadedImages(mapMarkerImages(page));
    await expectLoadedImages(page.locator('[data-sidebar-place-icon]'));

    await clickMarker(page, firstPlaceId);
    await expectSelectedPlace(page, firstPlaceId);
    await expect(sidebarRow(page, firstPlaceId)).toBeInViewport();
    await expect(page.locator('.trip-editor-toolbar')).toContainText(firstPlaceName);
    await expect(page.locator('.trip-editor-toolbar__status')).toContainText(`Selected place: ${firstPlaceName}`);
    await expect(page.locator('.trip-editor-place-editor-row')).toHaveCount(0);
    await expect(page.locator('.leaflet-popup')).toContainText(firstPlaceName);
    await expect(page.locator('.leaflet-popup')).toContainText('Marker popup rich note');
    await captureEvidence(page, testInfo, 'map-click-selects-sidebar-status');

    await sidebarRow(page, secondPlaceId).click();
    await expectSelectedPlace(page, secondPlaceId);
    await expectNotSelected(page, firstPlaceId);
    await expect(page.locator('.trip-editor-toolbar')).toContainText(secondPlaceName);
    await expect(page.locator('.trip-editor-toolbar__status')).toContainText(`Selected place: ${secondPlaceName}`);
    await expect(page.locator('.trip-editor-place-editor-row')).toHaveCount(0);
    await captureEvidence(page, testInfo, 'sidebar-click-selects-map-status');

    await sidebarRow(page, firstPlaceId).getByRole('button', { name: 'Edit', exact: true }).click();
    await expect(page.getByRole('heading', { name: new RegExp(`Edit Place - ${escapeRegex(firstPlaceName)}`) })).toBeVisible();
    await expectSelectedPlace(page, firstPlaceId);
    await expectNotSelected(page, secondPlaceId);
    await expect(page.locator('.trip-editor-place-editor-row')).toHaveCount(1);

    await page.getByLabel('Sidebar search').fill('second place');
    await expectSelectedPlace(page, firstPlaceId);
    await expect(sidebarRow(page, firstPlaceId)).toBeVisible();
    await page.getByLabel('Sidebar search').fill('');

    await page.locator('#trip-editor-place-form').getByLabel('Name').fill(`${firstPlaceName} saved`);
    await page.getByRole('button', { name: 'Save Place' }).click();
    await expect(page.locator('.trip-editor-save-state').filter({ hasText: /Saved/i }).first()).toBeVisible();
    await expectSelectedPlace(page, firstPlaceId);

    await page.getByRole('button', { name: 'Cancel' }).click();
    await expectSelectedPlace(page, firstPlaceId);
  });

  test('guards marker and sidebar place selection while a place editor is dirty', async ({ page }, testInfo) => {
    await signIn(page);
    await loadWorkspaceWithMarkerFixture(page);

    await sidebarRow(page, firstPlaceId).getByRole('button', { name: 'Edit', exact: true }).click();
    await expect(expectPlaceEditor(page, firstPlaceName)).toBeVisible();
    await page.locator('#trip-editor-place-form').getByLabel('Address').fill('Unsaved dirty marker guard address');
    await expectSelectedPlace(page, firstPlaceId);

    await clickMarker(page, secondPlaceId);
    const markerDiscard = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(markerDiscard).toBeVisible();
    await markerDiscard.getByRole('button', { name: 'Keep editing' }).click();

    await expect(expectPlaceEditor(page, firstPlaceName)).toBeVisible();
    await expect(page.locator('#trip-editor-place-form').getByLabel('Address')).toHaveValue('Unsaved dirty marker guard address');
    await expectSelectedPlace(page, firstPlaceId);
    await expectNotSelected(page, secondPlaceId);
    await expect(page.locator('.trip-editor-toolbar')).toContainText(firstPlaceName);
    await expect(page.locator('.leaflet-popup')).toHaveCount(0);
    await captureEvidence(page, testInfo, 'dirty-marker-selection-cancel-keeps-editor');

    await sidebarRow(page, secondPlaceId).click();
    const sidebarDiscard = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(sidebarDiscard).toBeVisible();
    await sidebarDiscard.getByRole('button', { name: 'Keep editing' }).click();

    await expect(expectPlaceEditor(page, firstPlaceName)).toBeVisible();
    await expect(page.locator('#trip-editor-place-form').getByLabel('Address')).toHaveValue('Unsaved dirty marker guard address');
    await expectSelectedPlace(page, firstPlaceId);
    await expectNotSelected(page, secondPlaceId);

    await clickMarker(page, secondPlaceId);
    const confirmDiscard = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(confirmDiscard).toBeVisible();
    await confirmDiscard.getByRole('button', { name: 'Discard' }).click();

    await expect(expectPlaceEditor(page, firstPlaceName)).toHaveCount(0);
    await expectSelectedPlace(page, secondPlaceId);
    await expectNotSelected(page, firstPlaceId);
    await expect(page.locator('.leaflet-popup')).toContainText(secondPlaceName);
  });

  test('shows icon choices and keeps long place names from overlapping actions', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await signIn(page);
    await loadWorkspaceWithMarkerFixture(page);
    await expectPlaceRowDoesNotOverlapEdit(page, firstPlaceId);

    await page.setViewportSize({ width: 390, height: 900 });
    await expectPlaceRowDoesNotOverlapEdit(page, firstPlaceId);

    await sidebarRow(page, firstPlaceId).getByRole('button', { name: 'Edit', exact: true }).click();
    await expect(page.locator('[data-place-icon-choice]').first()).toBeVisible();
    await expectLoadedImages(page.locator('[data-place-icon-choice]').first());
  });

  test('renders notes images through the proxy while saving canonical external image URLs', async ({ page }) => {
    const requests: Record<string, any>[] = [];
    await signIn(page);
    await loadWorkspaceWithMarkerFixture(page, requests);

    await sidebarRow(page, firstPlaceId).getByRole('button', { name: 'Edit', exact: true }).click();
    const editorImage = page.locator('#trip-editor-place-form .ql-editor img').first();
    await expect(editorImage).toHaveAttribute('src', /\/Public\/ProxyImage\?url=/);
    await expectLoadedImages(editorImage);

    await page.locator('#trip-editor-place-form').getByLabel('Address').fill('Canonical image save check');
    await page.getByRole('button', { name: 'Save Place' }).click();

    await expect.poll(() => requests.length).toBe(1);
    expect(requests[0].notesHtml).toContain(externalImageUrl);
    expect(requests[0].notesHtml).not.toContain('/Public/ProxyImage');
  });
});

async function loadWorkspaceWithMarkerFixture(page: Page, requests: Record<string, any>[] = []): Promise<MutableEditorState> {
  await page.unroute(editorApiMatcher).catch(() => undefined);
  await page.route(/\/Public\/ProxyImage\?url=/i, async route => {
    await route.fulfill({ status: 200, contentType: 'image/png', body: tinyPng });
  });
  const state = await loadEditorStateFixture(page) as MutableEditorState;
  prepareMarkerState(state);
  await page.route(editorApiMatcher, async route => routeEditorState(route, state, requests));
  await page.goto(absoluteUrl(editorPath));
  await expectMountedWorkspace(page);
  return state;
}

async function routeEditorState(route: Route, state: MutableEditorState, requests: Record<string, any>[]): Promise<void> {
  const request = route.request();
  if (request.method() === 'GET') {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
    return;
  }

  if (request.method() === 'PUT' && request.url().includes(`/places/${firstPlaceId}`)) {
    const body = request.postDataJSON() as Record<string, any>;
    requests.push(body);
    state.placesById[firstPlaceId] = { ...state.placesById[firstPlaceId], ...body };
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(mutationResult(state.placesById[firstPlaceId], { places: [state.placesById[firstPlaceId]] })) });
    return;
  }

  throw new Error(`Unexpected marker parity mutation ${request.method()} ${request.url()}`);
}

function prepareMarkerState(state: MutableEditorState): void {
  state.permissions.canEditRegions = true;
  state.permissions.canEditPlaces = true;
  state.metadata.center = null;
  state.metadata.zoom = null;
  state.regionsById = {
    [regionId]: {
      id: regionId,
      tripId: state.tripId,
      name: 'PW marker parity region',
      notesHtml: '',
      coverImage: null,
      center: null,
      displayOrder: 1,
      isShadow: false,
      capabilities: editableCapabilities()
    }
  };
  state.regionOrder = [regionId];
  state.placesById = {
    [firstPlaceId]: placeFixture(state, firstPlaceId, firstPlaceName, 'camera', 'bg-blue', { latitude: 37.9838, longitude: 23.7275 }, true),
    [secondPlaceId]: placeFixture(state, secondPlaceId, secondPlaceName, 'star', 'bg-red', { latitude: 38.2, longitude: 24.05 }, false)
  };
  state.placeOrderByRegionId = { [regionId]: [firstPlaceId, secondPlaceId] };
  state.areasById = {};
  state.areaOrderByRegionId = { [regionId]: [] };
  state.segmentsById = {};
  state.segmentOrder = [];
}

function placeFixture(state: MutableEditorState, id: string, name: string, iconName: string, markerColor: string, location: Record<string, number>, visited: boolean): Record<string, any> {
  return {
    id,
    tripId: state.tripId,
    regionId,
    name,
    notesHtml: `<p>Marker popup rich note for ${name}</p><p><img src="${externalImageUrl}"></p>`,
    address: 'Athens, Greece',
    location,
    iconName: state.options.iconNames.includes(iconName) ? iconName : state.options.iconNames[0] ?? 'marker',
    markerColor: state.options.markerColorClasses.includes(markerColor) ? markerColor : state.options.markerColorClasses[0] ?? 'bg-blue',
    displayOrder: 1,
    visitSummary: { placeId: id, visitCount: visited ? 2 : 0, isVisited: visited, firstVisitAt: null, lastVisitAt: null },
    capabilities: editableCapabilities()
  };
}

function editableCapabilities(): Record<string, boolean> {
  return { canEdit: true, canRename: true, canDelete: true, canReorder: true, canMove: true, canAddChildren: true, canTargetForSearchAdd: true };
}

function mutationResult(data: Record<string, any>, affected: Record<string, any>): Record<string, any> {
  return {
    success: true,
    data,
    affected: {
      metadata: null,
      regions: [],
      regionOrder: null,
      places: [],
      placeOrdersByRegionId: {},
      areas: [],
      areaOrdersByRegionId: {},
      segments: [],
      segmentOrder: null,
      tags: [],
      tagOrder: null,
      visitProgress: null,
      options: null,
      ...affected
    },
    deletedIds: { regions: [], places: [], areas: [], segments: [], tags: [] },
    warnings: []
  };
}

function sidebarRow(page: Page, placeId: string): Locator {
  return page.locator(`[data-place-id="${placeId}"]`);
}

function markerImage(page: Page, placeId: string): Locator {
  return page.locator(`[data-place-marker-icon="${placeId}"]`);
}

function expectPlaceEditor(page: Page, placeName: string): Locator {
  return page.getByRole('heading', { name: new RegExp(`Edit Place - ${escapeRegex(placeName)}`) });
}

function mapMarkerImages(page: Page): Locator {
  return page.locator('[data-place-marker-icon]');
}

async function expectSelectedPlace(page: Page, placeId: string): Promise<void> {
  await expect(sidebarRow(page, placeId)).toHaveClass(/trip-editor-place-row--active/);
  await expect(markerImage(page, placeId).locator('xpath=ancestor::*[contains(@class, "trip-editor-map-marker")]')).toHaveClass(/trip-editor-map-marker--selected/);
}

async function expectNotSelected(page: Page, placeId: string): Promise<void> {
  await expect(sidebarRow(page, placeId)).not.toHaveClass(/trip-editor-place-row--active/);
  await expect(markerImage(page, placeId).locator('xpath=ancestor::*[contains(@class, "trip-editor-map-marker")]')).not.toHaveClass(/trip-editor-map-marker--selected/);
}

async function expectLoadedImages(images: Locator): Promise<void> {
  const count = await images.count();
  expect(count, 'Expected at least one image to validate.').toBeGreaterThan(0);
  for (let index = 0; index < count; index += 1) {
    await expect.poll(async () => images.nth(index).evaluate(image => image instanceof HTMLImageElement && image.complete && image.naturalWidth > 0 && image.naturalHeight > 0)).toBe(true);
  }
}

async function clickMarker(page: Page, placeId: string): Promise<void> {
  await expect(markerImage(page, placeId)).toBeVisible();
  await markerImage(page, placeId).evaluate(element => {
    element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
  });
}

async function captureEvidence(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  await page.screenshot({ fullPage: true, path: testInfo.outputPath('screenshots', `${name}.png`) });
}

async function expectPlaceRowDoesNotOverlapEdit(page: Page, placeId: string): Promise<void> {
  const row = sidebarRow(page, placeId);
  await expect(row).toBeVisible();
  const nameBox = await row.locator('.trip-editor-place-row__name').boundingBox();
  const editBox = await row.getByRole('button', { name: 'Edit', exact: true }).boundingBox();
  expect(nameBox).not.toBeNull();
  expect(editBox).not.toBeNull();
  expect(nameBox!.x + nameBox!.width, 'Place name must not overlap the Edit button.').toBeLessThanOrEqual(editBox!.x);
}

function escapeRegex(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
