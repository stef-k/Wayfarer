import { expect, test, type Page } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** Uses the established authenticated User fixture; credentials and pairing secrets are never logged. */
const signIn = async (page: Page) => {
    const config = loadSharedLayoutConfig();
    await page.goto('/Identity/Account/Login');
    await page.getByLabel('Username').fill(config.username);
    await page.getByLabel('Password').fill(config.password);
    await Promise.all([
        page.waitForURL(url => !url.pathname.endsWith('/Account/Login')),
        page.getByRole('button', { name: 'Log in' }).click()
    ]);
};

/** A single mounted journey proves asset wiring, actual QR rendering and secret-free navigation restoration. */
test('connect apps reveals once and stays secret-free after Done, Back and reload', async ({ page, context }, testInfo) => {
    await signIn(page);
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    const safeStatus = async () => {
        const response = await page.request.get('/User/ApiToken', { headers: { Accept: 'application/json' } });
        expect(response.headers()['cache-control']).toBe('no-store');
        return response.json();
    };
    const before = await safeStatus();
    await page.goto('/User/Settings');
    await page.getByRole('link', { name: 'Connect apps', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Connect apps to Wayfarer' })).toBeVisible();
    await expect(page.locator('#connection-token')).toHaveText('');
    await expect(page.locator('#connection-qr')).toBeEmpty();
    expect(await safeStatus()).toEqual(before);
    const responsePromise = page.waitForResponse(response =>
        response.request().method() === 'POST' && /\/User\/ApiToken\/(Create|Replace)$/.test(response.url()));
    await page.getByRole('button', { name: before ? 'Replace connection token' : 'Create connection token', exact: true }).click();
    if (before) await page.getByRole('button', { name: 'Replace token and reconnect apps', exact: true }).click();
    const response = await responsePromise;
    expect(response.status()).toBe(before ? 200 : 201);
    expect(response.headers()['cache-control']).toBe('no-store');
    const issued = await response.json();
    await expect(page.locator('#connection-token')).toHaveText(issued.token);
    await expect(page.locator('#connection-qr svg')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Copy token', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Copy server address', exact: true }).click();
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(new URL(page.url()).origin);
    await page.getByRole('button', { name: 'Copy token', exact: true }).click();
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(issued.token);
    await page.getByRole('button', { name: 'Done — hide token', exact: true }).click();
    await expect(page.locator('#connection-token')).toHaveText('');
    await expect(page.locator('#connection-qr')).toBeEmpty();
    await expect(page.locator('#connection-qr')).not.toHaveAttribute('title');
    await page.goto('/User/Settings');
    await page.goBack();
    await expect(page.locator('#connection-token')).toHaveText('');
    await expect(page.locator('#connection-qr')).toBeEmpty();
    await page.reload();
    await expect(page.locator('#connection-token')).toHaveText('');
    await expect(page.locator('#connection-qr')).toBeEmpty();
    expect(await safeStatus()).toEqual({ tokenId: issued.tokenId, issuedAt: issued.issuedAt });
    // Capture only the safe status page; no bearer token or pairing graphic goes into a screenshot.
    await page.screenshot({ path: testInfo.outputPath('connect-apps-hidden.png'), fullPage: true });
});
