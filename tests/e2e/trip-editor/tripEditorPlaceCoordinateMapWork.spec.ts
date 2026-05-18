import { expect, test, type Locator, type Page, type Route, type TestInfo } from '@playwright/test';
import {
  absoluteUrl,
  editorApiPath,
  expectMountedWorkspace,
  expectNoSearchAddUi,
  loadEditorStateFixture,
  signIn,
  editorPath
} from './tripEditorTestUtils';

type MutableEditorState = Record<string, any>;
type Coordinate = { latitude: number; longitude: number };

const editablePlaceId = '00000000-0000-0000-0000-000000261001';
const secondPlaceId = '00000000-0000-0000-0000-000000261002';
const editablePlaceName = 'PW coordinate place';
const secondPlaceName = 'PW coordinate switch place';
const editorApiMatcher = new RegExp(`${editorApiPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?:/.*)?$`, 'i');
const forbiddenPickRequest = /nominatim|geocode|geosearch|search-add|searchadd|\/search(?:[/?#]|$)/i;

test.describe.serial('Trip Editor place coordinate map-work', () => {
  test('add-place picks a temporary coordinate and Done updates only the draft', async ({ page }, testInfo) => {
    await signIn(page);
    await loadWorkspaceWithCoordinateFixture(page);
    const mutations = watchEditorMutations(page);
    const forbidden = watchForbiddenPickRequests(page);

    await firstEditableRegion(page).getByRole('button', { name: 'Add Place' }).click();
    const form = page.locator('#trip-editor-place-form');
    await expect(page.getByRole('heading', { name: 'Add Place' })).toBeVisible();
    await clickMap(page, { xRatio: 0.28, yRatio: 0.62 });
    await expect(form.getByLabel('Latitude')).toHaveValue('');
    await expect(form.getByLabel('Longitude')).toHaveValue('');

    await expectPickOnMapHelp(page);
    const normalCursor = await mapCursor(page);
    await page.getByRole('button', { name: 'Pick on map' }).click();
    const mapWork = page.getByRole('region', { name: 'Map work' });
    await expect(mapWork).toContainText('Pick place location');
    await expect(mapWork).toContainText('No coordinate selected');
    await expect(mapWork.getByRole('button', { name: 'Done' })).toBeDisabled();
    await expectNoSearchAddUi(page);
    await expect.poll(() => mapCursor(page)).toBe('default');

    await clickMap(page, { xRatio: 0.38, yRatio: 0.46 });
    await expect(mapWork).toContainText('Selected');
    await expect(page.getByTitle('Selected place location preview')).toHaveCount(1);
    await expectLoadedImages(page.locator('[data-coordinate-preview-marker]'));
    await expect(page.locator('[data-coordinate-preview-marker]')).toHaveAttribute('src', /\/icons\/wayfarer-map-icons\/dist\/png\/marker\/bg-blue\/marker\.png$/);
    await captureEvidence(page, testInfo, 'pick-on-map-preview-marker');
    await expect(mapWork.getByRole('button', { name: 'Done' })).toBeEnabled();
    expect(mutations(), 'Done has not been clicked and no mutation should have run.').toEqual([]);

    await mapWork.getByRole('button', { name: 'Done' }).click();
    await expect(form.getByLabel('Latitude')).not.toHaveValue('');
    await expect(form.getByLabel('Longitude')).not.toHaveValue('');
    await expect(page.getByTitle('Selected place location preview')).toHaveCount(0);
    await expect.poll(() => mapCursor(page)).toBe(normalCursor);
    expect(mutations(), 'Done must not call create/update/order/delete endpoints.').toEqual([]);
    expect(forbidden(), 'Coordinate picking must not call geocode/search providers.').toEqual([]);
  });

  test('Pick on map cancels active distance measurement before handling map clicks', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithCoordinateFixture(page);

    await openEditablePlace(page);
    const measureButton = utilityButton(page, 'Measure distance');
    await measureButton.click();
    await expect(measureButton).toHaveClass(/active/);
    await clickMap(page, { xRatio: 0.32, yRatio: 0.48 });

    await page.getByRole('button', { name: 'Pick on map' }).click();
    await expect(measureButton).not.toHaveClass(/active/);
    await clickMap(page, { xRatio: 0.48, yRatio: 0.42 });

    const mapWork = page.getByRole('region', { name: 'Map work' });
    await expect(mapWork).toContainText('Selected');
    await expect(mapWork.getByRole('button', { name: 'Done' })).toBeEnabled();
    await expect(page.getByTitle('Selected place location preview')).toHaveCount(1);
    await expect(page.locator('.trip-editor-map-distance-label')).toHaveCount(0);
  });

  test('edit-place docked Cancel restores coordinate fields only', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithCoordinateFixture(page);
    const forbidden = watchForbiddenPickRequests(page);

    await openEditablePlace(page);
    const form = page.locator('#trip-editor-place-form');
    await form.getByLabel('Name').fill('Unsaved coordinate test name');
    await form.getByLabel('Address').fill('Unsaved coordinate test address');
    const before = await draftCoordinates(page);

    await page.getByRole('button', { name: 'Pick on map' }).click();
    await clickMap(page, { xRatio: 0.64, yRatio: 0.38 });
    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Cancel' }).click();
    const dialog = page.getByRole('dialog', { name: 'Discard map editing changes?' });
    await expect(dialog).toBeVisible();
    await dialog.getByRole('button', { name: 'Discard' }).click();

    await expectDraftCoordinates(page, before);
    await expect(form.getByLabel('Name')).toHaveValue('Unsaved coordinate test name');
    await expect(form.getByLabel('Address')).toHaveValue('Unsaved coordinate test address');
    expect(forbidden(), 'Canceling coordinate pick must not call geocode/search providers.').toEqual([]);
  });

  test('Pick on map keeps the docked place editor stable in the sidebar', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await signIn(page);
    await loadWorkspaceWithCoordinateFixture(page);

    await openEditablePlace(page);
    await page.locator('.trip-editor-place-editor-row .trip-editor-surface--docked').evaluate(element => {
      element.scrollIntoView({ block: 'center', inline: 'nearest' });
    });
    const before = await sidebarEditorState(page);
    expect(before.contextVisible, 'The active place editor should start visible before map-work.').toBe(true);
    await expect(formNameField(page)).toHaveValue(editablePlaceName);
    await expect(page.locator('#trip-editor-place-form').getByLabel('Latitude')).toHaveValue('10');

    await page.getByRole('button', { name: 'Pick on map' }).click();
    await expect(page.getByRole('region', { name: 'Map work' })).toContainText('Pick place location');
    const after = await sidebarEditorState(page);

    expect(Math.abs(after.scrollTop - before.scrollTop), 'Starting coordinate map-work should not move the sidebar enough to hide the active editor.').toBeLessThanOrEqual(96);
    expect(after.contextVisible, 'The active place editor context should remain visible while map-work is active.').toBe(true);
    expect(after.contextTop, 'The active place editor context should stay inside the sidebar viewport.').toBeGreaterThanOrEqual(after.sidebarTop - 1);
    expect(after.contextBottom, 'The active place editor context should stay inside the sidebar viewport.').toBeLessThanOrEqual(after.sidebarBottom + 1);
    await expect(page.locator('#trip-editor-place-form')).toBeVisible();
    await expect(formNameField(page)).toBeVisible();
    await expect(formNameField(page)).toHaveValue(editablePlaceName);
    await expect(page.locator('#trip-editor-place-form').getByLabel('Latitude')).toBeVisible();
    await expect(page.locator('#trip-editor-place-form').getByLabel('Latitude')).toHaveValue('10');
    await expect(page.getByRole('button', { name: 'Save Place' })).toBeDisabled();
  });

  test('edit-place Done applies the draft coordinate and moves the selected marker', async ({ page }) => {
    await useMapWorkViewport(page);
    await signIn(page);
    await loadWorkspaceWithCoordinateFixture(page);
    const mutations = watchEditorMutations(page);

    await openEditablePlace(page);
    await expectDraftCoordinates(page, { latitude: '10', longitude: '20' });
    const originalAnchor = await markerAnchor(page.getByTitle(editablePlaceName));

    await page.getByRole('button', { name: 'Pick on map' }).click();
    await clickMap(page, { xRatio: 0.58, yRatio: 0.44 });
    const previewAnchor = await markerAnchor(page.getByTitle('Selected place location preview'));

    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Done' }).click();
    const form = page.locator('#trip-editor-place-form');
    await expect(form.getByLabel('Latitude')).not.toHaveValue('10');
    await expect(form.getByLabel('Longitude')).not.toHaveValue('20');
    await expect(page.getByTitle('Selected place location preview')).toHaveCount(0);
    await expect(page.locator(`.trip-editor-map-marker--selected[title="${editablePlaceName}"]`)).toBeVisible();

    const selectedAnchor = await markerAnchor(page.getByTitle(editablePlaceName));
    expect(Math.hypot(selectedAnchor.x - originalAnchor.x, selectedAnchor.y - originalAnchor.y), 'Selected marker should move away from the saved coordinate.').toBeGreaterThan(20);
    expect(Math.abs(selectedAnchor.x - previewAnchor.x), 'Selected marker should move to the picked preview x-position.').toBeLessThanOrEqual(3);
    expect(Math.abs(selectedAnchor.y - previewAnchor.y), 'Selected marker should move to the picked preview y-position.').toBeLessThanOrEqual(16);
    expect(mutations(), 'Done must move only the client draft marker and must not persist.').toEqual([]);
  });

  test('edit-place expanded enters map-work and returns to expanded surface', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithCoordinateFixture(page);

    await openEditablePlace(page);
    await page.getByRole('button', { name: 'Expand Editor' }).click();
    const expanded = page.getByRole('dialog', { name: new RegExp(`Edit Place - ${editablePlaceName}`) });
    await expect(expanded).toBeVisible();

    await expanded.getByRole('button', { name: 'Pick on map' }).click();
    const mapWork = page.getByRole('region', { name: 'Map work' });
    await expect(mapWork).toContainText('Selected 10, 20');
    await expect(mapWork.getByRole('button', { name: 'Done' })).toBeEnabled();
    await expect(expanded).toHaveCount(0);

    await mapWork.getByRole('button', { name: 'Done' }).click();
    await expect(page.getByRole('dialog', { name: new RegExp(`Edit Place - ${editablePlaceName}`) })).toBeVisible();
  });

  test('active map-work consumes persisted place marker clicks without opening popups or switching editors', async ({ page }) => {
    await useMapWorkViewport(page);
    await signIn(page);
    await loadWorkspaceWithCoordinateFixture(page);
    const mutations = watchEditorMutations(page);
    const forbidden = watchForbiddenPickRequests(page);

    await openEditablePlace(page);
    const form = page.locator('#trip-editor-place-form');
    await expectDraftCoordinates(page, { latitude: '10', longitude: '20' });

    await page.getByRole('button', { name: 'Pick on map' }).click();
    const mapWork = page.getByRole('region', { name: 'Map work' });
    await expect(mapWork).toContainText('Selected 10, 20');

    await clickMarkerByTitle(page, secondPlaceName);
    await expect(page.locator('.leaflet-popup')).toHaveCount(0);
    await expect(mapWork).toContainText(`Edit Place - ${editablePlaceName}`);
    await expect(mapWork).not.toContainText(`Edit Place - ${secondPlaceName}`);
    await expect(form).toBeVisible();
    await expect(form.getByLabel('Name')).toHaveValue(editablePlaceName);
    await expect(mapWork).toContainText('Selected 11, 21');
    expect(mutations(), 'Marker click during pick mode must not call editor mutations.').toEqual([]);

    await mapWork.getByRole('button', { name: 'Done' }).click();
    await expectDraftCoordinates(page, { latitude: '11', longitude: '21' });
    expect(mutations(), 'Done after marker pick must still be draft-only.').toEqual([]);
    expect(forbidden(), 'Marker-based coordinate picking must not call geocode/search providers.').toEqual([]);
    await expect(form.getByLabel('Name')).toHaveValue(editablePlaceName);
  });

  test('dirty map-work switch prompts before the normal dirty draft prompt', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithCoordinateFixture(page);

    await openEditablePlace(page);
    await page.locator('#trip-editor-place-form').getByLabel('Name').fill('Unsaved switch name');
    await page.getByRole('button', { name: 'Pick on map' }).click();
    await clickMap(page, { xRatio: 0.54, yRatio: 0.34 });

    await page.getByRole('button', { name: 'Add Region' }).click();
    const mapDiscard = page.getByRole('dialog', { name: 'Discard map editing changes?' });
    await expect(mapDiscard).toBeVisible();
    await mapDiscard.getByRole('button', { name: 'Keep editing' }).click();
    await expect(page.getByRole('region', { name: 'Map work' })).toBeVisible();

    await page.getByRole('button', { name: 'Add Region' }).click();
    await page.getByRole('dialog', { name: 'Discard map editing changes?' }).getByRole('button', { name: 'Discard' }).click();
    const draftDiscard = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(draftDiscard).toBeVisible();
    await draftDiscard.getByRole('button', { name: 'Keep editing' }).click();
    await expect(page.getByRole('heading', { name: new RegExp(`Edit Place - ${editablePlaceName}`) })).toBeVisible();
    await expect(page.locator('#trip-editor-place-form').getByLabel('Name')).toHaveValue('Unsaved switch name');
  });

  test('Save after Done sends the picked coordinate through the existing place endpoint', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithCoordinateFixture(page);
    const forbidden = watchForbiddenPickRequests(page);
    const savedRequests: Array<Record<string, any>> = [];
    await page.route(editorApiMatcher, async route => {
      const request = route.request();
      if (request.method() === 'GET') {
        await route.fallback();
        return;
      }

      if (request.method() !== 'PUT' || !request.url().endsWith(`/places/${editablePlaceId}`)) {
        throw new Error(`Unexpected editor mutation ${request.method()} ${request.url()}`);
      }

      const body = request.postDataJSON() as Record<string, any>;
      savedRequests.push(body);
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(editorMutationResult({ ...body, id: editablePlaceId }))
      });
    });

    await openEditablePlace(page);
    await page.getByRole('button', { name: 'Pick on map' }).click();
    await clickMap(page, { xRatio: 0.42, yRatio: 0.42 });
    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Done' }).click();
    expect(savedRequests, 'Done should not save before Save Place is clicked.').toEqual([]);

    const picked = await draftCoordinates(page);
    await page.getByRole('button', { name: 'Save Place' }).click();
    await expect.poll(() => savedRequests.length).toBe(1);
    expect(String(savedRequests[0].location.latitude)).toBe(picked.latitude);
    expect(String(savedRequests[0].location.longitude)).toBe(picked.longitude);
    expect(forbidden(), 'Saving a picked coordinate must not call geocode/search providers.').toEqual([]);
  });
});

