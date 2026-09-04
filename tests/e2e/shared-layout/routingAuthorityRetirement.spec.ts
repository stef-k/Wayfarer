import { expect, test, type Page } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

test('keeps personal provider setup while retired routing surfaces remain absent', async ({ page }) => {
  await signIn(page);

  await page.goto('/User/LocationProviderSettings');
  await expect(page).toHaveURL(/\/User\/LocationProviderSettings\/?$/i);
  await expect(page.getByRole('heading', { name: 'Geocoding', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Directions', exact: true })).toBeVisible();
  await expect(page.getByText('Mapbox Directions is not implemented and is not offered.')).toBeVisible();
  await expect(page.locator('a[href*="RoutingSettings"], a[href*="RoutingProvider"]')).toHaveCount(0);

  await expectNotFound(page, '/User/RoutingSettings');
  await expectNotFound(page, '/User/ApiToken');
});

// Signs in through the established local authenticated-browser fixture.
async function signIn(page: Page): Promise<void> {
  const config = loadSharedLayoutConfig();
  await page.goto('/Identity/Account/Login');
  await page.getByLabel('Username').fill(config.username);
  await page.getByLabel('Password').fill(config.password);
  await Promise.all([
    page.waitForURL(url => !url.pathname.endsWith('/Account/Login')),
    page.getByRole('button', { name: 'Log in' }).click()
  ]);
}

// Verifies retired GET endpoints remain unroutable instead of redirecting.
async function expectNotFound(page: Page, path: string): Promise<void> {
  const response = await page.goto(path);
  expect(response?.status()).toBe(404);
}
