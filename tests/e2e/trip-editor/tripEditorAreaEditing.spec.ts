import { expect, test, type Page, type Route } from '@playwright/test';
import {
  absoluteUrl,
  editorApiPath,
  expectMountedWorkspace,
  expectNoSearchAddUi,
  legacyEditPath,
  loadEditorStateFixture,
  signIn,
  workspacePath
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

    await page.getByRole('button', { name: 'Cancel' }).click();
    await page.getByRole('dialog', { name: 'Discard changes?' }).getByRole('button', { name: 'Discard' }).click();
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
    await page.getByRole('button', { name: 'Draw/Edit Area' }).click();
    const mapWork = page.getByRole('region', { name: 'Map work' });
    await expect(mapWork).toContainText('Draw area polygon');
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

  test('new area starts without a temporary polygon and Save after Done persists it', async ({ page }) => {
    await signIn(page);
    const state = await loadWorkspaceWithAreaFixture(page);
    const regionId = normalRegion(state).id;
    const savedRequests: Array<Record<string, any>> = [];
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

  test('Cancel rolls back only geometry and delete confirms dirty discard before danger', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithAreaFixture(page);

    await openEditableArea(page);
    const form = page.locator('#trip-editor-area-form');
    await form.getByLabel('Name').fill('Unsaved area name');
    await page.getByRole('button', { name: 'Draw/Edit Area' }).click();
    await dragFirstEditableVertex(page);
    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Cancel' }).click();
    await page.getByRole('dialog', { name: 'Discard map editing changes?' }).getByRole('button', { name: 'Discard' }).click();
    await expect(form.getByLabel('Name')).toHaveValue('Unsaved area name');

    await page.getByRole('button', { name: 'Delete' }).click();
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

  test('reorders areas within one normal region and persists after reload', async ({ page }) => {
    await signIn(page);
    const state = await loadWorkspaceWithAreaFixture(page);
    const regionId = normalRegion(state).id;
    await page.unroute(editorApiMatcher);
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

  test('legacy trip edit page still loads', async ({ page }) => {
    await signIn(page);
    await page.goto(absoluteUrl(legacyEditPath));
    await expect(page).toHaveURL(new RegExp(`/User/Trip/Edit/${configTripIdPattern()}$`, 'i'));
    await expect(page.locator('body')).not.toContainText('Trip Editor development server is not available');
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

async function loadWorkspaceWithAreaFixture(page: Page): Promise<MutableEditorState> {
  await page.unroute(editorApiMatcher).catch(() => undefined);
  const state = await loadEditorStateFixture(page) as MutableEditorState;
  prepareAreaState(state);
  await page.route(editorApiMatcher, async route => routeEditorReadOnly(route, state));
  await page.goto(absoluteUrl(workspacePath));
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

async function clickMap(page: Page, position: { xRatio: number; yRatio: number }): Promise<void> {
  const map = page.getByLabel('Read-only trip map');
  const box = await map.boundingBox();
  expect(box, 'Trip Editor map should be visible before clicking it.').not.toBeNull();
  await page.mouse.click(box!.x + box!.width * position.xRatio, box!.y + box!.height * position.yRatio);
}

async function drawTriangle(page: Page): Promise<void> {
  await clickMap(page, { xRatio: 0.35, yRatio: 0.35 });
  await clickMap(page, { xRatio: 0.45, yRatio: 0.35 });
  await clickMap(page, { xRatio: 0.40, yRatio: 0.45 });
  await clickMap(page, { xRatio: 0.35, yRatio: 0.35 });
}

async function dragFirstEditableVertex(page: Page): Promise<void> {
  const vertex = page.locator('.leaflet-editing-icon').first();
  await expect(vertex).toBeVisible();
  const box = await vertex.boundingBox();
  expect(box, 'Editable area vertex should have a rendered box.').not.toBeNull();
  await page.mouse.move(box!.x + box!.width / 2, box!.y + box!.height / 2);
  await page.mouse.down();
  await page.mouse.move(box!.x + box!.width / 2 + 24, box!.y + box!.height / 2 + 16, { steps: 4 });
  await page.mouse.up();
}

async function dragAreaRow(page: Page, fromName: string, toName: string): Promise<void> {
  const from = areaRow(page, fromName);
  const to = areaRow(page, toName);
  await from.getByRole('button', { name: 'Drag to reorder area' }).dragTo(to);
}

async function expectAreaOrder(page: Page, names: string[]): Promise<void> {
  await expect.poll(async () => {
    const rows = await firstEditableRegion(page).locator('[data-area-id]').all();
    return Promise.all(rows.map(row => row.locator('span').first().innerText()));
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
