import { expect, test, type Page, type TestInfo } from '@playwright/test';
import {
  absoluteUrl,
  editorApiPath,
  expectInitializedTripMap,
  expectMountedWorkspace,
  expectNoSearchAddUi,
  loadEditorStateFixture,
  regionCard,
  regionEditButton,
  signIn,
  editorPath
} from './tripEditorTestUtils';

type MutableEditorState = Record<string, any>;

test.describe.serial('Trip Editor map navigation toolbar', () => {
  test('renders real commands without mutating metadata drafts', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expectMountedWorkspace(page);

    const toolbar = page.locator('.trip-editor-toolbar');
    await expect(toolbar).toBeVisible();
    await expect(toolbar.getByRole('button', { name: 'Fit All' })).toBeVisible();
    await expect(toolbar.getByRole('button', { name: 'Recenter Saved Trip View' })).toBeVisible();
    await expect(toolbar.getByRole('button', { name: 'Focus Active Entity' })).toBeVisible();
    await expectInitializedTripMap(page);
    await expectNoSearchAddUi(page);

    const before = await metadataMapFieldValues(page);
    const fitAll = toolbar.getByRole('button', { name: 'Fit All' });
    if (await fitAll.isEnabled()) {
      await fitAll.click();
      await expect(toolbar.locator('.trip-editor-toolbar__status')).toContainText('Fit all geometry');
    } else {
      await expect(fitAll).toBeDisabled();
    }

    const recenter = toolbar.getByRole('button', { name: 'Recenter Saved Trip View' });
    if (await recenter.isEnabled()) {
      await recenter.click();
      await expect(toolbar.locator('.trip-editor-toolbar__status')).toContainText('Recentered saved trip view');
    } else {
      await expect(recenter).toBeDisabled();
    }

    const focus = toolbar.getByRole('button', { name: 'Focus Active Entity' });
    if (await focus.isEnabled()) {
      await focus.click();
      await expect(toolbar.locator('.trip-editor-toolbar__status')).toContainText('Focused trip map');
    }

    await expectMetadataMapFieldValues(page, before);
    await expect(page.locator('.trip-editor-surface--docked .trip-editor-save-state').first()).toContainText('Saved');

    await page.locator('.trip-editor-surface--docked').getByRole('button', { name: 'Close' }).click();
    await expect(toolbar.getByRole('button', { name: 'Focus Active Entity' })).toBeDisabled();
  });

  test('availability follows saved view and empty geometry state', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithEditorState(page, state => {
      state.metadata.center = { latitude: 37.9838, longitude: 23.7275 };
      state.metadata.zoom = 8;
    });

    await expect(page.locator('.trip-editor-toolbar').getByRole('button', { name: 'Recenter Saved Trip View' })).toBeEnabled();

    await loadWorkspaceWithEditorState(page, state => {
      clearNavigationGeometry(state);
      state.metadata.center = null;
      state.metadata.zoom = null;
    });

    const toolbar = page.locator('.trip-editor-toolbar');
    await expect(toolbar.getByRole('button', { name: 'Fit All' })).toBeDisabled();
    await expect(toolbar.getByRole('button', { name: 'Recenter Saved Trip View' })).toBeDisabled();
    await expect(toolbar.getByRole('button', { name: 'Focus Active Entity' })).toBeDisabled();
  });

  test('initial map load uses URL view before saved view and fit-bounds fallback', async ({ page }, testInfo) => {
    await signIn(page);
    await loadWorkspaceWithEditorState(page, state => {
      preparePlaceLocationFocusState(state);
      state.metadata.center = { latitude: -33.8688, longitude: 151.2093 };
      state.metadata.zoom = 4;
    }, `${editorPath}?lat=12.3456&lng=45.6789&zoom=2`);
    await expectMapViewNear(page, { latitude: 12.3456, longitude: 45.6789, zoom: 2 });

    await loadWorkspaceWithEditorState(page, state => {
      preparePlaceLocationFocusState(state);
      state.metadata.center = { latitude: 37.9838, longitude: 23.7275 };
      state.metadata.zoom = 9;
    });
    await expectMapViewNear(page, { latitude: 37.9838, longitude: 23.7275, zoom: 9 });
    await captureEvidence(page, testInfo, 'saved-view-load');

    await loadWorkspaceWithEditorState(page, state => {
      clearNavigationGeometry(state);
      state.metadata.center = null;
      state.metadata.zoom = null;
      const region = normalRegion(state);
      test.skip(!region, 'Configured Trip Editor fixture has no normal region for fit-bounds fallback coverage.');
      addAreaGeometry(state, region!.id, '00000000-0000-0000-0000-000000260104', 'PW initial fit fallback area');
    });
    await expectMapCenterInRange(page, { minLatitude: 36.8, maxLatitude: 38.2, minLongitude: 22.8, maxLongitude: 24.2 });
    await captureEvidence(page, testInfo, 'fit-bounds-fallback-load');
  });

  test('metadata focus falls back to Fit All when saved trip view is missing', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithEditorState(page, state => {
      clearNavigationGeometry(state);
      state.metadata.center = null;
      state.metadata.zoom = null;
      const region = normalRegion(state);
      test.skip(!region, 'Configured Trip Editor fixture has no normal region for metadata fallback geometry.');
      addAreaGeometry(state, region!.id, '00000000-0000-0000-0000-000000260101', 'PW metadata fallback area');
    });

    const toolbar = page.locator('.trip-editor-toolbar');
    const focus = toolbar.getByRole('button', { name: 'Focus Active Entity' });
    await expect(toolbar.getByRole('button', { name: 'Recenter Saved Trip View' })).toBeDisabled();
    await expect(focus).toBeEnabled();

    await focus.click();
    await expect(toolbar.locator('.trip-editor-toolbar__status')).toContainText('Focused trip map');
    await expectUsableMapView(page);
  });

  test('focuses active region and place targets without inventing coordinates', async ({ page }) => {
    await signIn(page);
    const fixture = await loadWorkspaceWithEditorState(page, state => prepareNavigationFocusState(state));
    const toolbar = page.locator('.trip-editor-toolbar');
    const focus = toolbar.getByRole('button', { name: 'Focus Active Entity' });
    const region = regionCard(page, fixture.regionName);

    await regionEditButton(region).click();
    await expect(focus).toBeEnabled();
    await focus.click();
    await expect(toolbar.locator('.trip-editor-toolbar__status')).toContainText('Focused region');

    await region.getByText(fixture.placeName).locator('xpath=ancestor::li[contains(@class, "trip-editor-place-row")]').getByRole('button', { name: 'Edit', exact: true }).click();
    await expect(focus).toBeDisabled();

    await region.getByRole('button', { name: 'Add Place' }).click();
    await expect(page.getByRole('heading', { name: 'Add Place' })).toBeVisible();
    const before = await placeDraftCoordinateValues(page);
    await expect(focus).toBeEnabled();
    await focus.click();
    await expect(toolbar.locator('.trip-editor-toolbar__status')).toContainText('Focused parent region');
    await expectPlaceDraftCoordinateValues(page, before);

    await page.getByRole('button', { name: 'Add Region' }).click();
    await expect(page.getByRole('heading', { name: 'Add Region' })).toBeVisible();
    await expect(focus).toBeDisabled();
  });

  test('focuses a saved place location from the edit surface', async ({ page }) => {
    await signIn(page);
    const fixture = await loadWorkspaceWithEditorState(page, state => preparePlaceLocationFocusState(state));
    const toolbar = page.locator('.trip-editor-toolbar');

    await toolbar.getByRole('button', { name: 'Recenter Saved Trip View' }).click();
    const before = await readMapView(page);

    const region = regionCard(page, fixture.regionName);
    await region.getByText(fixture.placeName).locator('xpath=ancestor::li[contains(@class, "trip-editor-place-row")]').getByRole('button', { name: 'Edit', exact: true }).click();
    await expect(toolbar.getByRole('button', { name: 'Focus Active Entity' })).toBeEnabled();
    await toolbar.getByRole('button', { name: 'Focus Active Entity' }).click();

    await expect(toolbar.locator('.trip-editor-toolbar__status')).toContainText('Focused place');
    await expectMapViewChanged(page, before, 'Place focus should move away from the intentionally distinct saved trip view.');
    await expectUsableMapView(page);
  });

  test('fits segment route geometry and endpoint fallback geometry', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithEditorState(page, state => prepareSegmentGeometryState(state, true));
    const toolbar = page.locator('.trip-editor-toolbar');

    await toolbar.getByRole('button', { name: 'Recenter Saved Trip View' }).click();
    const routeBefore = await readMapView(page);
    await toolbar.getByRole('button', { name: 'Fit All' }).click();
    await expect(toolbar.locator('.trip-editor-toolbar__status')).toContainText('Fit all geometry');
    await expectMapViewChanged(page, routeBefore, 'Fit All should include explicit segment route geometry.');
    await expectRenderedRouteGeometry(page);

    await loadWorkspaceWithEditorState(page, state => prepareSegmentGeometryState(state, false));
    await toolbar.getByRole('button', { name: 'Recenter Saved Trip View' }).click();
    const fallbackBefore = await readMapView(page);
    await toolbar.getByRole('button', { name: 'Fit All' }).click();
    await expect(toolbar.locator('.trip-editor-toolbar__status')).toContainText('Fit all geometry');
    await expectMapViewChanged(page, fallbackBefore, 'Fit All should include segment endpoint fallback geometry.');
    await expectRenderedRouteGeometry(page);
    await expectUsableMapView(page);
  });

  test('focuses region center when it is the only region geometry', async ({ page }) => {
    await signIn(page);
    const fixture = await loadWorkspaceWithEditorState(page, state => prepareRegionCenterOnlyState(state));
    const toolbar = page.locator('.trip-editor-toolbar');

    await toolbar.getByRole('button', { name: 'Recenter Saved Trip View' }).click();
    const before = await readMapView(page);

    await regionEditButton(regionCard(page, fixture.regionName)).click();
    await expect(toolbar.getByRole('button', { name: 'Focus Active Entity' })).toBeEnabled();
    await toolbar.getByRole('button', { name: 'Focus Active Entity' }).click();

    await expect(toolbar.locator('.trip-editor-toolbar__status')).toContainText('Focused region');
    await expectMapViewChanged(page, before, 'Region focus should move to the region center-only geometry.');
    await expectUsableMapView(page);
  });

  test('defers to map-work toolbar', async ({ page }) => {
    await signIn(page);
    const fixture = await loadWorkspaceWithEditorState(page, state => {
      const region = normalRegion(state);
      test.skip(!region, 'Configured Trip Editor fixture has no normal region for map-work toolbar coverage.');
      return { regionName: region!.name };
    });

    await regionCard(page, fixture.regionName).getByRole('button', { name: 'Add Place' }).click();
    await expect(page.getByRole('heading', { name: 'Add Place' })).toBeVisible();
    await page.getByRole('button', { name: 'Pick on map' }).click();

    const toolbar = page.locator('.trip-editor-toolbar');
    await expect(toolbar.getByRole('button', { name: 'Fit All' })).toHaveCount(0);
    await expect(toolbar.getByRole('button', { name: 'Recenter Saved Trip View' })).toHaveCount(0);
    await expect(toolbar.getByRole('button', { name: 'Focus Active Entity' })).toHaveCount(0);

    const mapWork = page.getByRole('region', { name: 'Map work' });
    await expect(mapWork).toContainText('Pick place location');
    await expect(mapWork.getByRole('button', { name: 'Done' })).toBeVisible();
    await expect(mapWork.getByRole('button', { name: 'Cancel' })).toBeVisible();
    await clickMap(page, { xRatio: 0.42, yRatio: 0.46 });
    await expect(mapWork).toContainText('Selected');
    await expect(mapWork.getByRole('button', { name: 'Done' })).toBeEnabled();
    await mapWork.getByRole('button', { name: 'Done' }).click();
    await expect(toolbar.getByRole('button', { name: 'Fit All' })).toBeVisible();
  });
});

