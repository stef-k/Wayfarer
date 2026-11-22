import { expect, test } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** Greek Location binding and protected recovery run through the real routes and persisted row. */
test('protected Location Web form preserves capture instant and retained conflict drafts', async ({ page }, testInfo) => {
    const config = loadSharedLayoutConfig();
    await page.route('**/*', async route => {
        const url = new URL(route.request().url());
        if (!['localhost', '127.0.0.1'].includes(url.hostname) || url.pathname.toLowerCase().startsWith('/api/tiles'))
            return route.abort();
        return route.continue();
    });
    await page.goto('/Identity/Account/Login');
    await page.getByLabel('Username').fill(config.username);
    await page.getByLabel('Password').fill(config.password);
    await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')), page.getByRole('button', { name: 'Log in' }).click()]);
    await page.goto('/User/Visit');
    await page.locator('#language-open').click();
    await page.locator('#language-options [value="el-GR"]').check();
    await Promise.all([page.waitForEvent('domcontentloaded'), page.locator('#language-apply').click()]);
    await expect(page.locator('html')).toHaveAttribute('lang', 'el-GR');
    await expect(page.locator('#visit-summary')).toHaveText('Σύνολο: 0 επισκέψεις');

    // Create supplies one run-owned row. A valid field must survive the first rejected submission.
    const returnUrl = '/User/Location/AllLocations?keep=1#history';
    await page.goto('/User/Location/Create');
    await expect(page.locator('#language-open')).toHaveCount(0);
    await page.locator('#Latitude').fill('92.5');
    await page.locator('#Longitude').fill('23.72123456');
    await page.locator('#Altitude').fill('123.45');
    await page.locator('#LocalTimestamp').fill('2026-10-29T10:15');
    await page.locator('#Address').fill('Original & authored');
    await page.locator('.rich-notes-content').fill('Original notes');
    const add = page.getByRole('button', { name: 'Προσθήκη καταγραφής τοποθεσίας', exact: true });
    await add.click();
    await expect(page.locator('[data-valmsg-for="Latitude"]')).toContainText('μεταξύ -90 και 90');
    await expect(page.locator('#Latitude')).toHaveValue('92.5');
    await expect(page.locator('#Longitude')).toHaveValue('23.72123456');
    await expect(page.locator('#Altitude')).toHaveValue('123.45');
    await expect(page.locator('#LocalTimestamp')).toHaveValue('2026-10-29T10:15');
    await page.locator('#Latitude').fill('37.98123456');
    await add.click();
    const saved = page.getByRole('link', { name: 'Άνοιγμα και επεξεργασία αποθηκευμένης καταγραφής' });
    await expect(saved).toBeVisible();
    const edit = new URL((await saved.getAttribute('href'))!, page.url());
    edit.searchParams.set('returnUrl', returnUrl);
    await page.goto(edit.href);
    await expect(page.locator('#language-open')).toHaveCount(0);
    await expect(page.locator('#Latitude')).toHaveValue('37.98123456');
    await expect(page.locator('#Longitude')).toHaveValue('23.72123456');
    await expect(page.locator('#Altitude')).toHaveValue('123.45');
    await expect(page.locator('#LocalTimestamp')).toHaveValue('2026-10-29T10:15');
    await expect(page.locator('#ReturnUrl')).toHaveValue(returnUrl);
    const id = Number(await page.locator('#locationForm').getAttribute('data-location-id'));
    const detail = async () => {
        const response = await page.request.get(`/api/location/history/${id}?viewerZone=UTC`);
        expect(response.status()).toBe(200); return response.json();
    };
    const before = await detail();
    expect(before.record.position).toMatchObject({ latitude: 37.98123456, longitude: 23.72123456 });
    expect(before.record.altitude).toBe(123.45);
    expect(Date.parse(before.record.eventUtc)).toBe(Date.parse('2026-10-29T08:15Z'));
    let dropped = false;
    let posted: any = null;
    await page.route('**/api/location/*/mutations/*/*', async route => {
        if (route.request().method() === 'POST' && !dropped) {
            dropped = true; posted = route.request().postDataJSON();
            const response = await route.fetch(); expect(response.status()).toBe(200);
            return route.abort('failed'); // The production transaction really committed before the lost browser reply.
        }
        return route.continue();
    });
    await page.locator('#Latitude').fill('40.7'); await page.locator('#Longitude').fill('-74');
    await Promise.all([page.waitForEvent('load'), page.getByRole('button', { name: 'Αποθήκευση', exact: true }).click()]);
    expect(dropped).toBe(true); expect(posted.changes.eventUtc).toBeUndefined();
    expect(posted.changes.manualAddress).toBeUndefined();
    const moved = await detail();
    expect(moved.record.eventUtc).toBe(before.record.eventUtc);
    expect(moved.record.position.latitude).toBe(40.7); expect(moved.record.position.longitude).toBe(-74);
    expect(moved.locationMutation.originalCreateAddressPublication).toBe('closed');
    // A distinct independent request changes the same field after this form observed its originals.
    const base = moved.locationMutation;
    const csrf = await page.locator('input[name=__RequestVerificationToken]').first().inputValue();
    const external = await page.request.post(`/api/location/${id}/mutations/${base.baseId}/${crypto.randomUUID()}`, {
        headers: { RequestVerificationToken: csrf },
        data: { contract: 'location-mutation-1', normalization: base.normalization, adapter: 'web-edit', baseHandle: base.baseHandle,
            kind: 'update', expected: { uuid: base.baseValues.uuid, notesHtml: base.baseValues.notesHtml }, changes: { notes: { op: 'set', html: 'Newer server Notes' } } }
    });
    expect(external.status()).toBe(200);
    await page.locator('#notes [contenteditable=true]').fill('My retained draft');
    await page.getByRole('button', { name: 'Αποθήκευση', exact: true }).click();
    await expect(page.locator('#location-save-status')).toContainText('Η καταγραφή άλλαξε αφού την ανοίξατε.');
    await expect(page.locator('#notes [contenteditable=true]')).toContainText('My retained draft');
    await page.reload();
    await expect(page.locator('#location-save-status')).toContainText('Το διατηρημένο προσχέδιο επανήλθε.');
    await expect(page.locator('#notes [contenteditable=true]')).toContainText('My retained draft');
    expect((await detail()).record.notesHtml).toBe('Newer server Notes');

    await expect(page.locator('#LocalTimestamp')).toHaveValue('2026-10-29T04:15');
    if (await page.locator('body').getAttribute('data-bs-theme') !== 'dark') await page.locator('#themeToggle').click();
    await expect(page.locator('body')).toHaveAttribute('data-bs-theme', 'dark');
    await page.setViewportSize({ width: 640, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: process.env.WAYFARER_AREA7_SCREENSHOT || testInfo.outputPath('protected-location-retained-draft.png'), fullPage: true });
});