async function loadWorkspaceWithCoordinateFixture(page: Page): Promise<void> {
  await page.unroute(editorApiMatcher).catch(() => undefined);
  const state = await loadEditorStateFixture(page) as MutableEditorState;
  prepareCoordinateState(state);
  await page.route(editorApiMatcher, async route => routeEditorReadOnly(route, state));
  await page.goto(absoluteUrl(editorPath));
  await expectMountedWorkspace(page);
}

async function routeEditorReadOnly(route: Route, state: MutableEditorState): Promise<void> {
  if (route.request().method() !== 'GET') {
    throw new Error(`Unexpected editor mutation ${route.request().method()} ${route.request().url()}`);
  }

  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
}

function prepareCoordinateState(state: MutableEditorState): void {
  const region = Object.values(state.regionsById).find((item: any) => !item.isShadow) as any;
  if (!region) {
    throw new Error('Configured Trip Editor fixture must contain a normal region.');
  }

  state.placesById[editablePlaceId] = placeFixture(state, region.id, editablePlaceId, editablePlaceName, { latitude: 10, longitude: 20 });
  state.placesById[secondPlaceId] = placeFixture(state, region.id, secondPlaceId, secondPlaceName, { latitude: 11, longitude: 21 });
  state.placeOrderByRegionId[region.id] = [editablePlaceId, secondPlaceId, ...(state.placeOrderByRegionId[region.id] ?? []).filter((id: string) => id !== editablePlaceId && id !== secondPlaceId)];
}

