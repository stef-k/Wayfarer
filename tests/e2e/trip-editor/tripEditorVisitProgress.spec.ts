import { expect, test, type Page, type Route } from '@playwright/test';
import {
  absoluteUrl,
  editorApiPath,
  expectMountedWorkspace,
  loadEditorStateFixture,
  signIn,
  workspacePath
} from './tripEditorTestUtils';

type MutableEditorState = Record<string, any>;

const regionOneId = '00000000-0000-0000-0000-000000273101';
const regionTwoId = '00000000-0000-0000-0000-000000273102';
const visitedPlaceId = '00000000-0000-0000-0000-000000273201';
const notVisitedPlaceId = '00000000-0000-0000-0000-000000273202';
const missingHistoryPlaceId = '00000000-0000-0000-0000-000000273203';
const allVisitedPlaceId = '00000000-0000-0000-0000-000000273204';
const newerVisitId = '00000000-0000-0000-0000-000000273302';
const olderVisitId = '00000000-0000-0000-0000-000000273301';
const tiedVisitId = '00000000-0000-0000-0000-000000273300';
const editorApiMatcher = new RegExp(`${editorApiPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?:/.*)?$`, 'i');

test.describe.serial('Trip Editor visit progress and history', () => {
  test('opens from the sidebar and filters region-grouped place rows', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithVisitFixture(page, prepareMixedVisitState);

    await openVisits(page);
    const dialog = visitDialog(page);
    await expect(dialog).toContainText('2 / 3 places visited');
    await expect(visitPlaceRow(page, visitedPlaceId)).toContainText('PW visited place');
    await expect(visitPlaceRow(page, notVisitedPlaceId)).toContainText('PW not visited place');
    await expect(visitPlaceRow(page, missingHistoryPlaceId)).toContainText('PW missing history place');

    await dialog.getByRole('radio', { name: 'Visited', exact: true }).check();
    await expect(visitPlaceRow(page, visitedPlaceId)).toBeVisible();
    await expect(visitPlaceRow(page, missingHistoryPlaceId)).toBeVisible();
    await expect(visitPlaceRow(page, notVisitedPlaceId)).toHaveCount(0);
    await expect(dialog.getByRole('region', { name: 'PW visit region two' })).toHaveCount(0);

    await dialog.getByRole('radio', { name: 'Not visited', exact: true }).check();
    await expect(visitPlaceRow(page, notVisitedPlaceId)).toBeVisible();
    await expect(visitPlaceRow(page, visitedPlaceId)).toHaveCount(0);

    await dialog.getByRole('radio', { name: 'All', exact: true }).check();
    await expect(visitPlaceRow(page, visitedPlaceId)).toBeVisible();
    await expect(visitPlaceRow(page, notVisitedPlaceId)).toBeVisible();
  });

  test('shows place summaries and newest-first read-only history rows', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithVisitFixture(page, prepareMixedVisitState);

    await openVisits(page);
    const row = visitPlaceRow(page, visitedPlaceId);
    await expect(row).toContainText('Visited');
    await expect(row).toContainText('3 visits');
    await expect(row).toContainText('First visit');
    await expect(row).toContainText('2026-01-01 08:00 UTC');
    await expect(row).toContainText('Last visit');
    await expect(row).toContainText('2026-01-03 08:00 UTC');

    const historyIds = await row.locator('[data-visit-id]').evaluateAll(elements => elements.map(element => (element as HTMLElement).dataset.visitId));
    expect(historyIds).toEqual([tiedVisitId, newerVisitId, olderVisitId]);
    await expect(row.locator(`[data-visit-id="${newerVisitId}"]`)).toContainText('PW visited place');
    await expect(row.locator(`[data-visit-id="${newerVisitId}"]`)).toContainText('PW visit region one');
    await expect(row.locator(`[data-visit-id="${newerVisitId}"]`)).toContainText('2026-01-03 08:00 UTC');
    await expect(row.locator(`[data-visit-id="${newerVisitId}"]`)).toContainText('Open');
    await expect(row.locator(`[data-visit-id="${newerVisitId}"]`)).toContainText('Duration unavailable');
    await expect(row.locator(`[data-visit-id="${olderVisitId}"]`)).toContainText('45 min');
  });

  test('blocks Manage visit navigation when dirty draft discard is canceled', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithVisitFixture(page, prepareMixedVisitState);
    await page.locator('#trip-editor-metadata-form').getByLabel('Name').fill('Unsaved visit guard trip');

    await openVisits(page);
    await visitPlaceRow(page, visitedPlaceId).locator(`[data-visit-id="${newerVisitId}"]`).getByRole('link', { name: 'Manage visit' }).click();
    const discard = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(discard).toBeVisible();
    await discard.getByRole('button', { name: 'Keep editing' }).click();

    await expect(page).toHaveURL(new RegExp(`${workspacePath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}/?$`));
    await expect(visitDialog(page)).toBeVisible();
    await expect(page.locator('#trip-editor-metadata-form').getByLabel('Name')).toHaveValue('Unsaved visit guard trip');
  });

  test('blocks Manage visit navigation when active map-work cancel is rejected', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 1000 });
    await signIn(page);
    await loadWorkspaceWithVisitFixture(page, prepareMixedVisitState);

    await page.getByText('PW visited place').locator('xpath=ancestor::li[contains(@class, "trip-editor-place-row")]').getByRole('button', { name: 'Edit', exact: true }).click();
    await page.getByRole('button', { name: 'Pick on map' }).click();
    await clickMap(page, { xRatio: 0.45, yRatio: 0.45 });

    await openVisits(page);
    await visitPlaceRow(page, visitedPlaceId).locator(`[data-visit-id="${newerVisitId}"]`).getByRole('link', { name: 'Manage visit' }).click();
    const mapDiscard = page.getByRole('dialog', { name: 'Discard map editing changes?' });
    await expect(mapDiscard).toBeVisible();
    await mapDiscard.getByRole('button', { name: 'Keep editing' }).click();

    await expect(page).toHaveURL(new RegExp(`${workspacePath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}/?$`));
    await expect(page.getByRole('region', { name: 'Map work' })).toBeVisible();
  });

  test('navigates Manage visit with a current returnUrl after shared guard approval', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithVisitFixture(page, prepareMixedVisitState);
    await page.route(new RegExp(`/User/Visit/Edit/${newerVisitId}(?:[?#].*)?$`, 'i'), async route => {
      await route.fulfill({ status: 200, contentType: 'text/html', body: '<!doctype html><title>Manage visit</title>' });
    });
    await page.locator('#trip-editor-metadata-form').getByLabel('Name').fill('Unsaved visit navigation trip');

    await openVisits(page);
    await visitPlaceRow(page, visitedPlaceId).locator(`[data-visit-id="${newerVisitId}"]`).getByRole('link', { name: 'Manage visit' }).click();
    await page.getByRole('dialog', { name: 'Discard changes?' }).getByRole('button', { name: 'Discard' }).click();

    await expect(page).toHaveURL(url => {
      return url.pathname === `/User/Visit/Edit/${newerVisitId}` && url.searchParams.get('returnUrl') === workspacePath;
    });
  });

  test('renders required empty states', async ({ page }) => {
    await signIn(page);
    await loadWorkspaceWithVisitFixture(page, prepareNoPlacesState);
    await openVisits(page);
    await expect(visitDialog(page)).toContainText('No places in this trip yet.');

    await loadWorkspaceWithVisitFixture(page, prepareNoVisitsState);
    await openVisits(page);
    await expect(visitDialog(page)).toContainText('No visit history yet.');
    await visitDialog(page).getByRole('radio', { name: 'Visited', exact: true }).check();
    await expect(visitDialog(page)).toContainText('No visited places yet.');

    await loadWorkspaceWithVisitFixture(page, prepareAllVisitedState);
    await openVisits(page);
    await visitDialog(page).getByRole('radio', { name: 'Not visited', exact: true }).check();
    await expect(visitDialog(page)).toContainText('All places have visits.');

    await loadWorkspaceWithVisitFixture(page, prepareMixedVisitState);
    await openVisits(page);
    await expect(visitPlaceRow(page, missingHistoryPlaceId)).toContainText('No visit history rows available for this place.');
  });
});

