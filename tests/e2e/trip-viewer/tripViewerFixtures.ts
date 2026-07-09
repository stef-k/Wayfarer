import { expect, type Page } from '@playwright/test';

// Provides deterministic server-state and shell fixtures shared by Trip Viewer browser specs.

export async function loadMockedViewer(
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

export function mockViewerState(options?: {
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

export async function expectMarkerInsideMap(page: Page, markerSelector: string): Promise<void> {
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
