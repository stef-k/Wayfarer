import { expect, test, type Page, type Route } from '@playwright/test';
import {
  absoluteUrl,
  closeDraftWithDiscard,
  editorApiPath,
  expectMountedWorkspace,
  expectNoSearchAddUi,
  expectTripMapDescription,
  loadEditorStateFixture,
  signIn,
  editorPath
} from './tripEditorTestUtils';

type MutableEditorState = Record<string, any>;

const areaId = '00000000-0000-0000-0000-000000248001';
const secondAreaId = '00000000-0000-0000-0000-000000248002';
const areaName = 'PW editable area';
const secondAreaName = 'PW second area';
const editorApiMatcher = new RegExp(`${editorApiPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?:/.*)?$`, 'i');

test.describe.serial('Trip Editor area editing', () => {
  test('adds and edits areas through one docked or expanded shared draft', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithAreaFixture(page);

    const region = firstEditableRegion(page);
    await region.getByRole('button', { name: 'Add Area' }).click();
    await expect(page.getByRole('heading', { name: 'Add Area' })).toBeVisible();
    await expect(page.locator('#trip-editor-area-form')).toHaveCount(1);
    await expect(page.locator('#trip-editor-area-form').getByLabel('Name')).toHaveValue('Area');
    await expect(page.locator('#trip-editor-area-form')).toContainText('No polygon drawn');
    await expect(page.getByRole('button', { name: 'Save Geometry' })).toHaveCount(0);

    await closeDraftWithDiscard(page);
    await openEditableArea(page);
    await expect(page.locator('#trip-editor-area-form').getByLabel('Name')).toHaveValue(areaName);
    await page.getByRole('button', { name: 'Expand Editor' }).click();
    await expect(page.getByRole('dialog', { name: new RegExp(`Edit Area - ${areaName}`) })).toBeVisible();
    await expect(page.locator('#trip-editor-area-form')).toHaveCount(1);
  });

  test('Draw/Edit Area enters map-work and Done updates only the draft', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithAreaFixture(page);
    const mutations = watchEditorMutations(page);

    await openEditableArea(page);
    const measureButton = page.locator('.trip-editor-map-utilities').getByRole('button', { name: 'Measure distance' });
    await measureButton.click();
    await expect(measureButton).toHaveClass(/active/);
    await page.getByRole('button', { name: 'Draw/Edit Area' }).click();
    await expectTripMapDescription(page, 'Edit the Area geometry. Click the map to place polygon vertices; Done updates the draft.');
    const mapWork = page.getByRole('region', { name: 'Map work' });
    await expect(mapWork).toContainText('Draw area polygon');
    await expect(measureButton).not.toHaveClass(/active/);
    await expect(mapWork).toContainText('3 vertices ready');
    await expect(mapWork.getByRole('button', { name: 'Done' })).toBeEnabled();
    await expect(page.locator('.trip-editor-toolbar').getByRole('button', { name: 'Fit All' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Pick on map' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /save geometry/i })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /marker drag|drag marker|route edit|edit route|geocode|search.?add/i })).toHaveCount(0);
    await expectNoSearchAddUi(page);

    await mapWork.getByRole('button', { name: 'Done' }).click();
    await expect(page.locator('#trip-editor-area-form')).toContainText('3 polygon vertices');
    expect(mutations(), 'Area map-work Done must not call create/update/geometry endpoints.').toEqual([]);
  });

  test('new area starts without a temporary polygon and Save after Done sends a mocked create request', async ({ page }) => {
    await useMapWorkViewport(page);
    await signIn(page);
    const state = await loadWorkspaceWithAreaFixture(page);
    const regionId = normalRegion(state).id;
    const savedRequests: Array<Record<string, any>> = [];
    // Mocked area mutations prove request shape and affected-slice UI handling, not real area CRUD persistence.
    await page.route(editorApiMatcher, async route => {
      if (route.request().method() === 'GET') {
        await route.fallback();
        return;
      }

      expect(route.request().method()).toBe('POST');
      expect(route.request().url()).toContain(`/regions/${regionId}/areas`);
      const body = route.request().postDataJSON() as Record<string, any>;
      savedRequests.push(body);
      const savedArea = areaFixture(state, regionId, '00000000-0000-0000-0000-000000248099', body.name, body.geometry);
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(areaMutationResult(savedArea, { [regionId]: [areaId, secondAreaId, savedArea.id] })) });
    });

    await firstEditableRegion(page).getByRole('button', { name: 'Add Area' }).click();
    await page.getByRole('button', { name: 'Draw/Edit Area' }).click();
    const mapWork = page.getByRole('region', { name: 'Map work' });
    await expect(mapWork).toContainText('No polygon ready');
    await expect(mapWork.getByRole('button', { name: 'Done' })).toBeDisabled();
    await drawTriangle(page);
    await expect(mapWork).toContainText('3 vertices ready');
    expect(savedRequests).toEqual([]);

    await mapWork.getByRole('button', { name: 'Done' }).click();
    await page.locator('#trip-editor-area-form').getByLabel('Name').fill('PW saved area');
    await page.getByRole('button', { name: 'Save Area' }).click();
    await expect.poll(() => savedRequests.length).toBe(1);
    expect(savedRequests[0].geometry.type).toBe('Polygon');
    expect(savedRequests[0].geometry.coordinates[0]).toHaveLength(4);
  });

  test('existing polygon map-work preserves interior rings in the mocked update request', async ({ page }) => {
    await signIn(page);
    const savedRequests: Array<Record<string, any>> = [];
    const state = await loadWorkspaceWithAreaFixture(page, fixture => {
      fixture.areasById[areaId].geometry = polygonWithHole();
    });
    // Mocked area mutations prove request shape and affected-slice UI handling, not real area CRUD persistence.
    await page.route(editorApiMatcher, async route => {
      if (route.request().method() === 'GET') {
        await route.fallback();
        return;
      }

      expect(route.request().method()).toBe('PUT');
      expect(route.request().url()).toContain(`/areas/${areaId}`);
      const body = route.request().postDataJSON() as Record<string, any>;
      savedRequests.push(body);
      state.areasById[areaId] = { ...state.areasById[areaId], ...body };
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(areaMutationResult(state.areasById[areaId], { [state.areasById[areaId].regionId]: [areaId, secondAreaId] })) });
    });

    await openEditableArea(page);
    await page.getByRole('button', { name: 'Draw/Edit Area' }).click();
    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Done' }).click();
    await page.getByRole('button', { name: 'Save Area' }).click();
    await expect.poll(() => savedRequests.length).toBe(1);
    expect(savedRequests[0].geometry.coordinates).toHaveLength(2);
    expect(savedRequests[0].geometry.coordinates[1]).toEqual([[0.2, 0.2], [0.4, 0.2], [0.4, 0.4], [0.2, 0.2]]);
  });

  test('Cancel rolls back only geometry and delete confirms dirty discard before danger', async ({ page }) => {
    await useMapWorkViewport(page);
    await signIn(page);
    await loadWorkspaceWithAreaFixture(page);

    await openEditableArea(page);
    const form = page.locator('#trip-editor-area-form');
    await form.getByLabel('Name').fill('Unsaved area name');
    await page.getByRole('button', { name: 'Draw/Edit Area' }).click();
    await dragFirstEditableVertex(page);
    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Cancel' }).click();
    await discardDirtyMapWork(page);
    await expect(form.getByLabel('Name')).toHaveValue('Unsaved area name');

    await page.getByRole('button', { name: 'Delete', exact: true }).first().click({ force: true });
    await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toBeVisible();
    await page.getByRole('dialog', { name: 'Discard changes?' }).getByRole('button', { name: 'Discard' }).click();
    await expect(page.getByRole('dialog', { name: 'Delete area?' })).toBeVisible();
  });

  test('shadow region exposes no area add or reorder affordance', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithAreaFixture(page);
    const shadow = page.locator('.trip-editor-region-card--shadow').first();
    if (await shadow.count()) {
      await expect(shadow.getByRole('button', { name: 'Add Area' })).toHaveCount(0);
      await expect(shadow.getByRole('button', { name: 'Drag to reorder area' })).toHaveCount(0);
    }
  });

  test('reorders areas within one normal region and applies the mocked order response after reload', async ({ page }) => {
    await signIn(page);
    const state = await loadWorkspaceWithAreaFixture(page);
    const regionId = normalRegion(state).id;
    await page.unroute(editorApiMatcher);
    // This fulfilled order response proves frontend reorder handling only; pair with backend/real endpoint tests for CRUD proof.
    await page.route(editorApiMatcher, async route => {
      if (route.request().method() === 'GET') {
        await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
        return;
      }

      expect(route.request().method()).toBe('PUT');
      expect(route.request().url()).toContain(`/regions/${regionId}/areas/order`);
      const body = route.request().postDataJSON() as { areaIds: string[] };
      state.areaOrderByRegionId[regionId] = [...body.areaIds];
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(areaOrderMutationResult(regionId, body.areaIds, state)) });
    });

    await expectAreaOrder(page, [areaName, secondAreaName]);
    await dragAreaRow(page, areaName, secondAreaName);
    await expectAreaOrder(page, [secondAreaName, areaName]);
    await page.reload();
    await expectMountedWorkspace(page);
    await expectAreaOrder(page, [secondAreaName, areaName]);
  });

  test('expanded map-work returns to the expanded area editor without duplicating the draft', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithAreaFixture(page);

    await openEditableArea(page);
    await page.getByRole('button', { name: 'Expand Editor' }).click();
    const dialog = page.getByRole('dialog', { name: new RegExp(`Edit Area - ${areaName}`) });
    await expect(dialog).toBeVisible();
    await dialog.getByRole('button', { name: 'Draw/Edit Area' }).click();
    await expect(page.getByRole('region', { name: 'Map work' })).toContainText('Draw area polygon');
    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Done' }).click();
    await expect(dialog).toBeVisible();
    await expect(page.locator('#trip-editor-area-form')).toHaveCount(1);
  });

  test('dirty area map-work prompts before switching or closing', async ({ page }) => {
    await useMapWorkViewport(page);
    await signIn(page);
    await loadWorkspaceWithAreaFixture(page);

    await openEditableArea(page);
    await page.getByRole('button', { name: 'Draw/Edit Area' }).click();
    await dragFirstEditableVertex(page);
    await page.getByRole('button', { name: 'Add Region' }).click();
    const mapDialog = page.getByRole('dialog', { name: 'Discard map editing changes?' });
    await expect(mapDialog).toBeVisible();
    await mapDialog.getByRole('button', { name: 'Keep editing' }).click();
    await expect(page.getByRole('region', { name: 'Map work' })).toBeVisible();

    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Cancel' }).click();
    await expect(mapDialog).toBeVisible();
    await mapDialog.getByRole('button', { name: 'Discard' }).click();
    await expect(page.locator('#trip-editor-area-form')).toBeVisible();
  });

  test('canonical trip edit page mounts the Vue editor shell', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(editorPath));
    await expect(page).toHaveURL(new RegExp(`/User/Trip/Edit/${configTripIdPattern()}$`, 'i'));
    await expectMountedWorkspace(page);
  });

  test('map and marker clicks outside area map-work do not mutate area geometry', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithAreaFixture(page);

    await openEditableArea(page);
    const form = page.locator('#trip-editor-area-form');
    await expect(form).toContainText('3 polygon vertices');
    await clickMap(page, { xRatio: 0.25, yRatio: 0.25 });
    await expect(form).toContainText('3 polygon vertices');
    const marker = page.locator('.leaflet-marker-icon').first();
    if (await marker.count()) {
      await marker.click({ force: true });
      await expect(form).toContainText('3 polygon vertices');
    }
  });
});

