import { expect, test, type Locator, type Page, type Route, type TestInfo } from '@playwright/test';
import {
  absoluteUrl,
  activeEditorCancelButton,
  editorApiPath,
  editorPath,
  expectMountedWorkspace,
  loadEditorStateFixture,
  signIn
} from './tripEditorTestUtils';

type MutableEditorState = Record<string, any>;
type TripEditorContainmentMetrics = {
  bodyHeight: number; documentHeight: number; footerTop: number | null; mapHeight: number;
  sidebarClientHeight: number; sidebarScrollHeight: number; stableOverflow: Array<{ selector: string; overflow: number }>;
  surfaceBodyOverflowY: string; viewportHeight: number; workspaceHeight: number;
};

const regionId = '00000000-0000-0000-0000-000000283101';
const firstPlaceId = '00000000-0000-0000-0000-000000283201';
const secondPlaceId = '00000000-0000-0000-0000-000000283202';
const fallbackPlaceId = '00000000-0000-0000-0000-000000283203';
const firstPlaceName = 'PW marker parity place with a deliberately long sidebar name that must wrap without covering the Edit action';
const secondPlaceName = 'PW marker parity second place';
const fallbackPlaceName = 'PW marker parity fallback marker place';
const externalImageUrl = 'https://images.example.test/pw-rich-note.png';
const editorApiMatcher = new RegExp(`${editorApiPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?:/.*)?$`, 'i');
const tinyPng = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=', 'base64');

