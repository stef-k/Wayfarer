import { expect, test } from '@playwright/test';
import { loadMockedViewer, mockViewerState } from './tripViewerFixtures';

test('renders mocked #335 desktop viewer state and sidebar detail selection', async ({ page }) => {
  await loadMockedViewer(page);

  await expect(page.getByRole('button', { name: /Trip Mocked Desktop Trip/ })).toBeVisible();
  await expect(page.getByRole('button', { name: /Trip Mocked Desktop Trip/ }).getByText('Harbor')).toBeVisible();
  await expect(page.getByRole('button', { name: /Region Harbor/ })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Harbor Cafe 1 Dock Street' })).toBeVisible();
  await expect(page.getByRole('button', { name: /Waterfront Zone/ })).toBeVisible();
  await expect(page.getByRole('button', { name: /Harbor Cafe to Lookout/ })).toBeVisible();
  await expect(page.locator('.trip-viewer-map .leaflet-tile-pane')).toHaveCount(1);

  const markerAlts = await page.locator('.trip-viewer-map-marker__image').evaluateAll(images => images.map(image => image.getAttribute('alt') ?? ''));
  expect(markerAlts.indexOf('Harbor Cafe, visited 2 time(s)')).toBeLessThan(markerAlts.indexOf('Lookout'));

  await page.getByRole('button', { name: /Waterfront Zone/ }).click();

  await expect(page.getByRole('heading', { name: 'Waterfront Zone' })).toBeVisible();
  await expect(page.getByLabel('Selection details').getByText('Media note', { exact: true })).toBeVisible();
  await expect(page.locator('.trip-viewer-detail__notes img')).toHaveAttribute('src', /\/Public\/ProxyImage\?url=/);
});

test('renders #335 tags beside the full-trip detail', async ({ page }) => {
  await loadMockedViewer(page);

  await expect(page.getByLabel('Trip tags').getByText('Harbor')).toBeVisible();
});

test('opens readable document mode from #335 readable action and preserves image-only notes', async ({ page }) => {
  await loadMockedViewer(page, mockViewerState({ mapSnapshotUrl: '/Public/Trips/trip-1/MapSnapshot' }));

  await page.getByRole('button', { name: 'Readable itinerary' }).click();

  const document = page.getByRole('dialog', { name: 'Readable trip itinerary' });
  await expect(document.getByRole('heading', { name: 'Mocked Desktop Trip' })).toBeVisible();
  await expect(document.getByRole('img', { name: 'Trip map snapshot' })).toHaveAttribute('src', '/Public/Trips/trip-1/MapSnapshot');
  await expect(document.locator('.trip-viewer-readable__region > header').getByRole('heading', { name: 'Harbor', exact: true })).toBeVisible();
  await expect(document.getByRole('heading', { name: 'Harbor Cafe', exact: true })).toBeVisible();
  await expect(document.getByRole('heading', { name: 'Waterfront Zone', exact: true })).toBeVisible();
  await expect(document.getByText('Media note', { exact: true })).toBeVisible();
  await expect(document.locator('.trip-viewer-notes img')).toHaveAttribute('src', /\/Public\/ProxyImage\?url=/);
  await expect(document.getByRole('button', { name: 'Back to top' })).toBeVisible();
});

test('shows a DTO-backed readable map fallback when no map snapshot URL is returned', async ({ page }) => {
  await loadMockedViewer(page, mockViewerState({ mapSnapshotUrl: null }));

  await page.getByRole('button', { name: 'Readable itinerary' }).click();

  const mapPreview = page.getByRole('dialog', { name: 'Readable trip itinerary' }).getByLabel('Readable map preview');
  await expect(mapPreview.getByRole('heading', { name: 'Map preview' })).toBeVisible();
  await expect(mapPreview.getByText('Map preview unavailable')).toBeVisible();
  await expect(mapPreview.getByText('Showing read-only map context from returned trip state.')).toBeVisible();
  await expect(mapPreview.getByText('Places')).toBeVisible();
  await expect(mapPreview.getByText('Places').locator('..').getByText('2', { exact: true })).toBeVisible();
  await expect(mapPreview.getByText('Initial center')).toBeVisible();
  await expect(mapPreview.getByText('20.00000, 0.00000')).toBeVisible();
});

test('invokes browser print from the #335 print action without export navigation', async ({ page }) => {
  await page.addInitScript(() => {
    Object.defineProperty(window, 'print', {
      configurable: true,
      value: () => { (window as unknown as { __printed?: boolean }).__printed = true; }
    });
  });
  await loadMockedViewer(page);

  await page.getByRole('button', { name: 'More actions' }).click();
  await page.getByRole('menuitem', { name: 'Print' }).click();

  await expect(page.getByRole('dialog', { name: 'Readable trip itinerary' })).toBeVisible();
  await expect.poll(() => page.evaluate(() => (window as unknown as { __printed?: boolean }).__printed === true)).toBe(true);
  expect(new URL(page.url()).pathname).toBe('/__trip-viewer-test.html');
});

test('hides visit badges and counts when #335 progress flags deny display', async ({ page }) => {
  const state = mockViewerState({ canDisplayProgress: false, canDisplayCounts: false, canReadVisitCounts: false }); await loadMockedViewer(page, state);
  await expect(page.getByAltText('Harbor Cafe')).toBeVisible(); await expect(page.getByAltText(/visited 2 time/)).toHaveCount(0);
  await page.getByRole('button', { name: 'Harbor Cafe 1 Dock Street' }).click();
  await expect(page.getByLabel('Selection details').getByText('Visits')).toHaveCount(0); await expect(page.getByLabel('Selection details').getByText('Progress')).toHaveCount(0);
});

test('renders progress counts, filters, and private history only from #335 progress permissions', async ({ page }) => {
  await loadMockedViewer(page, mockViewerState({ canDisplayHistory: true, canReadVisitHistory: true }));

  const progress = page.getByLabel('Visit progress');
  await expect(progress.getByText('1 / 2 places')).toBeVisible(); await expect(progress.getByText('50% visited')).toBeVisible(); await expect(progress.getByRole('button', { name: 'Visited', exact: true })).toBeVisible();
  const regionProgress = progress.locator('.trip-viewer-progress__region');
  await expect(regionProgress.getByText('Harbor Cafe')).toBeVisible(); await expect(progress.getByLabel('Visit history', { exact: true }).getByText('Harbor Cafe')).toBeVisible(); await expect(progress.getByText('30 min')).toBeVisible();

  await progress.getByRole('button', { name: 'Not visited', exact: true }).click();
  await expect(regionProgress.getByText('Lookout')).toBeVisible(); await expect(regionProgress.getByText('Harbor Cafe')).toHaveCount(0);
});

test('searches returned DTO text, notes, tags, addresses, and shows no-results state', async ({ page }) => {
  await loadMockedViewer(page);

  await page.getByLabel('Search viewer content').getByPlaceholder('Search places, notes, tags').fill('dock coffee');
  await expect(page.getByRole('button', { name: /place Harbor Cafe/ })).toBeVisible();
  await page.getByRole('button', { name: /place Harbor Cafe/ }).click();
  await expect(page.getByRole('heading', { name: 'Harbor Cafe' })).toBeVisible();
  await page.getByRole('button', { name: 'Back' }).click();

  await page.getByLabel('Search viewer content').getByPlaceholder('Search places, notes, tags').fill('harbor');
  await expect(page.getByRole('button', { name: /tag Harbor/ })).toBeVisible();

  await page.getByLabel('Search viewer content').getByPlaceholder('Search places, notes, tags').fill('not-present');
  await expect(page.getByText('No matching trip content.')).toBeVisible();
});

test('uses one desktop content surface plus map with contained viewport and sidebar hide show', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 820 });
  await loadMockedViewer(page);

  const contentSurface = page.getByRole('complementary', { name: 'Trip content', exact: true });
  await expect(contentSurface).toBeVisible();
  await expect(page.getByLabel('Trip map')).toBeVisible();
  await expect(page.locator('.trip-viewer-workspace > .trip-viewer-detail')).toHaveCount(0);
  await expect(page.locator('.trip-viewer-workspace > .trip-viewer-navigation')).toHaveCount(0);
  await expect(page.locator('.trip-viewer-mobile-drawer')).toBeHidden();

  await expect.poll(() => page.evaluate(() => {
    const root = document.getElementById('trip-viewer-app');
    const scrollingElement = document.scrollingElement;
    return Boolean(root)
      && root!.clientHeight <= window.innerHeight
      && scrollingElement !== null
      && scrollingElement.scrollHeight <= scrollingElement.clientHeight + 1;
  })).toBe(true);

  const widthWithPanel = await page.getByLabel('Trip map').evaluate(element => element.getBoundingClientRect().width);
  await page.getByRole('button', { name: 'Hide' }).click();
  await expect(contentSurface).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Show trip' })).toBeVisible();
  const widthWithoutPanel = await page.getByLabel('Trip map').evaluate(element => element.getBoundingClientRect().width);
  expect(widthWithoutPanel).toBeGreaterThan(widthWithPanel);

  await page.getByRole('button', { name: 'Show trip' }).click();
  await expect(contentSurface).toBeVisible();
});

