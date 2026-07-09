import { expect, test, type Page } from '@playwright/test';

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

test('renders #335 tags and action contract items with readable and print parity actions', async ({ page }) => {
  await loadMockedViewer(page);

  await expect(page.getByLabel('Trip tags').getByText('Harbor')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Share' })).toHaveAttribute('href', '/Public/TripsNext/trip-1');
  await expect(page.getByRole('link', { name: 'Public URL' })).toHaveAttribute('href', '/Public/TripsNext/trip-1');
  await expect(page.getByRole('link', { name: 'Clone sign-in' })).toHaveAttribute('href', '/Identity/Account/Login');
  await expect(page.getByRole('button', { name: 'Readable' })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Print' })).toBeEnabled();
});

test('opens readable document mode from #335 readable action and preserves image-only notes', async ({ page }) => {
  await loadMockedViewer(page, mockViewerState({ mapSnapshotUrl: '/Public/Trips/trip-1/MapSnapshot' }));

  await page.getByRole('button', { name: 'Readable' }).click();

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

  await page.getByRole('button', { name: 'Readable' }).click();

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

  await page.getByRole('button', { name: 'Print' }).click();

  await expect(page.getByRole('dialog', { name: 'Readable trip itinerary' })).toBeVisible();
  await expect.poll(() => page.evaluate(() => (window as unknown as { __printed?: boolean }).__printed === true)).toBe(true);
  expect(new URL(page.url()).pathname).toBe('/__trip-viewer-test.html');
});

test('represents allowed non-get actions as deferred preview actions', async ({ page }) => {
  await loadMockedViewer(page, mockViewerState({ clonePost: true }));

  await expect(page.getByRole('button', { name: 'Clone' })).toBeDisabled();
  await expect(page.getByRole('link', { name: 'Clone sign-in' })).toHaveCount(0);
});

test('syncs mocked map marker and popup selection to the detail surface', async ({ page }) => {
  await loadMockedViewer(page);

  await page.getByAltText(/Harbor Cafe/).click();

  await expect(page.getByRole('heading', { name: 'Harbor Cafe' })).toBeVisible();
  await expect(page.getByLabel('Selection details').getByText('Dock coffee and breakfast.')).toBeVisible();
  await expect(page.locator('.leaflet-popup').getByText('Region')).toBeVisible();
  await expect(page.locator('.leaflet-popup').getByText('Harbor', { exact: true })).toBeVisible();
  await expect(page.locator('.leaflet-popup').getByText('Coordinates')).toBeVisible();
  await expect(page.locator('.leaflet-popup').getByText('Address')).toBeVisible();
  await expect(page.locator('.leaflet-popup').getByText('1 Dock Street')).toBeVisible();
  await page.getByRole('button', { name: 'Back' }).click();
  await expect(page.getByRole('button', { name: 'Harbor Cafe 1 Dock Street' })).toHaveClass(/trip-viewer-list-item--selected/);

  await page.getByRole('button', { name: /Trip Mocked Desktop Trip/ }).click();
  await page.getByAltText(/Harbor Cafe/).click();
  await page.getByRole('button', { name: 'View details' }).click();

  await expect(page.getByRole('heading', { name: 'Harbor Cafe' })).toBeVisible();
});

test('uses Wayfarer marker icons, attribution, and read-only map tools', async ({ page }) => {
  await page.addInitScript(() => {
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: {
        writeText: async (value: string) => {
          (window as unknown as { __copiedMapLink?: string }).__copiedMapLink = value;
        }
      }
    });
  });
  await loadMockedViewer(page);

  await expect(page.locator('.trip-viewer-map-marker__image[alt^="Harbor Cafe"]')).toHaveAttribute('src', /\/icons\/wayfarer-map-icons\/dist\/png\/marker\/bg-blue\/eat\.png$/);
  await expect(page.locator('.trip-viewer-map-marker__image[alt="Lookout"]')).toHaveAttribute('src', /\/icons\/wayfarer-map-icons\/dist\/png\/marker\/bg-green\/camera\.png$/);
  await expect(page.getByRole('link', { name: 'Wayfarer' })).toBeVisible();
  await expect(page.getByText('Test tiles')).toBeVisible();
  await expect(page.getByText(/Zoom: \d+/)).toBeVisible();

  await page.getByRole('button', { name: 'Copy map link' }).click();
  const copied = await page.waitForFunction(() => (window as unknown as { __copiedMapLink?: string }).__copiedMapLink).then(handle => handle.jsonValue() as Promise<string>);
  const copiedUrl = new URL(copied);
  expect(copiedUrl.searchParams.get('lat')).toMatch(/^-?\d+\.\d{6}$/);
  expect(copiedUrl.searchParams.get('lon')).toMatch(/^-?\d+\.\d{6}$/);
  expect(copiedUrl.searchParams.get('zoom')).toMatch(/^\d+$/);

  await page.getByRole('button', { name: 'Measure distance' }).click();
  const mapBox = await page.getByLabel('Trip map').boundingBox();
  expect(mapBox).not.toBeNull();
  await page.mouse.click(mapBox!.x + mapBox!.width * 0.45, mapBox!.y + mapBox!.height * 0.45);
  await page.mouse.click(mapBox!.x + mapBox!.width * 0.55, mapBox!.y + mapBox!.height * 0.55);
  await expect(page.locator('.trip-viewer-map-distance-label')).toContainText(/km/);
});

