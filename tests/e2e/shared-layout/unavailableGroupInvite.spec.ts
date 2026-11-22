import { randomUUID } from 'node:crypto';
import { expect, test } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** A stale picker result exercises the real refusal, then the narrow map exercises localized focus and actions. */
test('Greek Groups shows the unavailable invite and narrow roster actions', async ({ page, context, baseURL }) => {
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
    await context.addCookies([{ name: '.AspNetCore.Culture', value: 'c=el-GR%7Cuic=el-GR', url: baseURL! }]);
    await page.goto(`/User/Groups/Members?groupId=${group.id}`);
    await expect(page.locator('html')).toHaveAttribute('lang', 'el-GR');
    await expect(page.getByRole('heading', { name: 'Μέλη – Unavailable invite browser group' })).toBeVisible();
    await expect(page.getByRole('cell', { name: 'Κάτοχος', exact: true })).toBeVisible();
    const target = randomUUID();
    await page.route('**/api/users/search?**', route =>
        route.fulfill({ json: [{ id: target, userName: 'Former account', displayName: null }] }));
    await page.getByLabel('Αναζήτηση χρήστη').fill('Former account');
    await expect(page.locator('#inviteeUserId')).toHaveValue(target);
    const [response] = await Promise.all([
        page.waitForResponse(r => r.url().endsWith('/User/Groups/InviteAjax')),
        page.getByRole('button', { name: 'Αποστολή πρόσκλησης' }).click()
    ]);
    expect(response.status()).toBe(400);
    await expect(page.locator('#alertMessage')).toHaveText('Αυτός ο λογαριασμός δεν είναι πλέον διαθέσιμος. Επιλέξτε άλλον λογαριασμό.');
    await expect(page.locator('#alertMessage')).toBeVisible();
    await expect(page.locator('#invitesTable tr[data-invite-id]')).toHaveCount(0);

    await page.setViewportSize({ width: 375, height: 760 });
    await page.goto(`/User/Groups/Map?groupId=${group.id}`);
    const open = page.getByRole('button', { name: 'Μέλη', exact: true });
    const panel = page.getByRole('complementary', { name: 'Μέλη' });
    const close = panel.getByRole('button', { name: 'Κλείσιμο πίνακα μελών' });
    await expect(panel).toHaveCount(0);
    await open.click();
    await expect(page.locator('.group-map-members-toggle')).toHaveAttribute('aria-expanded', 'true');
    await expect(close).toBeFocused();
    await expect(panel.getByRole('button', { name: `Εμφάνιση μόνο του μέλους ${config.username}` })).toBeVisible();
    await expect(panel.getByRole('button', { name: 'Απόκρυψη όλων' })).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(open).toBeFocused();
    await expect(panel).toHaveCount(0);

    const history = page.waitForResponse(response => {
        const request = response.request();
        if (request.method() !== 'POST' || !request.url().endsWith('/locations/query')) return false;
        const body = request.postDataJSON();
        return body.DateType === 'day' && body.Year === 2025 && body.Month === 1 && body.Day === 15;
    });
    await page.locator('#datePicker').fill('2025-01-15');
    const result = await history;
    expect(result.status()).toBe(200);
    expect(result.request().postDataJSON().UserIds).toHaveLength(1);
    await expect(page.locator('#groupHistoryStatus')).toHaveText(
        'Δεν υπάρχουν καταγραφές τοποθεσίας σε αυτή την περιοχή του χάρτη για την επιλεγμένη περίοδο.');
});