test('contains desktop preview below authenticated MVC chrome after shell shifts', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 820 });
  await loadMockedViewer(page, {
    state: mockViewerState({ viewerMode: 'private' }),
    configMode: 'private',
    shellChrome: 'late-shift'
  });

  await expect(page.locator('.test-mvc-chrome--expanded')).toBeVisible();
  await expect(page.getByLabel('Trip map')).toBeVisible();

  await expect.poll(() => page.evaluate(() => {
    const root = document.getElementById('trip-viewer-app');
    const footer = document.querySelector('footer');
    const scrollingElement = document.scrollingElement;
    if (!root || !footer || !scrollingElement) return false;

    const rootRect = root.getBoundingClientRect();
    const footerRect = footer.getBoundingClientRect();
    return rootRect.bottom <= footerRect.top + 1
      && footerRect.bottom <= window.innerHeight + 1
      && scrollingElement.scrollHeight <= scrollingElement.clientHeight + 1;
  })).toBe(true);
});

test('desktop entity detail replaces contents and can return to full trip state', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 820 });
  await loadMockedViewer(page);

  await page.getByRole('button', { name: /Waterfront Zone/ }).click();

  await expect(page.getByLabel('Trip content').getByRole('heading', { name: 'Waterfront Zone' })).toBeVisible();
  await expect(page.getByLabel('Trip content').getByText('Media note', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: /Waterfront Zone/ })).toHaveCount(0);

  await page.getByRole('button', { name: 'Back' }).click();
  await expect(page.getByRole('button', { name: /Waterfront Zone/ })).toBeVisible();

  await page.getByRole('button', { name: /Waterfront Zone/ }).click();
  await page.getByRole('button', { name: 'Full trip', exact: true }).click();
  await expect(page.getByRole('button', { name: /Trip Mocked Desktop Trip/ })).toHaveClass(/trip-viewer-list-item--selected/);
  await expect(page.getByRole('button', { name: /Waterfront Zone/ })).toBeVisible();
});

