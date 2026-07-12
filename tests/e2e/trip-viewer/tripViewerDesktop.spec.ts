import { expect, test } from '@playwright/test';
import { loadMockedViewer, mockViewerState } from './tripViewerFixtures';

test('renders mocked #335 desktop viewer state and sidebar detail selection', async ({ page }) => {
  await loadMockedViewer(page);

  await expect(page.getByRole('heading', { name: 'Mocked Desktop Trip', level: 1 })).toBeVisible();
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

test('opens readable document mode from #335 readable action and preserves image-only notes', async ({ page }) => {
  await loadMockedViewer(page, mockViewerState({ mapSnapshotUrl: '/Public/Trips/trip-1/MapSnapshot' }));

  await page.getByRole('button', { name: 'Readable itinerary' }).click();

  const document = page.getByRole('dialog', { name: 'Readable trip itinerary' });
  await expect(document.getByRole('heading', { name: 'Mocked Desktop Trip' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Back to trip notes' })).toHaveCount(0);
  await expect(document.getByRole('img', { name: 'Trip map snapshot' })).toHaveAttribute('src', '/Public/Trips/trip-1/MapSnapshot');
  await expect(document.getByRole('heading', { name: 'Regions', level: 2 })).toBeVisible();
  await expect(document.getByRole('heading', { name: 'Harbor', level: 3, exact: true })).toBeVisible();
  await expect(document.getByRole('heading', { name: 'Harbor Cafe', exact: true })).toBeVisible();
  await expect(document.getByRole('heading', { name: 'Waterfront Zone', exact: true })).toBeVisible();
  await expect(document.getByText('Media note', { exact: true })).toBeVisible();
  await expect(document.locator('.trip-viewer-notes img')).toHaveAttribute('src', /\/Public\/ProxyImage\?url=/);
  await expect(document.getByRole('button', { name: 'Back to top', includeHidden: true })).toBeHidden();
});

test('shows a DTO-backed readable map fallback when no map snapshot URL is returned', async ({ page }) => {
  await loadMockedViewer(page, mockViewerState({ mapSnapshotUrl: null }));

  await page.getByRole('button', { name: 'Readable itinerary' }).click();

  const mapPreview = page.getByRole('dialog', { name: 'Readable trip itinerary' }).getByLabel('Readable map preview');
  await expect(mapPreview.getByRole('heading', { name: 'Map' })).toBeVisible();
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

test('searches returned DTO text, notes, tags, addresses, and shows no-results state', async ({ page }) => {
  await loadMockedViewer(page);

  await page.getByLabel('Search viewer content').getByPlaceholder('Search places, notes, tags').fill('dock coffee');
  await expect(page.getByRole('button', { name: /place Harbor Cafe/ })).toBeVisible();
  await page.getByRole('button', { name: /place Harbor Cafe/ }).click();
  await expect(page.getByRole('heading', { name: 'Harbor Cafe' })).toBeVisible();
  await page.getByRole('button', { name: 'Back to content' }).click();

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

test('uses one sticky desktop command surface and one hierarchy collection', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 820 });
  await loadMockedViewer(page);

  const content = page.getByLabel('Trip content');
  await expect(content.getByRole('heading', { name: 'Mocked Desktop Trip', level: 1 })).toHaveCount(1);
  await expect(content.locator('.trip-viewer-command-header')).toHaveCount(1);
  await expect(content.locator('.trip-viewer-command-header .trip-viewer-search')).toHaveCount(1);
  await expect(content.locator('.trip-viewer-command-header .trip-viewer-actions')).toHaveCount(1);
  await expect(content.locator('.trip-viewer-hierarchy-body .trip-viewer-sidebar')).toHaveCount(1);
  await expect(content.locator('.trip-viewer-sidebar__overview')).toHaveCount(1);
  await expect(content.locator('.trip-viewer-sidebar__trip')).toHaveCount(0);
  await expect(content.locator('.trip-viewer-detail')).toHaveCount(0);
  await expect(content.getByRole('button', { name: 'Full trip', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Recenter full trip' })).toHaveCount(1);

  const headerTop = await content.locator('.trip-viewer-command-header').evaluate(element => element.getBoundingClientRect().top);
  await content.locator('.trip-viewer-hierarchy-body').evaluate(element => { element.scrollTop = element.scrollHeight; });
  await expect.poll(() => content.locator('.trip-viewer-command-header').evaluate(element => element.getBoundingClientRect().top)).toBe(headerTop);
});

test('renders server-gated compact visit progress with public and private disclosures', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 820 });
  await loadMockedViewer(page);

  const overview = page.locator('.trip-viewer-sidebar__overview');
  const progress = overview.getByLabel('Visit progress');
  await expect(progress).toBeVisible();
  await expect(progress.getByText('Example Owner')).toBeVisible();
  await expect(progress.getByText('1 / 2 places')).toBeVisible();
  await expect(progress.getByText('50% visited')).toBeVisible();
  await expect(progress.locator('[role="progressbar"]')).toHaveAttribute('aria-valuenow', '50');
  await expect(progress.getByRole('button', { name: 'View progress' })).toBeVisible();

  await progress.getByRole('button', { name: 'View progress' }).click();
  const publicDialog = page.getByRole('dialog', { name: 'Visit progress details' });
  await expect(publicDialog).toBeVisible();
  await expect(publicDialog.getByText('Harbor Cafe')).toBeVisible();
  await expect(publicDialog.getByText(/Visit history|2026-|All|Not visited|Visited/)).toHaveCount(0);
  await page.keyboard.press('Escape');
  await expect(publicDialog).toHaveCount(0);
  await expect(progress.getByRole('button', { name: 'View progress' })).toBeFocused();

  const denied = mockViewerState({ canDisplayProgress: false, canDisplayCounts: false, canReadVisitCounts: false });
  await loadMockedViewer(page, denied);
  await expect(page.getByLabel('Visit progress')).toHaveCount(0);

  const privateOwner = mockViewerState({ viewerMode: 'private', canDisplayHistory: true, canReadVisitHistory: true }) as any;
  privateOwner.permissions.isOwner = true;
  await loadMockedViewer(page, { state: privateOwner, configMode: 'private' });
  await expect(page.getByLabel('Visit progress').getByRole('button', { name: 'Visit history' })).toBeVisible();
  await page.getByRole('button', { name: 'Visit history' }).click();
  await expect(page.getByRole('dialog', { name: 'Visit history' }).getByText('Jul 4, 2026')).toBeVisible();
});

test('keeps visit progress out of embed and mounts its compact summary once on mobile', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await loadMockedViewer(page);
  await page.getByRole('button', { name: 'Browse trip contents' }).click();
  await expect(page.locator('.trip-viewer-sidebar__overview').getByLabel('Visit progress')).toHaveCount(1);

  await loadMockedViewer(page, { state: mockViewerState({ viewerMode: 'embed' }), configMode: 'embed' });
  await expect(page.getByLabel('Visit progress')).toHaveCount(0);
  await expect(page.getByRole('dialog', { name: /Visit/ })).toHaveCount(0);
});

test('mounts exactly one responsive command and hierarchy composition at each breakpoint', async ({ page }) => {
  for (const viewport of [
    { name: 'desktop', width: 1280, height: 820, compact: false },
    { name: 'tablet', width: 800, height: 1024, compact: true },
    { name: 'mobile', width: 390, height: 844, compact: true }
  ]) {
    await page.setViewportSize(viewport);
    await loadMockedViewer(page);

    if (viewport.compact) {
      await page.getByRole('button', { name: 'Browse trip contents' }).click();
      await expect(page.locator('.trip-viewer-content-surface')).toHaveCount(0);
      await expect(page.locator('.trip-viewer-mobile-drawer')).toHaveCount(1);
    } else {
      await expect(page.locator('.trip-viewer-content-surface')).toHaveCount(1);
      await expect(page.locator('.trip-viewer-mobile-drawer')).toHaveCount(0);
    }

    await expect(page.locator('.trip-viewer-command-header')).toHaveCount(1);
    await expect(page.getByRole('heading', { name: 'Mocked Desktop Trip', level: 1 })).toHaveCount(1);
    await expect(page.locator('.trip-viewer-actions')).toHaveCount(1);
    await expect(page.locator('.trip-viewer-sidebar__overview')).toHaveCount(1);
    await expect(page.locator('.trip-viewer-sidebar')).toHaveCount(1);
    if (viewport.compact) {
      await expect(page.locator('.trip-viewer-sidebar__notes-viewport')).toHaveCSS('max-height', '224px');
    }
  }
});

test('renders the scrollable trip overview once with ordered tags and display-safe notes', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 820 });
  const state = mockViewerState() as any;
  state.tagOrder = ['marina', 'missing', 'harbor'];
  state.tagsBySlug = {
    harbor: { id: 'tag-1', name: 'Harbor', slug: 'harbor' },
    marina: { id: 'tag-2', name: 'Marina', slug: 'marina' }
  };
  await loadMockedViewer(page, state);

  const content = page.getByLabel('Trip content');
  const overview = content.locator('.trip-viewer-sidebar__overview');
  await expect(overview).toHaveCount(1);
  await expect(overview.locator('.trip-viewer-tags li')).toHaveText(['Marina', 'Harbor']);
  await expect(overview.getByRole('heading', { name: 'Trip notes', level: 2 })).toHaveCount(1);
  await expect(overview.getByText('Trip overview note.', { exact: true })).toHaveCount(1);
  await expect(content.getByText('Trip overview note.', { exact: true })).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Back to trip notes' })).toHaveCount(0);

  state.trip.notes = {
    displayHtml: '<p><img src="/Public/ProxyImage?url=https%3A%2F%2Fimages.example.test%2Ftrip.jpg" loading="lazy"></p>',
    plainText: '',
    hasRenderableContent: true,
    hasTextContent: false,
    hasMediaContent: true
  };
  await loadMockedViewer(page, state);
  await expect(overview.locator('.trip-viewer-notes img')).toHaveCount(1);
  await expect(overview.getByText('Media note', { exact: true })).toHaveCount(1);

  state.trip.notes = { displayHtml: '', plainText: '', hasRenderableContent: false, hasTextContent: false, hasMediaContent: false };
  state.tagOrder = ['missing'];
  await loadMockedViewer(page, state);
  await expect(overview.locator('.trip-viewer-tags')).toHaveCount(0);
  await expect(overview.locator('.trip-viewer-notes')).toHaveCount(0);
});

test('bounds overflowing trip notes and returns only their viewport to its focused heading', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 820 });
  const state = mockViewerState() as any;
  state.trip.notes = {
    displayHtml: `<p>${'Long trip note. '.repeat(1400)}</p>`,
    plainText: 'Long trip note.',
    hasRenderableContent: true,
    hasTextContent: true,
    hasMediaContent: false
  };
  await loadMockedViewer(page, state);

  const overview = page.locator('.trip-viewer-sidebar__overview');
  const notesViewport = overview.locator('.trip-viewer-sidebar__notes-viewport');
  await expect(notesViewport).toHaveClass(/trip-viewer-sidebar__notes-viewport--overflowing/);
  await expect(notesViewport).toHaveCSS('max-height', '288px');
  await expect(page.getByRole('button', { name: 'Back to trip notes' })).toHaveCount(0);

  await notesViewport.evaluate(element => { element.scrollTop = 200; element.dispatchEvent(new Event('scroll')); });
  const back = page.getByRole('button', { name: 'Back to trip notes' });
  await expect(back).toBeVisible();
  await back.click();

  await expect.poll(() => notesViewport.evaluate(element => element.scrollTop)).toBe(0);
  await expect.poll(() => page.evaluate(() => document.activeElement?.id)).toBe('trip-viewer-trip-notes-heading');

  await notesViewport.evaluate(element => { element.scrollTop = 200; element.dispatchEvent(new Event('scroll')); });
  await expect(back).toBeVisible();
  await page.getByRole('button', { name: 'Readable itinerary' }).click();
  await expect(page.getByRole('button', { name: 'Back to trip notes' })).toHaveCount(0);
});

