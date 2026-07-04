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

test('renders #335 tags and action contract items without adding deferred behavior', async ({ page }) => {
  await loadMockedViewer(page);

  await expect(page.getByLabel('Trip tags').getByText('Harbor')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Share' })).toHaveAttribute('href', '/Public/TripsNext/trip-1');
  await expect(page.getByRole('link', { name: 'Public URL' })).toHaveAttribute('href', '/Public/TripsNext/trip-1');
  await expect(page.getByRole('link', { name: 'Clone sign-in' })).toHaveAttribute('href', '/Identity/Account/Login');
  await expect(page.getByRole('button', { name: 'Readable' })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Print' })).toBeDisabled();
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
  await expect(page.getByRole('button', { name: 'Harbor Cafe 1 Dock Street' })).toHaveClass(/trip-viewer-list-item--selected/);

  await page.getByRole('button', { name: /Trip Mocked Desktop Trip/ }).click();
  await page.getByAltText(/Harbor Cafe/).click();
  await page.getByRole('button', { name: 'View details' }).click();

  await expect(page.getByRole('heading', { name: 'Harbor Cafe' })).toBeVisible();
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

test('preserves #337 persistent desktop surfaces at desktop width', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 820 });
  await loadMockedViewer(page);

  await expect(page.getByLabel('Trip contents')).toBeVisible();
  await expect(page.getByLabel('Trip map')).toBeVisible();
  await expect(page.getByLabel('Selection details')).toBeVisible();
  await expect(page.locator('.trip-viewer-mobile-drawer')).toBeHidden();
});

test('uses a mobile map-first drawer with hierarchy, detail, collapse, and escape states', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await loadMockedViewer(page);

  await expect(page.getByLabel('Trip map')).toBeVisible();
  await expect(page.locator('.trip-viewer-mobile-drawer--peek')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Contents' })).toBeVisible();

  await page.getByRole('button', { name: 'Contents' }).click();
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

  await page.getByRole('button', { name: 'Contents' }).click();
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

async function loadMockedViewer(page: Page, state: unknown = mockViewerState(), status = 200): Promise<void> {
  await page.route('**/__trip-viewer-test.html', route => route.fulfill({
    contentType: 'text/html',
    body: `<!doctype html>
      <html>
        <head><title>Trip Viewer Test</title></head>
        <body>
          <div id="trip-viewer-app"
            data-trip-id="trip-1"
            data-trip-name="Mocked Desktop Trip"
            data-viewer-mode="public"
            data-viewer-state-endpoint="/viewer-state"
            data-public-view-url="/Public/TripsNext/trip-1"
            data-open-canonical-url=""
            data-tiles-url="/tiles/{z}/{x}/{y}.png"
            data-tile-attribution="Test tiles"
            data-asset-mode="development"></div>
          <script type="module" src="/ClientApps/trip-viewer/src/main.ts"></script>
        </body>
      </html>`
  }));

  await page.route('**/viewer-state', route => route.fulfill({
    contentType: 'application/json',
    status,
    body: JSON.stringify(state)
  }));

  await page.route('**/tiles/**', route => route.fulfill({
    contentType: 'image/png',
    body: transparentPng()
  }));

  await page.goto('/__trip-viewer-test.html');
  if (status === 200) {
    await expect(page.locator('.trip-viewer-workspace')).toBeVisible();
  }
}

function mockViewerState(options?: { canDisplayProgress?: boolean; canDisplayCounts?: boolean; canReadVisitCounts?: boolean; clonePost?: boolean }): unknown {
  const canDisplayProgress = options?.canDisplayProgress ?? true;
  const canDisplayCounts = options?.canDisplayCounts ?? true;
  const canReadVisitCounts = options?.canReadVisitCounts ?? true;

  return {
    viewerMode: 'public',
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
      canDisplayHistory: false,
      totalPlaces: 2,
      visitedPlaces: canDisplayCounts ? 1 : 0,
      percentVisited: canDisplayCounts ? 50 : 0,
      placeSummariesByPlaceId: {
        'place-1': { placeId: 'place-1', visitCount: 2, isVisited: true, firstVisitAt: null, lastVisitAt: null }
      },
      historyRows: []
    },
    permissions: {
      canViewPrivateState: false,
      canViewPublicState: true,
      canViewEmbedState: false,
      isOwner: false,
      canReadNotes: true,
      canReadVisitCounts,
      canReadVisitHistory: false,
      canToggleShareProgress: false,
      canUseReadableMode: true,
      canPrint: true
    },
    actions: {
      edit: action(false),
      clone: options?.clonePost ? action(true, '/User/Trip/Clone/trip-1', 'POST') : action(false, '/Identity/Account/Login', 'GET', true),
      exportWayfarerKml: action(true, '/Trip/ExportWayfarerKml/trip-1'),
      exportGoogleMyMapsKml: action(true, '/Trip/ExportGoogleMyMapsKml/trip-1'),
      exportPdf: action(true, '/Trip/ExportPdf/trip-1'),
      share: action(true, '/Public/TripsNext/trip-1'),
      copyPublicUrl: action(true, '/Public/TripsNext/trip-1'),
      copyCoverUrl: action(false),
      copyMapSnapshotUrl: action(false),
      fullscreen: action(false),
      openCanonical: action(false),
      readable: action(true),
      print: action(true)
    },
    map: {
      initialView: { latitude: 20, longitude: 0, zoom: 2, source: 'world', canonicalQuery: 'lat=20&lon=0&zoom=2' },
      acceptedQueryParameters: ['lat', 'lon', 'lng', 'zoom'],
      emittedQueryParameters: ['lat', 'lon', 'zoom'],
      tileUrlTemplate: '/tiles/{z}/{x}/{y}.png',
      tileAttribution: 'Test tiles'
    }
  };
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

function transparentPng(): Buffer {
  return Buffer.from(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=',
    'base64'
  );
}
