import { expect, test } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** One actual-control smoke: partial results, explicit context, dismissal/recheck and saved-version draft choice. */
test('optional Check plan keeps known findings beside Unknown and never silently saves drafts', async ({ page }, testInfo) => {
  const config = loadSharedLayoutConfig();
  const tripId = process.env.WAYFARER_E2E_TRIP_ID!;
  await page.setViewportSize({ width: 1440, height: 1100 });
  await page.goto('/Identity/Account/Login');
  await page.getByLabel('Username').fill(config.username);
  await page.getByLabel('Password').fill(config.password);
  await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')), page.getByRole('button', { name: 'Log in' }).click()]);
  await page.goto(`/User/Trip/Edit/${tripId}`);
  await expect(page.locator('.trip-editor-workspace')).toBeVisible();
  // Seed only the supervisor's owned graph through protected production saves; interaction itself uses actual controls.
  const statuses = await page.evaluate(async trip => {
    const token = (document.querySelector('#trip-editor-antiforgery input[name=__RequestVerificationToken]') as HTMLInputElement).value;
    const graph = await (await fetch(`/api/trips/${trip}/editor`)).json();
    const region = Object.values(graph.regionsById).find((r: any) => !r.isShadow) as any;
    const place = Object.values(graph.placesById)[0] as any;
    const period = (start: string, end: string, kind: string, label: string) => ({ format: 1, entries: [{ id: crypto.randomUUID(), kind, label,
      when: { form: 'period', start: { state: 'known', value: { precision: 'dateTime', local: `2027-03-02T${start}`, zone: 'UTC' } },
        end: { state: 'known', value: { precision: 'dateTime', local: `2027-03-02T${end}`, zone: 'UTC' } } } }] });
    const statuses = [];
    for (const [kind, id, parent, value] of [
      ['trip', trip, trip, { format: 1, entries: [{ id: crypto.randomUUID(), when: { form: 'at', value: { precision: 'month', local: '2027-03' } } }] }],
      ['region', region.id, trip, period('10:00', '11:00', 'window', 'Selected context')],
      ['place', place.id, region.id, period('10:30', '11:30', 'presence', 'Required visit')]
    ] as const) statuses.push((await fetch(`/api/trips/${trip}/editor/planning/${kind}/${id}`, { method: 'PATCH',
      headers: { 'Content-Type': 'application/json', RequestVerificationToken: token },
      body: JSON.stringify({ expected: { parentId: parent, planning: null }, operation: 'set', value }) })).status);
    return statuses;
  }, tripId);
  expect(statuses).toEqual([200, 200, 200]);
  await page.reload();
  const check = page.getByRole('region', { name: 'Check plan', exact: true });
  let writes = 0;
  page.on('request', request => { if (['PATCH', 'PUT', 'DELETE'].includes(request.method())) writes++; });
  await check.getByRole('button', { name: 'Check plan', exact: true }).click();
  await expect(check.getByLabel('Planning results')).toContainText('Unknown');
  await check.locator('summary').filter({ hasText: 'Coverage and targeted check inputs' }).click();
  const visit = check.locator('details.plan-check__entry').filter({ hasText: 'Required visit' });
  await visit.locator('summary').click();
  await visit.getByLabel('Browser region · Selected context', { exact: true }).check();
  await check.getByRole('button', { name: 'Use for this check · check saved version' }).click();
  const mismatch = check.locator('article').filter({ hasText: '30 minutes definitely outside' });
  await expect(mismatch).toBeVisible();
  await expect(check.getByLabel('Planning results')).toContainText('Unknown');
  await mismatch.getByRole('button', { name: 'Dismiss for this review' }).click();
  await expect(check).toContainText('1 collapsed findings; counts retained.');
  await check.getByRole('button', { name: 'Check again', exact: true }).click();
  await expect(mismatch).toBeVisible();
  await check.locator('summary').filter({ hasText: 'Coverage and targeted check inputs' }).click();
  await check.getByRole('button', { name: 'Check plan', exact: true }).scrollIntoViewIfNeeded();
  await page.screenshot({ path: process.env.WAYFARER_CHECK_SCREENSHOTS ? `${process.env.WAYFARER_CHECK_SCREENSHOTS}/partial-results.png` : testInfo.outputPath('partial-results.png') });
  const planning = page.locator('[data-planning]');
  await planning.locator('summary').first().click();
  await planning.getByRole('button', { name: 'Add another date', exact: true }).click();
  await check.getByRole('button', { name: 'Check plan', exact: true }).click();
  await expect(check.getByRole('group', { name: 'Choose version to check' })).toBeVisible();
  await page.screenshot({ path: process.env.WAYFARER_CHECK_SCREENSHOTS ? `${process.env.WAYFARER_CHECK_SCREENSHOTS}/draft-choice.png` : testInfo.outputPath('draft-choice.png') });
  await check.getByRole('button', { name: 'Check saved version', exact: true }).click();
  await expect(mismatch).toBeVisible();
  expect(writes).toBe(0);
});