async function loadWorkspaceWithAreaFixture(page: Page, configure?: (state: MutableEditorState) => void): Promise<MutableEditorState> {
  await page.unroute(editorApiMatcher).catch(() => undefined);
  const state = await loadEditorStateFixture(page) as MutableEditorState;
  prepareAreaState(state);
  configure?.(state);
  await page.route(editorApiMatcher, async route => routeEditorReadOnly(route, state));
  await page.goto(absoluteUrl(editorPath));
  await expectMountedWorkspace(page);
  return state;
}

async function routeEditorReadOnly(route: Route, state: MutableEditorState): Promise<void> {
  if (route.request().method() !== 'GET') {
    throw new Error(`Unexpected editor mutation ${route.request().method()} ${route.request().url()}`);
  }

  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
}

function prepareAreaState(state: MutableEditorState): void {
  const region = normalRegion(state);
  state.permissions.canEditAreas = true;
  region.capabilities.canAddChildren = true;
  state.areasById[areaId] = areaFixture(state, region.id, areaId, areaName, polygon(0));
  state.areasById[secondAreaId] = areaFixture(state, region.id, secondAreaId, secondAreaName, polygon(2));
  state.areaOrderByRegionId[region.id] = [areaId, secondAreaId];
}

function normalRegion(state: MutableEditorState): any {
  const region = Object.values(state.regionsById).find((item: any) => !item.isShadow) as any;
  if (!region) {
    throw new Error('Configured Trip Editor fixture must contain a normal region.');
  }

  return region;
}

