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
  test('manual Add Place preserves the viewport and seeds one marker at the captured center', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithCoordinateFixture(page);
    const forbidden = watchForbiddenPickRequests(page);

    await page.goto(absoluteUrl(`${editorPath}?lat=37.9715321&lng=23.7257498&zoom=16`));
    await expectMountedWorkspace(page);
    const before = await mapView(page);
    await firstEditableRegion(page).getByRole('button', { name: 'Add Place' }).click();
    const form = page.locator('#trip-editor-place-form');
    await expect(page.getByRole('heading', { name: 'Add Place' })).toBeVisible();
    await expect.poll(() => mapView(page)).toEqual(before);
    await expect.poll(async () => Number(await form.getByLabel('Latitude').inputValue())).toBeCloseTo(before.latitude, 6);
    await expect.poll(async () => Number(await form.getByLabel('Longitude').inputValue())).toBeCloseTo(before.longitude, 6);
    await expect(activeDraftMarkers(page)).toHaveCount(1);
    expect(forbidden(), 'Opening a manual draft must not contact search providers.').toEqual([]);
  });

  test('add-place reuses one marker through Pick and Done without persisting', async ({ page }, testInfo) => {
    await signIn(page);
    await loadWorkspaceWithCoordinateFixture(page);
    const mutations = watchEditorMutations(page);
    const forbidden = watchForbiddenPickRequests(page);

    await firstEditableRegion(page).getByRole('button', { name: 'Add Place' }).click();
    const form = page.locator('#trip-editor-place-form');
    const initial = await draftCoordinates(page);

    await expectPickOnMapHelp(page);
    const normalCursor = await mapCursor(page);
    await page.getByRole('button', { name: 'Pick on map' }).click();
    const mapWork = page.getByRole('region', { name: 'Map work' });
    await expect(mapWork).toContainText('Pick place location');
    await expect(mapWork).toContainText('Selected');
    await expect(mapWork.getByRole('button', { name: 'Done' })).toBeEnabled();
    await expect(form.getByLabel('Latitude')).toBeReadOnly();
    await expect(form.getByLabel('Longitude')).toBeReadOnly();
    await expectNoSearchAddUi(page);
    await expect.poll(() => mapCursor(page)).toBe('default');

    await clickMap(page, { xRatio: 0.38, yRatio: 0.46 });
    await expect(mapWork).toContainText('Selected');
    await expect(activeDraftMarkers(page)).toHaveCount(1);
    await expectLoadedImages(activeDraftMarkers(page));
    await captureEvidence(page, testInfo, 'pick-on-map-preview-marker');
    await expect(mapWork.getByRole('button', { name: 'Done' })).toBeEnabled();
    expect(mutations(), 'Done has not been clicked and no mutation should have run.').toEqual([]);

    await mapWork.getByRole('button', { name: 'Done' }).click();
    await expect(form.getByLabel('Latitude')).not.toHaveValue('');
    await expect(form.getByLabel('Longitude')).not.toHaveValue('');
    await expect(activeDraftMarkers(page)).toHaveCount(1);
    expect(await draftCoordinates(page)).not.toEqual(initial);
    await expect.poll(() => mapCursor(page)).toBe(normalCursor);
    expect(mutations(), 'Done must not call create/update/order/delete endpoints.').toEqual([]);
    expect(forbidden(), 'Coordinate picking must not call geocode/search providers.').toEqual([]);
  });

  test('Pick styling preserves the pending coordinate across Done and coordinate-only Cancel', async ({ page }) => {
    await useMapWorkViewport(page);
    await signIn(page);
    await loadWorkspaceWithCoordinateFixture(page);
    await firstEditableRegion(page).getByRole('button', { name: 'Add Place' }).click();
    const baseline = await draftCoordinates(page);
    const view = await mapView(page);

    await page.getByRole('button', { name: 'Pick on map' }).click();
    await clickMap(page, { xRatio: 0.62, yRatio: 0.39 });
    const pending = await markerCoordinate(page);
    expect(pending).not.toEqual(baseline);
    await selectDraftStyle(page, 'icon');
    await selectDraftStyle(page, 'color');
    await expectMarkerCoordinate(page, pending);
    await expect(activeDraftMarkers(page)).toHaveCount(1);
    await expect.poll(() => mapView(page)).toEqual(view);

    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Done' }).click();
    await expectDraftCoordinates(page, pending);
    const selectedIcon = await selectedDraftStyle(page, 'icon');
    const selectedColor = await selectedDraftStyle(page, 'color');

    await page.getByRole('button', { name: 'Pick on map' }).click();
    await clickMap(page, { xRatio: 0.35, yRatio: 0.64 });
    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Cancel' }).click();
    await page.getByRole('dialog', { name: 'Discard map editing changes?' }).getByRole('button', { name: 'Discard' }).click();
    await expectDraftCoordinates(page, pending);
    await expectMarkerCoordinate(page, pending);
    expect(await selectedDraftStyle(page, 'icon')).toBe(selectedIcon);
    expect(await selectedDraftStyle(page, 'color')).toBe(selectedColor);
    await expect(activeDraftMarkers(page)).toHaveCount(1);
    await expect.poll(() => mapView(page)).toEqual(view);
  });

  test('phone Pick actions remain visible, contained, non-overlapping, and operable', async ({ page }) => {
    await signIn(page);
    for (const width of [390, 430]) {
      await page.setViewportSize({ width, height: 844 });
      await loadWorkspaceWithCoordinateFixture(page);
      await firstEditableRegion(page).getByRole('button', { name: 'Add Place' }).click();
      await page.getByRole('button', { name: 'Pick on map' }).click();
      const mapWork = page.getByRole('region', { name: 'Map work' });
      const done = mapWork.getByRole('button', { name: 'Done' });
      const cancel = mapWork.getByRole('button', { name: 'Cancel' });
      await expect(done).toBeInViewport();
      await expect(cancel).toBeInViewport();
      const layout = await page.evaluate(() => {
        const drawer = document.querySelector<HTMLElement>('.trip-editor-sidebar--mobile-drawer');
        const actions = document.querySelector<HTMLElement>('.trip-editor-map-work-toolbar__actions');
        const buttons = Array.from(actions?.querySelectorAll<HTMLElement>('button') ?? []).map(button => button.getBoundingClientRect());
        return {
          actionsContained: Boolean(actions && actions.scrollWidth <= actions.clientWidth),
          drawerContained: Boolean(drawer && drawer.scrollWidth <= drawer.clientWidth),
          overlap: buttons.length >= 2 && buttons[0].right > buttons[buttons.length - 1].left
        };
      });
      expect(layout.actionsContained, `${width}px Pick actions should not scroll horizontally.`).toBe(true);
      expect(layout.drawerContained, `${width}px drawer should not overflow horizontally.`).toBe(true);
      expect(layout.overlap, `${width}px Done and Cancel should not overlap.`).toBe(false);
      await done.click();
      await expect(mapWork).toHaveCount(0);
      await page.getByRole('button', { name: 'Pick on map' }).click();
      await cancel.click();
      await expect(mapWork).toHaveCount(0);
    }
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
    await expect(activeDraftMarkers(page)).toHaveCount(1);
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
    const originalAnchor = await markerAnchor(activeDraftMarkers(page));

    await page.getByRole('button', { name: 'Pick on map' }).click();
    await clickMap(page, { xRatio: 0.58, yRatio: 0.44 });
    const previewAnchor = await markerAnchor(activeDraftMarkers(page));

    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Done' }).click();
    const form = page.locator('#trip-editor-place-form');
    await expect(form.getByLabel('Latitude')).not.toHaveValue('10');
    await expect(form.getByLabel('Longitude')).not.toHaveValue('20');
    await expect(activeDraftMarkers(page)).toHaveCount(1);

    const selectedAnchor = await markerAnchor(activeDraftMarkers(page));
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

  test('new-place map Cancel, Reset, and form Cancel restore their distinct baselines', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithCoordinateFixture(page);
    await firstEditableRegion(page).getByRole('button', { name: 'Add Place' }).click();
    const form = page.locator('#trip-editor-place-form');
    const initialDraft = await draftCoordinates(page);
    const initialView = await mapView(page);

    await page.getByRole('button', { name: 'Pick on map' }).click();
    await clickMap(page, { xRatio: 0.35, yRatio: 0.35 });
    await clickMap(page, { xRatio: 0.65, yRatio: 0.55 });
    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Cancel' }).click();
    await page.getByRole('dialog', { name: 'Discard map editing changes?' }).getByRole('button', { name: 'Discard' }).click();
    await expectDraftCoordinates(page, initialDraft);
    await expectMarkerCoordinate(page, initialDraft);

    await form.getByLabel('Name').fill('Changed draft name');
    await form.getByLabel('Latitude').fill('12.5');
    await form.getByLabel('Longitude').fill('44.25');
    await expectMarkerCoordinate(page, { latitude: '12.5', longitude: '44.25' });
    await page.getByRole('button', { name: 'Reset' }).click();
    await expectDraftCoordinates(page, initialDraft);
    await expectMarkerCoordinate(page, initialDraft);
    await expect.poll(() => mapView(page)).toEqual(initialView);

    await form.getByLabel('Name').fill('Discard this draft');
    await page.getByRole('button', { name: 'Cancel' }).click();
    await page.getByRole('dialog', { name: 'Discard changes?' }).getByRole('button', { name: 'Discard' }).click();
    await expect(form).toHaveCount(0);
    await expect(activeDraftMarkers(page)).toHaveCount(0);
    await expect.poll(() => mapView(page)).toEqual(initialView);
  });

  test('direct coordinate pairs preserve zero and ignore blank, partial, and out-of-range input', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithCoordinateFixture(page);
    await openEditablePlace(page);
    const form = page.locator('#trip-editor-place-form');
    const initialMarker = await markerCoordinate(page);
    const initialView = await mapView(page);

    await form.getByLabel('Latitude').fill('');
    await form.getByLabel('Longitude').fill('0');
    await expectMarkerCoordinate(page, initialMarker);
    await form.getByLabel('Latitude').fill('0');
    await expectMarkerCoordinate(page, { latitude: '0', longitude: '0' });
    await form.getByLabel('Latitude').fill('91');
    await expectMarkerCoordinate(page, { latitude: '0', longitude: '0' });
    await form.getByLabel('Latitude').fill('10');
    await form.getByLabel('Longitude').fill('181');
    await expectMarkerCoordinate(page, { latitude: '0', longitude: '0' });
    await expect.poll(() => mapView(page)).toEqual(initialView);
  });

  test('dragging the one Pick marker changes pending state only until Done', async ({ page }) => {
    await useMapWorkViewport(page);
    await signIn(page);
    await loadWorkspaceWithCoordinateFixture(page);
    await openEditablePlace(page);
    const before = await draftCoordinates(page);
    const marker = activeDraftMarkers(page);

    await page.getByRole('button', { name: 'Pick on map' }).click();
    await expect(marker).toHaveClass(/leaflet-marker-draggable/);
    const box = await marker.boundingBox();
    expect(box).not.toBeNull();
    await page.mouse.move(box!.x + box!.width / 2, box!.y + box!.height / 2);
    await page.mouse.down();
    await page.mouse.move(box!.x + box!.width / 2 + 80, box!.y + box!.height / 2 + 45, { steps: 8 });
    await page.mouse.up();

    await expectDraftCoordinates(page, before);
    expect(await markerCoordinate(page)).not.toEqual(before);
    await expect(activeDraftMarkers(page)).toHaveCount(1);
    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Done' }).click();
    expect(await draftCoordinates(page)).not.toEqual(before);
    await expect(activeDraftMarkers(page)).toHaveCount(1);
  });

  test('failed Save retains the complete retryable draft marker and viewport', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithCoordinateFixture(page);
    await page.route(editorApiMatcher, async route => {
      if (route.request().method() === 'GET') {
        await route.fallback();
        return;
      }
      await route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ title: 'Injected save failure' }) });
    });
    await openEditablePlace(page);
    const form = page.locator('#trip-editor-place-form');
    await form.getByLabel('Name').fill('Retryable name');
    await form.getByLabel('Latitude').fill('12.25');
    await form.getByLabel('Longitude').fill('22.75');
    const beforeView = await mapView(page);

    await page.getByRole('button', { name: 'Save Place' }).click();
    await expect(page.getByText(/Place save failed|Injected save failure/).first()).toBeVisible();
    await expect(form.getByLabel('Name')).toHaveValue('Retryable name');
    await expectDraftCoordinates(page, { latitude: '12.25', longitude: '22.75' });
    await expectMarkerCoordinate(page, { latitude: '12.25', longitude: '22.75' });
    await expect(page.getByRole('button', { name: 'Reset' })).toBeEnabled();
    await expect.poll(() => mapView(page)).toEqual(beforeView);
  });

  test('Save after Done sends the picked coordinate to a mocked existing place endpoint', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithCoordinateFixture(page);
    const forbidden = watchForbiddenPickRequests(page);
    const savedRequests: Array<Record<string, any>> = [];
    const serverCoordinate = { latitude: 12.125, longitude: 22.875 };
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
      // Fulfilled here to prove picked-coordinate request shape and UI save handling only.
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(editorMutationResult({ ...body, id: editablePlaceId, location: serverCoordinate }))
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
    await expectDraftCoordinates(page, { latitude: String(serverCoordinate.latitude), longitude: String(serverCoordinate.longitude) });
    await expectMarkerCoordinate(page, { latitude: String(serverCoordinate.latitude), longitude: String(serverCoordinate.longitude) });
    expect(forbidden(), 'Saving a picked coordinate must not call geocode/search providers.').toEqual([]);
  });
});

