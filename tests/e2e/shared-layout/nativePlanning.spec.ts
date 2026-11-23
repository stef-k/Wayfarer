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

test('Greek My Trips native review and language switching retain recoverable intent', async ({ page }, testInfo) => {
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
  expect(excluded).toMatch(/name="WayfarerSchemaVersion">\s*<value>5<\/value>/); expect(excluded).not.toContain('name="Planning"');
  await openExport(); await expect(include).not.toBeChecked(); await include.check();
  await expect(page.getByRole('dialog')).toContainText('Server access controls cannot revoke an already downloaded file.');
  download = page.waitForEvent('download'); await page.getByRole('button', { name: 'Download KML' }).click();
  const personal = await readFile((await (await download).path())!, 'utf8');
  expect(personal).toContain('name="Planning"'); expect(personal).toMatch(/name="WayfarerSchemaVersion">\s*<value>5<\/value>/);
  await openExport(); await expect(include).not.toBeChecked(); await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await page.goto(`/Public/Trips/${id}`); await expect(page.locator('#personal-native-export')).toHaveCount(0);
  await page.goto('/User/Trip?origin=review#trip-stats');
  const openLanguage = page.locator('#language-open');
  const chooseLanguage = async (tag: string) => {
    await openLanguage.click();
    await page.locator(`#language-options [value="${tag}"]`).check();
    await page.locator('#language-apply').click();
  };
  let preferenceWrites = 0;
  page.on('request', request => { if (new URL(request.url()).pathname === '/Language/Set') preferenceWrites++; });
  await chooseLanguage('el-GR');
  await expect(page.locator('html')).toHaveAttribute('lang', 'el-GR');
  await expect(page.getByRole('heading', { name: 'Τα ταξίδια μου' })).toBeVisible();
  await expect(page).toHaveURL(/origin=review#trip-stats$/);

  for (const [route, heading] of [['Compose', 'Σύγκριση και συνδυασμός ταξιδιών'], ['Copy', 'Αντιγραφή σε υπάρχον ταξίδι']]) {
    await page.goto(`/User/Trip/${route}`);
    await expect(page.locator('html')).toHaveAttribute('lang', 'el-GR');
    await expect(page.locator('#trip-composition-app').getByRole('heading', { name: heading, exact: true })).toBeVisible();
  }
  await page.goto('/User/Trip?origin=review#trip-stats');
  const select = () => page.locator('#importFile').setInputFiles({
    name: 'personal-v5.kml', mimeType: 'application/vnd.google-earth.kml+xml', buffer: Buffer.from(personal.replace('2027-03', '2027-04'))
  });
  const controls = page.locator('#trip-import-controls');
  const update = controls.locator('#btnUpdate');
  const selectedPlan = controls.getByLabel('Χρήση εισαγόμενου σχεδιασμού', { exact: true });
  await select();
  await update.click();
  await expect(controls.getByLabel('Διατήρηση υπάρχοντος', { exact: true })).toBeChecked();
  await selectedPlan.check();
  await chooseLanguage('en-US');
  await expect(page.locator('#language-keep')).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(openLanguage).toBeFocused();
  await expect(selectedPlan).toBeChecked();
  await expect(controls).toContainText('personal-v5.kml');
  expect(preferenceWrites).toBe(1);

  // A known refusal preserves the reviewed file and permits an explicit refresh.
  await page.route('**/User/Trip/Import', route => route.fulfill({
    status: 409, contentType: 'application/json', json: { status: 'error', code: 'import_review_stale' }
  }), { times: 1 });
  await controls.locator('#btnApplyImport').click();
  await expect(controls).toContainText('Το ταξίδι ή το αρχείο άλλαξε.');
  await expect(selectedPlan).toBeChecked();
  await update.click();
  await expect(controls.locator('#btnApplyImport')).toBeEnabled();
  await expect(selectedPlan).toBeChecked();
  await controls.locator('summary').first().click();
  await controls.locator('summary').last().click();
  await expect(controls).toContainText('Spring visit');
  await controls.screenshot({ path: process.env.WAYFARER_AREA9_SCREENSHOT || testInfo.outputPath('greek-trip-import.png') });
  await Promise.all([page.waitForURL(`**/User/Trip/Edit/${id}`), controls.locator('#btnApplyImport').click()]);
  await expect(page.locator('[data-planning] > summary')).toContainText('2027-04');

  // Simulate a lost mutation reply through the same mounted importer and authorize exactly one preference write.
  await page.goto('/User/Trip?origin=review#trip-stats');
  await select();
  await update.click();
  await page.route('**/User/Trip/Import', route => route.abort(), { times: 1 });
  await controls.locator('#btnApplyImport').click();
  await expect(controls).toContainText('Το αποτέλεσμα της εισαγωγής δεν επιβεβαιώθηκε.');
  const marker = () => controls.evaluate(el => sessionStorage.getItem(`wayfarer:native-import-pending:${(el as HTMLElement).dataset.owner}`));
  const unresolved = await marker();
  expect(unresolved).toMatch(/^pending:/);
  await chooseLanguage('en-US');
  await page.locator('#language-confirm').click();
  await expect(page.locator('html')).toHaveAttribute('lang', 'en-US');
  await expect(page).toHaveURL(/origin=review#trip-stats$/);
  await expect(controls).toContainText('The import outcome could not be confirmed.');
  await expect(page.locator('#importFile')).toBeDisabled();
  await expect(page.locator('#importFileMyMaps')).toBeDisabled();
  expect(await marker()).toBe(unresolved);
  expect(preferenceWrites).toBe(2);
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
