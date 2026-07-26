import { expect, test, type Page, type Route } from '@playwright/test';
import {
  absoluteUrl,
  activeEditorCancelButton,
  editorApiPath,
  expectMountedWorkspace,
  expectNoLegacyEditorAction,
  expectNoSearchAddUi,
  loadEditorStateFixture,
  signIn,
  editorPath
} from './tripEditorTestUtils';

type MutableEditorState = Record<string, any>;

const segmentId = '00000000-0000-0000-0000-000000266001';
const secondSegmentId = '00000000-0000-0000-0000-000000266002';
const editorApiMatcher = new RegExp(`${editorApiPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?:/.*)?$`, 'i');

test.describe.serial('Trip Editor segment editing', () => {
  test('adds and edits segments through docked and expanded shared draft', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithSegmentFixture(page);

    await page.getByRole('button', { name: 'Add Segment' }).click();
    await expect(page.getByRole('heading', { name: 'Add Segment' })).toBeVisible();
    await expect(page.locator('#trip-editor-segment-form')).toHaveCount(1);
    await page.locator('#trip-editor-segment-form').getByLabel('Transport mode').selectOption('walk');

    await activeEditorCancelButton(page).click();
    await page.getByRole('dialog', { name: 'Discard changes?' }).getByRole('button', { name: 'Discard' }).click();
    await openEditableSegment(page);
    await page.getByRole('button', { name: 'Expand Editor' }).click();
    await expect(page.getByRole('dialog', { name: /Edit Segment -/ })).toBeVisible();
    await expect(page.locator('#trip-editor-segment-form')).toHaveCount(1);
  });

  test('open segment draft owns one test-observable route representation', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithSegmentFixture(page);

    await openEditableSegment(page);

    const activeRoute = page.locator(`[data-segment-id="${segmentId}"][data-route-owner="draft"]`);
    await expect(activeRoute).toHaveCount(1);
    await expect(activeRoute).toHaveAttribute('data-route-kind', 'custom');
    await expect(page.locator(`[data-segment-id="${segmentId}"][data-route-owner="saved"]`)).toHaveCount(0);
  });

  test('docked segment map-work context spans and stays contained in its row', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithSegmentFixture(page, state => {
      const [first, second] = Object.values(state.placesById) as Array<Record<string, any>>;
      first.name = 'A very long departure station name that must wrap inside the segment row';
      second.name = 'An equally long arrival station name that must remain contained';
    });

    await openEditableSegment(page);
    await page.getByRole('button', { name: 'Draw/Edit Route' }).click();

    const row = segmentRow(page, segmentId);
    const context = row.locator('.trip-editor-surface-context');
    await expect(context).toBeVisible();
    for (const viewport of [{ width: 1280, height: 800 }, { width: 430, height: 800 }, { width: 390, height: 800 }]) {
      await page.setViewportSize(viewport);
      const layout = await row.evaluate(element => {
        const contextElement = element.querySelector<HTMLElement>('.trip-editor-surface-context');
        if (!contextElement) {
          throw new Error('Expected active segment map-work context.');
        }

        const rowBounds = element.getBoundingClientRect();
        const contextBounds = contextElement.getBoundingClientRect();
        return {
          contained: element.scrollWidth <= element.clientWidth && contextElement.scrollWidth <= contextElement.clientWidth,
          spansRow: Math.abs(contextBounds.left - rowBounds.left) <= 1 && Math.abs(contextBounds.right - rowBounds.right) <= 1
        };
      });
      expect(layout).toEqual({ contained: true, spansRow: true });
    }
  });

  test('route map-work from docked and expanded sends a mocked route save only after Save', async ({ page }) => {
    await signIn(page);
    const state = await loadWorkspaceWithSegmentFixture(page);
    const savedRequests: Array<Record<string, any>> = [];
    await page.unroute(editorApiMatcher);
    // Mocked segment mutations prove request shape and route UI handling, not real segment CRUD persistence.
    await page.route(editorApiMatcher, async route => {
      if (route.request().method() === 'GET') {
        await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
        return;
      }

      expect(route.request().method()).toBe('PUT');
      expect(route.request().url()).toContain(`/segments/${segmentId}`);
      const body = route.request().postDataJSON() as Record<string, any>;
      savedRequests.push(body);
      state.segmentsById[segmentId] = { ...state.segmentsById[segmentId], ...body };
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(segmentMutationResult(state.segmentsById[segmentId], null)) });
    });

    await openEditableSegment(page);
    await expectNoLegacyEditorAction(page);
    await page.getByRole('button', { name: 'Draw/Edit Route' }).click();
    const mapWork = page.getByRole('region', { name: 'Map work' });
    await expect(mapWork).toContainText('Draw segment route');
    await expect(mapWork.getByRole('button', { name: 'Done' })).toBeEnabled();
    const workRoute = page.locator(`[data-segment-id="${segmentId}"][data-route-owner="work"]`);
    await expect(workRoute).toHaveAttribute('data-route-kind', 'custom');
    await expect(page.locator('.trip-editor-toolbar').getByRole('button', { name: 'Fit All' })).toHaveCount(0);
    await expectNoLegacyEditorAction(page);
    await expect(page.getByRole('button', { name: /pick on map|draw\/edit area|geocode|search.?add|marker drag/i })).toHaveCount(0);
    await expectNoSearchAddUi(page);

    const editHandle = page.locator('.leaflet-editing-icon').last();
    const handleBox = await editHandle.boundingBox();
    if (!handleBox) {
      throw new Error('Expected an editable segment route vertex.');
    }
    await page.mouse.move(handleBox.x + handleBox.width / 2, handleBox.y + handleBox.height / 2);
    await page.mouse.down();
    await page.mouse.move(handleBox.x + handleBox.width / 2 + 40, handleBox.y + handleBox.height / 2 - 30);
    await page.mouse.up();
    const workPath = await workRoute.getAttribute('d');

    await mapWork.getByRole('button', { name: 'Done' }).click();
    await expectNoLegacyEditorAction(page);
    expect(savedRequests, 'Done must not call the segment save endpoint.').toEqual([]);
    await expect(page.locator('#trip-editor-segment-form')).toContainText('2 custom route points');
    const draftRoute = page.locator(`[data-segment-id="${segmentId}"][data-route-owner="draft"]`);
    await expect(draftRoute).toHaveAttribute('d', workPath ?? '');
    await expect(page.locator(`[data-segment-id="${segmentId}"][data-route-owner="saved"]`)).toHaveCount(0);

    await page.getByRole('button', { name: 'Save Segment' }).click();
    await expect.poll(() => savedRequests.length).toBe(1);
    expect(savedRequests[0].route?.type).toBe('LineString');
    expect(savedRequests[0].route?.coordinates).toHaveLength(2);
    await expect(page.locator(`[data-segment-id="${segmentId}"][data-route-owner="saved"]`)).toHaveCount(1);
    await expect(draftRoute).toHaveCount(0);

    await openEditableSegment(page);
    await page.getByRole('button', { name: 'Expand Editor' }).click();
    await page.getByRole('dialog', { name: /Edit Segment -/ }).getByRole('button', { name: 'Draw/Edit Route' }).click();
    await expect(page.getByRole('region', { name: 'Map work' })).toContainText('Draw segment route');
  });

  test('no-op Done preserves a nullable draft route and its endpoint fallback', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithSegmentFixture(page);

    await segmentRow(page, secondSegmentId).locator('.trip-editor-list-button').click();
    const form = page.locator('#trip-editor-segment-form');
    await expect(form).toContainText('Endpoint fallback available until saved');
    await page.getByRole('button', { name: 'Draw/Edit Route' }).click();
    await expect(page.locator(`[data-segment-id="${secondSegmentId}"][data-route-owner="work"]`)).toHaveAttribute('data-route-kind', 'fallback');
    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Done' }).click();

    await expect(form).toContainText('Endpoint fallback available until saved');
    await expect(form).not.toContainText('custom route points');
    await expect(page.getByRole('button', { name: 'Clear Route' })).toBeDisabled();
    await expect(page.locator(`[data-segment-id="${secondSegmentId}"][data-route-owner="draft"][data-route-kind="fallback"]`)).toHaveCount(1);
  });

  test('failed Save retains the complete visible retryable draft and viewport', async ({ page }) => {
    await signIn(page);
    const state = await loadWorkspaceWithSegmentFixture(page);
    await page.unroute(editorApiMatcher);
    await page.route(editorApiMatcher, async route => {
      if (route.request().method() === 'GET') {
        await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
        return;
      }

      await route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ title: 'Forced segment save failure' }) });
    });

    await openEditableSegment(page);
    const form = page.locator('#trip-editor-segment-form');
    const draftRoute = page.locator(`[data-segment-id="${segmentId}"][data-route-owner="draft"]`);
    const routePath = await draftRoute.getAttribute('d');
    const map = page.locator('.trip-editor-map');
    const viewport = await map.evaluate(element => ({ lat: element.dataset.tripEditorMapLat, lng: element.dataset.tripEditorMapLng, zoom: element.dataset.tripEditorMapZoom }));
    await form.getByLabel('Estimated distance km').fill('99');
    await page.getByRole('button', { name: 'Save Segment' }).click();

    await expect(page.getByRole('status').filter({ hasText: 'Save failed' })).toBeVisible();
    await expect(form.getByLabel('Estimated distance km')).toHaveValue('99');
    await expect(draftRoute).toHaveAttribute('d', routePath ?? '');
    await expect(map).toHaveAttribute('data-trip-editor-map-lat', viewport.lat ?? '');
    await expect(map).toHaveAttribute('data-trip-editor-map-lng', viewport.lng ?? '');
    await expect(map).toHaveAttribute('data-trip-editor-map-zoom', viewport.zoom ?? '');
  });

  test('Cancel route map-work rolls back only route and delete confirms dirty discard first', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithSegmentFixture(page);

    await openEditableSegment(page);
    const form = page.locator('#trip-editor-segment-form');
    await form.getByLabel('Estimated distance km').fill('99');
    await page.getByRole('button', { name: 'Draw/Edit Route' }).click();
    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Clear Route' }).click();
    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Cancel' }).click();
    await page.getByRole('dialog', { name: 'Discard map editing changes?' }).getByRole('button', { name: 'Discard' }).click();
    await expect(form.getByLabel('Estimated distance km')).toHaveValue('99');
    await expect(form).toContainText('2 custom route points');
    await expect(page.locator(`[data-segment-id="${segmentId}"][data-route-owner="draft"][data-route-kind="custom"]`)).toHaveCount(1);

    await page.getByRole('button', { name: 'Clear Route' }).click();
    await expect(form).toContainText('Endpoint fallback available until saved');
    await expect(page.locator(`[data-segment-id="${segmentId}"][data-route-owner="draft"][data-route-kind="fallback"]`)).toHaveCount(1);

    await page.getByRole('button', { name: 'Reset' }).click();
    await expect(form).toContainText('2 custom route points');
    await expect(form.getByLabel('Estimated distance km')).toHaveValue('2');
    await form.getByLabel('Estimated distance km').fill('99');

    await page.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toBeVisible();
    await page.getByRole('dialog', { name: 'Discard changes?' }).getByRole('button', { name: 'Discard' }).click();
    await expect(page.getByRole('dialog', { name: 'Delete segment?' })).toBeVisible();
  });

  test('row delete canceled dirty switch keeps the active segment and skips destructive delete', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithSegmentFixture(page);

    await openEditableSegment(page);
    const form = page.locator('#trip-editor-segment-form');
    await form.getByLabel('Estimated distance km').fill('99');

    await segmentRow(page, secondSegmentId).getByRole('button', { name: 'Delete segment' }).click();
    const discardDialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(discardDialog).toBeVisible();
    await discardDialog.getByRole('button', { name: 'Keep editing' }).click();

    await expect(page.getByRole('dialog', { name: 'Delete segment?' })).toHaveCount(0);
    await expect(segmentRow(page, secondSegmentId)).toBeVisible();
    await expect(form.getByLabel('Estimated distance km')).toHaveValue('99');
    await expect(segmentRow(page, segmentId).locator('#trip-editor-segment-form')).toHaveCount(1);
    await expect(page.locator(`[data-segment-id="${segmentId}"][data-route-owner="draft"]`)).toHaveCount(1);
    await expect(page.locator(`[data-segment-id="${secondSegmentId}"][data-route-owner="saved"]`)).toHaveCount(1);
  });

  test('client-session visibility hides map route without changing API or reload defaults', async ({ page }) => {
    test.setTimeout(60_000);
    await signIn(page);
    await loadWorkspaceWithSegmentFixture(page);
    const routeCount = async () => page.locator('.leaflet-overlay-pane path').count();
    const initialRoutes = await routeCount();

    await page.getByRole('button', { name: 'Hide segment' }).first().click();
    await expect.poll(routeCount).toBeLessThan(initialRoutes);
    await page.reload();
    await expectMountedWorkspace(page);
    await expect.poll(routeCount).toBeGreaterThan(0);
  });

  test('reorders segments and applies the mocked order response after reload', async ({ page }) => {
    test.setTimeout(60_000);
    await signIn(page);
    const state = await loadWorkspaceWithSegmentFixture(page);
    await page.unroute(editorApiMatcher);
    // This fulfilled order response proves frontend reorder handling only; pair with backend/real endpoint tests for CRUD proof.
    await page.route(editorApiMatcher, async route => {
      if (route.request().method() === 'GET') {
        await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
        return;
      }

      expect(route.request().url()).toContain('/segments/order');
      const body = route.request().postDataJSON() as { segmentIds: string[] };
      state.segmentOrder = [...body.segmentIds];
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(segmentOrderMutationResult(state)) });
    });

    await expectSegmentOrder(page, [segmentId, secondSegmentId]);
    await segmentRow(page, segmentId).getByRole('button', { name: 'Drag to reorder segment' }).focus();
    await page.keyboard.press('ArrowDown');
    await expectSegmentOrder(page, [secondSegmentId, segmentId]);
    await page.reload();
    await expectMountedWorkspace(page);
    await expectSegmentOrder(page, [secondSegmentId, segmentId]);
  });
});

