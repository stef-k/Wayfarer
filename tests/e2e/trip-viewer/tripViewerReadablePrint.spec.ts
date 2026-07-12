import { expect, test } from '@playwright/test';
import { loadMockedViewer, mockViewerState } from './tripViewerFixtures';

// Keeps #348 readable-document and print behavior separate from desktop layout and map interaction coverage.
test('renders the readable document in its required order without viewer chrome', async ({ page }) => {
  await loadMockedViewer(page, mockViewerState({ mapSnapshotUrl: '/Public/Trips/trip-1/MapSnapshot' }));
  await page.getByRole('button', { name: 'Readable itinerary' }).click();

  const readable = page.getByRole('dialog', { name: 'Readable trip itinerary' });
  await expect(readable.getByRole('heading', { name: 'Mocked Desktop Trip', level: 1 })).toBeVisible();
  await expect(readable.getByRole('heading', { name: 'Map', level: 2 })).toBeVisible();
  await expect(readable.getByRole('heading', { name: 'Tags', level: 2 })).toBeVisible();
  await expect(readable.getByRole('heading', { name: 'Trip notes', level: 2 })).toBeVisible();
  await expect(readable.getByRole('heading', { name: 'Regions', level: 2 })).toBeVisible();
  await expect(readable.getByRole('heading', { name: 'Segments', level: 2 })).toBeVisible();
  await expect(readable.getByText('public viewer')).toHaveCount(0);
  await expect(readable.getByRole('navigation')).toHaveCount(0);
  await expect(readable.getByRole('button', { includeHidden: true })).toHaveText(['Close', 'Print', 'Back to top']);
});

test('omits unavailable sections while preserving safe links and proxied image-only notes', async ({ page }) => {
  const state = mockViewerState() as any;
  state.trip.notes = {
    displayHtml: '<p><a href="https://example.test/guide" rel="noopener noreferrer" target="_blank">Guide</a></p>',
    plainText: 'Guide',
    hasRenderableContent: true,
    hasTextContent: true,
    hasMediaContent: false
  };
  state.tagsBySlug = {};
  state.tagOrder = [];
  state.trip.coverImage = null;
  state.trip.updatedAt = 'not-a-date';
  await loadMockedViewer(page, state);
  await page.getByRole('button', { name: 'Readable itinerary' }).click();

  const readable = page.getByRole('dialog', { name: 'Readable trip itinerary' });
  await expect(readable.getByRole('heading', { name: 'Tags', level: 2 })).toHaveCount(0);
  await expect(readable.getByText(/^Updated /)).toHaveCount(0);
  await expect(readable.getByRole('link', { name: 'Guide' })).toHaveAttribute('rel', 'noopener noreferrer');
  await expect(readable.locator('.trip-viewer-notes img')).toHaveAttribute('src', /\/Public\/ProxyImage\?url=/);
  await expect(readable.getByText('No notes.')).toHaveCount(0);
});

test('closes to the originating readable trigger without changing navigation state', async ({ page }) => {
  await loadMockedViewer(page);
  const trigger = page.getByRole('button', { name: 'Readable itinerary' });
  await trigger.click();
  await page.getByRole('dialog', { name: 'Readable trip itinerary' }).getByRole('button', { name: 'Close' }).click();
  await expect(page.getByRole('dialog', { name: 'Readable trip itinerary' })).toHaveCount(0);
  await expect(trigger).toBeFocused();
  expect(new URL(page.url()).pathname).toBe('/__trip-viewer-test.html');
});

test('renders private and public readable documents from display-safe fields only', async ({ page }) => {
  const state = mockViewerState({ viewerMode: 'private' }) as any;
  state.trip.privateUrl = '/User/Trip/View/private-trip';
  state.trip.coverImage = { displayUrl: '/Public/Trips/trip-1/CoverImage', rawUrl: 'https://private.example.test/cover.jpg', copyUrl: null };
  await loadMockedViewer(page, { state, configMode: 'private' });
  await page.getByRole('button', { name: 'Readable itinerary' }).click();

  const readable = page.getByRole('dialog', { name: 'Readable trip itinerary' });
  await expect(readable.getByRole('img', { name: 'Cover for Mocked Desktop Trip' })).toHaveAttribute('src', '/Public/Trips/trip-1/CoverImage');
  await expect(readable.getByText('private-trip')).toHaveCount(0);
  await expect(readable.getByText('private.example.test')).toHaveCount(0);
  await expect(readable.getByText(/Export PDF|Copy map snapshot URL|viewerMode/)).toHaveCount(0);
});