test.describe.serial('Trip Editor marker and notes parity', () => {
  test('keeps map markers, sidebar rows, popups, and selection synchronized both directions', async ({ page }, testInfo) => {
    await signIn(page);
    await loadWorkspaceWithMarkerFixture(page);

    await expectLoadedImages(mapMarkerImages(page));
    await expectLoadedImages(page.locator('[data-sidebar-place-icon]'));
    await expectLoadedImages(regionMarkerImages(page));
    await expect(regionMarkerImages(page).first()).toHaveAttribute('src', /\/icons\/wayfarer-map-icons\/dist\/png\/marker\/bg-red\/map\.png$/);
    await expect(markerImage(page, fallbackPlaceId)).toHaveAttribute('src', /\/icons\/wayfarer-map-icons\/dist\/png\/marker\/bg-blue\/marker\.png$/);
    await expect(sidebarRow(page, fallbackPlaceId).locator('[data-sidebar-place-icon]')).toHaveAttribute('src', /\/icons\/wayfarer-map-icons\/dist\/png\/marker\/bg-blue\/marker\.png$/);
    await captureEvidence(page, testInfo, 'region-marker-app-asset');

    await clickMarker(page, firstPlaceId);
    await expectSelectedPlace(page, firstPlaceId);
    await expect(sidebarRow(page, firstPlaceId)).toBeInViewport();
    await expect(page.locator('.trip-editor-toolbar')).toContainText(firstPlaceName);
    await expect(page.locator('.trip-editor-toolbar__status')).toContainText(`Selected place: ${firstPlaceName}`);
    await expect(page.locator('.trip-editor-place-editor-row')).toHaveCount(0);
    await expect(regionCard(page).locator('.trip-editor-area-list')).toHaveCount(0);
    await expectRegionAddActionsAttached(page);
    await expectPlaceIconColumnAligned(page);
    await expect(page.locator('.leaflet-popup')).toContainText(firstPlaceName);
    await expect(page.locator('.leaflet-popup')).toContainText('PW marker parity region');
    await expect(page.locator('.leaflet-popup')).toContainText('Lat: 37.98380');
    await expect(page.locator('.leaflet-popup')).toContainText('Lon: 23.72750');
    await expect(page.locator('.leaflet-popup')).toContainText('Address: Athens, Greece');
    await expect(page.locator('.leaflet-popup')).toContainText('Visits: 2 visits');
    await expect(page.locator('.leaflet-popup')).toContainText('Marker popup rich note');
    await expectPopupSupportsScrolling(page);
    await expectAttribution(page);
    await captureEvidence(page, testInfo, 'map-click-selects-sidebar-status');
    await setTheme(page, 'dark');
    await expectAttribution(page);
    await captureEvidence(page, testInfo, 'dark-selected-marker-popup-attribution');
    await setTheme(page, 'light');

    await sidebarRow(page, secondPlaceId).click();
    await expectSelectedPlace(page, secondPlaceId);
    await expectNotSelected(page, firstPlaceId);
    await expect(page.locator('.trip-editor-toolbar')).toContainText(secondPlaceName);
    await expect(page.locator('.trip-editor-toolbar__status')).toContainText(`Selected place: ${secondPlaceName}`);
    await expect(page.locator('.trip-editor-place-editor-row')).toHaveCount(0);
    await expectRegionAddActionsAttached(page);
    await expectPlaceIconColumnAligned(page);
    await expect(page.locator('.leaflet-popup')).toHaveCount(0);
    await captureEvidence(page, testInfo, 'sidebar-click-selects-map-status');

    await sidebarRow(page, secondPlaceId).click();
    await expectNoSelectedPlace(page);
    await expect(page.locator('.leaflet-popup')).toHaveCount(0);
    await captureEvidence(page, testInfo, 'selected-place-row-click-clears-selection');

    await sidebarRow(page, firstPlaceId).getByRole('button', { name: 'Edit', exact: true }).click();
    await expect(page.getByRole('heading', { name: new RegExp(`Edit Place - ${escapeRegex(firstPlaceName)}`) })).toBeVisible();
    await expectSelectedPlace(page, firstPlaceId);
    await expectNotSelected(page, secondPlaceId);
    await expect(page.locator('.trip-editor-place-editor-row')).toHaveCount(1);
    await expectNonEmptyPlaceEditorRow(page);

    await page.getByLabel('Sidebar search').fill('second place');
    await expectSelectedPlace(page, firstPlaceId);
    await expect(sidebarRow(page, firstPlaceId)).toBeVisible();
    await page.getByLabel('Sidebar search').fill('');

    await page.locator('#trip-editor-place-form').getByLabel('Name').fill(`${firstPlaceName} saved`);
    await page.getByRole('button', { name: 'Save Place' }).click();
    await expect(page.locator('.trip-editor-save-state').filter({ hasText: /Place saved/i }).first()).toBeVisible();
    await expectSelectedPlace(page, firstPlaceId);

    await activeEditorCancelButton(page).click();
    await expectNoSelectedPlace(page);
    await expect(page.locator('.trip-editor-place-editor-row')).toHaveCount(0);
    await expect(page.locator('.leaflet-popup')).toHaveCount(0);
    await captureEvidence(page, testInfo, 'clean-edit-cancel-clears-selection');
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
    await captureEvidence(page, testInfo, 'dirty-marker-selection-discard-selects-target');
  });

  test('clear selection follows the selected place editor dirty contract', async ({ page }, testInfo) => {
    await signIn(page);
    await loadWorkspaceWithMarkerFixture(page);

    await clickMarker(page, firstPlaceId);
    await expectSelectedPlace(page, firstPlaceId);
    await expect(page.locator('.leaflet-popup')).toContainText(firstPlaceName);

    await page.getByRole('button', { name: 'Clear Selection' }).click();
    await expectNoSelectedPlace(page);
    await expect(page.locator('.leaflet-popup')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Clear Selection' })).toHaveCount(0);
    await captureEvidence(page, testInfo, 'selected-place-clear-action-clears-surfaces');

    await sidebarRow(page, firstPlaceId).getByRole('button', { name: 'Edit', exact: true }).click();
    await expect(expectPlaceEditor(page, firstPlaceName)).toBeVisible();
    await page.locator('#trip-editor-place-form').getByLabel('Address').fill('Unsaved dirty clear selection address');

    await page.getByRole('button', { name: 'Clear Selection' }).click();
    const keepDialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(keepDialog).toBeVisible();
    await keepDialog.getByRole('button', { name: 'Keep editing' }).click();

    await expect(expectPlaceEditor(page, firstPlaceName)).toBeVisible();
    await expect(page.locator('#trip-editor-place-form').getByLabel('Address')).toHaveValue('Unsaved dirty clear selection address');
    await expectSelectedPlace(page, firstPlaceId);
    await captureEvidence(page, testInfo, 'dirty-clear-selection-keep-editing');

    await page.getByRole('button', { name: 'Clear Selection' }).click();
    const discardDialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(discardDialog).toBeVisible();
    await discardDialog.getByRole('button', { name: 'Discard' }).click();

    await expect(expectPlaceEditor(page, firstPlaceName)).toHaveCount(0);
    await expectNoSelectedPlace(page);
    await expect(page.locator('.leaflet-popup')).toHaveCount(0);
    await captureEvidence(page, testInfo, 'dirty-clear-selection-discard');
  });

  test('docked return from expanded place editor preserves the selected owning row', async ({ page }, testInfo) => {
    await signIn(page);
    await loadWorkspaceWithMarkerFixture(page);

    await sidebarRow(page, firstPlaceId).getByRole('button', { name: 'Edit', exact: true }).click();
    await expectSelectedPlace(page, firstPlaceId);
    await page.locator('.trip-editor-place-editor-row .trip-editor-surface--docked').getByRole('button', { name: 'Expand Editor' }).click();

    const expanded = page.getByRole('dialog', { name: new RegExp(`Edit Place - ${escapeRegex(firstPlaceName)}`) });
    await expect(expanded).toBeVisible();
    await captureEvidence(page, testInfo, 'expanded-selected-place-editor');

    await expanded.getByRole('button', { name: 'Dock to sidebar' }).click();
    await expect(page.locator('.trip-editor-place-editor-row .trip-editor-surface--docked')).toBeVisible();
    await expectNonEmptyPlaceEditorRow(page);
    await expectEditorRowUnderPlace(page, firstPlaceId);
    await expect(sidebarRow(page, firstPlaceId)).toBeInViewport();
    await expectSelectedPlace(page, firstPlaceId);
    await captureEvidence(page, testInfo, 'expanded-docked-selected-place-preserved');
  });

  test('keeps long place names from overlapping actions', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await signIn(page);
    await loadWorkspaceWithMarkerFixture(page);
    await expectPlaceRowDoesNotOverlapEdit(page, firstPlaceId);

    await page.setViewportSize({ width: 390, height: 900 });
    await page.getByRole('button', { name: 'Regions', exact: true }).click();
    await expectPlaceRowDoesNotOverlapEdit(page, firstPlaceId);

  });

  test('renders notes images through the proxy while sending canonical external image URLs', async ({ page }) => {
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

  test('shows deterministic mocked place save success and error feedback', async ({ page }) => {
    const requests: Record<string, any>[] = [];
    await signIn(page);
    await loadWorkspaceWithMarkerFixture(page, requests);

    await sidebarRow(page, firstPlaceId).getByRole('button', { name: 'Edit', exact: true }).click();
    await page.locator('#trip-editor-place-form').getByLabel('Address').fill('Successful save feedback address');
    await page.getByRole('button', { name: 'Save Place' }).click();
    await expect.poll(() => requests.length).toBe(1);
    const successFeedback = page.locator('.trip-editor-save-state').filter({ hasText: /Place saved/i }).first();
    await expect(successFeedback).toBeVisible();
    await expect(successFeedback).toHaveClass(/text-bg-success.*trip-editor-save-state--success/);
    await expect(successFeedback).not.toHaveClass(/text-bg-info/);
    await expect(page.getByRole('alert')).toHaveCount(0);

    await page.locator('#trip-editor-place-form').getByLabel('Address').fill('Failed save feedback address');
    await failNextPlaceSave(page, 'Injected place save failure.');
    await page.getByRole('button', { name: 'Save Place' }).click();
    const errorFeedback = page.getByRole('alert');
    await expect(errorFeedback).toContainText('Injected place save failure.');
    await expect(errorFeedback).toHaveClass(/trip-editor-form-error/);
    const failedState = page.locator('.trip-editor-save-state').filter({ hasText: 'Save failed' }).first();
    await expect(failedState).toBeVisible();
    await expect(failedState).toHaveClass(/text-bg-danger.*trip-editor-save-state--danger/);
  });

  test('keeps docked place editing contained without expanding the page', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await signIn(page);
    await loadWorkspaceWithMarkerFixture(page);
    const before = await tripEditorContainmentMetrics(page);

    await sidebarRow(page, firstPlaceId).getByRole('button', { name: 'Edit', exact: true }).click();
    await expect(page.getByRole('heading', { name: new RegExp(`Edit Place - ${escapeRegex(firstPlaceName)}`) })).toBeVisible();
    await expectNonEmptyPlaceEditorRow(page);
    await expectTripEditorContainment(page, before);
  });

  test('keeps popup and attribution usable at a narrow viewport', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 390, height: 900 });
    await signIn(page);
    await loadWorkspaceWithMarkerFixture(page);

    await clickMarker(page, firstPlaceId);
    await expectSelectedPlace(page, firstPlaceId);
    await expect(page.locator('.leaflet-popup')).toContainText('PW marker parity region');
    await expectPopupSupportsScrolling(page);
    await expectAttribution(page);
    await expectNoPageOverflow(page);
    await captureEvidence(page, testInfo, 'narrow-popup-attribution-smoke');
  });
});

