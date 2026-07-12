import { expect, test } from '@playwright/test';
import { loadMockedViewer, mockViewerState } from './tripViewerFixtures';
import { distanceDisplay, durationDisplay } from '../../../ClientApps/trip-viewer/src/viewModel';

// Covers #351's legacy-informed place-detail composition independently from map and hierarchy ownership.
test('renders one place identity with compact optional location and no visit fact', async ({ page }) => {
  const state = mockViewerState() as any;
  await loadMockedViewer(page, state);

  await page.getByRole('button', { name: 'Harbor Cafe 1 Dock Street' }).click();
  const detail = page.getByLabel('Selection details');
  const context = page.locator('.trip-viewer-command-header__detail-context');
  await expect(detail.getByRole('heading', { name: 'Harbor Cafe' })).toHaveCount(1);
  await expect(context.getByText(/Harbor Cafe|Place:/)).toHaveCount(0);
  await expect(detail.locator('.trip-viewer-detail__location')).toHaveText('1 Dock Street40.70100, -74.00200');
  await expect(detail.getByText('Address', { exact: true })).toHaveCount(0);
  await expect(detail.getByText('Coordinates', { exact: true })).toHaveCount(0);
  await expect(detail.getByText('Visits', { exact: true })).toHaveCount(0);

  state.placesById['place-1'].address = '';
  state.placesById['place-1'].location = null;
  await loadMockedViewer(page, state);
  await page.getByRole('button', { name: 'Harbor Cafe Place' }).click();
  await expect(page.getByLabel('Selection details').locator('.trip-viewer-detail__location')).toHaveCount(0);
  await expect(page.getByLabel('Selection details').getByText(/Not set/)).toHaveCount(0);
});

test('keeps the selected place identity solely in detail on mobile', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await loadMockedViewer(page);
  await page.getByRole('button', { name: 'Browse trip contents' }).click();
  await page.getByLabel('Trip hierarchy').getByRole('button', { name: 'Harbor Cafe 1 Dock Street' }).click();

  await expect(page.getByLabel('Selected trip details').getByRole('heading', { name: 'Harbor Cafe' })).toHaveCount(1);
  await expect(page.locator('.trip-viewer-mobile-drawer__title').getByText(/Harbor Cafe|Place/)).toHaveCount(0);
});

test('formats nullable and corrupt segment estimates as #349 presentation states', () => {
  expect(distanceDisplay(10)).toEqual({ detail: '10 km', compact: '10 km' });
  expect(durationDisplay(61)).toEqual({ detail: '1 hr 1 min', compact: '1 hr 1 min' });
  expect(distanceDisplay(null)).toEqual({ detail: 'Distance not provided.', compact: null });
  expect(durationDisplay(0)).toEqual({ detail: 'Duration unavailable.', compact: null });
  expect(distanceDisplay(Number.NaN)).toEqual({ detail: 'Distance unavailable.', compact: null });
});

// Keeps #349 DTO-backed detail formatting separate from layout, map interaction, and print ownership.
test('formats valid segment estimates and keeps absent or invalid estimates out of search', async ({ page }) => {
  const state = mockViewerState() as any;
  await loadMockedViewer(page, state);

  await page.getByRole('button', { name: /Harbor Cafe to Lookout/ }).click();
  const detail = page.getByLabel('Selection details');
  await expect(detail.getByText('Distance')).toBeVisible();
  await expect(detail.getByText('1.2 km')).toBeVisible();
  await expect(detail.getByText('18 min')).toBeVisible();

  await page.getByRole('button', { name: 'Back' }).click();
  const search = page.getByLabel('Search viewer content').getByPlaceholder('Search places, notes, tags');
  await search.fill('1.2 km');
  await expect(page.getByRole('button', { name: /segment Harbor Cafe to Lookout/ })).toBeVisible();

  state.segmentsById['segment-1'].estimatedDistanceKm = null;
  state.segmentsById['segment-1'].estimatedDurationMinutes = 0;
  await loadMockedViewer(page, state);
  await page.getByRole('button', { name: /Harbor Cafe to Lookout/ }).click();
  await expect(page.getByLabel('Selection details').getByText('Distance not provided.')).toBeVisible();
  await expect(page.getByLabel('Selection details').getByText('Duration unavailable.')).toBeVisible();
  await page.getByRole('button', { name: 'Back' }).click();
  await search.fill('unavailable');
  await expect(page.getByText('No matching trip content.')).toBeVisible();
});

