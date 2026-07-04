import { expect, test, type Page } from '@playwright/test';

test('renders mocked #335 desktop viewer state and sidebar detail selection', async ({ page }) => {
  await loadMockedViewer(page);

  await expect(page.getByRole('button', { name: /Trip Mocked Desktop Trip/ })).toBeVisible();
  await expect(page.getByRole('button', { name: /Region Harbor/ })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Harbor Cafe 1 Dock Street' })).toBeVisible();
  await expect(page.getByRole('button', { name: /Waterfront Zone/ })).toBeVisible();
  await expect(page.getByRole('button', { name: /Harbor Cafe to Lookout/ })).toBeVisible();
  await expect(page.locator('.trip-viewer-map .leaflet-tile-pane')).toHaveCount(1);

  await page.getByRole('button', { name: /Waterfront Zone/ }).click();

  await expect(page.getByRole('heading', { name: 'Waterfront Zone' })).toBeVisible();
  await expect(page.getByLabel('Selection details').getByText('Media note', { exact: true })).toBeVisible();
  await expect(page.locator('.trip-viewer-detail__notes img')).toHaveAttribute('src', /\/Public\/ProxyImage\?url=/);
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

async function loadMockedViewer(page: Page): Promise<void> {
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
    body: JSON.stringify(mockViewerState())
  }));

  await page.route('**/tiles/**', route => route.fulfill({
    contentType: 'image/png',
    body: transparentPng()
  }));

  await page.goto('/__trip-viewer-test.html');
  await expect(page.getByRole('heading', { name: 'Mocked Desktop Trip' })).toBeVisible();
}

function mockViewerState(): unknown {
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
      },
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
      canDisplayProgress: true,
      canDisplayCounts: true,
      canDisplayHistory: false,
      totalPlaces: 2,
      visitedPlaces: 1,
      percentVisited: 50,
      placeSummariesByPlaceId: {},
      historyRows: []
    },
    permissions: {
      canViewPrivateState: false,
      canViewPublicState: true,
      canViewEmbedState: false,
      isOwner: false,
      canReadNotes: true,
      canReadVisitCounts: true,
      canReadVisitHistory: false,
      canToggleShareProgress: false,
      canUseReadableMode: true,
      canPrint: true
    },
    actions: {
      edit: action(false),
      clone: action(false, '/Identity/Account/Login', 'GET', true),
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
