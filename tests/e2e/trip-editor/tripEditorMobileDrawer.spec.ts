import { expect, test, type Page, type Route, type TestInfo } from '@playwright/test';
import {
  absoluteUrl,
  activeEditorSurface,
  editorApiPath,
  editorPath,
  expectInitializedTripMap,
  expectMountedWorkspace,
  firstVisibleAddPlace,
  loadEditorStateFixture,
  signIn
} from './tripEditorTestUtils';

const geocodePath = /\/api\/trips\/[^/]+\/editor\/geocode\/search/i;
const editorApiMatcher = new RegExp(`${editorApiPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?:/.*)?$`, 'i');
type MutableEditorState = Record<string, any>;

test.describe.serial('Trip Editor mobile bottom drawer', () => {
  test('phone starts map-first with Trip as the drawer tab', async ({ page }, testInfo) => {
    await signIn(page);
    await openEditorWithTripSummaryFixture(page, { width: 390, height: 844 });

    await expect(page.getByRole('navigation', { name: 'Trip editor sections' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Trip', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('button', { name: 'Regions' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Segments' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Edit Trip' })).toBeVisible();
    const tripTab = page.locator('.trip-editor-mobile-drawer__tab--trip');
    await expect(tripTab).toContainText('Public trip');
    await expect(tripTab).toContainText('Share progress: Enabled');
    await expect(tripTab.getByRole('link', { name: 'Open public trip' })).toHaveAttribute('href', '/Public/Trips/mobile-drawer-public');
    await expect(tripTab.getByRole('link', { name: 'Open progress URL' })).toHaveAttribute('href', '/Public/Trips/mobile-drawer-progress');
    await expect(page.locator('.trip-editor-surface--docked .trip-editor-metadata')).toHaveCount(0);
    await expectDrawerState(page, 'expanded-view');
    await expectMapFirstPhoneLayout(page);
    await capture(page, testInfo, 'phone-light-initial-trip-map-first');

    await page.evaluate(() => document.documentElement.setAttribute('data-bs-theme', 'dark'));
    await expectMapFirstPhoneLayout(page);
    await capture(page, testInfo, 'phone-dark-initial-trip-map-first');
  });

  test('phone drawer exposes deterministic collapsed, peek, and expanded view states', async ({ page }) => {
    await signIn(page);
    await openEditorAt(page, { width: 390, height: 844 });

    await expectDrawerState(page, 'expanded-view');

    await page.getByRole('button', { name: 'Collapse' }).click();
    await expectDrawerState(page, 'collapsed');
    await expect(page.getByRole('navigation', { name: 'Trip editor sections' })).toBeHidden();
    await expectMapFirstPhoneLayout(page);

    await page.getByRole('button', { name: 'Peek' }).click();
    await expectDrawerState(page, 'peek');
    await expect(page.getByRole('navigation', { name: 'Trip editor sections' })).toBeVisible();
    await expectMapFirstPhoneLayout(page);

    await page.getByRole('button', { name: 'Expand' }).click();
    await expectDrawerState(page, 'expanded-view');
    await expect(page.getByRole('button', { name: 'Trip', exact: true })).toHaveAttribute('aria-pressed', 'true');
  });

  test('phone routes tab-owned editing, search-add, and map-work through the drawer', async ({ page }, testInfo) => {
    await signIn(page);
    await routeGeocode(page);
    await openEditorAt(page, { width: 390, height: 844 });

    await page.getByRole('button', { name: 'Regions' }).click();
    await expect(page.getByRole('button', { name: 'Regions' })).toHaveAttribute('aria-pressed', 'true');
    await capture(page, testInfo, 'phone-regions-tab');

    await page.getByRole('button', { name: 'Segments' }).click();
    await expect(page.getByRole('button', { name: 'Segments' })).toHaveAttribute('aria-pressed', 'true');
    await capture(page, testInfo, 'phone-segments-tab');

    await page.getByRole('button', { name: 'Trip', exact: true }).click();
    await page.getByRole('button', { name: 'Edit Trip' }).click();
    await expect(page.getByRole('button', { name: 'Trip', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('.trip-editor-surface--docked .trip-editor-metadata')).toBeVisible();
    await expectDrawerState(page, 'expanded-edit');
    await capture(page, testInfo, 'phone-dark-active-trip-edit');
    await page.locator('.trip-editor-surface--docked').getByRole('button', { name: 'Close' }).click();

    await page.getByRole('searchbox', { name: 'Map search' }).fill('drawer search place');
    await page.getByRole('region', { name: 'Map search' }).getByRole('button', { name: 'Search' }).click();
    await page.getByRole('button', { name: 'Drawer Search Place' }).click();
    await expectContainedSearch(page);
    await capture(page, testInfo, 'phone-search-results-add-place');
    await page.getByRole('button', { name: 'Add as place' }).click();
    await expect(page.getByRole('button', { name: 'Regions' })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('heading', { name: 'Add Place' })).toBeVisible();
    await capture(page, testInfo, 'phone-search-add-active-place-edit');

    await page.getByRole('button', { name: 'Pick on map' }).click();
    await expect(page.getByRole('region', { name: 'Map work' })).toBeVisible();
    await expectDrawerState(page, 'peek');
    await expect(page.getByRole('region', { name: 'Map search' })).toHaveCount(0);
    await expectMapFirstPhoneLayout(page);
    await capture(page, testInfo, 'phone-place-coordinate-map-work');
    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Cancel' }).click();
  });

  test('dirty metadata tab switch keeps editing on cancel and discards before switching', async ({ page }) => {
    await signIn(page);
    await openEditorAt(page, { width: 390, height: 844 });

    await page.getByRole('button', { name: 'Edit Trip' }).click();
    const form = activeEditorSurface(page);
    const name = form.getByLabel('Name');
    const originalName = await name.inputValue();
    const draftName = `${originalName} mobile tab draft`;
    await name.fill(draftName);

    await page.getByRole('button', { name: 'Segments' }).click();
    const keepDialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(keepDialog).toContainText('Discard unsaved trip changes before switching tabs?');
    await keepDialog.getByRole('button', { name: 'Keep editing' }).click();
    await expect(page.getByRole('button', { name: 'Trip', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(name).toHaveValue(draftName);

    await page.getByRole('button', { name: 'Segments' }).click();
    const discardDialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(discardDialog).toContainText('Discard unsaved trip changes before switching tabs?');
    await discardDialog.getByRole('button', { name: 'Discard' }).click();
    await expect(page.getByRole('button', { name: 'Segments' })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('#trip-editor-metadata-form')).toHaveCount(0);
  });

  test('dirty metadata blocks marker selection routing until discard', async ({ page }) => {
    await signIn(page);
    await openEditorAt(page, { width: 390, height: 844 });
    await expectInitializedTripMap(page);

    await page.getByRole('button', { name: 'Edit Trip' }).click();
    const form = activeEditorSurface(page);
    const name = form.getByLabel('Name');
    const originalName = await name.inputValue();
    const draftName = `${originalName} marker route draft`;
    await name.fill(draftName);

    await tapFirstSavedPlaceMarker(page);
    const keepDialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(keepDialog).toContainText('Discard unsaved trip changes before switching tabs?');
    await keepDialog.getByRole('button', { name: 'Keep editing' }).click();
    await expect(drawerTab(page, 'Trip')).toHaveAttribute('aria-pressed', 'true');
    await expect(name).toHaveValue(draftName);
    await expectSelectedMarkerCount(page, 0);
    await expect(page.locator('.leaflet-popup')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Clear Selection' })).toHaveCount(0);

    await tapFirstSavedPlaceMarker(page);
    const discardDialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(discardDialog).toContainText('Discard unsaved trip changes before switching tabs?');
    await discardDialog.getByRole('button', { name: 'Discard' }).click();
    await expect(drawerTab(page, 'Regions')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('#trip-editor-metadata-form')).toHaveCount(0);
    await expectSelectedMarkerCount(page, 1);
    await expect(page.locator('.leaflet-popup')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Clear Selection' })).toBeVisible();
  });

  test('dirty regions-owned area editor remains visible during same-tab marker selection', async ({ page }) => {
    await signIn(page);
    await openEditorAt(page, { width: 390, height: 844 });
    await expectInitializedTripMap(page);

    await page.getByRole('button', { name: 'Regions' }).click();
    await page.getByRole('button', { name: 'Add Area' }).first().click();
    const form = activeEditorSurface(page);
    const name = form.getByLabel('Name');
    await expect(page.getByRole('heading', { name: 'Add Area' })).toBeVisible();
    await name.fill('Mobile dirty area selection guard');

    await tapFirstSavedPlaceMarker(page);
    await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toHaveCount(0);
    await expect(drawerTab(page, 'Regions')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('heading', { name: 'Add Area' })).toBeVisible();
    await expect(name).toHaveValue('Mobile dirty area selection guard');
    await expectSelectedMarkerCount(page, 1);
    await expect(page.getByRole('button', { name: 'Clear Selection' })).toBeVisible();
  });

  test('dirty segment editor blocks marker selection routing until discard', async ({ page }) => {
    await signIn(page);
    await openEditorAt(page, { width: 390, height: 844 });
    await expectInitializedTripMap(page);

    await page.getByRole('button', { name: 'Segments' }).click();
    await page.getByRole('button', { name: 'Add Segment' }).click();
    const form = activeEditorSurface(page);
    const distance = form.getByLabel('Estimated distance km');
    await expect(page.getByRole('heading', { name: 'Add Segment' })).toBeVisible();
    await distance.fill('14');

    await tapFirstSavedPlaceMarker(page);
    const keepDialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(keepDialog).toContainText('Discard unsaved segment changes before switching tabs?');
    await keepDialog.getByRole('button', { name: 'Keep editing' }).click();
    await expect(drawerTab(page, 'Segments')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('heading', { name: 'Add Segment' })).toBeVisible();
    await expect(distance).toHaveValue('14');
    await expectSelectedMarkerCount(page, 0);
    await expect(page.locator('.leaflet-popup')).toHaveCount(0);

    await tapFirstSavedPlaceMarker(page);
    const discardDialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(discardDialog).toContainText('Discard unsaved segment changes before switching tabs?');
    await discardDialog.getByRole('button', { name: 'Discard' }).click();
    await expect(drawerTab(page, 'Regions')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('heading', { name: 'Add Segment' })).toHaveCount(0);
    await expectSelectedMarkerCount(page, 1);
    await expect(page.locator('.leaflet-popup')).toBeVisible();
  });

  test('dirty region and place editors cannot be hidden by mobile tab switching', async ({ page }) => {
    await signIn(page);
    await openEditorAt(page, { width: 390, height: 844 });

    await page.getByRole('button', { name: 'Regions' }).click();
    await page.locator('.trip-editor-region-card--normal').first().locator('.trip-editor-region-card__header').getByRole('button', { name: 'Edit' }).click();
    await expectDirtyTabSwitchGuard(page, {
      dirtyFieldLabel: 'Name',
      draftValue: 'Mobile dirty region tab guard',
      owningTab: 'Regions',
      targetTab: 'Segments',
      promptText: 'Discard unsaved region changes before switching tabs?',
      activeHeading: /Edit Region -/
    });

    await page.getByRole('button', { name: 'Regions' }).click();
    await firstVisibleAddPlace(page).click();
    await expectDirtyTabSwitchGuard(page, {
      dirtyFieldLabel: 'Name',
      draftValue: 'Mobile dirty place tab guard',
      owningTab: 'Regions',
      targetTab: 'Trip',
      promptText: 'Discard unsaved place changes before switching tabs?',
      activeHeading: 'Add Place'
    });
  });

  test('dirty segment editor cannot be hidden by mobile tab switching', async ({ page }) => {
    await signIn(page);
    await openEditorAt(page, { width: 390, height: 844 });

    await page.getByRole('button', { name: 'Segments' }).click();
    await page.getByRole('button', { name: 'Add Segment' }).click();
    await expectDirtyTabSwitchGuard(page, {
      dirtyFieldLabel: 'Estimated distance km',
      draftValue: '12',
      owningTab: 'Segments',
      targetTab: 'Trip',
      promptText: 'Discard unsaved segment changes before switching tabs?',
      activeHeading: 'Add Segment'
    });
  });

  test('metadata draft survives desktop-phone-desktop breakpoint transitions', async ({ page }) => {
    await signIn(page);
    await openEditorAt(page, { width: 1280, height: 900 });

    const nameInput = page.locator('#trip-editor-metadata-form').getByLabel('Name');
    const originalName = await nameInput.inputValue();
    const draftName = `${originalName} unsaved resize`;
    await nameInput.fill(draftName);

    await page.setViewportSize({ width: 390, height: 844 });
    await expect(page.getByRole('navigation', { name: 'Trip editor sections' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Trip', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('#trip-editor-metadata-form').getByLabel('Name')).toHaveValue(draftName);

    await page.locator('.trip-editor-surface--docked').getByRole('button', { name: 'Close' }).click();
    const discardDialog = page.getByRole('dialog', { name: 'Discard changes?' });
    await expect(discardDialog).toBeVisible();
    await discardDialog.getByRole('button', { name: 'Keep editing' }).click();
    await expect(discardDialog).toHaveCount(0);
    await expect(page.locator('#trip-editor-metadata-form').getByLabel('Name')).toHaveValue(draftName);

    await page.setViewportSize({ width: 1280, height: 900 });
    await expect(page.getByRole('navigation', { name: 'Trip editor sections' })).toHaveCount(0);
    await expect(page.locator('#trip-editor-metadata-form').getByLabel('Name')).toHaveValue(draftName);

    await page.getByRole('button', { name: 'Cancel / Reset' }).click();
    await expect(page.locator('#trip-editor-metadata-form').getByLabel('Name')).toHaveValue(originalName);
    await page.locator('.trip-editor-surface--docked').getByRole('button', { name: 'Close' }).click();
    await expect(page.getByRole('dialog', { name: 'Discard changes?' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Edit Trip' })).toBeVisible();
  });

  test('protected tablet and intermediate widths do not activate the drawer', async ({ page }, testInfo) => {
    await signIn(page);
    for (const viewport of [
      { name: 'exact-boundary-641', width: 641, height: 900 },
      { name: 'intermediate-700', width: 700, height: 900 },
      { name: 'tablet-768', width: 768, height: 1024 }
    ]) {
      await openEditorAt(page, { width: viewport.width, height: viewport.height });
      await expect(page.getByRole('navigation', { name: 'Trip editor sections' })).toHaveCount(0);
      await expect(page.locator('.trip-editor-sidebar--mobile-drawer')).toHaveCount(0);
      await expect(page.locator('.trip-editor-surface--docked .trip-editor-metadata')).toBeVisible();
      await expect(page.locator('.trip-editor-map-shell > .trip-editor-map-search')).toBeVisible();
      await capture(page, testInfo, `${viewport.name}-drawer-not-active`);
    }
  });

  test('desktop wide keeps the accepted sidebar and map shell', async ({ page }, testInfo) => {
    await signIn(page);
    await openEditorAt(page, { width: 1440, height: 1000 });

    await expect(page.getByRole('navigation', { name: 'Trip editor sections' })).toHaveCount(0);
    await expect(page.locator('.trip-editor-sidebar--mobile-drawer')).toHaveCount(0);
    await expect(page.locator('.trip-editor-surface--docked .trip-editor-metadata')).toBeVisible();
    await expect(page.locator('.trip-editor-map-shell > .trip-editor-map-search')).toBeVisible();

    const metrics = await page.evaluate(() => {
      const sidebar = document.querySelector<HTMLElement>('.trip-editor-sidebar')?.getBoundingClientRect();
      const map = document.querySelector<HTMLElement>('.trip-editor-map-shell')?.getBoundingClientRect();
      return { mapLeft: map?.left ?? 0, sidebarLeft: sidebar?.left ?? 0 };
    });
    expect(metrics.sidebarLeft).toBeLessThan(metrics.mapLeft);
    await capture(page, testInfo, 'desktop-wide-accepted-layout');
  });
});

async function openEditorAt(page: Page, viewport: { width: number; height: number }): Promise<void> {
  await page.setViewportSize(viewport);
  await page.goto(absoluteUrl(editorPath));
  await expectMountedWorkspace(page);
}

// Opens a read-only state variant with public progress URLs for Trip summary assertions.
async function openEditorWithTripSummaryFixture(page: Page, viewport: { width: number; height: number }): Promise<void> {
  await page.setViewportSize(viewport);
  await page.unroute(editorApiMatcher).catch(() => undefined);
  const state = await loadEditorStateFixture(page) as MutableEditorState;
  state.metadata.isPublic = true;
  state.metadata.shareProgressEnabled = true;
  state.metadata.publicUrl = '/Public/Trips/mobile-drawer-public';
  state.metadata.progressPublicUrl = '/Public/Trips/mobile-drawer-progress';
  await page.route(editorApiMatcher, async route => {
    if (route.request().method() !== 'GET') {
      throw new Error(`Unexpected Trip summary fixture mutation ${route.request().method()} ${route.request().url()}`);
    }

    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
  });
  await page.goto(absoluteUrl(editorPath));
  await expectMountedWorkspace(page);
}

async function expectDrawerState(page: Page, state: string): Promise<void> {
  await expect(page.locator('.trip-editor-sidebar--mobile-drawer')).toHaveAttribute('data-mobile-drawer-state', state);
}

async function expectDirtyTabSwitchGuard(
  page: Page,
  options: {
    activeHeading: string | RegExp;
    dirtyFieldLabel: string;
    draftValue: string;
    owningTab: string;
    promptText: string;
    targetTab: string;
  }
): Promise<void> {
  const form = activeEditorSurface(page);
  const field = form.getByLabel(options.dirtyFieldLabel);
  await expect(page.getByRole('heading', { name: options.activeHeading })).toBeVisible();
  await field.fill(options.draftValue);

  await drawerTab(page, options.targetTab).click();
  const keepDialog = page.getByRole('dialog', { name: 'Discard changes?' });
  await expect(keepDialog).toContainText(options.promptText);
  await keepDialog.getByRole('button', { name: 'Keep editing' }).click();
  await expect(drawerTab(page, options.owningTab)).toHaveAttribute('aria-pressed', 'true');
  await expect(field).toHaveValue(options.draftValue);

  await drawerTab(page, options.targetTab).click();
  const discardDialog = page.getByRole('dialog', { name: 'Discard changes?' });
  await expect(discardDialog).toContainText(options.promptText);
  await discardDialog.getByRole('button', { name: 'Discard' }).click();
  await expect(drawerTab(page, options.targetTab)).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('heading', { name: options.activeHeading })).toHaveCount(0);
}

function drawerTab(page: Page, name: string) {
  return page.getByRole('navigation', { name: 'Trip editor sections' }).getByRole('button', { name, exact: true });
}

async function tapFirstSavedPlaceMarker(page: Page): Promise<void> {
  const marker = page.locator('[data-place-marker-icon]').first();
  await expect(marker).toBeVisible();
  await marker.evaluate(element => {
    element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
  });
}

async function expectSelectedMarkerCount(page: Page, count: number): Promise<void> {
  await expect(page.locator('.trip-editor-map-marker--selected [data-place-marker-icon]')).toHaveCount(count);
}

async function expectMapFirstPhoneLayout(page: Page): Promise<void> {
  await expectInitializedTripMap(page);
  const metrics = await page.evaluate(() => {
    const workspace = document.querySelector<HTMLElement>('.trip-editor-workspace')?.getBoundingClientRect();
    const map = document.querySelector<HTMLElement>('.trip-editor-map')?.getBoundingClientRect();
    const drawer = document.querySelector<HTMLElement>('.trip-editor-sidebar--mobile-drawer')?.getBoundingClientRect();
    return {
      bodyWidth: document.body.scrollWidth,
      clientWidth: document.documentElement.clientWidth,
      documentHeight: document.scrollingElement?.scrollHeight ?? document.documentElement.scrollHeight,
      drawerTop: drawer?.top ?? 0,
      mapBottom: map?.bottom ?? 0,
      mapHeight: map?.height ?? 0,
      mapTop: map?.top ?? 0,
      viewportHeight: window.innerHeight,
      workspaceHeight: workspace?.height ?? 0,
      workspaceTop: workspace?.top ?? 0
    };
  });

  expect(metrics.bodyWidth, 'Phone layout should not create horizontal overflow.').toBeLessThanOrEqual(metrics.clientWidth + 1);
  expect(metrics.documentHeight, 'Phone drawer layout should keep page-level scroll bounded.').toBeLessThanOrEqual(metrics.viewportHeight + 2);
  expect(metrics.workspaceHeight, 'Phone workspace should be viewport-bounded.').toBeLessThanOrEqual(metrics.viewportHeight + 2);
  expect(metrics.mapTop, 'Map should begin at the top of the phone workspace.').toBeLessThanOrEqual(metrics.workspaceTop + 2);
  expect(metrics.mapHeight, 'Map should remain the primary phone workspace.').toBeGreaterThan(metrics.viewportHeight * 0.72);
  expect(metrics.drawerTop, 'Drawer should sit over the lower part of the map.').toBeGreaterThan(metrics.viewportHeight * 0.45);
  expect(metrics.mapBottom, 'Map should continue behind the drawer instead of being pushed below it.').toBeGreaterThan(metrics.drawerTop);
}

async function expectContainedSearch(page: Page): Promise<void> {
  const metrics = await page.locator('.trip-editor-map-search__results').evaluate(element => {
    const styles = getComputedStyle(element);
    return {
      maxHeight: styles.maxHeight,
      overflowY: styles.overflowY,
      right: element.getBoundingClientRect().right,
      viewportWidth: window.innerWidth
    };
  });
  expect(metrics.maxHeight).not.toBe('none');
  expect(['auto', 'scroll']).toContain(metrics.overflowY);
  expect(metrics.right).toBeLessThanOrEqual(metrics.viewportWidth + 1);
}

async function routeGeocode(page: Page): Promise<void> {
  await page.route(geocodePath, async (route: Route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        query: new URL(route.request().url()).searchParams.get('q') ?? '',
        attribution: 'Drawer search attribution',
        results: [{
          id: 'drawer:search-place',
          provider: 'drawer',
          name: 'Drawer Search Place',
          displayName: 'Drawer Search Place, Athens',
          address: 'Athens, Greece',
          category: 'tourism',
          type: 'attraction',
          latitude: 37.9715,
          longitude: 23.7257
        }]
      })
    });
  });
}

async function capture(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  await page.screenshot({ fullPage: true, path: testInfo.outputPath('screenshots', `${name}.png`) });
}