function placeFixture(state: MutableEditorState, regionId: string, id: string, name: string, location: Coordinate): Record<string, any> {
  return {
    id,
    tripId: state.tripId,
    regionId,
    name,
    notesHtml: '',
    address: 'Coordinate fixture address',
    location,
    iconName: state.options.iconNames[0] ?? 'marker',
    markerColor: state.options.markerColorClasses[0] ?? 'bg-blue',
    displayOrder: 1,
    visitSummary: { placeId: id, visitCount: 0, isVisited: false, firstVisitAt: null, lastVisitAt: null },
    capabilities: editableCapabilities()
  };
}

function editableCapabilities(): Record<string, boolean> {
  return {
    canEdit: true,
    canRename: true,
    canDelete: true,
    canReorder: true,
    canMove: true,
    canAddChildren: true,
    canTargetForSearchAdd: false
  };
}

async function openEditablePlace(page: Page): Promise<void> {
  await firstEditableRegion(page).getByText(editablePlaceName).locator('xpath=ancestor::li[contains(@class, "trip-editor-place-row")]').getByRole('button', { name: 'Edit', exact: true }).click();
  await expect(page.getByRole('heading', { name: new RegExp(`Edit Place - ${editablePlaceName}`) })).toBeVisible();
}

function formNameField(page: Page): Locator {
  return page.locator('#trip-editor-place-form').getByLabel('Name');
}