function areaFixture(state: MutableEditorState, regionId: string, id: string, name: string, geometry: any): Record<string, any> {
  return {
    id,
    tripId: state.tripId,
    regionId,
    name,
    notesHtml: '',
    fillHex: '#ff6600',
    geometry,
    displayOrder: 1,
    capabilities: {
      canEdit: true,
      canRename: true,
      canDelete: true,
      canReorder: true,
      canMove: false,
      canAddChildren: false,
      canTargetForSearchAdd: false
    }
  };
}

async function openEditableArea(page: Page): Promise<void> {
  await firstEditableRegion(page).getByText(areaName).locator('xpath=ancestor::*[contains(@class, "trip-editor-area-row")]').getByRole('button', { name: 'Edit', exact: true }).click();
  await expect(page.getByRole('heading', { name: new RegExp(`Edit Area - ${areaName}`) })).toBeVisible();
}

function firstEditableRegion(page: Page) {
  return page.locator('.trip-editor-region-card--normal').first();
}

async function useMapWorkViewport(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1280, height: 1000 });
}

// Clicks only within the currently visible Leaflet surface so fixed page chrome cannot consume map-work gestures.
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
  const x = visibleLeft + (visibleRight - visibleLeft) * position.xRatio;
  const y = visibleTop + (visibleBottom - visibleTop) * position.yRatio;
  await page.mouse.move(x, y);
  await page.waitForTimeout(75);
  await page.mouse.click(x, y);
  await page.waitForTimeout(75);
}