async function loadWorkspaceWithMarkerFixture(page: Page, requests: Record<string, any>[] = []): Promise<MutableEditorState> {
  await page.unroute(editorApiMatcher).catch(() => undefined);
  // Keep attribution browser proof deterministic without contacting a public tile provider.
  await page.route(/\/Public\/tiles\/\d+\/\d+\/\d+\.png/i, async route => {
    await route.fulfill({ status: 200, contentType: 'image/png', body: tinyPng });
  });
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
    // Mocked place mutations cover frontend request shape and feedback states, not real endpoint persistence.
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
      center: { latitude: 37.95, longitude: 23.7 },
      displayOrder: 1,
      isShadow: false,
      capabilities: editableCapabilities()
    }
  };
  state.regionOrder = [regionId];
  state.placesById = {
    [firstPlaceId]: placeFixture(state, firstPlaceId, firstPlaceName, 'camera', 'bg-blue', { latitude: 37.9838, longitude: 23.7275 }, true),
    [secondPlaceId]: placeFixture(state, secondPlaceId, secondPlaceName, 'star', 'bg-red', { latitude: 38.2, longitude: 24.05 }, false),
    [fallbackPlaceId]: { ...placeFixture(state, fallbackPlaceId, fallbackPlaceName, '', '', { latitude: 38.1, longitude: 23.85 }, false), iconName: '', markerColor: '' }
  };
  state.placeOrderByRegionId = { [regionId]: [firstPlaceId, secondPlaceId, fallbackPlaceId] };
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
    notesHtml: `<p>Marker popup rich note for ${name}. ${'Long popup note content. '.repeat(14)}</p><p><img src="${externalImageUrl}"></p>`,
    address: id === firstPlaceId ? `Athens, Greece. ${'Long address content for popup body scrolling. '.repeat(12)}` : 'Athens, Greece',
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