async function loadWorkspaceWithEditorState<T>(page: Page, mutate: (state: MutableEditorState) => T, path = editorPath): Promise<T> {
  await page.unroute(`**${editorApiPath}`).catch(() => undefined);
  const state = await loadEditorStateFixture(page) as MutableEditorState;
  const result = mutate(state);
  // Route only the editor read model so toolbar coverage can vary geometry without mutating runbook data.
  await page.route(`**${editorApiPath}`, async route => {
    if (route.request().method() !== 'GET') {
      throw new Error(`Map navigation fixture route blocked unexpected ${route.request().method()} ${route.request().url()}`);
    }

    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
  });
  await page.goto(absoluteUrl(path));
  await expectMountedWorkspace(page);
  return result;
}

type MapViewSnapshot = { mapPaneTransform: string; markerTransforms: string[]; pathCount: number; tilePaneTransform: string };

function clearNavigationGeometry(state: MutableEditorState): void {
  Object.values(state.regionsById).forEach((region: any) => {
    region.center = null;
  });
  Object.values(state.placesById).forEach((place: any) => {
    place.location = null;
  });
  Object.values(state.segmentsById).forEach((segment: any) => {
    segment.route = null;
  });
  state.areasById = {};
  state.areaOrderByRegionId = Object.fromEntries(Object.keys(state.areaOrderByRegionId ?? {}).map(regionId => [regionId, []]));
}

