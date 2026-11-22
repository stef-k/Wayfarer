import { expect, test } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** Runs the real upload and import document, then delivers one content-free status hint at the EventSource boundary. */
test('Greek location transfer feedback survives an import status refresh', async ({ page, context }, testInfo) => {
    const config = loadSharedLayoutConfig();
    await page.goto('/Identity/Account/Login');
    await page.getByLabel('Username').fill(config.username);
    await page.getByLabel('Password').fill(config.password);
    await Promise.all([
        page.waitForURL(url => !url.pathname.endsWith('/Account/Login')),
        page.getByRole('button', { name: 'Log in' }).click()
    ]);
    await context.setExtraHTTPHeaders({ 'Accept-Language': 'el-GR' });
    await page.goto('/User/LocationImport/Upload');
    await expect(page.locator('html')).toHaveAttribute('lang', 'el-GR');
    await page.getByLabel('Τύπος αρχείου').selectOption('Csv');
    await page.getByLabel('Επιλογή αρχείου').setInputFiles({
        name: 'history.txt', mimeType: 'text/plain', buffer: Buffer.from('Latitude,Longitude,TimestampUtc\n')
    });
    const submit = page.getByRole('button', { name: 'Μεταφόρτωση αρχείου', exact: true });
    await submit.click();
    await expect(page.locator('[data-valmsg-for="File"]')).toContainText('Η κατάληξη αρχείου «.txt» δεν είναι έγκυρη');
    await expect(page.getByLabel('Τύπος αρχείου')).toHaveValue('Csv');

    let releaseRefresh!: () => void;
    const refresh = new Promise<void>(resolve => { releaseRefresh = resolve; });
    let delivered = false;
    await page.route('**/api/sse/import', async route => {
        if (delivered) return route.abort();
        delivered = true;
        await refresh;
        await route.fulfill({ contentType: 'text/event-stream', body: 'data: {"type":"import-state"}\n\n' });
    });
    await page.getByLabel('Επιλογή αρχείου').setInputFiles({
        name: 'history.csv', mimeType: 'text/csv', buffer: Buffer.from('Latitude,Longitude,TimestampUtc\n')
    });
    await submit.click();
    await expect(page.locator('main [role="alert"]')).toContainText('Το αρχείο μεταφορτώθηκε');
    const status = page.locator('#locationImport .importStatus');
    await expect(status).toHaveText('Σε παύση');

    const reload = page.waitForNavigation();
    releaseRefresh();
    await reload;
    await expect(status).toHaveText('Σε παύση');
    await expect(page.locator('html')).toHaveAttribute('lang', 'el-GR');
    await expect(page.locator('#enrichment-status')).toHaveText('Δεν έχει επιλεγεί πάροχος γεωκωδικοποίησης.');
    await page.screenshot({ path: process.env.WAYFARER_AREA8_SCREENSHOT
        || testInfo.outputPath('location-transfers-el-GR.png'), fullPage: true });
});
