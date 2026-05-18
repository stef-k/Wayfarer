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

type TripEditorContainmentMetrics = {
  bodyHeight: number;
  documentHeight: number;
  footerTop: number | null;
  sidebarClientHeight: number;
  sidebarOverflowY: string;
  sidebarScrollHeight: number;
  surfaceBodyOverflowY: string;
  viewportHeight: number;
  viewportWidth: number;
  workspaceHeight: number;
};

const regionId = '00000000-0000-0000-0000-000000288301';
const placeId = '00000000-0000-0000-0000-000000288302';
const iconNames = [
  'camera', 'star', 'marker', 'anchor', 'atm', 'barbecue', 'beach', 'bike', 'boat', 'camping', 'car', 'drink',
  'eat', 'flag', 'flight', 'hotel', 'map', 'museum', 'park', 'parking', 'train', 'walk', 'water', 'wifi'
];
const editorApiMatcher = new RegExp(`${editorApiPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?:/.*)?$`, 'i');

test.describe('Trip Editor icon selector', () => {
  test('filters, selects, sends a mocked place save, and stays contained across responsive themes', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await signIn(page);
    const requests: Record<string, any>[] = [];
    await loadWorkspaceWithIconFixture(page, requests);
    const beforePlaceEdit = await tripEditorContainmentMetrics(page);

    await openPlace(page);
    await expectSelectedPlaceRowProminent(page);
    await expectDockedEditorComfortable(page);
    await expectTripEditorContainment(page, beforePlaceEdit);
    const iconSelector = page.locator('[data-selector-kind="icon"]');
    const colorSelector = page.locator('[data-selector-kind="color"]');
    await expect(iconSelector.locator('[data-icon-selector-selected-name]')).toHaveText('camera');
    await expect(colorSelector.locator('[data-icon-selector-selected-name]')).toHaveText('Blue');
    await expectLoadedImages(iconSelector.locator('[data-icon-selector-selected-image]'));

    await openIconSelector(page);
    await expectIconSelectorOptionsRender(page);
    await expectIconSelectorScrolls(page);
    await expectSelectorPanelContained(page);
    await expectDockedEditorComfortable(page);
    await expectNoPageOverflow(page);
    await captureEvidence(page, testInfo, 'desktop-light-icon-selector-open');

    await setTheme(page, 'dark');
    await expectSelectedPlaceRowProminent(page);
    await expectDockedEditorComfortable(page);
    await expectNoPageOverflow(page);
    await captureEvidence(page, testInfo, 'desktop-dark-icon-selector-open');
    await setTheme(page, 'light');

    await filterIconSelector(page, 'star');
    await expectOnlyMatchingIconOptions(page, 'star');
    await page.locator('[data-icon-selector-search]').press('Enter');
    await expect(iconSelector.locator('[data-icon-selector-selected-name]')).toHaveText('star');
    await expect(iconSelector.locator('[data-icon-selector-selected-image]')).toHaveAttribute('src', /\/marker\/bg-blue\/star\.png$/);
    await expect(sidebarPlaceIcon(page)).toHaveAttribute('src', /\/marker\/bg-blue\/star\.png$/);
    await expect(mapMarkerIcon(page)).toHaveAttribute('src', /\/marker\/bg-blue\/star\.png$/);
    await expectLoadedImages(iconSelector.locator('[data-icon-selector-selected-image]'));

    await openColorSelector(page);
    await expectColorSelectorOptions(page);
    await expectSelectorPanelContained(page);
    await filterOpenSelector(page, 'purple');
    await expectOnlyMatchingIconOptions(page, 'purple');
    await page.locator('[data-selector-kind="color"] [data-icon-selector-search]').press('Enter');
    await expect(colorSelector.locator('[data-icon-selector-selected-name]')).toHaveText('Purple');
    await expect(iconSelector.locator('[data-icon-selector-selected-image]')).toHaveAttribute('src', /\/marker\/bg-purple\/star\.png$/);
    await expect(sidebarPlaceIcon(page)).toHaveAttribute('src', /\/marker\/bg-purple\/star\.png$/);
    await expect(mapMarkerIcon(page)).toHaveAttribute('src', /\/marker\/bg-purple\/star\.png$/);

    await openIconSelector(page);
    await page.locator('[data-icon-selector-search]').press('Escape');
    await expect(page.locator('[data-icon-selector-panel]')).toHaveCount(0);

    await openIconSelector(page);
    await page.keyboard.press('Tab');
    await expect(page.locator('[data-icon-selector-panel]')).toHaveCount(0);

    await page.getByRole('button', { name: 'Save Place' }).click();
    await expect.poll(() => requests.length).toBe(1);
    expect(requests[0].iconName).toBe('star');
    expect(requests[0].markerColor).toBe('bg-purple');
    await expect(page.locator('.trip-editor-save-state').filter({ hasText: /Place saved/i }).first()).toBeVisible();

    await page.reload();
    await expectMountedWorkspace(page);
    await openPlace(page);
    await expect(page.locator('[data-selector-kind="icon"] [data-icon-selector-selected-name]')).toHaveText('star');
    await expect(page.locator('[data-selector-kind="color"] [data-icon-selector-selected-name]')).toHaveText('Purple');
    await expectLoadedImages(page.locator('[data-icon-selector-selected-image]'));

    await page.setViewportSize({ width: 390, height: 900 });
    await openIconSelector(page);
    await expectSelectorPanelContained(page);
    await expectDockedEditorComfortable(page);
    await expectTripEditorContainment(page, await tripEditorContainmentMetrics(page));
    await expectNoPageOverflow(page);
    await captureEvidence(page, testInfo, 'narrow-light-icon-selector-open');
    await setTheme(page, 'dark');
    await expectSelectedPlaceRowProminent(page);
    await expectDockedEditorComfortable(page);
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
    // This fulfilled response proves selector request shape and UI handling, not persisted icon/color CRUD.
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
  state.options.markerColorClasses = ['bg-blue', 'bg-purple', 'bg-black', 'bg-green', 'bg-red'];
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
  await page.locator('[data-selector-kind="icon"] [data-icon-selector-trigger]').click();
  await expect(page.locator('[data-selector-kind="icon"] [data-icon-selector-panel]')).toBeVisible();
  await expect(page.locator('[data-selector-kind="icon"] [data-icon-selector-search]')).toBeFocused();
}

async function openColorSelector(page: Page): Promise<void> {
  await page.locator('[data-selector-kind="color"] [data-icon-selector-trigger]').click();
  await expect(page.locator('[data-selector-kind="color"] [data-icon-selector-panel]')).toBeVisible();
  await expect(page.locator('[data-selector-kind="color"] [data-icon-selector-search]')).toBeFocused();
}

async function filterIconSelector(page: Page, query: string): Promise<void> {
  await page.locator('[data-selector-kind="icon"] [data-icon-selector-search]').fill(query);
  await expect(page.locator('[data-icon-selector-option]').first()).toBeVisible();
}

async function filterOpenSelector(page: Page, query: string): Promise<void> {
  await page.locator('[data-icon-selector-panel] [data-icon-selector-search]').fill(query);
  await expect(page.locator('[data-icon-selector-panel] [data-icon-selector-option]').first()).toBeVisible();
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
  const metrics = await page.locator('[data-selector-kind="icon"] [data-icon-selector-options]').evaluate(element => {
    const styles = window.getComputedStyle(element);
    return { maxHeight: styles.maxHeight, overflowY: styles.overflowY, scrolls: element.scrollHeight > element.clientHeight };
  });
  expect(metrics.maxHeight, 'Icon options panel should enforce a max-height.').not.toBe('none');
  expect(metrics.overflowY, 'Icon options panel should scroll internally.').toBe('auto');
  expect(metrics.scrolls, 'Icon options panel should have internal scroll when many icons are available.').toBe(true);
}

async function expectColorSelectorOptions(page: Page): Promise<void> {
  const optionNames = await page.locator('[data-selector-kind="color"] [data-icon-selector-option-name]').allTextContents();
  expect(optionNames).toEqual(['Blue', 'Purple', 'Black', 'Green', 'Red']);
  await expect(page.locator('[data-selector-kind="color"] [data-color-selector-option-swatch]')).toHaveCount(5);
  await expect(page.locator('[data-selector-kind="color"] [data-icon-selector-search]')).toBeVisible();
}

async function expectSelectorPanelContained(page: Page): Promise<void> {
  const metrics = await page.locator('[data-icon-selector-panel]').evaluate(panel => {
    const panelBox = panel.getBoundingClientRect();
    const formBox = document.querySelector<HTMLElement>('#trip-editor-place-form')?.getBoundingClientRect();
    const surfaceBox = document.querySelector<HTMLElement>('.trip-editor-place-editor-row .trip-editor-surface')?.getBoundingClientRect();
    return {
      formWidth: formBox?.width ?? 0,
      panelRight: panelBox.right,
      panelWidth: panelBox.width,
      surfaceRight: surfaceBox?.right ?? 0,
      viewportWidth: document.documentElement.clientWidth
    };
  });
  expect(metrics.panelWidth, 'Open selector should have a comfortable row width.').toBeGreaterThanOrEqual(Math.min(300, metrics.formWidth - 4));
  expect(metrics.panelRight, 'Open selector should stay inside the viewport.').toBeLessThanOrEqual(metrics.viewportWidth + 1);
  expect(metrics.panelRight, 'Open selector should stay inside the editor surface.').toBeLessThanOrEqual(metrics.surfaceRight + 1);
}

async function expectSelectedPlaceRowProminent(page: Page): Promise<void> {
  const row = page.locator(`[data-place-id="${placeId}"]`);
  await expect(row).toHaveClass(/trip-editor-place-row--active/);
  const styles = await row.evaluate(element => {
    const computed = window.getComputedStyle(element);
    return {
      backgroundColor: computed.backgroundColor,
      borderColor: computed.borderLeftColor,
      borderLeftWidth: Number.parseFloat(computed.borderLeftWidth),
      borderTopWidth: Number.parseFloat(computed.borderTopWidth)
    };
  });
  expect(styles.borderLeftWidth, 'Selected place row should have a prominent left selection indicator.').toBeGreaterThanOrEqual(4);
  expect(styles.borderTopWidth, 'Selected place row should have a stronger selected-state border.').toBeGreaterThanOrEqual(2);
  expect(styles.borderColor, 'Selected place row should use an accent border, not an error color.').not.toBe('rgb(220, 53, 69)');
  expect(styles.backgroundColor, 'Selected place row should keep a visible selected-state background.').not.toBe('rgba(0, 0, 0, 0)');
}

async function expectDockedEditorComfortable(page: Page): Promise<void> {
  const metrics = await page.locator('.trip-editor-place-editor-row .trip-editor-surface--docked').evaluate(surface => {
    const surfaceBox = surface.getBoundingClientRect();
    const body = surface.querySelector<HTMLElement>('.trip-editor-surface__body');
    const bodyBox = body?.getBoundingClientRect();
    const sidebar = document.querySelector<HTMLElement>('.trip-editor-sidebar');
    return {
      bodyHeight: bodyBox?.height ?? 0,
      bodyOverflowY: body ? window.getComputedStyle(body).overflowY : '',
      panelBottom: document.querySelector<HTMLElement>('[data-icon-selector-panel]')?.getBoundingClientRect().bottom ?? 0,
      sidebarClientHeight: sidebar?.clientHeight ?? 0,
      sidebarOverflowY: sidebar ? window.getComputedStyle(sidebar).overflowY : '',
      sidebarScrollHeight: sidebar?.scrollHeight ?? 0,
      surfaceHeight: surfaceBox.height,
      viewportHeight: window.innerHeight,
      viewportWidth: window.innerWidth
    };
  });
  const desktopMinimum = Math.min(660, metrics.viewportHeight - 140);
  if (metrics.viewportWidth > 900) {
    expect(metrics.surfaceHeight, 'Docked place editor should have enough height for selector controls and fields.').toBeGreaterThanOrEqual(desktopMinimum);
    expect(metrics.bodyHeight, 'Docked place editor body should leave comfortable room for fields.').toBeGreaterThanOrEqual(420);
  } else {
    expect(metrics.sidebarScrollHeight, 'Narrow sidebar should keep editor overflow contained in sidebar scrolling.').toBeGreaterThan(metrics.sidebarClientHeight);
  }

  expect(metrics.bodyOverflowY, 'Docked place editor body should scroll internally.').toBe('auto');
  expect(['auto', 'scroll']).toContain(metrics.sidebarOverflowY);
  if (metrics.panelBottom > 0) {
    expect(metrics.panelBottom, 'Open selector panel should stay inside the usable editor surface.').toBeLessThanOrEqual(metrics.viewportHeight + 1);
  }
}

async function tripEditorContainmentMetrics(page: Page): Promise<TripEditorContainmentMetrics> {
  return await page.evaluate(() => {
    const sidebar = document.querySelector<HTMLElement>('.trip-editor-sidebar');
    const surfaceBody = document.querySelector<HTMLElement>('.trip-editor-place-editor-row .trip-editor-surface__body');
    const footer = document.querySelector<HTMLElement>('body footer, .footer');
    const workspace = document.querySelector<HTMLElement>('.trip-editor-workspace');
    return {
      bodyHeight: document.body?.scrollHeight ?? 0,
      documentHeight: document.documentElement.scrollHeight,
      footerTop: footer ? footer.getBoundingClientRect().top : null,
      sidebarClientHeight: sidebar?.clientHeight ?? 0,
      sidebarOverflowY: sidebar ? window.getComputedStyle(sidebar).overflowY : '',
      sidebarScrollHeight: sidebar?.scrollHeight ?? 0,
      surfaceBodyOverflowY: surfaceBody ? window.getComputedStyle(surfaceBody).overflowY : '',
      viewportHeight: window.innerHeight,
      viewportWidth: window.innerWidth,
      workspaceHeight: workspace?.getBoundingClientRect().height ?? 0
    };
  });
}

async function expectTripEditorContainment(page: Page, before: TripEditorContainmentMetrics): Promise<void> {
  const after = await tripEditorContainmentMetrics(page);
  expect(after.documentHeight - before.documentHeight, 'Opening place edit and selector should not expand document height.').toBeLessThanOrEqual(80);
  expect(after.bodyHeight - before.bodyHeight, 'Opening place edit and selector should not expand body height.').toBeLessThanOrEqual(80);
  if (after.viewportWidth > 900) {
    expect(after.workspaceHeight, 'Trip Editor workspace should remain bounded on desktop.').toBeLessThanOrEqual(after.viewportHeight + 1);
  }
  expect(after.sidebarScrollHeight, 'Place editor overflow should stay inside the sidebar/editor containers.').toBeGreaterThanOrEqual(after.sidebarClientHeight);
  expect(after.surfaceBodyOverflowY, 'Docked place editor body should scroll internally.').toBe('auto');
  if (before.footerTop !== null && after.footerTop !== null) {
    expect(after.footerTop - before.footerTop, 'Opening place edit and selector should not push the footer down.').toBeLessThanOrEqual(80);
  }
}

function sidebarPlaceIcon(page: Page): Locator {
  return page.locator(`[data-place-id="${placeId}"] [data-sidebar-place-icon]`);
}

function mapMarkerIcon(page: Page): Locator {
  return page.locator(`[data-place-marker-icon="${placeId}"]`);
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
