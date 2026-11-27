import { expect, test } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

// Exercise the rendered label and recoverable validation response with the managed account.
test('Timeline Settings labels the threshold and keeps a rejected custom delay editable', async ({ page }) => {
  const fixture = loadSharedLayoutConfig();
  await page.goto('/Identity/Account/Login');
  await page.getByLabel('Username').fill(fixture.username);
  await page.getByLabel('Password').fill(fixture.password);
  await Promise.all([
    page.waitForURL(url => !url.pathname.endsWith('/Login')),
    page.getByRole('button', { name: 'Log in' }).click()
  ]);

  await page.goto('/User/Timeline/Settings');
  const threshold = page.getByRole('combobox', { name: 'Time Threshold', exact: true });
  await page.getByText('Time Threshold', { exact: true }).click();
  await expect(threshold).toBeFocused();

  const publicToggle = page.getByRole('switch', { name: 'Make Timeline Public' });
  const customDelay = page.getByLabel('Custom Time Threshold', { exact: true });
  await expect(publicToggle).not.toBeChecked();
  await expect(customDelay).toBeHidden();
  await publicToggle.check();
  await expect(threshold).toHaveValue('1d');
  await threshold.selectOption('custom');
  await customDelay.fill('invalid-delay');
  const [response] = await Promise.all([
    page.waitForResponse(response => response.request().method() === 'POST'
      && new URL(response.url()).pathname === '/User/Timeline/UpdateSettings'),
    page.getByRole('button', { name: 'Save Changes' }).click()
  ]);
  expect(response.status()).toBe(200);
  await expect(threshold).toHaveValue('custom');
  await expect(customDelay).toBeVisible();
  await expect(customDelay).toHaveValue('invalid-delay');
  const fieldError = page.locator('[data-valmsg-for="CustomThreshold"]');
  await expect(fieldError).toBeVisible();
  await expect(fieldError).toHaveText('Invalid time threshold format. Use values like 1d, 2.5w or 0.1h.');
  await customDelay.fill('2.5w');

  // A fresh read distinguishes the rejected draft from persisted privacy settings.
  await page.goto('/User/Timeline/Settings');
  await expect(publicToggle).not.toBeChecked();
  await expect(threshold).toHaveValue('');
});
