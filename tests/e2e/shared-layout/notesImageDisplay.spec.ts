import { expect, test } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** One mounted modal/Print path and one failed editor image establish the DOM/network boundaries. */
test('notes images stay proxied before insertion while local maps and canonical saves retain their URLs', async ({ page }, testInfo) => {
    const config = loadSharedLayoutConfig();
    const tripId = process.env.WAYFARER_E2E_TRIP_ID!;
    const source = 'https://images.example.test/note?a=1&literal=&amp;';
    const notes = '<p>Image note</p><img src="https://images.example.test/note?a=1&amp;literal=&amp;amp;">';
    const direct: string[] = [];
    const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=', 'base64');
    await page.context().route('https://images.example.test/**', async route => {
        direct.push(route.request().url());
        await route.abort();
    });
    await page.context().route('**/Public/ProxyImage?**', route => route.fulfill({ contentType: 'image/png', body: png }));
    await page.context().route('**/thumbs/trips/**', route => route.fulfill({ contentType: 'image/png', body: png }));
    await page.goto('/Identity/Account/Login');
    await page.getByLabel('Username').fill(config.username);
    await page.getByLabel('Password').fill(config.password);
    await Promise.all([
        page.waitForURL(url => !url.pathname.endsWith('/Account/Login')),
        page.getByRole('button', { name: 'Log in' }).click()
    ]);
    await page.goto(`/User/Trip/Edit/${tripId}`);
    const token = await page.locator('#trip-editor-antiforgery input[name="__RequestVerificationToken"]').inputValue();
    const api = `/api/trips/${tripId}/editor`;
    const state = await (await page.request.get(api)).json();
    const update = await page.request.patch(`${api}/metadata/conditional`, {
        headers: { RequestVerificationToken: token }, data: {
            expected: { notesHtml: state.metadata.originalValues.notesHtml },
            changes: { notesHtml: notes }
        }
    });
    expect(update.ok(), `Metadata fixture returned ${update.status()}: ${await update.text()}`).toBeTruthy();

    await page.goto(`/Public/Trips/${tripId}`);
    await page.locator('[data-bs-target="#readableViewModal"]').click();
    const modal = page.locator('#readableViewModal');
    await expect(modal).toBeVisible();
    const map = modal.locator('img[src^="/thumbs/trips/"]');
    const originalMap = await map.getAttribute('src');
    await expect(map).toHaveAttribute('src', new RegExp(`^/thumbs/trips/${tripId}-800x450.jpg`));
    const note = modal.locator('.trip-notes-readable img');
    const proxied = await note.getAttribute('src');
    expect(new URL(proxied!, page.url()).searchParams.get('url')).toBe(source);
    await page.screenshot({ path: testInfo.outputPath('notes-readable-modal.png'), fullPage: true });
    await page.context().addInitScript(() => { window.print = () => {}; });
    const popupPromise = page.waitForEvent('popup');
    await page.locator('#btn-print-modal').click();
    const printed = await popupPromise;
    await expect(printed.locator('img[src^="/thumbs/trips/"]')).toHaveAttribute('src', originalMap!);
    const printedSource = await printed.locator('.trip-notes-readable img').getAttribute('src');
    const printUrl = new URL(printedSource!, page.url());
    expect(printUrl.searchParams.get('url')).toBe(source);
    expect(printUrl.searchParams.get('maxWidth')).toBe('600');
    expect(printUrl.searchParams.get('quality')).toBe('85');
    await printed.close();
    expect((await (await page.request.get(api)).json()).metadata.notesHtml).toBe(notes);

    // A 501 fixture proves that preview failure cannot trigger a direct-origin fallback.
    await page.context().unroute('**/Public/ProxyImage?**');
    await page.context().route('**/Public/ProxyImage?**', route => route.fulfill({ status: 501, headers: { 'cache-control': 'no-store' }, body: 'unavailable' }));
    await page.goto('/User/Location/Create');
    await expect(page.locator('#notes .ql-editor')).toBeVisible();
    const failedPreview = page.waitForResponse(response => response.url().includes('/Public/ProxyImage?'));
    await page.evaluate(value => {
        const quill = (window as any).Quill.find(document.querySelector('#notes'));
        quill.insertEmbed(0, 'image', value);
    }, source);
    expect((await failedPreview).status()).toBe(501);
    const image = page.locator('#notes .ql-editor img');
    expect(new URL((await image.getAttribute('src'))!, page.url()).searchParams.get('url')).toBe(source);
    // Run the actual form handler without posting a Location outside the fixture's ownership.
    await page.evaluate(() => document.querySelector('#notes')!.closest('form')!.dispatchEvent(new Event('submit', { cancelable: true })));
    const saved = await page.locator('#Notes').inputValue();
    expect(saved).toContain('src="https://images.example.test/note?a=1&amp;literal=&amp;amp;"');
    expect(saved).not.toContain('/Public/ProxyImage');
    expect(direct).toEqual([]);
});
