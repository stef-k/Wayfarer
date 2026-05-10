import { expect, test, type Page } from '@playwright/test';
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
  workspacePath
} from './tripEditorTestUtils';

type MutableEditorState = Record<string, any>;

test.describe.serial('Trip Editor map navigation toolbar', () => {
  test('renders real commands without mutating metadata drafts', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(workspacePath));
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
    await expect(toolbar.locator('.trip-editor-toolbar__status')).toContainText('Focused place');
    await expectPlaceDraftCoordinateValues(page, before);

    await page.getByRole('button', { name: 'Add Region' }).click();
    await expect(page.getByRole('heading', { name: 'Add Region' })).toBeVisible();
    await expect(focus).toBeDisabled();
  });

  test('defers to map-work toolbar', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(workspacePath));
    await expectMountedWorkspace(page);

    await enterMapWorkFromE2e(page);

    const toolbar = page.locator('.trip-editor-toolbar');
    await expect(toolbar.getByRole('button', { name: 'Fit All' })).toHaveCount(0);
    await expect(toolbar.getByRole('button', { name: 'Recenter Saved Trip View' })).toHaveCount(0);
    await expect(toolbar.getByRole('button', { name: 'Focus Active Entity' })).toHaveCount(0);

    const mapWork = page.getByRole('region', { name: 'Map work' });
    await expect(mapWork.getByRole('button', { name: 'Done' })).toBeVisible();
    await expect(mapWork.getByRole('button', { name: 'Cancel' })).toBeVisible();
    await mapWork.getByRole('button', { name: 'Done' }).click();
    await expect(toolbar.getByRole('button', { name: 'Fit All' })).toBeVisible();
  });
});

async function loadWorkspaceWithEditorState<T>(page: Page, mutate: (state: MutableEditorState) => T): Promise<T> {
  await page.unroute(`**${editorApiPath}`).catch(() => undefined);
  const state = await loadEditorStateFixture(page) as MutableEditorState;
  const result = mutate(state);
  await page.route(`**${editorApiPath}`, async route => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
  });
  await page.goto(absoluteUrl(workspacePath));
  await expectMountedWorkspace(page);
  return result;
}

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

  const region = Object.values(state.regionsById).find((item: any) => !item.isShadow) as any;
  if (!region) {
    throw new Error('Configured Trip Editor fixture must contain a normal region for map navigation coverage.');
  }

  const areaId = '00000000-0000-0000-0000-000000260001';
  state.areasById[areaId] = {
    id: areaId,
    tripId: state.tripId,
    regionId: region.id,
    name: 'PW navigation area',
    notesHtml: '',
    fillHex: '#22c55e',
    geometry: { type: 'Polygon', coordinates: [[[23, 37], [24, 37], [24, 38], [23, 38], [23, 37]]] },
    displayOrder: 1,
    capabilities: editableCapabilities()
  };
  state.areaOrderByRegionId[region.id] = [areaId];

  const placeId = state.placeOrderByRegionId[region.id]?.find((id: string) => state.placesById[id]) ?? '00000000-0000-0000-0000-000000260002';
  if (!state.placesById[placeId]) {
    state.placesById[placeId] = {
      id: placeId,
      tripId: state.tripId,
      regionId: region.id,
      name: 'PW navigation place',
      notesHtml: '',
      address: '',
      location: null,
      iconName: state.options.iconNames[0] ?? 'marker',
      markerColor: state.options.markerColorClasses[0] ?? 'bg-blue',
      displayOrder: 1,
      visitSummary: { placeId, visitCount: 0, isVisited: false, firstVisitAt: null, lastVisitAt: null },
      capabilities: editableCapabilities()
    };
    state.placeOrderByRegionId[region.id] = [...(state.placeOrderByRegionId[region.id] ?? []), placeId];
  }

  state.placesById[placeId].location = null;
  return { placeName: state.placesById[placeId].name, regionName: region.name };
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
    zoom: await page.getByLabel('Zoom').inputValue()
  };
}

async function expectMetadataMapFieldValues(page: Page, values: { latitude: string; longitude: string; zoom: string }): Promise<void> {
  await expect(page.getByLabel('Center Latitude')).toHaveValue(values.latitude);
  await expect(page.getByLabel('Center Longitude')).toHaveValue(values.longitude);
  await expect(page.getByLabel('Zoom')).toHaveValue(values.zoom);
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

async function enterMapWorkFromE2e(page: Page): Promise<void> {
  await page.evaluate(async () => {
    const moduleUrl = '/ClientApps/trip-editor/src/composables/useEditorSurface.ts';
    const surface = await import(/* @vite-ignore */ moduleUrl) as {
      enterMapWork: (options: {
        modeName: string;
        instruction: string;
        statusText: string;
        isDirty: () => boolean;
        snapshot: () => unknown;
        rollback: (snapshot: unknown) => void;
        done: () => void;
        cancel: () => void;
      }) => boolean;
    };
    surface.enterMapWork({
      modeName: 'Verify map work',
      instruction: 'Use map-work toolbar actions.',
      statusText: 'Navigation toolbar deferred',
      isDirty: () => false,
      snapshot: () => null,
      rollback: () => undefined,
      done: () => undefined,
      cancel: () => undefined
    });
  });
}