/** W971-1 requires a mounted ordinary form to prove real transport loss and retained newer input. */
test('Web creation retry retains unsaved differences before explicit protected Edit', async ({ page }, testInfo) => {
    const config = loadSharedLayoutConfig();
    await page.route('**/*', route => {
        const url = new URL(route.request().url());
        return !['localhost', '127.0.0.1'].includes(url.hostname) || url.pathname.toLowerCase().startsWith('/api/tiles')
            ? route.abort() : route.continue();
    });
    await page.goto('/Identity/Account/Login');
    await page.getByLabel('Username').fill(config.username);
    await page.getByLabel('Password').fill(config.password);
    await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')), page.getByRole('button', { name: 'Log in' }).click()]);
    await page.goto('/User/Location/Create');
    const key = await page.locator('#CreateKey').inputValue();
    await page.locator('#Latitude').fill('0'); await page.locator('#Longitude').fill('0');
    await page.locator('#LocalTimestamp').fill('2026-11-08T12:00');
    await page.locator('#Address').fill('Manual address');
    await page.locator('.rich-notes-content').fill('First attempted Notes');
    let lost = false;
    await page.route('**/User/Location/Create', async route => {
        if (route.request().method() === 'POST' && !lost) {
            const result = await route.fetch(); expect(result.status()).toBe(200);
            lost = true; return route.abort('failed');
        }
        return route.continue();
    });
    await page.getByRole('button', { name: 'Add Location', exact: true }).click();
    await expect(page.locator('#create-status')).toContainText('could not be confirmed');
    await page.locator('.rich-notes-content').fill('Newer unsaved Notes / Αλλαγή');
    await page.getByRole('button', { name: 'Retry original save', exact: true }).click();
    await expect(page.locator('#create-status')).toContainText('current form changes were not applied');
    await expect(page.locator('.rich-notes-content')).toContainText('Newer unsaved Notes');
    await expect(page.getByLabel('Actual saved location')).toContainText('First attempted Notes');
    await expect(page.locator('#CreateKey')).toHaveValue(key);
    await page.screenshot({ path: process.env.WAYFARER_W971_SCREENSHOT || testInfo.outputPath('971-unsaved-differences.png'), fullPage: true });
    await page.getByRole('link', { name: 'Open saved Location / Edit' }).click();
    await page.waitForURL(/\/User\/Location\/Edit\//);
    await page.locator('.rich-notes-content').fill('Newer unsaved Notes / Αλλαγή');
    await Promise.all([page.waitForEvent('load'), page.getByRole('button', { name: 'Save', exact: true }).click()]);
    const id = Number(await page.locator('#locationForm').getAttribute('data-location-id'));
    const response = await page.request.get(`/api/location/history/${id}?viewerZone=UTC`);
    const detail = await response.json();
    expect(detail.record.uuid).toBe(key);
    expect(detail.record.notesHtml).toContain('Newer unsaved Notes');
    expect(detail.record.source).toBe('web-create');
    expect(detail.record.originalTimeZoneId).toBeNull();
    expect(detail.record.submittedAtUtc).toBeNull();
});
