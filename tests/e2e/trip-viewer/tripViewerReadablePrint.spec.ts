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
  await expect(readable.getByRole('button')).toHaveText(['Close', 'Print', 'Back to top']);
});

test('uses only eligible snapshot actions and preserves the DTO fallback otherwise', async ({ page }) => {
  const ineligible = mockViewerState({ mapSnapshotUrl: '/Public/Trips/trip-1/MapSnapshot' }) as any;
  ineligible.actions.copyMapSnapshotUrl.method = null;
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

test('prints the readable document while hiding controls and viewer chrome', async ({ page }) => {
  await loadMockedViewer(page);
  await page.getByRole('button', { name: 'Readable itinerary' }).click();
  await page.emulateMedia({ media: 'print' });

  const readable = page.getByRole('dialog', { name: 'Readable trip itinerary' });
  await expect(readable.getByRole('heading', { name: 'Mocked Desktop Trip', level: 1 })).toBeVisible();
  await expect(readable.getByText('Trip overview note.')).toBeVisible();
  await expect(readable.getByRole('button')).toHaveCount(0);
  await expect(page.locator('.trip-viewer-map-shell')).toBeHidden();
  await expect(page.locator('.trip-viewer-mobile-drawer')).toBeHidden();
});

test('keeps readable document controls out of embed and avoids mobile overlap', async ({ page }) => {
  await loadMockedViewer(page, { state: mockViewerState({ viewerMode: 'embed' }), configMode: 'embed' });
  await expect(page.getByRole('dialog', { name: 'Readable trip itinerary' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Readable itinerary' })).toHaveCount(0);

  await page.setViewportSize({ width: 390, height: 844 });
  await loadMockedViewer(page);
  await page.getByRole('button', { name: 'Browse trip contents' }).click();
  await page.getByRole('button', { name: /Trip Mocked Desktop Trip/ }).click();
  await page.getByRole('button', { name: 'Readable itinerary' }).click();
  const readable = page.getByRole('dialog', { name: 'Readable trip itinerary' });
  await expect(readable).toBeVisible();
  await expect(page.locator('.trip-viewer-mobile-drawer')).toBeHidden();
});