function regionCard(page: Page): Locator {
  return page.locator(`[data-region-id="${regionId}"]`);
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

async function failNextPlaceSave(page: Page, message: string): Promise<void> {
  await page.route(editorApiMatcher, async route => {
    const request = route.request();
    if (request.method() === 'PUT' && request.url().includes(`/places/${firstPlaceId}`)) {
      await route.fulfill({
        status: 400,
        contentType: 'application/json',
        body: JSON.stringify({ title: message, status: 400, errors: {} })
      });
      return;
    }

    await route.fallback();
  }, { times: 1 });
}

function regionMarkerImages(page: Page): Locator {
  return page.locator('[data-region-marker-icon]');
}

async function expectSelectedPlace(page: Page, placeId: string): Promise<void> {
  await expect(sidebarRow(page, placeId)).toHaveClass(/trip-editor-place-row--active/);
  await expect(markerImage(page, placeId).locator('xpath=ancestor::*[contains(@class, "trip-editor-map-marker")]')).toHaveClass(/trip-editor-map-marker--selected/);
}

async function expectNotSelected(page: Page, placeId: string): Promise<void> {
  await expect(sidebarRow(page, placeId)).not.toHaveClass(/trip-editor-place-row--active/);
  await expect(markerImage(page, placeId).locator('xpath=ancestor::*[contains(@class, "trip-editor-map-marker")]')).not.toHaveClass(/trip-editor-map-marker--selected/);
}

async function expectNoSelectedPlace(page: Page): Promise<void> {
  await expect(sidebarRow(page, firstPlaceId)).not.toHaveClass(/trip-editor-place-row--active/);
  await expect(sidebarRow(page, secondPlaceId)).not.toHaveClass(/trip-editor-place-row--active/);
  await expect(markerImage(page, firstPlaceId).locator('xpath=ancestor::*[contains(@class, "trip-editor-map-marker")]')).not.toHaveClass(/trip-editor-map-marker--selected/);
  await expect(markerImage(page, secondPlaceId).locator('xpath=ancestor::*[contains(@class, "trip-editor-map-marker")]')).not.toHaveClass(/trip-editor-map-marker--selected/);
  await expect(page.locator('.trip-editor-toolbar')).not.toContainText('Selected place');
}

async function expectLoadedImages(images: Locator): Promise<void> {
  const count = await images.count();
  expect(count, 'Expected at least one image to validate.').toBeGreaterThan(0);
  for (let index = 0; index < count; index += 1) {
    await expect.poll(async () => images.nth(index).evaluate(image => image instanceof HTMLImageElement && image.complete && image.naturalWidth > 0 && image.naturalHeight > 0)).toBe(true);
  }
}

async function expectPopupSupportsScrolling(page: Page): Promise<void> {
  const popupWrapper = page.locator('.trip-editor-place-popup__content');
  const popupContent = page.locator('.trip-editor-place-popup__body');
  const popupHeader = page.locator('.trip-editor-place-popup__header');
  const popupFooter = page.locator('.trip-editor-place-popup__footer');
  await expect(popupWrapper).toBeVisible();
  await expect(popupContent).toBeVisible();
  await expect(popupHeader).toBeVisible();
  await expect(popupFooter).toBeVisible();
  await expect.poll(async () => popupContent.evaluate(element => {
    const styles = window.getComputedStyle(element);
    return styles.overflowY === 'auto' && styles.maxHeight !== 'none' && element.scrollHeight > element.clientHeight;
  })).toBe(true);
  await popupWrapper.evaluate(element => {
    element.scrollTop = 0;
  });
  await popupContent.evaluate(element => {
    element.scrollTop = element.scrollHeight;
  });
  await expect.poll(async () => popupWrapper.evaluate(element => element.scrollTop)).toBe(0);
  await expect.poll(async () => popupContent.evaluate(element => element.scrollTop > 0)).toBe(true);
  await expect(popupHeader).toBeVisible();
  await expect(popupFooter).toBeVisible();
}

async function expectAttribution(page: Page): Promise<void> {
  const attribution = page.locator('.leaflet-control-attribution');
  await expect(attribution).toHaveAttribute('aria-label', 'Map attribution');
  await expect(attribution).toHaveAttribute('title', 'Map attribution');
  await expect(attribution).toContainText('Wayfarer');
  await expect(attribution).toContainText('Stef K');
  await expect(attribution).toContainText('Leaflet');
  await expect(attribution).toContainText('OpenStreetMap');
  await expect(attribution.getByRole('link', { name: 'Wayfarer' })).toHaveAttribute('title', 'Powered by Wayfarer, made by Stef');
  await expect(attribution.getByRole('link', { name: 'Stef K' })).toHaveAttribute('title', 'Check my blog');
  const osmLink = attribution.getByRole('link', { name: 'OpenStreetMap', exact: true });
  await expect(osmLink).toHaveCount(1);
  await expect(osmLink).toHaveAttribute('href', 'https://www.openstreetmap.org/copyright');
  await expect(osmLink).toBeVisible();
  await expect.poll(async () => {
    const colors = await attribution.evaluate(element => {
      const styles = window.getComputedStyle(element);
      const link = element.querySelector('a');
      const linkStyles = link ? window.getComputedStyle(link) : null;
      return { background: styles.backgroundColor, foreground: styles.color, link: linkStyles?.color ?? '' };
    });
    return readableColor(colors.foreground, colors.background) && readableColor(colors.link, colors.background);
  }).toBe(true);
}

async function expectNoPageOverflow(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => {
    const viewportWidth = document.documentElement.clientWidth;
    return Math.max(
      document.documentElement.scrollWidth - viewportWidth,
      document.body ? document.body.scrollWidth - viewportWidth : 0
    );
  });
  expect(overflow, 'Popup and attribution should not create horizontal page overflow.').toBeLessThanOrEqual(1);
}

