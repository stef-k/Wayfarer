import { expect, test } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** Exercise the document locale, retained rule owner and read feedback at the mounted calendar seam. */
test('private Timeline language switch retains a frozen Week and localizes a failed read', async ({ page }) => {
    const fixture = loadSharedLayoutConfig();
    await page.goto('/Identity/Account/Login');
    await page.getByLabel('Username').fill(fixture.username);
    await page.getByLabel('Password').fill(fixture.password);
    await Promise.all([
        page.waitForURL(url => !url.pathname.endsWith('/Login')),
        page.getByRole('button', { name: 'Log in' }).click()
    ]);

    const period = '**/User/Timeline/calendar/period';
    const requests: URLSearchParams[] = [];
    page.on('request', request => {
        if (request.url().endsWith('/Timeline/calendar/period')) {
            requests.push(new URLSearchParams(request.postData() ?? ''));
        }
    });
    await page.goto('/User/Timeline/Chronological?viewType=week&year=2026&month=10&day=29&timeBasis=viewer&viewerZone=Europe%2FAthens&weekStartsOn=7#week');
    await expect(page.locator('#calendarCaption')).toContainText('Frozen week starts on Sunday');
    await expect(page.locator('#choosePeriod')).toHaveText('Oct 25, 2026 – Oct 31, 2026');
    await expect(page.locator('#timeline-summary')).toContainText('current');

    await page.locator('#connectionSettings').click();
    await page.locator('#maximumGap').fill('2.5');
    await page.locator('#rulesDialog .btn-close').click();
    let preferenceWrites = 0;
    page.on('request', request => {
        if (request.method() === 'POST' && new URL(request.url()).pathname === '/Language/Set') preferenceWrites++;
    });
    const applyGreek = async () => {
        await page.locator('#language-open').click();
        await page.locator('#language-options [value="el-GR"]').check();
        await page.locator('#language-apply').click();
        await expect(page.locator('#language-keep')).toBeFocused();
    };
    await applyGreek();
    await page.locator('#language-keep').click();
    await expect(page.locator('#language-open')).toBeFocused();
    expect(preferenceWrites).toBe(0);

    // Fail only the period read. The real app keeps its selection and reads analysis separately.
    await page.route(period, route => route.fulfill({ status: 503, json: { code: 'historyStoreUnavailable' } }));
    const englishUrl = page.url();
    await applyGreek();
    await Promise.all([
        page.waitForEvent('domcontentloaded'),
        page.locator('#language-confirm').click()
    ]);
    await expect(page.locator('html')).toHaveAttribute('lang', 'el-GR');
    await expect(page.locator('#choosePeriod')).toHaveText('25 Οκτ 2026 – 31 Οκτ 2026');
    await expect(page.locator('#calendarCaption')).toContainText('Σταθερή πρώτη ημέρα εβδομάδας: Κυριακή');
    await expect(page.locator('#timelineNotice')).toContainText('Το ιστορικό χρονολογίου είναι προσωρινά μη διαθέσιμο');
    expect(page.url()).toBe(englishUrl);
    expect(preferenceWrites).toBe(1);
    expect(requests.at(-1)?.toString()).toBe(requests[0]?.toString());

    if (await page.locator('body').getAttribute('data-bs-theme') !== 'dark') await page.locator('#themeToggle').click();
    await expect(page.locator('body')).toHaveAttribute('data-bs-theme', 'dark');
    await page.setViewportSize({ width: 640, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.locator('#connectionSettings').click();
    await expect(page.locator('#maximumGap')).toHaveValue('2.5');
    await expect(page.locator('#rulesDialogTitle')).toHaveText('Συνδέσεις και απόσταση');
    await page.locator('#maximumGap').fill('2,5');
    await page.locator('#applyRules').click();
    await expect(page.locator('#rulesError')).toContainText('με τελεία για τα δεκαδικά');

    await page.goto('/User/Timeline');
    await expect(page.locator('#timeline-header h1')).toContainText('Χρονολόγιο του χρήστη');
    await expect(page.locator('#timeline-summary')).toContainText('Σύνολο τοποθεσιών:');
});