function prepareNavigationFocusState(state: MutableEditorState): { placeName: string; regionName: string } {
  clearNavigationGeometry(state);
  state.metadata.center = null;
  state.metadata.zoom = null;

  const region = normalRegion(state);
  if (!region) {
    throw new Error('Configured Trip Editor fixture must contain a normal region for map navigation coverage.');
  }

  addAreaGeometry(state, region.id, '00000000-0000-0000-0000-000000260001', 'PW navigation area');

  const placeId = ensurePlace(state, region.id, '00000000-0000-0000-0000-000000260002', 'PW navigation place');
  state.placesById[placeId].location = null;
  return { placeName: state.placesById[placeId].name, regionName: region.name };
}

function preparePlaceLocationFocusState(state: MutableEditorState): { placeName: string; regionName: string } {
  clearNavigationGeometry(state);
  state.metadata.center = { latitude: -33.8688, longitude: 151.2093 };
  state.metadata.zoom = 4;

  const region = normalRegion(state);
  test.skip(!region, 'Configured Trip Editor fixture has no normal region for place focus coverage.');

  const placeId = ensurePlace(state, region!.id, '00000000-0000-0000-0000-000000260201', 'PW located place');
  state.placesById[placeId].location = { latitude: 48.8566, longitude: 2.3522 };
  return { placeName: state.placesById[placeId].name, regionName: region!.name };
}

