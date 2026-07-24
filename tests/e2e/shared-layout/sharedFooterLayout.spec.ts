import { expect, test, type Locator, type Page } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

const footerLinks = '.site-footer__link';
const tinyPng = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=', 'base64');
const viewports = {
  desktop: { width: 1440, height: 900 },
  tablet: { width: 768, height: 1024 },
  mobile: { width: 375, height: 667 }
};

test.describe('shared standard-layout footer', () => {
  for (const [name, viewport] of Object.entries(viewports)) {
    test(`keeps the short public page contained at ${name}`, async ({ page }) => {
      await page.setViewportSize(viewport);
      await page.goto('/Home/Privacy');
      await expectShortStandardFooter(page);
      await expect(page.locator(footerLinks)).toHaveCount(4);
      await expectMatchingColors(page, 'light');
    });
  }

  for (const theme of ['light', 'dark'] as const) {
    test(`matches footer link colors across ${theme} theme interaction states`, async ({ page }) => {
      await page.addInitScript(value => localStorage.setItem('theme', value), theme);
      await page.goto('/Home/Privacy');
      await expect(page.locator('body')).toHaveAttribute('data-bs-theme', theme);
      await expectMatchingColors(page, theme);
      await expectMatchingInteractionColors(page, 'hover');
      await expectMatchingInteractionColors(page, 'focus');
    });
  }

  test('scrolls normal long content without the footer overlaying it', async ({ page }) => {
    await page.goto('/Home/Privacy');
    await page.locator('.site-content main').evaluate(main => {
      const probe = document.createElement('div');
      probe.textContent = 'Shared layout scroll probe';
      probe.style.height = '220vh';
      main.append(probe);
    });
    const documentHeight = await page.evaluate(() => document.documentElement.scrollHeight);
    expect(documentHeight).toBeGreaterThan(await page.evaluate(() => innerHeight));
    await page.evaluate(() => scrollTo(0, document.documentElement.scrollHeight));
    await expect(page.locator('.site-footer')).toBeInViewport();
    const footerDocumentBottom = await page.locator('.site-footer').evaluate(element => element.getBoundingClientRect().bottom + scrollY);
    expect(Math.abs(footerDocumentBottom - documentHeight)).toBeLessThanOrEqual(1);
  });

  for (const [name, viewport] of Object.entries(viewports)) {
    test(`keeps the authenticated trip shell and legacy viewer contained at ${name}`, async ({ page }) => {
      await page.setViewportSize(viewport);
      await signIn(page);
      await page.goto('/User/Trip');
      await expectCompactFooter(page);

      const viewerLink = page.locator('a[href^="/User/Trip/View/"]').first();
      await expect(viewerLink).toBeVisible();
      const viewerHref = await viewerLink.getAttribute('href');
      expect(viewerHref, 'The authenticated trip index should expose a real legacy viewer route.').toBeTruthy();

      await page.goto(viewerHref!);
      await expectLegacyViewerContained(page);
    });
  }

  for (const [name, viewport] of Object.entries(viewports)) {
    test(`keeps the public legacy viewer map and sidebar contained at ${name}`, async ({ page }) => {
      await page.setViewportSize(viewport);
      const publicViewerHref = await discoverPublicViewer(page);

      await page.goto(publicViewerHref);
      await expectLegacyViewerContained(page);
      await expectPublicViewerGeometry(page, viewport.width);
    });
  }

  test('keeps stored trip coordinates centered through a mobile sidebar visibility cycle', async ({ page }) => {
    await page.setViewportSize(viewports.mobile);
    const publicViewerHref = await discoverPublicViewer(page);
    await page.goto(publicViewerHref);

    const tripView = await page.locator('#trip-view').evaluate(element => ({
      lat: Number((element as HTMLElement).dataset.tripLat),
      lon: Number((element as HTMLElement).dataset.tripLon),
      zoom: Number((element as HTMLElement).dataset.tripZoom)
    }));
    await page.goto(`${publicViewerHref}?lat=${tripView.lat}&lon=${tripView.lon}&zoom=${tripView.zoom}`);

    const initialCenter = await waitForMapLocationToSettle(page);
    expectMapCenter(initialCenter, tripView);

    await page.locator('#btn-collapse-sidebar').click();
    await expect(page.locator('#sidebar-primary')).toHaveAttribute('data-collapsed', 'true');
    const firstCollapsedCenter = await waitForMapLocationToSettle(page);
    expectMapCenter(firstCollapsedCenter, tripView);

    await page.locator('#btn-show-sidebar').click();
    await expect(page.locator('#sidebar-primary')).toHaveAttribute('data-collapsed', 'false');
    const shownCenter = await waitForMapLocationToSettle(page);
    expectMapCenter(shownCenter, tripView);

    await page.locator('#btn-collapse-sidebar').click();
    await expect(page.locator('#sidebar-primary')).toHaveAttribute('data-collapsed', 'true');
    const cycleCenter = await waitForMapLocationToSettle(page);
    expectMapCenter(cycleCenter, tripView);
  });

  test('keeps the discovered public viewer embed free of the standard footer stylesheet', async ({ page }) => {
    const publicViewerHref = await discoverPublicViewer(page);

    await page.goto(`${publicViewerHref}?embed=true`);
    await expect(page.locator('#trip-view')).toBeVisible();
    await expect(page.locator('.site-footer')).toHaveCount(0);
    await expect(page.locator('link[href*="/css/shared-layout.css"]')).toHaveCount(0);
  });

  test('uses equivalent resolved attribution in Editor and authenticated, public, embed, and print Viewer maps', async ({ page }, testInfo) => {
    // Intercept the local tile proxy so this proof cannot generate public-provider traffic.
    await page.route(/\/Public\/tiles\/\d+\/\d+\/\d+\.png/i, route =>
      route.fulfill({ status: 200, contentType: 'image/png', body: tinyPng }));

    await signIn(page);
    await page.goto('/User/Trip');
    const editorHref = await page.locator('a[href^="/User/Trip/Edit/"]').first().getAttribute('href');
    expect(editorHref).toBeTruthy();
    await page.goto(editorHref!);
    await expect(page.locator('#trip-editor-app .leaflet-container')).toBeVisible();
    await expectResolvedMapAttribution(page);
    await page.screenshot({
      fullPage: true,
      path: testInfo.outputPath('screenshots', 'editor-provider-attribution.png')
    });

    await page.goto('/User/Trip');
    const authenticatedHref = await page.locator('a[href^="/User/Trip/View/"]').first().getAttribute('href');
    expect(authenticatedHref).toBeTruthy();
    await page.goto(authenticatedHref!);
    await expectResolvedMapAttribution(page);

    const publicViewerHref = await discoverPublicViewer(page);
    await page.goto(publicViewerHref);
    await expectResolvedMapAttribution(page);
    await expectLinkedViewerEmergencyFallback(page);
    await page.screenshot({
      fullPage: true,
      path: testInfo.outputPath('screenshots', 'viewer-provider-attribution.png')
    });

    await page.goto(`${publicViewerHref}?embed=true`);
    await expectResolvedMapAttribution(page);

    await page.goto(`${publicViewerHref}?embed=true&print=1`);
    await expectResolvedMapAttribution(page);
  });
});