test('renders a valid area color decoratively without exposing technical values', async ({ page }) => {
  const state = mockViewerState() as any;
  await loadMockedViewer(page, state);
  await page.getByRole('button', { name: /Waterfront Zone/ }).click();

  const detail = page.getByLabel('Selection details');
  await expect(detail.locator('.trip-viewer-area-swatch')).toHaveAttribute('aria-hidden', 'true');
  await expect(detail.getByText('Color', { exact: true })).toHaveCount(0);
  await expect(detail.getByText('#0ea5e9')).toHaveCount(0);
  await expect(detail.getByText('Map boundary')).toBeVisible();
  await expect(detail.getByText('Available on the map.')).toBeVisible();
  await expect(detail.getByRole('button', { name: 'Focus on map' })).toBeVisible();
  await expect(detail.locator('.trip-viewer-area-swatch')).not.toHaveAttribute('aria-label', /./);
  await expect(detail.locator('.trip-viewer-area-swatch')).not.toHaveAttribute('title', /./);

  const areaDetailText = await detail.textContent();
  expect(areaDetailText).not.toMatch(/GeoJSON|WKT|Polygon available|storage[- ]key|opacity|debug/i);
  expect(areaDetailText).not.toMatch(/-?\d+(?:\.\d+)?\s*,\s*-?\d+(?:\.\d+)?/);
});

test('omits malformed area colors while retaining approved boundary facts', async ({ page }) => {
  const state = mockViewerState() as any;
  state.areasById['area-1'].fillHex = '#12xz';
  await loadMockedViewer(page, state);
  await page.getByRole('button', { name: /Waterfront Zone/ }).click();

  const detail = page.getByLabel('Selection details');
  await expect(detail.locator('.trip-viewer-area-swatch')).toHaveCount(0);
  await expect(detail.getByText('Color', { exact: true })).toHaveCount(0);
  await expect(detail.getByText('#12xz', { exact: true })).toHaveCount(0);
  await expect(detail.locator('[title*="#12xz" i], [aria-label*="#12xz" i]')).toHaveCount(0);
  await expect(detail.getByText('Map boundary')).toBeVisible();
  await expect(detail.getByText('Available on the map.')).toBeVisible();
  await expect(detail.getByRole('button', { name: 'Focus on map' })).toBeVisible();

  const areaDetailText = await detail.textContent();
  expect(areaDetailText).not.toMatch(/#12xz|GeoJSON|WKT|Polygon available|storage[- ]key|opacity|debug/i);
  expect(areaDetailText).not.toMatch(/-?\d+(?:\.\d+)?\s*,\s*-?\d+(?:\.\d+)?/);
});

test('renders absent area color and geometry as unavailable boundary facts', async ({ page }) => {
  const state = mockViewerState() as any;
  state.areasById['area-1'].fillHex = null;
  state.areasById['area-1'].geometry = null;
  state.areasById['area-1'].notes = { displayHtml: '', plainText: '', hasRenderableContent: false, hasTextContent: false, hasMediaContent: false };
  await loadMockedViewer(page, state);
  await page.getByRole('button', { name: /Waterfront Zone/ }).click();
  const missingDetail = page.getByLabel('Selection details');
  await expect(missingDetail.locator('.trip-viewer-area-swatch')).toHaveCount(0);
  await expect(missingDetail.getByText('No map boundary is available.')).toBeVisible();
  await expect(missingDetail.getByRole('button', { name: 'Focus on map' })).toHaveCount(0);
  await expect(missingDetail.getByRole('heading', { name: 'Notes' })).toHaveCount(0);
  await expect(missingDetail.getByText(/Polygon available|Geometry|Fill/)).toHaveCount(0);
});

test('uses the same segment omission rules in mobile and readable detail surfaces', async ({ page }) => {
  const state = mockViewerState() as any;
  state.segmentsById['segment-1'].estimatedDistanceKm = -1;
  state.segmentsById['segment-1'].estimatedDurationMinutes = null;
  await page.setViewportSize({ width: 390, height: 844 });
  await loadMockedViewer(page, state);
  await page.getByRole('button', { name: 'Browse trip contents' }).click();
  await page.getByLabel('Trip hierarchy').getByRole('button', { name: /Harbor Cafe to Lookout/ }).click();
  await expect(page.getByLabel('Selected trip details').getByText('Distance unavailable.')).toBeVisible();
  await expect(page.getByLabel('Selected trip details').getByText('Duration not provided.')).toBeVisible();

  await page.getByRole('button', { name: 'Back from trip details' }).click();
  await page.getByLabel('Trip hierarchy').getByRole('button', { name: /Trip Mocked Desktop Trip/ }).click();
  await page.getByRole('button', { name: 'Readable itinerary' }).click();
  const readable = page.getByRole('dialog', { name: 'Readable trip itinerary' });
  await expect(readable.locator('.trip-viewer-readable__segments').getByText(/-1|unavailable|not provided/)).toHaveCount(0);
});

test('keeps #349 detail data out of embed map-only output', async ({ page }) => {
  await loadMockedViewer(page, { state: mockViewerState({ viewerMode: 'embed' }), configMode: 'embed' });
  await expect(page.getByText(/Map boundary|Distance|Duration|#0ea5e9/)).toHaveCount(0);
  await expect(page.getByLabel('Trip contents')).toHaveCount(0);
  await expect(page.getByLabel('Trip viewer drawer')).toHaveCount(0);
});