function prepareSegmentGeometryState(state: MutableEditorState, useRoute: boolean): void {
  clearNavigationGeometry(state);
  state.metadata.center = { latitude: -33.8688, longitude: 151.2093 };
  state.metadata.zoom = 4;

  const region = normalRegion(state);
  test.skip(!region, 'Configured Trip Editor fixture has no normal region for segment geometry coverage.');

  const fromId = ensurePlace(state, region!.id, '00000000-0000-0000-0000-000000260301', 'PW segment from');
  const toId = ensurePlace(state, region!.id, '00000000-0000-0000-0000-000000260302', 'PW segment to');
  state.placesById[fromId].location = { latitude: 40.7128, longitude: -74.006 };
  state.placesById[toId].location = { latitude: 42.3601, longitude: -71.0589 };

  const segmentId = '00000000-0000-0000-0000-000000260303';
  state.segmentsById = {
    [segmentId]: {
      id: segmentId,
      tripId: state.tripId,
      fromPlaceId: fromId,
      toPlaceId: toId,
      mode: 'car',
      estimatedDistanceKm: null,
      estimatedDurationMinutes: null,
      notesHtml: '',
      route: useRoute
        ? { type: 'LineString', coordinates: [[-74.006, 40.7128], [-73, 41.25], [-71.0589, 42.3601]] }
        : null,
      displayOrder: 1,
      capabilities: editableCapabilities()
    }
  };
  state.segmentOrder = [segmentId];
}