async function signIn(page: Page): Promise<void> {
  const config = loadSharedLayoutConfig();
  await page.goto('/Identity/Account/Login?ReturnUrl=%2FUser%2FTrip');
  await page.getByLabel('Username').fill(config.username);
  await page.getByLabel('Password').fill(config.password);
  await Promise.all([
    page.waitForURL(url => !url.pathname.includes('/Identity/Account/Login')),
    page.getByRole('button', { name: 'Log in' }).click()
  ]);
}

async function expectShortStandardFooter(page: Page): Promise<void> {
  const footer = page.locator('.site-footer');
  await expect(footer).toBeVisible();
  expect(await footer.evaluate(element => getComputedStyle(element).position)).not.toBe('fixed');
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(await page.evaluate(() => innerWidth));
  const geometry = await footer.evaluate(element => ({
    documentHeight: document.documentElement.scrollHeight,
    footerBottom: element.getBoundingClientRect().bottom,
    viewportHeight: innerHeight
  }));
  expect(Math.abs(geometry.documentHeight - geometry.viewportHeight), 'The privacy page must remain a short document.').toBeLessThanOrEqual(1);
  expect(Math.abs(geometry.footerBottom - geometry.viewportHeight), 'The short-page footer must meet the viewport bottom.').toBeLessThanOrEqual(1);
}