function firstEditableRegion(page: Page) {
  return page.locator('.trip-editor-region-card--normal').first();
}

async function useMapWorkViewport(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1280, height: 1000 });
}

async function clickMap(page: Page, position: { xRatio: number; yRatio: number }): Promise<void> {
  const map = page.getByLabel('Read-only trip map');
  await map.evaluate((element, point) => {
    const box = element.getBoundingClientRect();
    const clientX = box.left + box.width * point.xRatio;
    const clientY = box.top + box.height * point.yRatio;
    for (const type of ['mousedown', 'mouseup', 'click']) {
      element.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true, clientX, clientY, view: window }));
    }
  }, position);
}

async function mapCursor(page: Page): Promise<string> {
  return page.getByLabel('Read-only trip map').evaluate(element => getComputedStyle(element).cursor);
}

function utilityButton(page: Page, name: string): Locator {
  return page.locator('.trip-editor-map-utilities').getByRole('button', { name });
}

// Dispatches through the persisted marker element when viewport chrome covers Playwright's default click point.
async function clickMarkerByTitle(page: Page, title: string): Promise<void> {
  const marker = page.getByTitle(title);
  await expect(marker).toBeVisible();
  await marker.evaluate(element => {
    for (const type of ['mousedown', 'mouseup', 'click']) {
      element.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true, view: window }));
    }
  });
}

