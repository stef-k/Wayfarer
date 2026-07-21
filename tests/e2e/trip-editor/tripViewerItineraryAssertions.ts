import { expect, type Page } from '@playwright/test';
import { absoluteUrl, editorPath, expectMountedWorkspace } from './tripEditorTestUtils';

/**
 * Verifies normal, readable, and browser-print itinerary parity using disposable editor data.
 */
export async function expectViewerItineraryParity(page: Page, regionName: string, firstPlace: string, secondPlace: string): Promise<void> {
  await page.goto(absoluteUrl(editorPath.replace('/Edit/', '/View/')));
  const normalRegion = page.locator(`[data-region-name="${regionName}"]`);
  await expect(normalRegion.locator('.itinerary-region-label')).toContainText(regionName);
  await expect(normalRegion.locator('[data-place-name]').nth(0)).toHaveAttribute('data-place-name', firstPlace);
  await expect(normalRegion.locator('[data-place-name]').nth(1)).toHaveAttribute('data-place-name', secondPlace);
  const normalLabels = await page.locator('#regions-accordion .itinerary-region-label, #regions-accordion .itinerary-place-label').allInnerTexts();
  const readableLabels = await page.locator('#readable-modal-body .itinerary-region-label, #readable-modal-body .itinerary-place-label').allInnerTexts();
  expect(readableLabels).toEqual(normalLabels);

  await page.locator('#btn-expand-readable').click();
  await expect(page.locator('#readableViewModal')).toBeVisible();
  const popupPromise = page.waitForEvent('popup');
  await page.locator('#btn-print-modal').click();
  const printPage = await popupPromise;
  await expect(printPage.locator('.itinerary-region-label')).not.toHaveCount(0);
  expect(await printPage.locator('.itinerary-region-label, .itinerary-place-label').allInnerTexts()).toEqual(readableLabels);
  expect(await printPage.locator('.places-list').evaluateAll(lists => lists.every(list => list.tagName !== 'OL' && getComputedStyle(list).display !== 'list-item'))).toBeTruthy();
  await printPage.close();

  await page.setViewportSize({ width: 375, height: 667 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(375);
  await expect(page.locator('.area-list-item .itinerary-place-label, .segment-list-item .itinerary-place-label')).toHaveCount(0);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(absoluteUrl(editorPath));
  await expectMountedWorkspace(page);
}