async function expectCompactFooter(page: Page): Promise<void> {
  const footer = page.locator('.site-footer');
  await expect(footer).toBeVisible();
  expect(await footer.evaluate(element => getComputedStyle(element).position)).not.toBe('fixed');
  const geometry = await footer.evaluate(element => {
    const bounds = element.getBoundingClientRect();
    return { height: bounds.height, left: bounds.left, right: bounds.right, viewportWidth: innerWidth };
  });
  expect(geometry.height).toBeLessThan(100);
  expect(geometry.left).toBeGreaterThanOrEqual(0);
  expect(geometry.right).toBeLessThanOrEqual(geometry.viewportWidth);
}

async function expectLegacyViewerContained(page: Page): Promise<void> {
  await expect(page.locator('#trip-view')).toBeVisible();
  await expectCompactFooter(page);
  const viewerBox = await page.locator('#trip-view').boundingBox();
  const footerBox = await page.locator('.site-footer').boundingBox();
  expect(viewerBox!.y + viewerBox!.height).toBeCloseTo(footerBox!.y, 0);
  expect(await page.evaluate(() => document.documentElement.scrollHeight)).toBeLessThanOrEqual(await page.evaluate(() => innerHeight) + 1);
}

/** Verifies that Leaflet renders the server-resolved provider HTML without client inference. */
async function expectResolvedMapAttribution(page: Page): Promise<void> {
  const attribution = page.locator('.leaflet-control-attribution');
  await expect(attribution).toHaveAttribute('aria-label', 'Map attribution');
  await expect(attribution).toHaveAttribute('title', 'Map attribution');
  const osmLink = attribution.getByRole('link', { name: 'OpenStreetMap', exact: true });
  await expect(osmLink).toHaveCount(1);
  await expect(osmLink).toHaveAttribute('href', 'https://www.openstreetmap.org/copyright');
  await expect(osmLink).toBeVisible();

  const configured = await page.evaluate(() =>
    (window as typeof window & { wayfarerTileConfig?: { attribution?: string } })
      .wayfarerTileConfig?.attribution ?? '');
  expect(configured).toContain('https://www.openstreetmap.org/copyright');
  const normalizedConfigured = await page.evaluate(html => {
    const container = document.createElement('div');
    container.innerHTML = html;
    return container.innerHTML;
  }, configured);
  await expect(attribution).toContainText('OpenStreetMap contributors');
  expect(await attribution.innerHTML()).toContain(normalizedConfigured);
}

/** Verifies the Viewer module's emergency fallback without adding its layer to the map. */
async function expectLinkedViewerEmergencyFallback(page: Page): Promise<void> {
  const fallback = await page.evaluate(async () => {
    const target = window as typeof window & { wayfarerTileConfig?: unknown };
    const configured = target.wayfarerTileConfig;
    delete target.wayfarerTileConfig;
    try {
      const module = await import(`/js/retryTileLayer.js?attribution-fallback=${Date.now()}`);
      return module.createTileLayer().options.attribution as string;
    } finally {
      target.wayfarerTileConfig = configured;
    }
  });

  expect(fallback).toContain(
    '<a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors');
}

/** Finds a real canonical public viewer route from the public trip index. */
async function discoverPublicViewer(page: Page): Promise<string> {
  await page.goto('/Public/Trips');
  const publicViewerHref = await page.locator('a[href^="/Public/Trips/"]').evaluateAll(links =>
    links.map(link => link.getAttribute('href')).find(href => /^\/Public\/Trips\/[0-9a-f-]+$/i.test(href ?? '')));
  expect(publicViewerHref, 'The public index should provide a real public viewer route.').toBeTruthy();
  return publicViewerHref!;
}