test('uses a mobile map-first drawer with hierarchy, detail, collapse, and escape states', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await loadMockedViewer(page);

  await expect(page.getByLabel('Trip map')).toBeVisible();
  await expect(page.locator('.trip-viewer-mobile-drawer--peek')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Browse trip contents' })).toBeVisible();

  await page.getByRole('button', { name: 'Browse trip contents' }).click();
  await expect(page.locator('.trip-viewer-mobile-drawer--hierarchy')).toBeVisible();
  await expect(page.getByLabel('Trip hierarchy').getByRole('button', { name: 'Harbor Cafe 1 Dock Street' })).toBeVisible();

  await page.getByLabel('Trip hierarchy').getByRole('button', { name: 'Harbor Cafe 1 Dock Street' }).click();
  await expect(page.locator('.trip-viewer-mobile-drawer--detail')).toBeVisible();
  await expect(page.getByLabel('Selected trip details').getByRole('heading', { name: 'Harbor Cafe' })).toBeVisible();
  await expect(page.getByLabel('Selected trip details').getByText('Dock coffee and breakfast.')).toBeVisible();

  await page.keyboard.press('Escape');
  await expect(page.locator('.trip-viewer-mobile-drawer--peek')).toBeVisible();

  await page.getByLabel('Collapse trip drawer').click();
  await expect(page.locator('.trip-viewer-mobile-drawer--collapsed')).toBeVisible();
  await expect(page.getByLabel('Trip map')).toBeVisible();
});

test('syncs mobile map and popup selection into the drawer detail view', async ({ page }) => {
  await page.setViewportSize({ width: 430, height: 932 });
  await loadMockedViewer(page);

  await page.getByAltText(/Harbor Cafe/).click();
  await expect(page.locator('.trip-viewer-mobile-drawer--detail')).toBeVisible();
  await expect(page.getByLabel('Selected trip details').getByRole('heading', { name: 'Harbor Cafe' })).toBeVisible();

  await page.getByRole('button', { name: 'Back' }).click();
  await expect(page.locator('.trip-viewer-mobile-drawer--peek')).toBeVisible();

  await page.getByAltText(/Harbor Cafe/).click();
  await page.getByRole('button', { name: 'View details' }).click();

  await expect(page.locator('.trip-viewer-mobile-drawer--detail')).toBeVisible();
  await expect(page.getByLabel('Selected trip details').getByRole('heading', { name: 'Harbor Cafe' })).toBeVisible();
});

