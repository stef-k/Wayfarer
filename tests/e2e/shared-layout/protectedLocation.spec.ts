import { expect, test } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** One real form save with response loss, followed by one focused conflict/draft observation. */
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
    // Established Web Create supplies one run-owned history row; no fixture or creation refactoring.
    await page.goto('/User/Location/Create');
    await page.locator('#Latitude').fill('37.98'); await page.locator('#Longitude').fill('23.72');
    await page.locator('#LocalTimestamp').fill('2026-10-29T10:15');
    await page.locator('.ql-editor').fill('Original notes');
    await Promise.all([page.waitForURL(/\/User\/Location\/Edit\//), page.getByRole('button', { name: 'Add Location', exact: true }).click()]);
    const id = Number(await page.locator('#locationForm').getAttribute('data-location-id'));
    const detail = async () => {
        const response = await page.request.get(`/api/location/history/${id}?viewerZone=UTC`);
        expect(response.status()).toBe(200); return response.json();
    };
    const before = await detail();
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
    await Promise.all([page.waitForEvent('load'), page.getByRole('button', { name: 'Save', exact: true }).click()]);
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
        data: { contract: 'location-mutation-1', normalization: 'location-values-1', adapter: 'web-edit', baseHandle: base.baseHandle,
            kind: 'update', expected: { uuid: base.baseValues.uuid, notesHtml: base.baseValues.notesHtml }, changes: { notes: { op: 'set', html: 'Newer server Notes' } } }
    });
    expect(external.status()).toBe(200);
    await page.locator('.ql-editor').fill('My retained draft');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(page.locator('#location-save-status')).toContainText('locationBaseStale');
    await expect(page.locator('.ql-editor')).toContainText('My retained draft');
    await page.reload();
    await expect(page.locator('#location-save-status')).toContainText('Restored your retained draft');
    await expect(page.locator('.ql-editor')).toContainText('My retained draft');
    expect((await detail()).record.notesHtml).toBe('Newer server Notes');
    await page.screenshot({ path: testInfo.outputPath('protected-location-retained-draft.png'), fullPage: true });
});