async function loadWorkspaceWithVisitFixture(page: Page, prepare: (state: MutableEditorState) => void): Promise<MutableEditorState> {
  await page.unroute(editorApiMatcher).catch(() => undefined);
  const state = await loadEditorStateFixture(page) as MutableEditorState;
  prepareBaseState(state);
  prepare(state);
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

function prepareBaseState(state: MutableEditorState): void {
  state.permissions.canReadVisitProgress = true;
  state.permissions.canEditPlaces = true;
  state.regionsById = {
    ...state.regionsById,
    [regionOneId]: regionFixture(state, regionOneId, 'PW visit region one'),
    [regionTwoId]: regionFixture(state, regionTwoId, 'PW visit region two')
  };
  state.regionOrder = [regionOneId, regionTwoId];
  state.placesById = {};
  state.placeOrderByRegionId = { [regionOneId]: [], [regionTwoId]: [] };
  state.areasById = {};
  state.areaOrderByRegionId = { [regionOneId]: [], [regionTwoId]: [] };
  state.segmentsById = {};
  state.segmentOrder = [];
}

function prepareMixedVisitState(state: MutableEditorState): void {
  state.placesById[visitedPlaceId] = placeFixture(state, regionOneId, visitedPlaceId, 'PW visited place', visitedSummary(visitedPlaceId, 3, '2026-01-01T08:00:00.000Z', '2026-01-03T08:00:00.000Z'));
  state.placesById[missingHistoryPlaceId] = placeFixture(state, regionOneId, missingHistoryPlaceId, 'PW missing history place', visitedSummary(missingHistoryPlaceId, 1, '2026-01-04T08:00:00.000Z', '2026-01-04T08:00:00.000Z'));
  state.placesById[notVisitedPlaceId] = placeFixture(state, regionTwoId, notVisitedPlaceId, 'PW not visited place', notVisitedSummary(notVisitedPlaceId));
  state.placeOrderByRegionId[regionOneId] = [visitedPlaceId, missingHistoryPlaceId];
  state.placeOrderByRegionId[regionTwoId] = [notVisitedPlaceId];
  state.visitProgress = {
    totalPlaces: 3,
    visitedPlaces: 2,
    percentVisited: 67,
    placeSummariesByPlaceId: {
      [visitedPlaceId]: state.placesById[visitedPlaceId].visitSummary,
      [missingHistoryPlaceId]: state.placesById[missingHistoryPlaceId].visitSummary,
      [notVisitedPlaceId]: state.placesById[notVisitedPlaceId].visitSummary
    },
    historyRows: [
      historyFixture(olderVisitId, visitedPlaceId, regionOneId, '2026-01-01T08:00:00.000Z', '2026-01-01T08:45:00.000Z', 45),
      historyFixture(newerVisitId, visitedPlaceId, regionOneId, '2026-01-03T08:00:00.000Z', null, null),
      historyFixture(tiedVisitId, visitedPlaceId, regionOneId, '2026-01-03T08:00:00.000Z', '2026-01-03T09:15:00.000Z', 75)
    ]
  };
}

function prepareNoPlacesState(state: MutableEditorState): void {
  state.visitProgress = { totalPlaces: 0, visitedPlaces: 0, percentVisited: 0, placeSummariesByPlaceId: {}, historyRows: [] };
}

function prepareNoVisitsState(state: MutableEditorState): void {
  state.placesById[notVisitedPlaceId] = placeFixture(state, regionOneId, notVisitedPlaceId, 'PW not visited place', notVisitedSummary(notVisitedPlaceId));
  state.placeOrderByRegionId[regionOneId] = [notVisitedPlaceId];
  state.visitProgress = {
    totalPlaces: 1,
    visitedPlaces: 0,
    percentVisited: 0,
    placeSummariesByPlaceId: { [notVisitedPlaceId]: state.placesById[notVisitedPlaceId].visitSummary },
    historyRows: []
  };
}

function prepareAllVisitedState(state: MutableEditorState): void {
  state.placesById[allVisitedPlaceId] = placeFixture(state, regionOneId, allVisitedPlaceId, 'PW all visited place', visitedSummary(allVisitedPlaceId, 1, '2026-01-05T08:00:00.000Z', '2026-01-05T08:30:00.000Z'));
  state.placeOrderByRegionId[regionOneId] = [allVisitedPlaceId];
  state.visitProgress = {
    totalPlaces: 1,
    visitedPlaces: 1,
    percentVisited: 100,
    placeSummariesByPlaceId: { [allVisitedPlaceId]: state.placesById[allVisitedPlaceId].visitSummary },
    historyRows: [historyFixture('00000000-0000-0000-0000-000000273399', allVisitedPlaceId, regionOneId, '2026-01-05T08:00:00.000Z', '2026-01-05T08:30:00.000Z', 30)]
  };
}

function regionFixture(state: MutableEditorState, id: string, name: string): Record<string, any> {
  return {
    id,
    tripId: state.tripId,
    name,
    notesHtml: '',
    coverImage: null,
    center: null,
    displayOrder: 1,
    isShadow: false,
    capabilities: { canEdit: true, canRename: true, canDelete: true, canReorder: true, canMove: false, canAddChildren: true, canTargetForSearchAdd: true }
  };
}

function placeFixture(state: MutableEditorState, regionId: string, id: string, name: string, visitSummary: Record<string, any>): Record<string, any> {
  return {
    id,
    tripId: state.tripId,
    regionId,
    name,
    notesHtml: '',
    address: 'Visit progress fixture address',
    location: { latitude: 10, longitude: 20 },
    iconName: state.options.iconNames[0] ?? 'marker',
    markerColor: state.options.markerColorClasses[0] ?? 'bg-blue',
    displayOrder: 1,
    visitSummary,
    capabilities: { canEdit: true, canRename: true, canDelete: true, canReorder: true, canMove: true, canAddChildren: true, canTargetForSearchAdd: false }
  };
}

function visitedSummary(placeId: string, visitCount: number, firstVisitAt: string, lastVisitAt: string): Record<string, any> {
  return { placeId, visitCount, isVisited: true, firstVisitAt, lastVisitAt };
}

function notVisitedSummary(placeId: string): Record<string, any> {
  return { placeId, visitCount: 0, isVisited: false, firstVisitAt: null, lastVisitAt: null };
}

function historyFixture(visitId: string, placeId: string, regionId: string, startedAt: string, endedAt: string | null, durationMinutes: number | null): Record<string, any> {
  return { visitId, placeId, regionId, startedAt, endedAt, durationMinutes };
}

async function openVisits(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Visits' }).click();
  await expect(visitDialog(page)).toBeVisible();
}

function visitDialog(page: Page) {
  return page.getByRole('dialog', { name: 'Visit progress and history' });
}

function visitPlaceRow(page: Page, placeId: string) {
  return page.locator(`[data-visit-place-id="${placeId}"]`);
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