async function tripEditorContainmentMetrics(page: Page): Promise<TripEditorContainmentMetrics> {
  return await page.evaluate(() => {
    const viewportWidth = document.documentElement.clientWidth;
    const sidebar = document.querySelector<HTMLElement>('.trip-editor-sidebar');
    const workspace = document.querySelector<HTMLElement>('.trip-editor-workspace');
    const map = document.querySelector<HTMLElement>('.trip-editor-map');
    const surfaceBody = document.querySelector<HTMLElement>('.trip-editor-place-editor-row .trip-editor-surface__body');
    const footer = document.querySelector<HTMLElement>('body footer, .footer');
    const stableOverflow = ['#trip-editor-app', '.trip-editor-shell', '.trip-editor-workspace']
      .map(selector => ({ selector, overflow: Math.max(0, (document.querySelector<HTMLElement>(selector)?.getBoundingClientRect().right ?? 0) - viewportWidth) }))
      .filter(result => result.overflow > 2);

    return {
      bodyHeight: document.body?.scrollHeight ?? 0,
      documentHeight: document.documentElement.scrollHeight,
      footerTop: footer ? footer.getBoundingClientRect().top : null,
      mapHeight: map?.getBoundingClientRect().height ?? 0,
      sidebarClientHeight: sidebar?.clientHeight ?? 0,
      sidebarScrollHeight: sidebar?.scrollHeight ?? 0,
      stableOverflow,
      surfaceBodyOverflowY: surfaceBody ? window.getComputedStyle(surfaceBody).overflowY : '',
      viewportHeight: window.innerHeight,
      workspaceHeight: workspace?.getBoundingClientRect().height ?? 0
    };
  });
}

