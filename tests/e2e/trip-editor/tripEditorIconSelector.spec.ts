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

const regionId = '00000000-0000-0000-0000-000000288301';
const placeId = '00000000-0000-0000-0000-000000288302';
const iconNames = [
  'camera', 'star', 'marker', 'anchor', 'atm', 'barbecue', 'beach', 'bike', 'boat', 'camping', 'car', 'drink',
  'eat', 'flag', 'flight', 'hotel', 'map', 'museum', 'park', 'parking', 'train', 'walk', 'water', 'wifi'
];
const editorApiMatcher = new RegExp(`${editorApiPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?:/.*)?$`, 'i');

test.describe('Trip Editor icon selector', () => {
  test('filters, selects, saves, and stays contained across responsive themes', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await signIn(page);
    const requests: Record<string, any>[] = [];
    await loadWorkspaceWithIconFixture(page, requests);

    await openPlace(page);
    const selector = page.locator('[data-icon-selector]');
    await expect(selector.locator('[data-icon-selector-selected-name]')).toHaveText('camera');
    await expectLoadedImages(selector.locator('[data-icon-selector-selected-image]'));

    await openIconSelector(page);
    await expectIconSelectorOptionsRender(page);
    await expectIconSelectorScrolls(page);
    await expectNoPageOverflow(page);
    await captureEvidence(page, testInfo, 'desktop-light-icon-selector-open');

    await setTheme(page, 'dark');
    await expectNoPageOverflow(page);
    await captureEvidence(page, testInfo, 'desktop-dark-icon-selector-open');
    await setTheme(page, 'light');

    await filterIconSelector(page, 'star');
    await expectOnlyMatchingIconOptions(page, 'star');
    await page.locator('[data-icon-selector-search]').press('Enter');
    await expect(selector.locator('[data-icon-selector-selected-name]')).toHaveText('star');
    await expect(selector.locator('[data-icon-selector-selected-image]')).toHaveAttribute('src', /\/marker\/bg-blue\/star\.png$/);
    await expectLoadedImages(selector.locator('[data-icon-selector-selected-image]'));

    await openIconSelector(page);
    await page.locator('[data-icon-selector-search]').press('Escape');
    await expect(page.locator('[data-icon-selector-panel]')).toHaveCount(0);

    await openIconSelector(page);
    await page.keyboard.press('Tab');
    await expect(page.locator('[data-icon-selector-panel]')).toHaveCount(0);

    await page.getByRole('button', { name: 'Save Place' }).click();
    await expect.poll(() => requests.length).toBe(1);
    expect(requests[0].iconName).toBe('star');
    await expect(page.locator('.trip-editor-save-state').filter({ hasText: /Place saved/i }).first()).toBeVisible();

    await page.reload();
    await expectMountedWorkspace(page);
    await openPlace(page);
    await expect(page.locator('[data-icon-selector-selected-name]')).toHaveText('star');
    await expectLoadedImages(page.locator('[data-icon-selector-selected-image]'));

    await page.setViewportSize({ width: 390, height: 900 });
    await openIconSelector(page);
    await expectNoPageOverflow(page);
    await captureEvidence(page, testInfo, 'narrow-light-icon-selector-open');
    await setTheme(page, 'dark');
    await expectNoPageOverflow(page);
    await captureEvidence(page, testInfo, 'narrow-dark-icon-selector-open');
  });
});

async function loadWorkspaceWithIconFixture(page: Page, requests: Record<string, any>[]): Promise<void> {
  await page.unroute(editorApiMatcher).catch(() => undefined);
  const state = await loadEditorStateFixture(page) as MutableEditorState;
  prepareIconState(state);
  await page.route(editorApiMatcher, async route => routeEditorState(route, state, requests));
  await page.goto(absoluteUrl(editorPath));
  await expectMountedWorkspace(page);
}

async function routeEditorState(route: Route, state: MutableEditorState, requests: Record<string, any>[]): Promise<void> {
  const request = route.request();
  if (request.method() === 'GET') {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
    return;
  }

  if (request.method() === 'PUT' && request.url().includes(`/places/${placeId}`)) {
    const body = request.postDataJSON() as Record<string, any>;
    requests.push(body);
    state.placesById[placeId] = { ...state.placesById[placeId], ...body };
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(mutationResult(state.placesById[placeId])) });
    return;
  }

  throw new Error(`Unexpected icon selector mutation ${request.method()} ${request.url()}`);
}

function prepareIconState(state: MutableEditorState): void {
  state.permissions.canEditRegions = true;
  state.permissions.canEditPlaces = true;
  state.options.iconNames = iconNames;
  state.regionsById = { [regionId]: { id: regionId, tripId: state.tripId, name: 'Icon Selector Region', notesHtml: '', coverImage: null, center: null, displayOrder: 1, isShadow: false, capabilities: editableCapabilities() } };
  state.regionOrder = [regionId];
  state.placesById = { [placeId]: { id: placeId, tripId: state.tripId, regionId, name: 'Icon Selector Place', notesHtml: '<p>Place notes</p>', address: 'Athens, Greece', location: { latitude: 37.9838, longitude: 23.7275 }, iconName: 'camera', markerColor: 'bg-blue', displayOrder: 1, visitSummary: { placeId, visitCount: 0, isVisited: false, firstVisitAt: null, lastVisitAt: null }, capabilities: editableCapabilities() } };
  state.placeOrderByRegionId = { [regionId]: [placeId] };
  state.areasById = {};
  state.areaOrderByRegionId = { [regionId]: [] };
  state.segmentsById = {};
  state.segmentOrder = [];
}

