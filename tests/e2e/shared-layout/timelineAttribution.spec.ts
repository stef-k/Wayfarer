import { expect, test } from '@playwright/test';
import { join } from 'node:path';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** Exercises the localized map footer on both authenticated Timeline routes with their normal styles. */
test('private Timeline maps show the localized branded footer and provider credit', async ({ page, context, baseURL }, testInfo) => {
    const { username, password } = loadSharedLayoutConfig();
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.addInitScript(() => localStorage.setItem('theme', 'light'));
    // Supply neutral tiles locally so the rendered controls can be checked without provider traffic.
    const tile = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=', 'base64');
    await page.route(/\/Public\/tiles\/\d+\/\d+\/\d+\.png/i, route =>
        route.fulfill({ contentType: 'image/png', body: tile }));

    await page.goto('/Identity/Account/Login');
    await page.getByLabel('Username').fill(username);
    await page.getByLabel('Password').fill(password);
    await Promise.all([
        page.waitForURL(url => !url.pathname.endsWith('/Account/Login')),
        page.getByRole('button', { name: 'Log in' }).click()
    ]);
    await context.addCookies([{ name: '.AspNetCore.Culture', value: 'c=el-GR%7Cuic=el-GR', url: baseURL! }]);

    const evidence = process.env.WAYFARER_BROWSER_EVIDENCE_DIRECTORY || testInfo.outputDir;
    const label = 'Αναφορά προέλευσης χάρτη';
    const routes = [
        { path: '/User/Timeline', capture: 'live-timeline-el-GR.png' },
        { path: '/User/Timeline/Chronological', capture: 'chronological-timeline-el-GR.png' }
    ];
    for (const { path, capture } of routes) {
        await test.step(path, async () => {
            await page.goto(path);
            await expect(page.locator('html')).toHaveAttribute('lang', 'el-GR');
            await expect(page.locator('body')).toHaveAttribute('data-bs-theme', 'light');
            const footer = page.locator('#mapContainer .leaflet-control-attribution');
            await expect(footer).toBeVisible();
            const wayfarer = footer.getByRole('link', { name: 'Wayfarer', exact: true });
            const author = footer.getByRole('link', { name: 'Stef K', exact: true });
            const leaflet = footer.getByRole('link', { name: 'Leaflet', exact: true });
            const provider = footer.getByRole('link', { name: 'OpenStreetMap', exact: true });
            await expect(wayfarer).toHaveAttribute('href', 'https://wayfarer.stefk.me');
            await expect(author).toHaveAttribute('href', 'https://stefk.me');
            await expect(leaflet).toHaveAttribute('href', 'https://leafletjs.com/');
            await expect(provider).toHaveAttribute('href', 'https://www.openstreetmap.org/copyright');
            await expect(footer).toContainText('OpenStreetMap contributors');
            await expect(footer.getByRole('link')).toHaveCount(4);
            await expect(footer).toHaveAttribute('aria-label', label);
            await expect(footer).toHaveAttribute('title', label);
            await expect(wayfarer).toHaveAttribute('title', 'Με την υποστήριξη του Wayfarer, δημιουργία του Stef');
            await expect(author).toHaveAttribute('title', 'Επισκεφθείτε το ιστολόγιο του Stef');
            await expect(wayfarer).toHaveCSS('text-decoration-line', 'none');
            await wayfarer.hover();
            await expect(wayfarer).toHaveCSS('text-decoration-line', 'underline');
            await page.screenshot({ path: join(evidence, capture), fullPage: true });
        });
    }
});