async function draftCoordinates(page: Page): Promise<{ latitude: string; longitude: string }> {
  const form = page.locator('#trip-editor-place-form');
  return {
    latitude: await form.getByLabel('Latitude').inputValue(),
    longitude: await form.getByLabel('Longitude').inputValue()
  };
}

async function expectDraftCoordinates(page: Page, values: { latitude: string; longitude: string }): Promise<void> {
  const form = page.locator('#trip-editor-place-form');
  await expect(form.getByLabel('Latitude')).toHaveValue(values.latitude);
  await expect(form.getByLabel('Longitude')).toHaveValue(values.longitude);
}

async function markerAnchor(locator: Locator): Promise<{ x: number; y: number }> {
  const box = await locator.boundingBox();
  expect(box, 'Expected marker element to have a rendered box.').not.toBeNull();
  return { x: box!.x + box!.width / 2, y: box!.y + box!.height };
}

async function expectLoadedImages(images: Locator): Promise<void> {
  const count = await images.count();
  expect(count, 'Expected at least one image to validate.').toBeGreaterThan(0);
  for (let index = 0; index < count; index += 1) {
    await expect.poll(async () => images.nth(index).evaluate(image => image instanceof HTMLImageElement && image.complete && image.naturalWidth > 0 && image.naturalHeight > 0)).toBe(true);
  }
}

