import { randomUUID } from 'node:crypto';
import { expect, test } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** A stale picker result submits the real Members form and displays the server's refusal. */
test('Members shows the unavailable account message after Send Invite', async ({ page }) => {
    const config = loadSharedLayoutConfig();
    await page.goto('/Identity/Account/Login');
    await page.getByLabel('Username').fill(config.username);
    await page.getByLabel('Password').fill(config.password);
    await Promise.all([
        page.waitForURL(url => !url.pathname.endsWith('/Account/Login')),
        page.getByRole('button', { name: 'Log in' }).click()
    ]);
    await page.goto('/Identity/Account/Manage');
    const token = await page.locator('#profile-form input[name="__RequestVerificationToken"]').inputValue();
    const created = await page.request.post('/api/groups', {
        headers: { RequestVerificationToken: token }, data: { name: 'Unavailable invite browser group' }
    });
    expect(created.status()).toBe(201);
    const group = await created.json();
    await page.goto(`/User/Groups/Members?groupId=${group.id}`);
    const target = randomUUID();
    await page.route('**/api/users/search?**', route =>
        route.fulfill({ json: [{ id: target, userName: 'Former account', displayName: null }] }));
    await page.getByLabel('Search User').fill('Former account');
    await expect(page.locator('#inviteeUserId')).toHaveValue(target);
    const [response] = await Promise.all([
        page.waitForResponse(r => r.url().endsWith('/User/Groups/InviteAjax')),
        page.getByRole('button', { name: 'Send Invite' }).click()
    ]);
    expect(response.status()).toBe(400);
    await expect(page.locator('#alertMessage')).toHaveText('This account is no longer available. Select another account.');
    await expect(page.locator('#alertMessage')).toBeVisible();
    await page.reload();
    await expect(page.locator('#invitesTable')).toBeVisible();
    await expect(page.locator('#invitesTable tr[data-invite-id]')).toHaveCount(0);
});