async function drawTriangle(page: Page): Promise<void> {
  await clickMap(page, { xRatio: 0.35, yRatio: 0.35 });
  await clickMap(page, { xRatio: 0.45, yRatio: 0.35 });
  await clickMap(page, { xRatio: 0.40, yRatio: 0.45 });
  await page.locator('.leaflet-editing-icon').first().click();
}

async function dragFirstEditableVertex(page: Page): Promise<void> {
  await page.evaluate(() => window.scrollTo(0, 0));
  const vertex = page.locator('.leaflet-editing-icon').first();
  await expect(vertex).toBeVisible();
  await vertex.scrollIntoViewIfNeeded();
  const box = await vertex.boundingBox();
  expect(box, 'Editable area vertex should have a browser-visible box before dragging.').not.toBeNull();
  const startX = box!.x + box!.width / 2;
  const startY = box!.y + box!.height / 2;
  await page.mouse.move(startX, startY);
  await page.mouse.down();
  await page.mouse.move(startX + 180, startY + 120, { steps: 16 });
  await page.mouse.up();
  await expect.poll(async () => {
    const movedBox = await vertex.boundingBox();
    return movedBox ? Math.abs(movedBox.x - box!.x) + Math.abs(movedBox.y - box!.y) : 0;
  }).toBeGreaterThan(8);
}

async function discardDirtyMapWork(page: Page): Promise<void> {
  const dialog = page.getByRole('dialog', { name: 'Discard map editing changes?' });
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Discard' }).click();
}

async function dragAreaRow(page: Page, fromName: string, toName: string): Promise<void> {
  const from = areaRow(page, fromName);
  await expect(areaRow(page, toName)).toBeVisible();
  await from.getByRole('button', { name: 'Drag to reorder area' }).focus();
  await page.keyboard.press('ArrowDown');
}

async function expectAreaOrder(page: Page, names: string[]): Promise<void> {
  await expect.poll(async () => {
    const rows = await firstEditableRegion(page).locator('[data-area-id]').all();
    return Promise.all(rows.map(row => row.locator('span').nth(1).innerText()));
  }).toEqual(names);
}

function areaRow(page: Page, name: string) {
  return firstEditableRegion(page).locator('[data-area-id]').filter({ hasText: name });
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

function areaMutationResult(area: Record<string, any>, orderByRegion: Record<string, string[]>): Record<string, any> {
  return {
    success: true,
    data: area,
    affected: {
      metadata: null,
      regions: [],
      regionOrder: null,
      places: [],
      placeOrdersByRegionId: {},
      areas: [area],
      areaOrdersByRegionId: orderByRegion,
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

function areaOrderMutationResult(regionId: string, order: string[], state: MutableEditorState): Record<string, any> {
  return {
    success: true,
    data: { regionId, areaOrder: order },
    affected: {
      metadata: null,
      regions: [],
      regionOrder: null,
      places: [],
      placeOrdersByRegionId: {},
      areas: order.map(id => state.areasById[id]),
      areaOrdersByRegionId: { [regionId]: order },
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

function configTripIdPattern(): string {
  return '[0-9a-f-]+';
}

function polygon(offset: number): Record<string, any> {
  return {
    type: 'Polygon',
    coordinates: [[[offset, 0], [offset + 1, 0], [offset + 1, 1], [offset, 0]]]
  };
}

function polygonWithHole(): Record<string, any> {
  return {
    type: 'Polygon',
    coordinates: [
      [[0, 0], [1, 0], [1, 1], [0, 0]],
      [[0.2, 0.2], [0.4, 0.2], [0.4, 0.4], [0.2, 0.2]]
    ]
  };
}