async function expectPickOnMapHelp(page: Page): Promise<void> {
  const pickButton = page.getByRole('button', { name: 'Pick on map' });
  await expect(pickButton).toHaveAttribute('title', "Pick this place's latitude and longitude on the map");
  const describedBy = await pickButton.getAttribute('aria-describedby');
  expect(describedBy, 'Pick on map should expose an accessible help description.').toBeTruthy();
  await expect(page.locator(`#${describedBy}`)).toContainText("Use the map to choose this place's latitude and longitude.");
}

async function captureEvidence(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  await page.screenshot({ fullPage: true, path: testInfo.outputPath('screenshots', `${name}.png`) });
}

async function sidebarEditorState(page: Page): Promise<{
  contextBottom: number;
  contextTop: number;
  contextVisible: boolean;
  scrollTop: number;
  sidebarBottom: number;
  sidebarTop: number;
}> {
  return await page.evaluate(() => {
    const sidebar = document.querySelector<HTMLElement>('.trip-editor-sidebar');
    const context = document.querySelector<HTMLElement>('.trip-editor-place-editor-row .trip-editor-surface-context, .trip-editor-place-editor-row .trip-editor-surface--docked');
    const sidebarBox = sidebar?.getBoundingClientRect();
    const contextBox = context?.getBoundingClientRect();
    return {
      contextBottom: contextBox?.bottom ?? 0,
      contextTop: contextBox?.top ?? 0,
      contextVisible: Boolean(contextBox && sidebarBox && contextBox.bottom > sidebarBox.top && contextBox.top < sidebarBox.bottom),
      scrollTop: sidebar?.scrollTop ?? 0,
      sidebarBottom: sidebarBox?.bottom ?? 0,
      sidebarTop: sidebarBox?.top ?? 0
    };
  });
}

function watchEditorMutations(page: Page): () => string[] {
  const urls: string[] = [];
  page.on('request', request => {
    if (editorApiMatcher.test(request.url()) && request.method() !== 'GET') {
      urls.push(`${request.method()} ${request.url()}`);
    }
  });
  return () => urls;
}

function watchForbiddenPickRequests(page: Page): () => string[] {
  const urls: string[] = [];
  page.on('request', request => {
    if (forbiddenPickRequest.test(request.url())) {
      urls.push(request.url());
    }
  });
  return () => urls;
}

function editorMutationResult(place: Record<string, any>): Record<string, any> {
  return {
    success: true,
    data: {
      ...place,
      tripId: '00000000-0000-0000-0000-000000000000',
      visitSummary: { placeId: editablePlaceId, visitCount: 0, isVisited: false, firstVisitAt: null, lastVisitAt: null },
      capabilities: editableCapabilities()
    },
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
      options: null
    },
    deletedIds: { regions: [], places: [], areas: [], segments: [], tags: [] },
    warnings: []
  };
}