test('shares the search query across responsive surfaces without retaining duplicate sidebar DOM', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 820 });
  await loadMockedViewer(page);

  const search = page.getByLabel('Search viewer content').getByPlaceholder('Search places, notes, tags');
  await search.fill('Colombo');
  await page.setViewportSize({ width: 800, height: 1024 });
  await page.getByRole('button', { name: 'Browse trip contents' }).click();
  await expect(search).toHaveValue('Colombo');
  await expect(page.locator('.trip-viewer-content-surface')).toHaveCount(0);
  await expect(page.locator('.trip-viewer-mobile-drawer')).toHaveCount(1);

  await search.fill('Harbor');
  await page.setViewportSize({ width: 1280, height: 820 });
  await expect(search).toHaveValue('Harbor');
  await expect(page.locator('.trip-viewer-content-surface')).toHaveCount(1);
  await expect(page.locator('.trip-viewer-mobile-drawer')).toHaveCount(0);
});

test('returns outer contents to its sticky trip heading without affecting long trip notes', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 820 });
  const state = mockViewerState() as any;
  state.trip.notes = {
    displayHtml: `<p>${'Long trip note. '.repeat(1400)}</p>`,
    plainText: 'Long trip note.',
    hasRenderableContent: true,
    hasTextContent: true,
    hasMediaContent: false
  };
  await loadMockedViewer(page, state);

  const outer = page.locator('.trip-viewer-hierarchy-body');
  const notes = page.locator('.trip-viewer-sidebar__notes-viewport');
  await expect(page.getByRole('button', { name: 'Back to trip contents' })).toHaveCount(0);
  await outer.evaluate(element => { element.scrollTop = 200; element.dispatchEvent(new Event('scroll')); });
  await expect(page.getByRole('button', { name: 'Back to trip contents' })).toBeVisible();

  await notes.evaluate(element => { element.scrollTop = 200; element.dispatchEvent(new Event('scroll')); });
  const outerBack = page.getByRole('button', { name: 'Back to trip contents' });
  const notesBack = page.getByRole('button', { name: 'Back to trip notes' });
  await expect(notesBack).toBeVisible();
  const [outerBox, notesBox] = await Promise.all([outerBack.boundingBox(), notesBack.boundingBox()]);
  expect(outerBox && notesBox && (outerBox.y + outerBox.height <= notesBox.y || notesBox.y + notesBox.height <= outerBox.y)).toBe(true);

  await outerBack.click();
  await expect.poll(() => outer.evaluate(element => element.scrollTop)).toBe(0);
  await expect(page.getByLabel('Trip content').getByRole('heading', { name: 'Mocked Desktop Trip', level: 1 })).toBeFocused();

  await outer.evaluate(element => { element.scrollTop = 200; element.dispatchEvent(new Event('scroll')); });
  await page.getByRole('button', { name: 'Readable itinerary' }).click();
  await expect(page.getByRole('button', { name: 'Back to trip contents' })).toHaveCount(0);
});