test('clearing search restores full trip hierarchy, selection, and map state', async ({ page }) => {
  await loadMockedViewer(page);

  await page.getByLabel('Search viewer content').getByPlaceholder('Search places, notes, tags').fill('dock coffee');
  await page.getByRole('button', { name: /place Harbor Cafe/ }).click();
  await expect(page.getByRole('heading', { name: 'Harbor Cafe' })).toBeVisible();
  await expect(page.locator('.trip-viewer-map-marker--selected .trip-viewer-map-marker__image[alt^="Harbor Cafe"]')).toBeVisible();

  await page.getByRole('button', { name: 'Back' }).click();
  await page.getByRole('button', { name: 'Clear' }).click();

  await expect(page.getByRole('heading', { name: 'Mocked Desktop Trip' })).toBeVisible();
  await expect(page.getByRole('button', { name: /Trip Mocked Desktop Trip/ })).toHaveClass(/trip-viewer-list-item--selected/);
  await expect(page.getByRole('button', { name: /Harbor Cafe/ })).toBeVisible();
  await expect(page.locator('.trip-viewer-map-marker--selected')).toHaveCount(0);
});

test('hides visit badges and counts when #335 progress flags deny display', async ({ page }) => {
  const state = mockViewerState({ canDisplayProgress: false, canDisplayCounts: false, canReadVisitCounts: false });
  await loadMockedViewer(page, state);

  await expect(page.getByAltText('Harbor Cafe')).toBeVisible();
  await expect(page.getByAltText(/visited 2 time/)).toHaveCount(0);

  await page.getByRole('button', { name: 'Harbor Cafe 1 Dock Street' }).click();

  await expect(page.getByLabel('Selection details').getByText('Visits')).toHaveCount(0);
  await expect(page.getByLabel('Selection details').getByText('Progress')).toHaveCount(0);
});

