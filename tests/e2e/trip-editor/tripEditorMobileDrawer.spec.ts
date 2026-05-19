import { expect, test, type Page, type Route, type TestInfo } from '@playwright/test';
import {
  absoluteUrl,
  editorPath,
  expectInitializedTripMap,
  expectMountedWorkspace,
  signIn
} from './tripEditorTestUtils';

const geocodePath = /\/api\/trips\/[^/]+\/editor\/geocode\/search/i;

test.describe.serial('Trip Editor mobile bottom drawer', () => {
  test('phone starts map-first with Trip as the drawer tab', async ({ page }, testInfo) => {
    await signIn(page);
    await openEditorAt(page, { width: 390, height: 844 });

    await expect(page.getByRole('navigation', { name: 'Trip editor sections' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Trip', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('button', { name: 'Regions' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Segments' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Edit Trip' })).toBeVisible();
    await expect(page.locator('.trip-editor-surface--docked .trip-editor-metadata')).toHaveCount(0);
    await expectMapFirstPhoneLayout(page);
    await capture(page, testInfo, 'phone-light-initial-trip-map-first');

    await page.evaluate(() => document.documentElement.setAttribute('data-bs-theme', 'dark'));
    await expectMapFirstPhoneLayout(page);
    await capture(page, testInfo, 'phone-dark-initial-trip-map-first');
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
    await expect(page.getByRole('region', { name: 'Map search' })).toHaveCount(0);
    await expectMapFirstPhoneLayout(page);
    await capture(page, testInfo, 'phone-place-coordinate-map-work');
    await page.getByRole('region', { name: 'Map work' }).getByRole('button', { name: 'Cancel' }).click();
  });

  test('protected tablet and intermediate widths do not activate the drawer', async ({ page }, testInfo) => {
    await signIn(page);
    for (const viewport of [
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