test('uses only eligible snapshot actions and preserves the DTO fallback otherwise', async ({ page }) => {
  const ineligible = mockViewerState({ mapSnapshotUrl: '/Public/Trips/trip-1/MapSnapshot' }) as any;
  ineligible.actions.copyMapSnapshotUrl.method = 'POST';
  await loadMockedViewer(page, ineligible);
  await page.getByRole('button', { name: 'Readable itinerary' }).click();

  const readable = page.getByRole('dialog', { name: 'Readable trip itinerary' });
  await expect(readable.getByRole('img', { name: 'Trip map snapshot' })).toHaveCount(0);
  await expect(readable.getByLabel('Readable map preview').getByText('Initial center')).toBeVisible();
});

test('keeps Back to top hidden until the readable scroll container passes 300px and focuses the title', async ({ page }) => {
  const state = mockViewerState() as any;
  state.trip.notes = {
    displayHtml: `<p>${'Long readable note. '.repeat(1200)}</p>`,
    plainText: 'Long readable note.',
    hasRenderableContent: true,
    hasTextContent: true,
    hasMediaContent: false
  };
  await loadMockedViewer(page, state);
  await page.getByRole('button', { name: 'Readable itinerary' }).click();

  const readable = page.getByRole('dialog', { name: 'Readable trip itinerary' });
  const document = readable.locator('.trip-viewer-readable__document');
  const top = readable.getByRole('button', { name: 'Back to top' });
  await expect(top).toBeHidden();
  await document.evaluate(element => element.scrollTo({ top: 320 }));
  await expect(top).toBeVisible();
  await top.click();
  await expect.poll(() => document.evaluate(element => element.scrollTop)).toBe(0);
  await expect(readable.getByRole('heading', { name: 'Mocked Desktop Trip', level: 1 })).toBeFocused();
});

test('prints only the readable document without viewer chrome or fixed controls', async ({ page }) => {
  await loadMockedViewer(page);
  await page.getByRole('button', { name: 'Readable itinerary' }).click();
  await page.emulateMedia({ media: 'print' });

  const readable = page.getByRole('dialog', { name: 'Readable trip itinerary' });
  await expect(readable.getByRole('heading', { name: 'Mocked Desktop Trip', level: 1 })).toBeVisible();
  await expect(readable.getByText('Trip overview note.')).toBeVisible();
  await expect(readable.getByRole('button')).toHaveCount(0);
  await expect(page.locator('.trip-viewer-content-surface')).toBeHidden();
  await expect(page.locator('.trip-viewer-navigation')).toBeHidden();
  await expect(page.locator('.trip-viewer-search')).toBeHidden();
  await expect(page.locator('.trip-viewer-map-shell')).toBeHidden();
  await expect(page.locator('.leaflet-control-container')).toBeHidden();
  await expect(page.locator('.trip-viewer-mobile-drawer')).toBeHidden();
  await expect(page.locator('.trip-viewer-content-surface__actions')).toBeHidden();
  await expect(page.getByRole('button', { name: 'Back to trip contents' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Back to trip notes' })).toHaveCount(0);
});

test('uses natural print pagination for regions and segments', async ({ page }) => {
  await loadMockedViewer(page);
  await page.getByRole('button', { name: 'Readable itinerary' }).click();
  await page.emulateMedia({ media: 'print' });

  await expect.poll(() => page.locator('.trip-viewer-readable__regions').evaluate(element => getComputedStyle(element).breakBefore)).toBe('auto');
  await expect.poll(() => page.locator('.trip-viewer-readable__segments').evaluate(element => getComputedStyle(element).breakBefore)).toBe('auto');
});

test('keeps readable document controls out of embed and avoids mobile overlap', async ({ page }) => {
  await loadMockedViewer(page, { state: mockViewerState({ viewerMode: 'embed' }), configMode: 'embed' });
  await expect(page.getByRole('dialog', { name: 'Readable trip itinerary' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Readable itinerary' })).toHaveCount(0);

  await page.setViewportSize({ width: 390, height: 844 });
  await loadMockedViewer(page);
  await page.getByRole('button', { name: 'Browse trip contents' }).click();
  await page.getByRole('button', { name: 'Readable itinerary' }).click();
  const readable = page.getByRole('dialog', { name: 'Readable trip itinerary' });
  await expect(readable).toBeVisible();
  await expect(readable.locator('.trip-viewer-mobile-drawer')).toHaveCount(0);
});