test('restores desktop and compact contents scroll after detail Back without resetting query or map state', async ({ page }) => {
  const viewer = {
    state: mockViewerState({ initialView: { latitude: 1, longitude: 2, zoom: 3, source: 'query', canonicalQuery: 'lat=1&lon=2&zoom=3' } }),
    pageUrl: '/__trip-viewer-test.html?lat=1&lon=2&zoom=3'
  };

  await page.setViewportSize({ width: 1280, height: 820 });
  await loadMockedViewer(page, viewer);
  const desktopBody = page.locator('.trip-viewer-hierarchy-body');
  const desktopSearch = page.getByLabel('Search viewer content').getByPlaceholder('Search places, notes, tags');
  await desktopSearch.fill('Harbor');
  await desktopBody.evaluate(element => { element.scrollTop = 180; });
  await page.getByRole('button', { name: /Waterfront Zone/ }).click();
  const desktopQuery = new URL(page.url()).search;
  await page.getByRole('button', { name: 'Back to content' }).click();
  await expect.poll(() => desktopBody.evaluate(element => element.scrollTop)).toBeGreaterThanOrEqual(170);
  await expect(desktopSearch).toHaveValue('Harbor');
  expect(new URL(page.url()).search).toBe(desktopQuery);

  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole('button', { name: 'Browse trip contents' }).click();
  const compactBody = page.locator('.trip-viewer-mobile-drawer__panel-body');
  await compactBody.evaluate(element => { element.scrollTop = 180; });
  await page.getByRole('button', { name: /Waterfront Zone/ }).click();
  const compactQuery = new URL(page.url()).search;
  await page.getByRole('button', { name: 'Back from trip details' }).click();
  await expect.poll(() => compactBody.evaluate(element => element.scrollTop)).toBeGreaterThanOrEqual(170);
  await expect(desktopSearch).toHaveValue('Harbor');
  expect(new URL(page.url()).search).toBe(compactQuery);
});