async function loadWorkspaceWithSegmentFixture(page: Page, customize?: (state: MutableEditorState) => void): Promise<MutableEditorState> {
  await page.unroute(editorApiMatcher).catch(() => undefined);
  const state = await loadEditorStateFixture(page) as MutableEditorState;
  prepareSegmentState(state);
  customize?.(state);
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

function prepareSegmentState(state: MutableEditorState): void {
  const [first, second] = Object.values(state.placesById) as Array<Record<string, any>>;
  if (!first || !second) {
    throw new Error('Configured Trip Editor fixture must contain at least two places for segment coverage.');
  }

  state.permissions.canEditSegments = true;
  state.segmentsById[segmentId] = segmentFixture(state, segmentId, first.id, second.id, 'PW first segment', [[23, 37], [24, 38]]);
  state.segmentsById[secondSegmentId] = segmentFixture(state, secondSegmentId, second.id, first.id, 'PW second segment', null);
  state.segmentOrder = [segmentId, secondSegmentId];
}

function segmentFixture(state: MutableEditorState, id: string, fromPlaceId: string, toPlaceId: string, notesHtml: string, route: any): Record<string, any> {
  return {
    id,
    tripId: state.tripId,
    fromPlaceId,
    toPlaceId,
    mode: 'walk',
    estimatedDistanceKm: 2,
    estimatedDurationMinutes: 30,
    notesHtml,
    route: route ? { type: 'LineString', coordinates: route } : null,
    displayOrder: id === segmentId ? 1 : 2,
    capabilities: { canEdit: true, canRename: false, canDelete: true, canReorder: true, canMove: false, canAddChildren: false, canTargetForSearchAdd: false }
  };
}

async function openEditableSegment(page: Page): Promise<void> {
  await segmentRow(page, segmentId).locator('.trip-editor-list-button').click();
  await expect(page.getByRole('heading', { name: /Edit Segment -/ })).toBeVisible();
}

function segmentRow(page: Page, id: string) {
  return page.locator(`[data-segment-id="${id}"]`);
}

async function expectSegmentOrder(page: Page, expected: string[]): Promise<void> {
  await expect.poll(async () => {
    return await page.locator('[data-segment-id]').evaluateAll(rows => rows.map(row => (row as HTMLElement).dataset.segmentId));
  }).toEqual(expected);
}

function segmentMutationResult(segment: Record<string, any>, order: string[] | null): Record<string, any> {
  return {
    success: true,
    data: segment,
    affected: affectedSlices([segment], order),
    deletedIds: { regions: [], places: [], areas: [], segments: [], tags: [] },
    warnings: []
  };
}

function segmentOrderMutationResult(state: MutableEditorState): Record<string, any> {
  return {
    success: true,
    data: { segmentOrder: state.segmentOrder },
    affected: affectedSlices(state.segmentOrder.map((id: string) => state.segmentsById[id]), state.segmentOrder),
    deletedIds: { regions: [], places: [], areas: [], segments: [], tags: [] },
    warnings: []
  };
}

function affectedSlices(segments: Record<string, any>[], segmentOrder: string[] | null): Record<string, any> {
  return {
    metadata: null,
    regions: [],
    regionOrder: null,
    places: [],
    placeOrdersByRegionId: {},
    areas: [],
    areaOrdersByRegionId: {},
    segments,
    segmentOrder,
    tags: [],
    tagOrder: null,
    visitProgress: null,
    options: null
  };
}