async function expectTripEditorContainment(page: Page, before: TripEditorContainmentMetrics): Promise<void> {
  const after = await tripEditorContainmentMetrics(page);
  expect(after.stableOverflow, 'Stable Trip Editor containers should fit within the viewport.').toEqual([]);
  expect(after.documentHeight - before.documentHeight, 'Opening place edit should not expand document height by many viewports.').toBeLessThanOrEqual(80);
  expect(after.bodyHeight - before.bodyHeight, 'Opening place edit should not expand body height by many viewports.').toBeLessThanOrEqual(80);
  expect(after.workspaceHeight, 'Trip Editor workspace should stay bounded by the viewport.').toBeLessThanOrEqual(after.viewportHeight + 1);
  expect(after.mapHeight, 'Trip Editor map should remain usable after opening place edit.').toBeGreaterThan(300);
  expect(after.sidebarScrollHeight, 'Place editor overflow should stay inside the sidebar/editor containers.').toBeGreaterThan(after.sidebarClientHeight);
  expect(after.surfaceBodyOverflowY, 'Docked place editor body should scroll internally.').toBe('auto');
  if (before.footerTop !== null && after.footerTop !== null) {
    expect(after.footerTop - before.footerTop, 'Opening place edit should not push the footer down.').toBeLessThanOrEqual(80);
  }
}

function readableColor(foreground: string, background: string): boolean {
  const foregroundRgb = parseRgb(foreground);
  const backgroundRgb = parseRgb(background);
  if (!foregroundRgb || !backgroundRgb) {
    return false;
  }

  const contrast = (relativeLuminance(foregroundRgb) + 0.05) / (relativeLuminance(backgroundRgb) + 0.05);
  return Math.max(contrast, 1 / contrast) >= 2;
}

function parseRgb(value: string): [number, number, number] | null {
  const rgbMatch = value.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)/);
  if (rgbMatch) {
    return [Number(rgbMatch[1]), Number(rgbMatch[2]), Number(rgbMatch[3])];
  }

  const srgbMatch = value.match(/color\(srgb\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)/);
  return srgbMatch ? [Number(srgbMatch[1]) * 255, Number(srgbMatch[2]) * 255, Number(srgbMatch[3]) * 255] : null;
}