function mutationResult(data: Record<string, any>): Record<string, any> {
  return {
    success: true,
    data,
    affected: { metadata: null, regions: [], regionOrder: null, places: [data], placeOrdersByRegionId: {}, areas: [], areaOrdersByRegionId: {}, segments: [], segmentOrder: null, tags: [], tagOrder: null, visitProgress: null, options: null },
    deletedIds: { regions: [], places: [], areas: [], segments: [], tags: [] },
    warnings: []
  };
}

function editableCapabilities(): Record<string, boolean> {
  return { canEdit: true, canRename: true, canDelete: true, canReorder: true, canMove: true, canAddChildren: true, canTargetForSearchAdd: true };
}

async function openPlace(page: Page): Promise<void> {
  await page.locator(`[data-place-id="${placeId}"]`).getByRole('button', { name: 'Edit', exact: true }).click();
  await expect(page.getByRole('heading', { name: /Edit Place - Icon Selector Place/ })).toBeVisible();
}

async function openIconSelector(page: Page): Promise<void> {
  await page.locator('[data-icon-selector-trigger]').click();
  await expect(page.locator('[data-icon-selector-panel]')).toBeVisible();
  await expect(page.locator('[data-icon-selector-search]')).toBeFocused();
}

async function filterIconSelector(page: Page, query: string): Promise<void> {
  await page.locator('[data-icon-selector-search]').fill(query);
  await expect(page.locator('[data-icon-selector-option]').first()).toBeVisible();
}

async function expectIconSelectorOptionsRender(page: Page): Promise<void> {
  const options = page.locator('[data-icon-selector-option]');
  await expect(options.first()).toBeVisible();
  await expect(options.first().locator('[data-icon-selector-option-name]')).not.toHaveText('');
  await expectLoadedImages(page.locator('[data-icon-selector-option-image]'));
}

async function expectOnlyMatchingIconOptions(page: Page, query: string): Promise<void> {
  const optionNames = await page.locator('[data-icon-selector-option-name]').allTextContents();
  expect(optionNames.length, 'Filtered icon selector should show at least one option.').toBeGreaterThan(0);
  expect(optionNames.every(name => name.toLocaleLowerCase().includes(query)), 'Every filtered icon option should match the search query.').toBe(true);
}

async function expectIconSelectorScrolls(page: Page): Promise<void> {
  const metrics = await page.locator('[data-icon-selector-options]').evaluate(element => {
    const styles = window.getComputedStyle(element);
    return { maxHeight: styles.maxHeight, overflowY: styles.overflowY, scrolls: element.scrollHeight > element.clientHeight };
  });
  expect(metrics.maxHeight, 'Icon options panel should enforce a max-height.').not.toBe('none');
  expect(metrics.overflowY, 'Icon options panel should scroll internally.').toBe('auto');
  expect(metrics.scrolls, 'Icon options panel should have internal scroll when many icons are available.').toBe(true);
}

async function expectLoadedImages(images: Locator): Promise<void> {
  const count = await images.count();
  expect(count, 'Expected at least one image to validate.').toBeGreaterThan(0);
  for (let index = 0; index < count; index += 1) {
    await expect.poll(async () => images.nth(index).evaluate(image => image instanceof HTMLImageElement && image.complete && image.naturalWidth > 0 && image.naturalHeight > 0)).toBe(true);
  }
}

async function expectNoPageOverflow(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => {
    const viewportWidth = document.documentElement.clientWidth;
    const documentOverflow = document.documentElement.scrollWidth - viewportWidth;
    const bodyOverflow = document.body ? document.body.scrollWidth - viewportWidth : 0;
    const containerOverflow = ['#trip-editor-app', '.trip-editor-shell', '.trip-editor-workspace']
      .map(selector => {
        const element = document.querySelector<HTMLElement>(selector);
        return { selector, overflow: element ? Math.max(0, element.getBoundingClientRect().right - viewportWidth) : 0 };
      })
      .filter(result => result.overflow > 2);
    return { documentOverflow, bodyOverflow, containerOverflow };
  });
  expect(overflow.documentOverflow, 'Icon selector should not introduce horizontal document overflow.').toBeLessThanOrEqual(2);
  expect(overflow.bodyOverflow, 'Icon selector should not introduce horizontal body overflow.').toBeLessThanOrEqual(2);
  expect(overflow.containerOverflow, 'Stable Trip Editor containers should fit within the viewport.').toEqual([]);
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate(value => document.documentElement.setAttribute('data-bs-theme', value), theme);
}

async function captureEvidence(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  await page.screenshot({ fullPage: true, path: testInfo.outputPath('screenshots', `${name}.png`) });
}
