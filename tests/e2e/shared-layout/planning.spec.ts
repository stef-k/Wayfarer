import { expect, test } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** One real-control smoke: quiet rough Trip date, refinement, repeated entry and persisted summary. */
test('private planning rough date and later refinement use real editor controls', async ({ page }, testInfo) => {
  const config = loadSharedLayoutConfig();
  const tripId = process.env.WAYFARER_E2E_TRIP_ID!;
  await page.goto('/Identity/Account/Login');
  await page.getByLabel('Username').fill(config.username);
  await page.getByLabel('Password').fill(config.password);
  await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')), page.getByRole('button', { name: 'Log in' }).click()]);
  await page.goto(`/User/Trip/Edit/${tripId}`);
  await expect(page.locator('.trip-editor-workspace')).toBeVisible();
  const planning = page.locator('[data-planning]');
  await planning.locator('summary').first().click();
  await planning.getByRole('button', { name: 'Add date', exact: true }).click();
  await planning.getByRole('button', { name: 'Month only', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Change precision', exact: true }).click();
  await planning.locator('input[type=month]').fill('2027-03');
  await planning.getByRole('button', { name: 'Save planning', exact: true }).click();
  await expect(planning.getByRole('status')).toHaveText('Planning saved.');
  await expect(planning.locator('summary').first()).toContainText('2027-03');
  await planning.getByRole('button', { name: 'Add another date', exact: true }).click();
  await planning.locator('input[type=date]').fill('2027-03-15');
  await planning.getByRole('button', { name: 'Add time', exact: true }).click();
  await expect(planning.locator('input[type=date]')).toHaveValue('2027-03-15');
  await planning.locator('input[type=time]').fill('10:15');
  await planning.getByRole('button', { name: 'Save planning', exact: true }).click();
  await expect(planning.getByRole('status')).toHaveText('Planning saved.');
  await page.screenshot({ path: testInfo.outputPath('planning-owner-editor.png') });
  await page.reload();
  await expect(page.locator('[data-planning] > summary')).toContainText('2027-03-15T10:15');
});