test('renders progress counts, filters, and private history only from #335 progress permissions', async ({ page }) => {
  await loadMockedViewer(page, mockViewerState({ canDisplayHistory: true, canReadVisitHistory: true }));

  const progress = page.getByLabel('Visit progress');
  await expect(progress.getByText('1 / 2 places')).toBeVisible();
  await expect(progress.getByText('50% visited')).toBeVisible();
  await expect(progress.getByRole('button', { name: 'Visited', exact: true })).toBeVisible();
  const regionProgress = progress.locator('.trip-viewer-progress__region');
  await expect(regionProgress.getByText('Harbor Cafe')).toBeVisible();
  await expect(progress.getByLabel('Visit history', { exact: true }).getByText('Harbor Cafe')).toBeVisible();
  await expect(progress.getByText('30 min')).toBeVisible();

  await progress.getByRole('button', { name: 'Not visited', exact: true }).click();
  await expect(regionProgress.getByText('Lookout')).toBeVisible();
  await expect(regionProgress.getByText('Harbor Cafe')).toHaveCount(0);
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

test('initial placeId query selects only a place returned by #335 state', async ({ page }) => {
  await loadMockedViewer(page, {
    state: mockViewerState({ initialView: { latitude: 1, longitude: 2, zoom: 3, source: 'query', canonicalQuery: 'lat=1&lon=2&zoom=3' } }),
    pageUrl: '/__trip-viewer-test.html?placeId=place-1&lat=1&lon=2&zoom=3'
  });

  await expect(page.getByRole('heading', { name: 'Harbor Cafe' })).toBeVisible();
  await page.getByRole('button', { name: 'Back' }).click();
  await expect(page.getByRole('button', { name: 'Harbor Cafe 1 Dock Street' })).toHaveClass(/trip-viewer-list-item--selected/);
  await expect(page.locator('.trip-viewer-map-marker--selected .trip-viewer-map-marker__image[alt^="Harbor Cafe"]')).toBeVisible();
  await expectMarkerInsideMap(page, '.trip-viewer-map-marker--selected .trip-viewer-map-marker__image[alt^="Harbor Cafe"]');
  expect(new URL(page.url()).searchParams.get('lat')).toBe('1.000000');
  expect(new URL(page.url()).searchParams.get('lon')).toBe('2.000000');

  await loadMockedViewer(page, { state: mockViewerState(), pageUrl: '/__trip-viewer-test.html?placeId=redacted-place' });
  await expect(page.getByRole('heading', { name: 'Mocked Desktop Trip' })).toBeVisible();
  await expect(page.locator('.trip-viewer-map-marker--selected')).toHaveCount(0);
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
  await page.getByRole('button', { name: 'Full trip' }).click();
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

async function loadMockedViewer(
  page: Page,
  input: unknown | {
    state?: unknown;
    status?: number;
    configMode?: 'private' | 'public' | 'embed';
    endpoint?: string;
    pageUrl?: string;
    onStateRequest?: (url: string) => void;
    shellChrome?: 'none' | 'late-shift';
  } = mockViewerState(),
  legacyStatus = 200
): Promise<void> {
  const options = isViewerLoadOptions(input)
    ? input
    : { state: input, status: legacyStatus };
  const status = options.status ?? 200;
  const endpoint = options.endpoint ?? '/viewer-state';
  const configMode = options.configMode ?? 'public';
  const shellChrome = options.shellChrome ?? 'none';
  const shellChromeMarkup = shellChrome === 'late-shift'
    ? `<style>
          .test-mvc-chrome, .test-mvc-footer { align-items: center; background: #f8f9fa; display: flex; padding: 0 24px; }
          .test-mvc-chrome { border-bottom: 1px solid #dee2e6; height: 96px; transition: height 40ms linear; }
          .test-mvc-chrome--expanded { height: 147px; }
          .test-mvc-footer { border-top: 1px solid #dee2e6; height: 25px; }
        </style>
        <header class="test-mvc-chrome">Authenticated shell</header>`
    : '';
  const shellFooterMarkup = shellChrome === 'late-shift'
    ? '<footer class="test-mvc-footer">Footer</footer>'
    : '';
  const shellShiftScript = shellChrome === 'late-shift'
    ? `<script>
          window.setTimeout(() => {
            document.querySelector('.test-mvc-chrome')?.classList.add('test-mvc-chrome--expanded');
          }, 80);
        </script>`
    : '';

  await page.route('**/__trip-viewer-test.html**', route => route.fulfill({
    contentType: 'text/html',
    body: `<!doctype html>
      <html>
        <head><title>Trip Viewer Test</title></head>
        <body>
          ${shellChromeMarkup}
          <div id="trip-viewer-app"
            data-trip-id="trip-1"
            data-trip-name="Mocked Desktop Trip"
            data-viewer-mode="${configMode}"
            data-viewer-state-endpoint="${endpoint}"
            data-public-view-url="/Public/TripsNext/trip-1"
            data-open-canonical-url="${configMode === 'embed' ? '/Public/TripsNext/trip-1' : ''}"
            data-tiles-url="/tiles/{z}/{x}/{y}.png"
            data-tile-attribution="Test tiles"
            data-asset-mode="development"></div>
          ${shellFooterMarkup}
          <script type="module" src="/ClientApps/trip-viewer/src/main.ts"></script>
          ${shellShiftScript}
        </body>
      </html>`
  }));

  await page.route('**/viewer-state**', route => {
    options.onStateRequest?.(route.request().url());
    return route.fulfill({
      contentType: 'application/json',
      status,
      body: JSON.stringify(options.state ?? mockViewerState())
    });
  });

  await page.route('**/tiles/**', route => route.fulfill({
    contentType: 'image/png',
    body: transparentPng()
  }));

  await page.route('**/Public/Trips/**/MapSnapshot', route => route.fulfill({
    contentType: 'image/png',
    body: transparentPng()
  }));

  await page.goto(options.pageUrl ?? '/__trip-viewer-test.html');
  if (status === 200) {
    await expect(page.locator('.trip-viewer-workspace')).toBeVisible();
  }
}

function mockViewerState(options?: {
  canDisplayProgress?: boolean;
  canDisplayCounts?: boolean;
  canReadVisitCounts?: boolean;
  clonePost?: boolean;
  canDisplayHistory?: boolean;
  canReadVisitHistory?: boolean;
  initialView?: { latitude: number; longitude: number; zoom: number; source: string; canonicalQuery: string };
  mapSnapshotUrl?: string | null;
  viewerMode?: 'private' | 'public' | 'embed';
}): unknown {
  const canDisplayProgress = options?.canDisplayProgress ?? true;
  const canDisplayCounts = options?.canDisplayCounts ?? true;
  const canReadVisitCounts = options?.canReadVisitCounts ?? true;
  const canDisplayHistory = options?.canDisplayHistory ?? false;
  const canReadVisitHistory = options?.canReadVisitHistory ?? false;
  const viewerMode = options?.viewerMode ?? 'public';
  const isEmbed = viewerMode === 'embed';

  return {
    viewerMode,
    trip: {
      id: 'trip-1',
      name: 'Mocked Desktop Trip',
      notes: notes('<p>Trip overview note.</p>', 'Trip overview note.'),
      isPublic: true,
      shareProgressEnabled: true,
      ownerDisplayName: 'Example Owner',
      coverImage: null,
      center: null,
      zoom: null,
      updatedAt: '2026-07-04T00:00:00Z',
      privateUrl: null,
      publicUrl: '/Public/TripsNext/trip-1',
      publicEmbedUrl: '/Public/TripsNext/trip-1?embed=true'
    },
    regionsById: {
      'region-1': {
        id: 'region-1',
        tripId: 'trip-1',
        name: 'Harbor',
        notes: notes('<p>Harbor region note.</p>', 'Harbor region note.'),
        coverImage: null,
        center: { latitude: 40.724, longitude: -74.025 },
        displayOrder: 1,
        placeIds: ['place-1', 'place-2'],
        areaIds: ['area-1']
      }
    },
    regionOrder: ['region-1'],
    placesById: {
      'place-2': {
        id: 'place-2',
        tripId: 'trip-1',
        regionId: 'region-1',
        name: 'Lookout',
        notes: notes('', ''),
        address: '',
        location: { latitude: 40.708, longitude: -74.01 },
        iconName: 'camera',
        markerColor: 'bg-green',
        displayOrder: 2,
        visitSummary: { placeId: 'place-2', visitCount: 0, isVisited: false, firstVisitAt: null, lastVisitAt: null }
      },
      'place-1': {
        id: 'place-1',
        tripId: 'trip-1',
        regionId: 'region-1',
        name: 'Harbor Cafe',
        notes: notes('<p>Dock coffee and breakfast.</p>', 'Dock coffee and breakfast.'),
        address: '1 Dock Street',
        location: { latitude: 40.701, longitude: -74.002 },
        iconName: 'eat',
        markerColor: 'bg-blue',
        displayOrder: 1,
        visitSummary: { placeId: 'place-1', visitCount: 2, isVisited: true, firstVisitAt: null, lastVisitAt: null }
      }
    },
    placeOrderByRegionId: { 'region-1': ['place-1', 'place-2'] },
    areasById: {
      'area-1': {
        id: 'area-1',
        tripId: 'trip-1',
        regionId: 'region-1',
        name: 'Waterfront Zone',
        notes: notes('<p><img src="/Public/ProxyImage?url=https%3A%2F%2Fimages.example.test%2Fwaterfront.jpg" loading="lazy"></p>', '', true),
        fillHex: '#0ea5e9',
        geometry: {
          type: 'Polygon',
          coordinates: [[[-74.012, 40.699], [-73.998, 40.699], [-73.998, 40.709], [-74.012, 40.709], [-74.012, 40.699]]]
        },
        displayOrder: 1
      }
    },
    areaOrderByRegionId: { 'region-1': ['area-1'] },
    segmentsById: {
      'segment-1': {
        id: 'segment-1',
        tripId: 'trip-1',
        fromPlaceId: 'place-1',
        toPlaceId: 'place-2',
        mode: 'walk',
        estimatedDistanceKm: 1.2,
        estimatedDurationMinutes: 18,
        notes: notes('<p>Waterfront walk.</p>', 'Waterfront walk.'),
        route: { type: 'LineString', coordinates: [[-74.002, 40.701], [-74.01, 40.708]] },
        fallbackStart: null,
        fallbackEnd: null,
        displayOrder: 1
      }
    },
    segmentOrder: ['segment-1'],
    tagsBySlug: { harbor: { id: 'tag-1', name: 'Harbor', slug: 'harbor' } },
    tagOrder: ['harbor'],
    visitProgress: {
      canDisplayProgress,
      canDisplayCounts,
      canDisplayHistory,
      totalPlaces: 2,
      visitedPlaces: canDisplayCounts ? 1 : 0,
      percentVisited: canDisplayCounts ? 50 : 0,
      placeSummariesByPlaceId: {
        'place-1': { placeId: 'place-1', visitCount: 2, isVisited: true, firstVisitAt: null, lastVisitAt: null }
      },
      historyRows: canDisplayHistory ? [{
        visitId: 'visit-1',
        placeId: 'place-1',
        regionId: 'region-1',
        startedAt: '2026-07-04T09:30:00Z',
        endedAt: '2026-07-04T10:00:00Z',
        durationMinutes: 30
      }] : []
    },
    permissions: {
      canViewPrivateState: false,
      canViewPublicState: !isEmbed,
      canViewEmbedState: isEmbed,
      isOwner: false,
      canReadNotes: true,
      canReadVisitCounts,
      canReadVisitHistory,
      canToggleShareProgress: false,
      canUseReadableMode: true,
      canPrint: true
    },
    actions: {
      edit: action(false),
      clone: isEmbed ? action(false) : options?.clonePost ? action(true, '/User/Trip/Clone/trip-1', 'POST') : action(false, '/Identity/Account/Login', 'GET', true),
      exportWayfarerKml: isEmbed ? action(false) : action(true, '/Trip/ExportWayfarerKml/trip-1'),
      exportGoogleMyMapsKml: isEmbed ? action(false) : action(true, '/Trip/ExportGoogleMyMapsKml/trip-1'),
      exportPdf: isEmbed ? action(false) : action(true, '/Trip/ExportPdf/trip-1'),
      share: isEmbed ? action(false) : action(true, '/Public/TripsNext/trip-1'),
      copyPublicUrl: isEmbed ? action(false) : action(true, '/Public/TripsNext/trip-1'),
      copyCoverUrl: action(false),
      copyMapSnapshotUrl: options?.mapSnapshotUrl ? action(true, options.mapSnapshotUrl) : action(false),
      fullscreen: action(false),
      openCanonical: isEmbed ? action(true, '/Public/TripsNext/trip-1') : action(false),
      readable: isEmbed ? action(false) : action(true),
      print: isEmbed ? action(false) : action(true)
    },
    map: {
      initialView: options?.initialView ?? { latitude: 20, longitude: 0, zoom: 2, source: 'world', canonicalQuery: 'lat=20&lon=0&zoom=2' },
      acceptedQueryParameters: ['lat', 'lon', 'lng', 'zoom'],
      emittedQueryParameters: ['lat', 'lon', 'zoom'],
      tileUrlTemplate: '/tiles/{z}/{x}/{y}.png',
      tileAttribution: 'Test tiles'
    }
  };
}

function isViewerLoadOptions(value: unknown): value is {
  state?: unknown;
  status?: number;
  configMode?: 'private' | 'public' | 'embed';
  endpoint?: string;
  pageUrl?: string;
  onStateRequest?: (url: string) => void;
} {
  return typeof value === 'object'
    && value !== null
    && ('state' in value || 'status' in value || 'configMode' in value || 'endpoint' in value || 'pageUrl' in value);
}

function notes(displayHtml: string, plainText: string, mediaOnly = false): unknown {
  return {
    displayHtml,
    plainText,
    hasRenderableContent: Boolean(displayHtml),
    hasTextContent: plainText.length > 0,
    hasMediaContent: mediaOnly
  };
}

function action(allowed: boolean, url: string | null = null, method: string | null = 'GET', requiresAuthentication = false): unknown {
  return { allowed, url, method, requiresAuthentication };
}

async function expectMarkerInsideMap(page: Page, markerSelector: string): Promise<void> {
  const mapBox = await page.getByLabel('Trip map').boundingBox();
  const markerBox = await page.locator(markerSelector).boundingBox();
  expect(mapBox).not.toBeNull();
  expect(markerBox).not.toBeNull();

  const markerCenterX = markerBox!.x + markerBox!.width / 2;
  const markerCenterY = markerBox!.y + markerBox!.height / 2;
  expect(markerCenterX).toBeGreaterThanOrEqual(mapBox!.x);
  expect(markerCenterX).toBeLessThanOrEqual(mapBox!.x + mapBox!.width);
  expect(markerCenterY).toBeGreaterThanOrEqual(mapBox!.y);
  expect(markerCenterY).toBeLessThanOrEqual(mapBox!.y + mapBox!.height);
}

function transparentPng(): Buffer {
  return Buffer.from(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=',
    'base64'
  );
}