async function loadWorkspaceWithCoordinateFixture(page: Page): Promise<void> {
  await blockTileRequests(page);
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

async function blockTileRequests(page: Page): Promise<void> {
  await page.route(/\/map\/tiles\//i, route => route.fulfill({ status: 204, body: '' }));
}

function activeDraftMarkers(page: Page): Locator {
  return page.locator('[data-place-draft-preview-marker], [data-coordinate-preview-marker]');
}

async function mapView(page: Page): Promise<{ latitude: number; longitude: number; zoom: number }> {
  return await page.getByLabel('Read-only trip map').evaluate(element => ({
    latitude: Number((element as HTMLElement).dataset.tripEditorMapLat),
    longitude: Number((element as HTMLElement).dataset.tripEditorMapLng),
    zoom: Number((element as HTMLElement).dataset.tripEditorMapZoom)
  }));
}

async function markerCoordinate(page: Page): Promise<{ latitude: string; longitude: string }> {
  return await activeDraftMarkers(page).evaluate(element => ({
    latitude: (element as HTMLElement).dataset.placeDraftLatitude ?? '',
    longitude: (element as HTMLElement).dataset.placeDraftLongitude ?? ''
  }));
}

async function expectMarkerCoordinate(page: Page, coordinate: { latitude: string; longitude: string }): Promise<void> {
  await expect.poll(() => markerCoordinate(page)).toEqual(coordinate);
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

// Selects a non-default draft style through the same controls used by the place form.
async function selectDraftStyle(page: Page, kind: 'icon' | 'color'): Promise<void> {
  const selector = page.locator(`[data-selector-kind="${kind}"]`);
  await selector.locator('[data-icon-selector-trigger]').click();
  const options = selector.locator('[data-icon-selector-option]');
  await expect(options.nth(1)).toBeVisible();
  await options.nth(1).click();
}

async function selectedDraftStyle(page: Page, kind: 'icon' | 'color'): Promise<string> {
  return (await page.locator(`[data-selector-kind="${kind}"] [data-icon-selector-selected-name]`).textContent())?.trim() ?? '';
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
  await expect(page.locator(`#${describedBy}`)).toContainText('Click the map or drag the marker. Done updates the draft; Save Place persists it.');
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
