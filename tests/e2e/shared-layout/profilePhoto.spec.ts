import { expect, test } from '@playwright/test';
import path from 'node:path';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** One mounted Profile/Group journey; lower seams own authorization and failure matrices. */
test('profile photo crop save Group display remove fallback and retained public preference', async ({ page }, testInfo) => {
    const config = loadSharedLayoutConfig();
    await page.goto('/Identity/Account/Login');
    await page.getByLabel('Username').fill(config.username);
    await page.getByLabel('Password').fill(config.password);
    await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')), page.getByRole('button', { name: 'Log in' }).click()]);
    await page.goto('/Identity/Account/Manage');
    await expect(page.locator('#photo-status')).toContainText('state refreshed');
    await expect(page.locator('#Input_ShowProfilePhotoOnPublicTrips')).not.toBeChecked();
    await page.locator('#photo-file').setInputFiles(path.resolve('tools/image-helper/fixtures/orientation-7.png'));
    await expect(page.locator('#photo-framing')).toBeVisible();
    await page.locator('#photo-zoom').focus(); await page.keyboard.press('ArrowRight');
    await page.locator('#photo-pan-x').focus(); await page.keyboard.press('ArrowRight');
    await page.screenshot({ path: process.env.WAYFARER_PROFILE_PHOTO_SCREENSHOT || testInfo.outputPath('profile-photo.png'), fullPage: true });
    const saved = page.waitForResponse(response => response.url().endsWith('/api/profile-photo/me') && response.request().method() === 'POST');
    await page.locator('#photo-save').click();
    const result = await saved;
    expect(result.status(), result.ok() ? '' : await result.text()).toBe(200);
    await expect(page.locator('#current-profile-photo')).toBeVisible();
    const token = await page.locator('#profile-form input[name="__RequestVerificationToken"]').inputValue();
    const created = await page.request.post('/api/groups', { headers: { RequestVerificationToken: token }, data: { name: 'Profile photo browser group' } });
    expect(created.status()).toBe(201);
    const group = await created.json();
    await page.goto(`/User/Groups/Map?groupId=${group.id}`);
    await expect(page.locator('#userSidebar .profile-photo img')).toHaveCount(1);
    await expect.poll(() => page.locator('#userSidebar .profile-photo img').evaluate((img: HTMLImageElement) => img.complete && img.naturalWidth > 0)).toBe(true);
    await page.goto('/Identity/Account/Manage');
    await expect(page.locator('#photo-status')).toContainText('state refreshed');
    await page.locator('#Input_ShowProfilePhotoOnPublicTrips').check();
    await Promise.all([page.waitForURL(url => url.pathname.endsWith('/Account/Manage')), page.locator('#update-profile-button').click()]);
    await expect(page.locator('#photo-consent')).toContainText('consent is ON');
    const removed = page.waitForResponse(response => response.url().endsWith('/api/profile-photo/me') && response.request().method() === 'DELETE');
    await page.locator('#photo-remove').click(); expect((await removed).status()).toBe(200);
    await expect(page.locator('#current-profile-photo')).toBeHidden();
    await expect(page.locator('#Input_ShowProfilePhotoOnPublicTrips')).toBeChecked();
    await page.goto(`/User/Groups/Map?groupId=${group.id}`);
    await expect(page.locator('#userSidebar .profile-photo img')).toHaveCount(0);
    await expect(page.locator('#userSidebar .profile-photo')).toContainText('S');
});

/** The mounted crop remains a draft until the browser saves the language and makes one safe GET. */
test('Greek Identity profile photo feedback and language draft protection', async ({ page, context, baseURL }) => {
    const config = loadSharedLayoutConfig();
    await page.goto('/Identity/Account/Login');
    await page.getByLabel('Username').fill(config.username);
    await page.getByLabel('Password').fill(config.password);
    await Promise.all([
        page.waitForURL(url => !url.pathname.endsWith('/Account/Login')),
        page.getByRole('button', { name: 'Log in' }).click()
    ]);
    await context.addCookies([{ name: '.AspNetCore.Culture', value: 'c=el-GR%7Cuic=el-GR', url: baseURL! }]);
    const profile = '/Identity/Account/Manage?panel=photo#profile-photo-editor';
    await page.goto(profile);
    await expect(page.locator('html')).toHaveAttribute('lang', 'el-GR');
    await expect(page.locator('#photo-status')).toContainText('Η κατάσταση της φωτογραφίας ανανεώθηκε.');
    await page.locator('#photo-file').setInputFiles({
        name: 'invalid.png',
        mimeType: 'image/png',
        buffer: Buffer.from('invalid image')
    });
    await expect(page.locator('#photo-status')).toContainText('Επιλέξτε ή εξαγάγετε JPEG, PNG ή στατικό WebP.');
    await page.locator('#photo-file').setInputFiles(path.resolve('tools/image-helper/fixtures/orientation-7.png'));
    await expect(page.locator('#photo-framing')).toBeVisible();
    await page.locator('#photo-zoom').focus();
    await page.keyboard.press('ArrowRight');
    const zoom = await page.locator('#photo-zoom').inputValue();

    const requests: string[] = [];
    page.on('request', request => {
        const { pathname } = new URL(request.url());
        if (pathname === '/Language/Set' || request.isNavigationRequest() ||
            (pathname === '/api/profile-photo/me' && request.method() !== 'GET')) {
            requests.push(`${request.method()} ${pathname}`);
        }
    });
    await page.route('**/Language/Set', route => route.fulfill({ status: 400 }), { times: 1 });
    const open = page.locator('#language-open');
    for (const firstAction of ['#language-keep', '#language-confirm']) {
        await open.click();
        await page.locator('#language-options input[value="en-US"]').check();
        await page.locator('#language-apply').click();
        await page.locator(firstAction).click();
        if (firstAction === '#language-keep') {
            await expect(open).toBeFocused();
            expect(requests).toEqual([]);
        } else {
            await expect(page.locator('#language-error')).toBeVisible();
            expect(requests).toEqual(['POST /Language/Set']);
        }
        await expect(page.locator('#photo-zoom')).toHaveValue(zoom);
        await expect(page.locator('#photo-file')).toHaveValue(/orientation-7\.png$/);
    }
    await page.locator('#language-confirm').click();
    await expect(page.locator('html')).toHaveAttribute('lang', 'en-US');
    expect(page.url()).toBe(new URL(profile, baseURL).href);
    await expect(page.locator('#photo-file')).toHaveValue('');
    expect(requests).toEqual(['POST /Language/Set', 'POST /Language/Set', 'GET /Identity/Account/Manage']);
});