test('keeps image-only mobile notes inside the scrollable detail drawer', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await loadMockedViewer(page);

  await page.getByRole('button', { name: 'Browse trip contents' }).click();
  await page.getByLabel('Trip hierarchy').getByRole('button', { name: /Waterfront Zone/ }).click();

  const detailPanel = page.getByLabel('Selected trip details');
  await expect(detailPanel.getByText('Media note', { exact: true })).toBeVisible();
  await expect(detailPanel.locator('.trip-viewer-detail__notes img')).toHaveAttribute('src', /\/Public\/ProxyImage\?url=/);
  await expect(detailPanel.locator('.trip-viewer-notes')).toHaveCSS('overflow-y', 'auto');
});

test('renders mobile not-found and auth states without partial trip data', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await loadMockedViewer(page, mockViewerState(), 404);

  await expect(page.locator('strong').filter({ hasText: 'Trip not found' })).toBeVisible();
  await expect(page.getByRole('button', { name: /Trip Mocked Desktop Trip/ })).toHaveCount(0);

  await loadMockedViewer(page, mockViewerState(), 403);
  await expect(page.locator('strong').filter({ hasText: 'Trip unavailable' })).toBeVisible();
  await expect(page.getByRole('button', { name: /Trip Mocked Desktop Trip/ })).toHaveCount(0);
});

test('renders embed as a screenshot-safe map-only preview with public open action', async ({ page }) => {
  await page.setViewportSize({ width: 800, height: 450 });
  await loadMockedViewer(page, {
    state: mockViewerState({ viewerMode: 'embed' }),
    configMode: 'embed',
    endpoint: '/viewer-state?embed=true&lat=40.1&lon=25.2&zoom=8'
  });

  await expect(page.locator('.trip-viewer-preview--embed')).toBeVisible();
  await expect(page.getByLabel('Trip map')).toBeVisible();
  await expect(page.locator('.trip-viewer-sidebar')).toHaveCount(0);
  await expect(page.getByLabel('Selection details')).toHaveCount(0);
  await expect(page.locator('.trip-viewer-mobile-drawer')).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Open trip' })).toHaveAttribute('href', '/Public/TripsNext/trip-1');
  await expect(page.getByRole('link', { name: 'Edit' })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Clone' })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Wayfarer KML' })).toHaveCount(0);
  await expect(page.getByLabel('Trip map')).toHaveCSS('min-height', '450px');
  await expect(page.getByRole('button', { name: 'Measure distance' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Copy map link' })).toHaveCount(0);
});

test('fetches the shell-emitted embed state endpoint with lng compatibility params intact', async ({ page }) => {
  const requested: string[] = [];
  await page.setViewportSize({ width: 390, height: 844 });
  await loadMockedViewer(page, {
    state: mockViewerState({ viewerMode: 'embed' }),
    configMode: 'embed',
    endpoint: '/viewer-state?embed=true&lat=40.1&lng=25.2&zoom=8',
    onStateRequest: url => requested.push(url)
  });

  expect(requested).toContain('http://localhost:5173/viewer-state?embed=true&lat=40.1&lng=25.2&zoom=8');
  await expect(page.locator('.trip-viewer-mobile-drawer')).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Open trip' })).toBeVisible();
});

test('renders compact embed not-found and auth states without app surfaces', async ({ page }) => {
  await page.setViewportSize({ width: 800, height: 450 });
  await loadMockedViewer(page, {
    state: mockViewerState({ viewerMode: 'embed' }),
    configMode: 'embed',
    status: 404
  });

  await expect(page.locator('.trip-viewer-preview--embed')).toBeVisible();
  await expect(page.locator('strong').filter({ hasText: 'Trip not found' })).toBeVisible();
  await expect(page.locator('.trip-viewer-sidebar')).toHaveCount(0);
  await expect(page.locator('.trip-viewer-mobile-drawer')).toHaveCount(0);

  await loadMockedViewer(page, {
    state: mockViewerState({ viewerMode: 'embed' }),
    configMode: 'embed',
    status: 403
  });
  await expect(page.locator('strong').filter({ hasText: 'Trip unavailable' })).toBeVisible();
  await expect(page.locator('.trip-viewer-sidebar')).toHaveCount(0);
});
