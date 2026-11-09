import { expect, test } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** One real W1 authoring journey; deterministic command/form tests own the larger behavior matrix. */
test('Location Tiptap toolbar, dialogs and real image proxy preserve protected Notes', async ({ page }, testInfo) => {
    const config = loadSharedLayoutConfig();
    const direct: string[] = [];
    page.on('request', request => {
        const url = new URL(request.url());
        if (!['localhost', '127.0.0.1'].includes(url.hostname)) direct.push(request.url());
    });
    // Tile requests are unrelated to the editor; image-proxy requests go to the actual server unchanged.
    await page.route('**/api/tiles/**', route => route.abort());
    await page.goto('/Identity/Account/Login');
    await page.getByLabel('Username').fill(config.username);
    await page.getByLabel('Password').fill(config.password);
    await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')), page.getByRole('button', { name: 'Log in' }).click()]);
    await page.goto('/User/Location/Create');
    await page.locator('#Latitude').fill('37.98'); await page.locator('#Longitude').fill('23.72');
    await page.locator('#LocalTimestamp').fill('2026-11-03T10:15');
    await page.locator('.rich-notes-content').fill('Original Notes');
    await page.getByRole('button', { name: 'Add Location', exact: true }).click();
    await page.getByRole('link', { name: 'Open saved Location / Edit' }).click();
    await page.waitForURL(/\/User\/Location\/Edit\//);
    const editor = page.getByRole('textbox', { name: 'Add Notes' });
    await expect(editor).toBeVisible();
    expect(await page.locator('script[src*="quill"]').count()).toBe(0);
    const id = Number(await page.locator('#locationForm').getAttribute('data-location-id'));
    const detail = async () => (await page.request.get(`/api/location/history/${id}?viewerZone=UTC`)).json();
    const before = await detail();
    await editor.focus();
    await page.locator('#Altitude').fill('123');
    const unrelated = page.waitForRequest(request => request.method() === 'POST' && request.url().includes('/mutations/'));
    await Promise.all([page.waitForEvent('load'), page.getByRole('button', { name: 'Save', exact: true }).click()]);
    expect((await unrelated).postDataJSON().changes.notes).toBeUndefined();
    expect((await detail()).record.notesHtml).toBe(before.record.notesHtml);

    await editor.fill('Typed text');
    await editor.press('Control+End');
    await page.getByRole('button', { name: 'Bold', exact: true }).click();
    await editor.pressSequentially(' bold');
    await expect(editor.locator('strong')).toHaveText(' bold');
    await editor.press('Control+A');
    await page.getByRole('combobox', { name: 'Font', exact: true }).selectOption('serif');
    await page.getByRole('button', { name: 'Link', exact: true }).click();
    await page.getByRole('textbox', { name: 'URL', exact: true }).fill('https://example.test/notes');
    await page.getByRole('button', { name: 'Apply', exact: true }).click();
    await expect(editor.locator('a')).toContainText('Typed text');
    await page.getByRole('button', { name: 'Link', exact: true }).click();
    await page.getByRole('textbox', { name: 'URL', exact: true }).press('Escape');
    await expect(editor).toBeFocused();
    await editor.press('Control+End');
    await page.getByRole('button', { name: 'Image', exact: true }).click();
    await page.getByRole('textbox', { name: 'URL', exact: true }).fill('https://httpbin.org/image/png');
    const proxy = page.waitForResponse(response => response.url().includes('/Public/ProxyImage?'));
    await page.getByRole('button', { name: 'Apply', exact: true }).click();
    const response = await proxy;
    expect(response.status(), await response.text()).toBe(200);
    await expect(editor.locator('img[src]')).toHaveJSProperty('naturalWidth', 100);
    await expect(editor.locator('img[src]')).toHaveAttribute('src', /^\/Public\/ProxyImage\?url=/);
    await editor.press('Control+A');
    await page.getByRole('button', { name: 'Remove formatting', exact: true }).click();
    await expect(editor.locator('a')).toContainText('Typed text');
    await expect(editor.locator('img[src]')).toHaveCount(1);
    await expect(editor.locator('strong, span[style]')).toHaveCount(0);
    // Reapply a supported standard font and prove the actual protected save/server publication retains it.
    await page.getByRole('combobox', { name: 'Font', exact: true }).selectOption('monospace');
    await Promise.all([page.waitForEvent('load'), page.getByRole('button', { name: 'Save', exact: true }).click()]);
    const saved = (await detail()).record.notesHtml;
    expect(saved).toContain('font-family: monospace;');
    expect(saved).toContain('href="https://example.test/notes"');
    expect(saved).toContain('src="https://httpbin.org/image/png"');
    expect(saved).not.toContain('ProxyImage');
    await expect(editor.locator('img[src]')).toHaveJSProperty('naturalWidth', 100);
    expect(direct).toEqual([]);
    const screenshot = process.env.WAYFARER_W1_SCREENSHOT || testInfo.outputPath('location-tiptap.png');
    await page.screenshot({ path: screenshot, fullPage: true });
    await page.locator('#notes').locator('..').screenshot({ path: screenshot.replace('.png', '-editor.png') });
});
