import { expect, test, type Page } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** Uses only the supervisor's run-owned authenticated Trip and the actual export/import controls. */
const login = async (page: Page) => {
  const config = loadSharedLayoutConfig();
  await page.goto('/Identity/Account/Login');
  await page.getByLabel('Username').fill(config.username);
  await page.getByLabel('Password').fill(config.password);
  await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')), page.getByRole('button', { name: 'Log in' }).click()]);
};

test('native personal download and reviewed update use real controls and retain recoverable intent', async ({ page }, testInfo) => {
  const id = process.env.WAYFARER_E2E_TRIP_ID!;
  await login(page);
  await page.goto('/User/Trip');
  const saved = await page.evaluate(async trip => {
    const token = (document.querySelector('input[name=__RequestVerificationToken]') as HTMLInputElement).value;
    return (await fetch(`/api/trips/${trip}/editor/planning/trip/${trip}`, { method: 'PATCH', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token },
      body: JSON.stringify({ expected: { parentId: trip, planning: null }, operation: 'set', value: { format: 1, entries: [
        { id: crypto.randomUUID(), when: { form: 'at', value: { precision: 'month', local: '2027-03' } }, label: 'Spring visit' }
      ] } }) })).status;
  }, id);
  expect(saved).toBe(200);
  await page.goto(`/User/Trip/View/${id}`);
  const openExport = async () => { await page.locator('#btn-more-actions').click(); await page.locator('#personal-native-export').click(); };
  await openExport();
  const include = page.getByLabel('Include private planning');
  await expect(include).not.toBeChecked();
  let download = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Download KML' }).click();
  const excluded = await readFile((await (await download).path())!, 'utf8');
  expect(excluded).toContain('<value>3</value>'); expect(excluded).not.toContain('name="Planning"');
  await openExport(); await expect(include).not.toBeChecked(); await include.check();
  await expect(page.getByRole('dialog')).toContainText('Server access controls cannot revoke an already downloaded file.');
  await page.getByRole('dialog').screenshot({ path: process.env.WAYFARER_NATIVE_SCREENSHOTS + '/personal-export.png' });
  download = page.waitForEvent('download'); await page.getByRole('button', { name: 'Download KML' }).click();
  const personal = await readFile((await (await download).path())!, 'utf8');
  expect(personal).toContain('name="Planning"'); expect(personal).toContain('<value>4</value>');
  await openExport(); await expect(include).not.toBeChecked(); await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await page.goto(`/Public/Trips/${id}`); await expect(page.locator('#personal-native-export')).toHaveCount(0);
  await page.goto('/User/Trip');
  await page.locator('#importFile').setInputFiles({ name: 'personal-v4.kml', mimeType: 'application/vnd.google-earth.kml+xml', buffer: Buffer.from(personal.replace('2027-03', '2027-04')) });
  const controls = page.locator('#trip-import-controls');
  await controls.getByRole('button', { name: 'Review update', exact: true }).click();
  await expect(controls.getByLabel('Keep existing', { exact: true })).toBeChecked();
  await controls.getByLabel('Use imported planning', { exact: true }).check();
  await controls.locator('summary').first().click(); await controls.locator('summary').last().click();
  await controls.screenshot({ path: process.env.WAYFARER_NATIVE_SCREENSHOTS + '/import-review.png' });
  // A known rejection keeps the same mounted file and chosen replacement for a deliberate fresh review.
  await page.route('**/User/Trip/Import', route => route.fulfill({ status: 422, contentType: 'application/json', body: JSON.stringify({ status: 'error', code: 'validation_failed' }) }), { times: 1 });
  await controls.getByRole('button', { name: 'Apply reviewed update' }).click();
  await expect(controls).toContainText('personal-v4.kml'); await expect(controls.getByLabel('Use imported planning', { exact: true })).toBeChecked();
  await controls.getByRole('button', { name: 'Refresh review' }).click();
  await expect(controls.getByLabel('Use imported planning', { exact: true })).toBeChecked();
  await Promise.all([page.waitForURL(`**/User/Trip/Edit/${id}`), controls.getByRole('button', { name: 'Apply reviewed update' }).click()]);
  await expect(page.locator('[data-planning] > summary')).toContainText('2027-04');
});