test('uses only the returned cover display URL and silently removes failed covers', async ({ page }) => {
  const presentCover = mockViewerState() as any;
  presentCover.trip.coverImage = { displayUrl: '/cover-present', rawUrl: 'https://private.example.test/raw-cover', copyUrl: null };
  await page.route('**/cover-present', route => route.fulfill({ contentType: 'image/png', body: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=', 'base64') }));
  await loadMockedViewer(page, presentCover);

  const cover = page.getByRole('img', { name: 'Cover for Mocked Desktop Trip' });
  await expect(cover).toHaveAttribute('src', '/cover-present');
  await expect(cover).toHaveAttribute('loading', 'eager');
  await expect(page.locator('text=https://private.example.test/raw-cover')).toHaveCount(0);

  const failedCover = mockViewerState() as any;
  failedCover.trip.coverImage = { displayUrl: '/cover-404', rawUrl: 'https://private.example.test/raw-cover', copyUrl: null };
  await page.route('**/cover-404', route => route.fulfill({ status: 404 }));
  await loadMockedViewer(page, failedCover);
  await expect(cover).toHaveCount(0);

  const withoutCover = mockViewerState() as any;
  withoutCover.trip.coverImage = null;
  await loadMockedViewer(page, withoutCover);
  await expect(page.getByRole('img', { name: 'Cover for Mocked Desktop Trip' })).toHaveCount(0);
});

test('returns from desktop detail without resetting the current map view', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 820 });
  await loadMockedViewer(page, {
    state: mockViewerState({ initialView: { latitude: 1, longitude: 2, zoom: 3, source: 'query', canonicalQuery: 'lat=1&lon=2&zoom=3' } }),
    pageUrl: '/__trip-viewer-test.html?lat=1&lon=2&zoom=3'
  });

  await page.getByRole('button', { name: /Waterfront Zone/ }).click();
  const beforeBack = new URL(page.url()).search;
  await page.getByRole('button', { name: 'Back to content' }).click();
  expect(new URL(page.url()).search).toBe(beforeBack);
  await expect(page.getByLabel('Trip content').getByRole('heading', { name: 'Mocked Desktop Trip', level: 1 })).toBeVisible();
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

  await page.getByRole('button', { name: 'Back to content' }).click();
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
  await expect(page.getByLabel('Trip hierarchy').locator('.trip-viewer-sidebar__overview')).toHaveCount(1);
  await expect(page.getByLabel('Trip hierarchy').getByText('Trip overview note.', { exact: true })).toHaveCount(1);
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
