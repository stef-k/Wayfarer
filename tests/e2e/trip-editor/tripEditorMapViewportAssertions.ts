import { expect, type Page } from '@playwright/test';
import { tripMap } from './tripEditorTestUtils';

/** Read the adapter's canonical terminal viewport, shared with the URL and metadata fields. */
export const readViewport = async (page: Page) => tripMap(page).evaluate(map => ({
  latitude: map.getAttribute('data-trip-editor-map-lat')!,
  longitude: map.getAttribute('data-trip-editor-map-lng')!,
  zoom: map.getAttribute('data-trip-editor-map-zoom')!
}));

/** Wait for the final animated viewport before checking its permalink representation. */
export const expectViewportUrl = async (page: Page): Promise<void> => {
  await expect(tripMap(page)).not.toHaveClass(/leaflet-zoom-anim/);
  await expect.poll(async () => {
    const view = await readViewport(page);
    const url = new URL(page.url());
    return url.searchParams.get('lat') === view.latitude && url.searchParams.get('lng') === view.longitude && url.searchParams.get('zoom') === view.zoom;
  }).toBe(true);
};

/** Real mouse drag with a slow release avoids depending on an inertial animation timeout. */
export const dragViewport = async (page: Page): Promise<void> => {
  const map = tripMap(page);
  const box = (await map.boundingBox())!;
  const before = await readViewport(page);
  const start = { x: box.x + box.width * 0.65, y: box.y + box.height * 0.6 };
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await page.mouse.move(start.x - 90, start.y + 50, { steps: 12 });
  await page.waitForTimeout(100); // Leaflet's inertia cutoff depends on the last drag sample.
  await page.mouse.up();
  await expect.poll(() => readViewport(page)).not.toEqual(before);
  await expectViewportUrl(page);
};

/** Shared draft assertion for both captured and explicitly transient navigation. */
export const expectViewportFields = async (page: Page, view: Awaited<ReturnType<typeof readViewport>>): Promise<void> => {
  await expect(page.getByLabel('Center Latitude')).toHaveValue(view.latitude);
  await expect(page.getByLabel('Center Longitude')).toHaveValue(view.longitude);
  await expect(page.getByRole('spinbutton', { name: 'Zoom' })).toHaveValue(view.zoom);
};