/** Verifies the rendered map and sidebar geometry without depending on map tile availability. */
async function expectPublicViewerGeometry(page: Page, viewportWidth: number): Promise<void> {
  const geometry = await page.evaluate(() => {
    const map = document.querySelector<HTMLElement>('#mapContainer')!;
    const sidebar = document.querySelector<HTMLElement>('#sidebar-primary')!;
    const mapBounds = map.getBoundingClientRect();
    const sidebarBounds = sidebar.getBoundingClientRect();
    return {
      documentWidth: document.documentElement.scrollWidth,
      mapHeight: mapBounds.height,
      sidebarLeft: sidebarBounds.left,
      sidebarRight: sidebarBounds.right,
      sidebarWidth: sidebarBounds.width
    };
  });

  expect(geometry.mapHeight, 'The canonical public map must have rendered height.').toBeGreaterThan(100);
  expect(geometry.documentWidth, 'The canonical viewer must not widen the document.').toBeLessThanOrEqual(viewportWidth + 1);
  expect(geometry.sidebarLeft).toBeGreaterThanOrEqual(0);
  expect(geometry.sidebarRight).toBeLessThanOrEqual(viewportWidth + 1);
  if (viewportWidth < 576) expect(geometry.sidebarWidth).toBeLessThan(500);
  else expect(geometry.sidebarWidth).toBeCloseTo(500, 0);
}

/** Waits until the permalink remains stable beyond the sidebar and map animation window. */
async function waitForMapLocationToSettle(page: Page): Promise<{ lat: number; lon: number }> {
  return page.evaluate(() => new Promise<{ lat: number; lon: number }>((resolve, reject) => {
    const startedAt = performance.now();
    let lastUrl = location.href;
    let lastChangeAt = startedAt;
    const timer = window.setInterval(() => {
      if (location.href !== lastUrl) {
        lastUrl = location.href;
        lastChangeAt = performance.now();
      }

      if (performance.now() - lastChangeAt >= 1_100) {
        window.clearInterval(timer);
        const params = new URL(location.href).searchParams;
        resolve({ lat: Number(params.get('lat')), lon: Number(params.get('lon')) });
      } else if (performance.now() - startedAt >= 6_000) {
        window.clearInterval(timer);
        reject(new Error('Map permalink did not settle after movement.'));
      }
    }, 50);
  }));
}

/** Compares the permalink center with the server-owned trip center. */
function expectMapCenter(actual: { lat: number; lon: number }, expected: { lat: number; lon: number }): void {
  expect(actual.lat).toBeCloseTo(expected.lat, 4);
  expect(actual.lon).toBeCloseTo(expected.lon, 4);
}

async function expectMatchingColors(page: Page, theme: string): Promise<void> {
  const colors = await page.locator(footerLinks).evaluateAll(links => links.map(link => getComputedStyle(link).color));
  expect(colors, `${theme} default footer colors`).toEqual([colors[0], colors[0], colors[0], colors[0]]);
}

async function expectMatchingInteractionColors(page: Page, interaction: 'hover' | 'focus'): Promise<void> {
  const colors: string[] = [];
  for (const link of await page.locator(footerLinks).all()) {
    if (interaction === 'hover') await link.hover();
    else await focusWithKeyboard(page, link);
    colors.push(await link.evaluate(element => getComputedStyle(element).color));
  }
  expect(colors, `${interaction} footer colors`).toEqual([colors[0], colors[0], colors[0], colors[0]]);
}

async function focusWithKeyboard(page: Page, link: Locator): Promise<void> {
  for (let attempt = 0; attempt < 40; attempt++) {
    if (await link.evaluate(element => document.activeElement === element)) return;
    await page.keyboard.press('Tab');
  }
  throw new Error('Footer link was not reachable by keyboard navigation.');
}
