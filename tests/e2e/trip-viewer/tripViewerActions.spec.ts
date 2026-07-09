import { expect, test } from '@playwright/test';
import { loadMockedViewer, mockActionState } from './tripViewerFixtures';

// Covers #347's action matrix separately from layout and map-interaction responsibilities.
test('groups private-owner actions into the required primary and More menu order', async ({ page }) => {
  await loadMockedViewer(page, { state: mockActionState('private-owner'), configMode: 'private' });

  await expect(page.getByRole('button', { name: 'Readable itinerary' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Edit' })).toHaveAttribute('target', '_blank');
  await page.getByRole('button', { name: 'More actions' }).click();

  await expect(page.locator('.trip-viewer-actions__group-label')).toHaveText(['Export', 'Trip']);
  await expect(page.getByRole('menuitem', { name: 'Wayfarer KML' })).toHaveAttribute('href', '/Trip/ExportWayfarerKml/trip-1');
  await expect(page.getByRole('menuitem', { name: 'Google My Maps KML' })).toHaveAttribute('href', '/Trip/ExportGoogleMyMapsKml/trip-1');
  await expect(page.getByRole('menuitem', { name: 'Export PDF' })).toHaveAttribute('target', '_blank');
  await expect(page.getByRole('menuitem', { name: 'Print' })).toBeVisible();
  await expect(page.getByRole('menuitem', { name: /Share|Clone/ })).toHaveCount(0);
});

test('deduplicates public aliases and preserves public-owner share ordering', async ({ page }) => {
  await loadMockedViewer(page, mockActionState('public-owner'));
  await page.getByRole('button', { name: 'More actions' }).click();

  await expect(page.locator('.trip-viewer-actions__group-label')).toHaveText(['Share', 'Export', 'Trip']);
  await expect(page.getByRole('menuitem', { name: 'Share (copy link)' })).toBeVisible();
  await expect(page.getByRole('menuitem', { name: 'Copy public URL' })).toHaveCount(0);
  await expect(page.getByRole('menuitem', { name: 'Copy cover image URL' })).toBeVisible();
  await expect(page.getByRole('menuitem', { name: 'Copy map snapshot URL' })).toBeVisible();
});

test('renders POST clone as accessible deferred UI and retains anonymous login navigation', async ({ page }) => {
  await loadMockedViewer(page, mockActionState('public-non-owner'));
  await page.getByRole('button', { name: 'More actions' }).click();
  const deferredClone = page.getByRole('menuitem', { name: 'Clone to My Trips' });
  await expect(deferredClone).toBeDisabled();
  await expect(deferredClone).toHaveAttribute('aria-disabled', 'true');
  await expect(deferredClone).toHaveAttribute('aria-describedby', 'trip-viewer-clone-deferred');
  await expect(page.getByRole('link', { name: /Clone/ })).toHaveCount(0);

  await loadMockedViewer(page, mockActionState('public-anonymous'));
  await page.getByRole('button', { name: 'More actions' }).click();
  await expect(page.getByRole('menuitem', { name: 'Sign in to clone' })).toHaveAttribute('href', '/Identity/Account/Login');
});

test('copies only server URLs with Clipboard success, fallback success, and visible failure guidance', async ({ page }) => {
  await page.addInitScript(() => {
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: async () => undefined } });
  });
  await loadMockedViewer(page, mockActionState('public-owner'));
  await page.getByRole('button', { name: 'More actions' }).click();
  await page.getByRole('menuitem', { name: 'Share (copy link)' }).click();
  await expect(page.getByRole('status')).toHaveText('Share (copy link) copied.');

  await page.addInitScript(() => {
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined });
    document.execCommand = () => true;
  });
  await loadMockedViewer(page, mockActionState('public-owner'));
  await page.getByRole('button', { name: 'More actions' }).click();
  await page.getByRole('menuitem', { name: 'Copy cover image URL' }).click();
  await expect(page.getByRole('status')).toHaveText('Copy cover image URL copied.');

  await page.addInitScript(() => {
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined });
    document.execCommand = () => false;
  });
  await loadMockedViewer(page, mockActionState('public-owner'));
  await page.getByRole('button', { name: 'More actions' }).click();
  await page.getByRole('menuitem', { name: 'Copy map snapshot URL' }).click();
  await expect(page.getByRole('status')).toContainText('Could not copy Copy map snapshot URL. Copy it manually.');
  await expect(page.getByRole('textbox', { name: 'Copy Copy map snapshot URL manually' })).toHaveValue('/Public/Trips/trip-1/MapSnapshot');
});

test('supports menu keyboard navigation, Escape focus return, and mobile full-trip placement', async ({ page }) => {
  await loadMockedViewer(page, mockActionState('public-owner'));
  const trigger = page.getByRole('button', { name: 'More actions' });
  await trigger.focus();
  await page.keyboard.press('ArrowDown');
  await expect(page.getByRole('menuitem', { name: 'Share (copy link)' })).toBeFocused();
  await page.keyboard.press('End');
  await expect(page.getByRole('menuitem', { name: 'Print' })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(trigger).toBeFocused();
  await trigger.click();
  await page.getByRole('heading', { name: 'Mocked Desktop Trip' }).click();
  await expect(page.getByRole('menu', { name: 'More trip actions' })).toHaveCount(0);
  await expect(trigger).toBeFocused();

  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.getByRole('button', { name: 'More actions' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Browse trip contents' }).click();
  await page.getByLabel('Trip hierarchy').getByRole('button', { name: /Trip Mocked Desktop Trip/ }).click();
  await expect(page.getByLabel('Selected trip details').getByRole('button', { name: 'Readable itinerary' })).toBeVisible();
  await expect(page.getByLabel('Selected trip details').getByRole('button', { name: 'More actions' })).toBeVisible();
});

test('keeps readable controls scoped and embed action aliases map-only', async ({ page }) => {
  await page.addInitScript(() => {
    Object.defineProperty(window, 'print', { configurable: true, value: () => { (window as unknown as { printed?: boolean }).printed = true; } });
  });
  await loadMockedViewer(page, mockActionState('public-owner'));
  await page.getByRole('button', { name: 'Readable itinerary' }).click();
  const readable = page.getByRole('dialog', { name: 'Readable trip itinerary' });
  await expect(readable.getByRole('button')).toHaveText(['Close', 'Print', 'Back to top']);
  await readable.getByRole('button', { name: 'Print' }).click();
  await expect.poll(() => page.evaluate(() => (window as unknown as { printed?: boolean }).printed === true)).toBe(true);

  await loadMockedViewer(page, { state: mockActionState('embed'), configMode: 'embed' });
  await expect(page.getByRole('link', { name: 'Open trip' })).toHaveCount(1);
  await expect(page.getByRole('navigation', { name: 'Trip actions' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'More actions' })).toHaveCount(0);
});