function relativeLuminance([red, green, blue]: [number, number, number]): number {
  const [r, g, b] = [red, green, blue].map(channel => {
    const value = channel / 255;
    return value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
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

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate(value => document.documentElement.setAttribute('data-bs-theme', value), theme);
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

async function expectRegionAddActionsAttached(page: Page): Promise<void> {
  const lastChildRow = sidebarRow(page, fallbackPlaceId);
  const addPlaceButton = regionCard(page).getByRole('button', { name: 'Add Place' });
  await expect(lastChildRow).toBeVisible();
  await expect(addPlaceButton).toBeVisible();

  const [rowBox, addBox] = await Promise.all([lastChildRow.boundingBox(), addPlaceButton.boundingBox()]);
  expect(rowBox, 'Last visible child row should have a rendered bounding box.').not.toBeNull();
  expect(addBox, 'Add Place button should have a rendered bounding box.').not.toBeNull();
  const addActionGap = addBox!.y - (rowBox!.y + rowBox!.height);
  expect(addActionGap, 'Add actions should have compact breathing room above the buttons.').toBeGreaterThanOrEqual(4);
  expect(addActionGap, 'Add actions should stay visually attached to region children without a blank panel gap.').toBeLessThanOrEqual(28);
}

async function expectPlaceIconColumnAligned(page: Page): Promise<void> {
  const firstRow = sidebarRow(page, firstPlaceId);
  const secondRow = sidebarRow(page, secondPlaceId);
  const firstIcon = firstRow.locator('.trip-editor-place-row__icon');
  const secondIcon = secondRow.locator('.trip-editor-place-row__icon');
  const firstImage = firstIcon.locator('img[data-sidebar-place-icon]');
  const secondImage = secondIcon.locator('img[data-sidebar-place-icon]');
  const [firstRowBox, secondRowBox, firstIconBox, secondIconBox, firstImageBox, secondImageBox] = await Promise.all([
    firstRow.boundingBox(),
    secondRow.boundingBox(),
    firstIcon.boundingBox(),
    secondIcon.boundingBox(),
    firstImage.boundingBox(),
    secondImage.boundingBox()
  ]);
  expect(firstRowBox, 'First place row should have a rendered bounding box.').not.toBeNull();
  expect(secondRowBox, 'Second place row should have a rendered bounding box.').not.toBeNull();
  expect(firstIconBox, 'First place icon column should have a rendered bounding box.').not.toBeNull();
  expect(secondIconBox, 'Second place icon column should have a rendered bounding box.').not.toBeNull();
  expect(firstImageBox, 'First place icon should have a rendered bounding box.').not.toBeNull();
  expect(secondImageBox, 'Second place icon should have a rendered bounding box.').not.toBeNull();

  const tolerance = 2;
  const firstColumnOffset = firstIconBox!.x - firstRowBox!.x;
  const secondColumnOffset = secondIconBox!.x - secondRowBox!.x;
  expect(Math.abs(firstColumnOffset - secondColumnOffset), 'Place icon columns should align consistently within their rows.').toBeLessThanOrEqual(tolerance);
  expect(Math.abs(firstImageBox!.x - firstIconBox!.x), 'First place icon should be left-aligned inside its icon column.').toBeLessThanOrEqual(tolerance);
  expect(Math.abs(secondImageBox!.x - secondIconBox!.x), 'Second place icon should be left-aligned inside its icon column.').toBeLessThanOrEqual(tolerance);
}

async function expectNonEmptyPlaceEditorRow(page: Page): Promise<void> {
  const editorRow = page.locator('.trip-editor-place-editor-row');
  await expect(editorRow).toHaveCount(1);
  await expect(editorRow.locator('.trip-editor-surface--docked')).toBeVisible();
  await expect(editorRow.getByRole('heading', { name: new RegExp(`Edit Place - ${escapeRegex(firstPlaceName)}`) })).toBeVisible();
}

async function expectEditorRowUnderPlace(page: Page, placeId: string): Promise<void> {
  const selectedRow = sidebarRow(page, placeId);
  const editorRow = page.locator('.trip-editor-place-editor-row');
  const followingRow = sidebarRow(page, secondPlaceId);
  const [selectedBox, editorBox, followingBox] = await Promise.all([selectedRow.boundingBox(), editorRow.boundingBox(), followingRow.boundingBox()]);
  expect(selectedBox, 'Selected place row should have a rendered bounding box.').not.toBeNull();
  expect(editorBox, 'Docked editor row should have a rendered bounding box.').not.toBeNull();
  expect(followingBox, 'Following place row should have a rendered bounding box.').not.toBeNull();
  expect(editorBox!.y, 'Docked editor row should render below its owning selected place row.').toBeGreaterThanOrEqual(selectedBox!.y + selectedBox!.height);
  expect(followingBox!.y, 'Docked editor row should stay before the next place row.').toBeGreaterThanOrEqual(editorBox!.y + editorBox!.height);
}

function escapeRegex(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
