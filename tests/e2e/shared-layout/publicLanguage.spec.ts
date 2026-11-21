import { expect, test } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

// Use the supervisor-owned account to make an empty public Timeline available for this smoke.
test('Greek public Timeline embed announces a copied map link', async ({ page, context, baseURL }) => {
  const fixture = loadSharedLayoutConfig();
  await page.goto('/Identity/Account/Login');
  await page.getByLabel('Username').fill(fixture.username);
  await page.getByLabel('Password').fill(fixture.password);
  await Promise.all([
    page.waitForURL(url => !url.pathname.endsWith('/Login')),
    page.getByRole('button', { name: 'Log in' }).click()
  ]);

  await page.goto('/User/Timeline/Settings');
  await page.getByRole('switch', { name: 'Make Timeline Public' }).check();
  await page.locator('#TimeThresholdDropdown').selectOption('1d');
  await Promise.all([
    page.waitForResponse(response => response.url().endsWith('/User/Timeline/UpdateSettings')
      && response.request().method() === 'POST' && response.status() === 302),
    page.getByRole('button', { name: 'Save Changes' }).click()
  ]);

  await context.addCookies([{ name: '.AspNetCore.Culture', value: 'c=el-GR%7Cuic=el-GR', url: baseURL! }]);
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await page.goto(`/Public/Users/Timeline/${encodeURIComponent(fixture.username)}/embed`);
  await expect(page.locator('html')).toHaveAttribute('lang', 'el-GR');
  await expect(page.locator('#timeline-map-status')).toHaveText('Δεν υπάρχουν δημόσιες τοποθεσίες σε αυτή την προβολή του χάρτη.');
  await expect(page.locator('#timeline-summary')).toContainText('Σύνολο καταγραφών τοποθεσίας: 0');
  await page.getByRole('button', { name: 'Αντιγραφή συνδέσμου χάρτη' }).click();
  await expect(page.locator('.leaflet-control-custom [role="status"]')).toHaveText('Ο σύνδεσμος χάρτη αντιγράφηκε');
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(page.url());
});