test('native import uncertainty recovery is reachable and requires confirmed abandonment and a new file', async ({ page }) => {
  const id = process.env.WAYFARER_E2E_TRIP_ID!;
  await login(page);
  const response = await page.request.get(`/Trip/ExportPersonalWayfarerKml/${id}?includePrivatePlanning=true`);
  expect(response.ok()).toBeTruthy(); const bytes = await response.body();
  await page.goto('/User/Trip');
  const select = () => page.locator('#importFile').setInputFiles({ name: 'retained.kml', mimeType: 'application/vnd.google-earth.kml+xml', buffer: bytes });
  await select(); const controls = page.locator('#trip-import-controls');
  await controls.getByRole('button', { name: 'Review update', exact: true }).click();
  let writes = 0; page.on('request', request => { if (new URL(request.url()).pathname === '/User/Trip/Import') writes++; });
  await controls.getByRole('button', { name: 'Cancel', exact: true }).click(); await expect(controls).toBeHidden(); expect(writes).toBe(0);
  await select(); await controls.getByRole('button', { name: 'Review update', exact: true }).click();
  await page.route('**/User/Trip/Import', route => route.abort(), { times: 1 });
  await controls.getByRole('button', { name: 'Apply reviewed update' }).click();
  await expect(controls).toContainText('outcome could not be confirmed');
  const heldWrites = writes; // Includes the deliberate duplicate lookup before the lost update reply.
  expect(heldWrites).toBe(2);
  await expect(controls.getByRole('button', { name: 'Create new Trip' })).toBeDisabled();
  await expect(controls.getByRole('button', { name: 'Apply reviewed update' })).toBeDisabled();
  await expect(page.locator('#importFile')).toBeDisabled();
  await page.reload(); await expect(page.locator('#importFile')).toBeDisabled();
  await expect(page.locator('#trip-import-controls')).toContainText('outcome could not be confirmed');
  const abandon = controls.getByRole('button', { name: 'Abandon unresolved attempt…' });
  await expect(abandon).toBeVisible();
  await expect(controls).toContainText('may already have succeeded');
  await expect(controls).toContainText('tab session');
  // Inspecting Trips is available but does not resolve the outcome or release the hold.
  const opened = page.waitForEvent('popup');
  await controls.getByRole('link', { name: 'Check My Trips' }).click();
  const inspection = await opened; await inspection.waitForLoadState(); await inspection.close();
  await expect(page.locator('#importFile')).toBeDisabled(); expect(writes).toBe(heldWrites);
  page.once('dialog', async dialog => {
    expect(dialog.type()).toBe('confirm');
    expect(dialog.message()).toContain('does not cancel or roll back server work');
    expect(dialog.message()).toContain('may create another copy');
    await dialog.dismiss();
  });
  await abandon.click(); await expect(page.locator('#importFile')).toBeDisabled();
  await expect(abandon).toBeVisible(); expect(writes).toBe(heldWrites);
  await controls.screenshot({ path: process.env.WAYFARER_NATIVE_SCREENSHOTS + '/import-recovery.png' });
  page.once('dialog', dialog => dialog.accept()); await abandon.click();
  await expect(abandon).toBeHidden(); await expect(page.locator('#importFile')).toBeEnabled();
  await expect(controls).toContainText('earlier import outcome is still unknown');
  await expect(controls.locator('#importPlanningRows')).toBeEmpty();
  await expect(controls.getByRole('button', { name: 'Apply reviewed update' })).toBeHidden();
  await expect(controls.getByRole('button', { name: 'Review update', exact: true })).toBeDisabled();
  await expect(controls.getByRole('button', { name: 'Create new Trip' })).toBeDisabled();
  expect(writes).toBe(heldWrites);
  // Selecting a file is the next deliberate request. The state-owner tests prove its exact multipart payload.
  const freshRequest = page.waitForRequest(request => new URL(request.url()).pathname === '/User/Trip/Import');
  await select();
  await freshRequest;
  await expect(controls.getByRole('button', { name: 'Review update', exact: true })).toBeEnabled();
  await expect(controls.getByRole('button', { name: 'Apply reviewed update' })).toBeHidden();
  expect(writes).toBe(heldWrites + 1); expect(page.url()).toContain('/User/Trip');
});