function prepareRegionCenterOnlyState(state: MutableEditorState): { regionName: string } {
  clearNavigationGeometry(state);
  state.metadata.center = { latitude: -33.8688, longitude: 151.2093 };
  state.metadata.zoom = 4;

  const region = normalRegion(state);
  test.skip(!region, 'Configured Trip Editor fixture has no normal region for region center coverage.');
  region!.center = { latitude: 64.1466, longitude: -21.9426 };
  return { regionName: region!.name };
}

function normalRegion(state: MutableEditorState): any | null {
  return Object.values(state.regionsById).find((item: any) => !item.isShadow) ?? null;
}

function addAreaGeometry(state: MutableEditorState, regionId: string, areaId: string, name: string): void {
  state.areasById[areaId] = {
    id: areaId,
    tripId: state.tripId,
    regionId,
    name,
    notesHtml: '',
    fillHex: '#22c55e',
    geometry: { type: 'Polygon', coordinates: [[[23, 37], [24, 37], [24, 38], [23, 38], [23, 37]]] },
    displayOrder: 1,
    capabilities: editableCapabilities()
  };
  state.areaOrderByRegionId[regionId] = [areaId];
}

function ensurePlace(state: MutableEditorState, regionId: string, placeId: string, name: string): string {
  const existingId = state.placeOrderByRegionId[regionId]?.find((id: string) => state.placesById[id]) ?? placeId;
  if (!state.placesById[existingId]) {
    state.placesById[existingId] = {
      id: existingId,
      tripId: state.tripId,
      regionId,
      name,
      notesHtml: '',
      address: '',
      location: null,
      iconName: state.options.iconNames[0] ?? 'marker',
      markerColor: state.options.markerColorClasses[0] ?? 'bg-blue',
      displayOrder: 1,
      visitSummary: { placeId: existingId, visitCount: 0, isVisited: false, firstVisitAt: null, lastVisitAt: null },
      capabilities: editableCapabilities()
    };
    state.placeOrderByRegionId[regionId] = [...(state.placeOrderByRegionId[regionId] ?? []), existingId];
  }

  return existingId;
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

async function metadataMapFieldValues(page: Page): Promise<{ latitude: string; longitude: string; zoom: string }> {
  return {
    latitude: await page.getByLabel('Center Latitude').inputValue(),
    longitude: await page.getByLabel('Center Longitude').inputValue(),
    zoom: await page.getByRole('spinbutton', { name: 'Zoom' }).inputValue()
  };
}

async function expectMetadataMapFieldValues(page: Page, values: { latitude: string; longitude: string; zoom: string }): Promise<void> {
  await expect(page.getByLabel('Center Latitude')).toHaveValue(values.latitude);
  await expect(page.getByLabel('Center Longitude')).toHaveValue(values.longitude);
  await expect(page.getByRole('spinbutton', { name: 'Zoom' })).toHaveValue(values.zoom);
}

async function placeDraftCoordinateValues(page: Page): Promise<{ latitude: string; longitude: string }> {
  const form = page.locator('#trip-editor-place-form');
  return {
    latitude: await form.getByLabel('Latitude').inputValue(),
    longitude: await form.getByLabel('Longitude').inputValue()
  };
}

async function expectPlaceDraftCoordinateValues(page: Page, values: { latitude: string; longitude: string }): Promise<void> {
  const form = page.locator('#trip-editor-place-form');
  await expect(form.getByLabel('Latitude')).toHaveValue(values.latitude);
  await expect(form.getByLabel('Longitude')).toHaveValue(values.longitude);
}

async function readMapView(page: Page): Promise<MapViewSnapshot> {
  await expectUsableMapView(page);
  return await page.getByLabel('Read-only trip map').evaluate(map => {
    const readTransform = (selector: string): string => {
      const element = map.querySelector(selector);
      return element ? getComputedStyle(element).transform : '';
    };

    return {
      mapPaneTransform: readTransform('.leaflet-map-pane'),
      markerTransforms: Array.from(map.querySelectorAll<HTMLElement>('.leaflet-marker-icon, .leaflet-interactive')).map(element => `${getComputedStyle(element).transform} ${element.getAttribute('d') ?? ''}`),
      pathCount: map.querySelectorAll('.leaflet-overlay-pane path').length,
      tilePaneTransform: readTransform('.leaflet-tile-pane')
    };
  });
}

async function readMapViewCoordinates(page: Page): Promise<{ latitude: number; longitude: number; zoom: number }> {
  await expectUsableMapView(page);
  return await page.getByLabel('Read-only trip map').evaluate(map => ({
    latitude: Number((map as HTMLElement).dataset.tripEditorMapLat),
    longitude: Number((map as HTMLElement).dataset.tripEditorMapLng),
    zoom: Number((map as HTMLElement).dataset.tripEditorMapZoom)
  }));
}

async function expectMapViewNear(page: Page, expected: { latitude: number; longitude: number; zoom: number }): Promise<void> {
  await expect.poll(async () => readMapViewCoordinates(page)).toEqual({
    latitude: expect.closeTo(expected.latitude, 0.0001),
    longitude: expect.closeTo(expected.longitude, 0.0001),
    zoom: expected.zoom
  });
}

async function expectMapCenterInRange(page: Page, expected: { minLatitude: number; maxLatitude: number; minLongitude: number; maxLongitude: number }): Promise<void> {
  const view = await readMapViewCoordinates(page);
  expect(view.latitude).toBeGreaterThanOrEqual(expected.minLatitude);
  expect(view.latitude).toBeLessThanOrEqual(expected.maxLatitude);
  expect(view.longitude).toBeGreaterThanOrEqual(expected.minLongitude);
  expect(view.longitude).toBeLessThanOrEqual(expected.maxLongitude);
}

async function captureEvidence(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  await page.screenshot({ fullPage: true, path: testInfo.outputPath('screenshots', `${name}.png`) });
}

async function expectMapViewChanged(page: Page, before: MapViewSnapshot, message: string): Promise<void> {
  const after = await readMapView(page);
  expect(JSON.stringify(after), message).not.toBe(JSON.stringify(before));
}

async function expectUsableMapView(page: Page): Promise<void> {
  await expectInitializedTripMap(page);
  const invalidTokens = await page.getByLabel('Read-only trip map').evaluate(map => {
    const viewText = [
      map.getAttribute('class') ?? '',
      ...Array.from(map.querySelectorAll<HTMLElement>('.leaflet-pane, .leaflet-marker-icon, .leaflet-interactive')).map(element => `${element.getAttribute('style') ?? ''} ${element.getAttribute('d') ?? ''}`)
    ].join(' ');
    return /(NaN|Infinity|undefined)/i.test(viewText);
  });
  expect(invalidTokens, 'Leaflet map view should not contain invalid coordinate artifacts.').toBeFalsy();
}

async function expectRenderedRouteGeometry(page: Page): Promise<void> {
  await expect(page.getByLabel('Read-only trip map').locator('.leaflet-overlay-pane path')).not.toHaveCount(0);
}

async function clickMap(page: Page, position: { xRatio: number; yRatio: number }): Promise<void> {
  const map = page.getByLabel('Read-only trip map');
  await map.scrollIntoViewIfNeeded();
  const box = await map.boundingBox();
  expect(box, 'Read-only trip map should be rendered before map-work clicks.').not.toBeNull();
  await page.mouse.click(box!.x + box!.width * position.xRatio, box!.y + box!.height * position.yRatio);
}
