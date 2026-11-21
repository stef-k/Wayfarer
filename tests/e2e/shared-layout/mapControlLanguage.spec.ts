import { expect, test, type Locator } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** Locates a point inside the visible map and clear of the page chrome. */
const mapPoint = async (map: Locator, x: number, y: number) => {
  const page = map.page();
  const box = await map.boundingBox();
  expect(box).not.toBeNull();
  const left = Math.max(box!.x, 0);
  const top = Math.max(box!.y, 0);
  const right = Math.min(box!.x + box!.width, page.viewportSize()!.width);
  const footer = await page.locator('body > footer').boundingBox();
  const bottom = Math.min(box!.y + box!.height, footer?.y ?? page.viewportSize()!.height, page.viewportSize()!.height);
  return { x: left + (right - left) * x, y: top + (bottom - top) * y };
};

/** Moves the pointer and places a map point after Leaflet.Draw's debounce. */
const clickMap = async (map: Locator, x: number, y: number) => {
  const page = map.page();
  const point = await mapPoint(map, x, y);
  await page.mouse.move(point.x, point.y);
  // Leaflet.Draw debounces vertex clicks for 50 ms after each mouseup.
  await page.waitForTimeout(75);
  await page.mouse.click(point.x, point.y);
};

/** Uses the same visible ruler contract for the Editor and Viewer implementations. */
const checkRuler = async (map: Locator, helpClass: string, labelClass: string) => {
  const button = map.getByRole('button', { name: 'Μέτρηση απόστασης' });
  const help = map.locator(helpClass);
  const label = map.locator(labelClass);
  await button.click();
  await expect(help).toContainText('Κάντε κλικ για να προσθέσετε σημεία.');
  await expect(help).toContainText('Πατήστε Esc για ακύρωση.');
  await clickMap(map, 0.35, 0.34);
  await expect(label).toHaveCount(0);
  await clickMap(map, 0.48, 0.34);
  await expect(label).toHaveText(/^\d[\d.]*,\d{2} χλμ\.$/);
  await map.page().keyboard.press('Escape');
  await expect(button).not.toHaveClass(/\bactive\b/);
  await expect(help).toHaveCount(0);
  await expect(label).toHaveCount(0);
};

test('Area 1 Greek map drawing and distance rulers', async ({ page }) => {
  test.setTimeout(60_000);
  const { username, password } = loadSharedLayoutConfig();
  const tripId = process.env.WAYFARER_E2E_TRIP_ID!;
  await page.setViewportSize({ width: 1536, height: 1024 });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.goto('/Identity/Account/Login');
  await page.getByLabel('Username').fill(username);
  await page.getByLabel('Password').fill(password);
  await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')),
    page.getByRole('button', { name: 'Log in' }).click()]);

  await page.goto(`/User/Trip/View/${tripId}?lat=37.5&lon=23.1&zoom=9`);
  await page.getByRole('button', { name: 'Language', exact: true }).click();
  await page.locator('#language-dialog input[value="el-GR"]').check();
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('lang', 'el-GR');
  await checkRuler(page.locator('#mapContainer'), '.help-popup', '.distance-label');

  await page.goto(`/User/Trip/Edit/${tripId}?lat=37.5&lng=23.1&zoom=9`);
  await expect(page.locator('html')).toHaveAttribute('lang', 'el-GR');
  const map = page.locator('.trip-editor-map.leaflet-container');
  await checkRuler(map, '.trip-editor-map-measure-help', '.trip-editor-map-distance-label');
  const prompt = map.locator('.leaflet-draw-tooltip');
  const work = page.getByRole('region', { name: 'Εργασία στον χάρτη' });

  await page.getByRole('button', { name: 'Προσθήκη ζώνης', exact: true }).first().click();
  await page.getByRole('button', { name: 'Σχεδίαση ή επεξεργασία ζώνης' }).click();
  await expect(prompt).toHaveText('Κάντε κλικ για να αρχίσετε τη σχεδίαση της ζώνης.');
  await expect(work).toContainText('Δεν υπάρχει έτοιμο πολύγωνο');
  await clickMap(map, 0.35, 0.34);
  await expect(prompt).toHaveText('Κάντε κλικ για να προσθέσετε άλλη κορυφή.');
  await clickMap(map, 0.50, 0.49);
  await clickMap(map, 0.50, 0.34);
  await expect(prompt).toHaveText('Κάντε κλικ στην πρώτη κορυφή για να κλείσετε τη ζώνη.');
  await clickMap(map, 0.35, 0.49);
  await expect(prompt).toHaveText('Οι πλευρές της ζώνης δεν επιτρέπεται να τέμνονται.');
  await expect(map.locator('.leaflet-editing-icon')).toHaveCount(3);
  await map.locator('.leaflet-editing-icon').first().click();
  await expect(prompt).toContainText('Σύρετε τις λαβές για να επεξεργαστείτε τη ζώνη.');
  await expect(work).toContainText('3 κορυφές έτοιμες');
  await expect(work.getByRole('button', { name: 'Τέλος' })).toBeEnabled();
  await work.getByRole('button', { name: 'Ακύρωση', exact: true }).click();
  await page.getByRole('button', { name: 'Απόρριψη', exact: true }).click();

  await page.getByRole('button', { name: 'Προσθήκη τμήματος', exact: true }).click();
  await page.getByRole('button', { name: 'Σχεδίαση ή επεξεργασία διαδρομής' }).click();
  await expect(prompt).toHaveText('Κάντε κλικ για να αρχίσετε τη σχεδίαση της διαδρομής.');
  await clickMap(map, 0.35, 0.34);
  await expect(prompt).toContainText('Κάντε κλικ για να προσθέσετε άλλο σημείο στη διαδρομή.');
  const liveDistance = prompt.locator('.leaflet-draw-tooltip-subtext');
  await expect(liveDistance).toHaveText('0 μ.');
  const nextPoint = await mapPoint(map, 0.50, 0.34);
  await page.mouse.move(nextPoint.x, nextPoint.y);
  await expect(liveDistance).toHaveText(/^[1-9][\d.]*,\d{2} χλμ\.$/);
  await clickMap(map, 0.50, 0.34);
  await expect(prompt).toContainText('Κάντε κλικ στο τελευταίο σημείο για να ολοκληρώσετε τη διαδρομή.');
  await map.locator('.leaflet-editing-icon').last().click();
  await expect(prompt).toHaveCount(0);
  await expect(map.locator('[data-route-point-key]')).toHaveCount(2);
  await expect(work).toContainText('2 σημεία διαδρομής · 2 επεξεργάσιμα');
});
