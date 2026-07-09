import { expect, test, type Page } from '@playwright/test';
import { loadMockedViewer, mockViewerState } from './tripViewerFixtures';

// Covers map-specific parity so layout and drawer regressions remain isolated in the desktop spec.

test('uses query coordinates only at initial load and restores the saved DTO trip view', async ({ page }) => {
  const state = mapState({ latitude: 1, longitude: 2, zoom: 3, source: 'query', canonicalQuery: 'lat=1&lon=2&zoom=3' });
  state.trip.center = { latitude: 40.71, longitude: -74.01 };
  state.trip.zoom = 11;
  await loadMockedViewer(page, { state, pageUrl: '/__trip-viewer-test.html?lat=1&lon=2&zoom=3' });

  await expectMapQuery(page, '1.000000', '2.000000', '3');
  await page.getByRole('button', { name: 'Full trip', exact: true }).click();
  await expectMapQuery(page, '40.710000', '-74.010000', '11');
});

test('search clear invokes the saved-trip reset and falls back from bounds to a safe view', async ({ page }) => {
  const state = mapState({ latitude: 1, longitude: 2, zoom: 3, source: 'query', canonicalQuery: 'lat=1&lon=2&zoom=3' });
  await loadMockedViewer(page, { state, pageUrl: '/__trip-viewer-test.html?lat=1&lon=2&zoom=3' });
  await page.getByLabel('Search viewer content').getByPlaceholder('Search places, notes, tags').fill('dock coffee');
  await page.getByRole('button', { name: 'Clear' }).click();
  await expect.poll(() => new URL(page.url()).searchParams.get('lat')).not.toBe('1.000000');

  clearRenderedMapContent(state);
  await loadMockedViewer(page, { state, pageUrl: '/__trip-viewer-test.html?lat=1&lon=2&zoom=3' });
  await page.getByRole('button', { name: 'Full trip', exact: true }).click();
  await expectMapQuery(page, '20.000000', '0.000000', '2');
});

test('offers the same compact non-embed recenter control on mobile but not embed', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  const state = mapState({ latitude: 1, longitude: 2, zoom: 3, source: 'query', canonicalQuery: 'lat=1&lon=2&zoom=3' });
  state.trip.center = { latitude: 40.71, longitude: -74.01 };
  state.trip.zoom = 11;
  await loadMockedViewer(page, { state, pageUrl: '/__trip-viewer-test.html?lat=1&lon=2&zoom=3' });
  await page.getByRole('button', { name: 'Recenter full trip' }).click();
  await expectMapQuery(page, '40.710000', '-74.010000', '11');

  await loadMockedViewer(page, { state: mapState(undefined, 'embed'), configMode: 'embed' });
  await expect(page.getByRole('button', { name: 'Recenter full trip' })).toHaveCount(0);
});

test('measure mode consumes feature taps and restores normal popup selection after exit', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await loadMockedViewer(page);
  await page.getByRole('button', { name: 'Measure distance' }).click();
  await page.getByAltText(/Harbor Cafe/).click();
  await expect(page.locator('.trip-viewer-map-distance-label')).toHaveCount(0);
  await expect(page.locator('.trip-viewer-mobile-drawer--detail')).toHaveCount(0);
  await expect.poll(() => page.locator('.leaflet-popup').count()).toBe(0);

  await page.getByRole('button', { name: 'Measure distance' }).click();
  await page.getByAltText(/Harbor Cafe/).click();
  await expect(page.locator('.leaflet-popup')).toBeVisible();
  await expect(page.locator('.trip-viewer-mobile-drawer--detail')).toBeVisible();
});

function mapState(initialView?: { latitude: number; longitude: number; zoom: number; source: string; canonicalQuery: string }, viewerMode?: 'embed') {
  const state = mockViewerState({ initialView, viewerMode }) as any;
  return state;
}

function clearRenderedMapContent(state: any): void {
  state.regionsById = {};
  state.regionOrder = [];
  state.placesById = {};
  state.placeOrderByRegionId = {};
  state.areasById = {};
  state.areaOrderByRegionId = {};
  state.segmentsById = {};
  state.segmentOrder = [];
}

async function expectMapQuery(page: Page, latitude: string, longitude: string, zoom: string): Promise<void> {
  await expect.poll(() => {
    const query = new URL(page.url()).searchParams;
    return [query.get('lat'), query.get('lon'), query.get('zoom')];
  }).toEqual([latitude, longitude, zoom]);
}
