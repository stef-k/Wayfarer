import { expect, test, type Page } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** Observes actual committed browser custody before allowing the lost response or subsequent reload. */
async function regionCustody(page: Page, tripId: string, id: string) {
  return page.evaluate(({ tripId, id }) => new Promise<{ name: string | null; generation: number; attempts: number }>((resolve, reject) => {
    const open = indexedDB.open('wayfarer-trip-metadata', 3);
    open.onerror = () => reject(open.error);
    open.onsuccess = () => {
      const db = open.result;
      const account = document.querySelector<HTMLElement>('#trip-editor-app')?.dataset.accountId;
      const read = db.transaction('drafts').objectStore('drafts').get([account!, tripId]);
      read.onerror = () => { db.close(); reject(read.error); };
      read.onsuccess = () => {
        const structure = read.result?.structure;
        const key = JSON.stringify(['region-fields', 'region', id]);
        const draft = structure?.drafts[key];
        resolve({ name: draft?.payload.changes.name ?? null, generation: draft?.generation ?? 0, attempts: structure?.attempts.length ?? 0 });
        db.close();
      };
    };
  }), { tripId, id });
}

test('typed Region recovery after reload keeps the newer draft and sends zero recovery writes', async ({ page, context }, info) => {
  test.setTimeout(75_000);
  const config = loadSharedLayoutConfig();
  const tripId = process.env.WAYFARER_E2E_TRIP_ID!;
  const endpoint = `/api/trips/${tripId}/editor`;
  await context.route('**/tiles/**', route => route.abort());
  await page.goto('/Identity/Account/Login');
  await page.getByLabel('Username').fill(config.username);
  await page.getByLabel('Password').fill(config.password);
  await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')),
    page.getByRole('button', { name: 'Log in' }).click()]);
  const editor = `/User/Trip/Edit/${tripId}`;
  await page.goto(editor);
  await expect(page.locator('.trip-editor-region-card--normal').first()).toBeVisible();

  const card = page.locator('.trip-editor-region-card--normal').first();
  const id = (await card.getAttribute('data-region-id'))!;
  const routePath = `${endpoint}/regions/${id}/conditional/receipt`;
  await card.locator('.trip-editor-region-card__header').getByRole('button', { name: 'Edit', exact: true }).click();
  const name = page.locator('#trip-editor-region-form').getByLabel('Name', { exact: true });
  await name.fill('Submitted Region B');
  await expect.poll(async () => (await regionCustody(page, tripId, id)).name).toBe('Submitted Region B');

  const committed = Promise.withResolvers<void>();
  const drop = Promise.withResolvers<void>();
  const writes: string[] = [];
  context.on('request', request => {
    if (new URL(request.url()).pathname.startsWith(endpoint) && !['GET', 'HEAD'].includes(request.method()))
      writes.push(request.method());
  });
  await page.route(`**${routePath}`, async route => {
    const reply = await route.fetch();
    expect(reply.status(), await reply.text()).toBe(200);
    expect((await reply.json()).state).toBe('applied');
    committed.resolve();
    await drop.promise;
    await route.abort();
  }, { times: 1 });
  await page.getByRole('button', { name: 'Save Region', exact: true }).click();
  await committed.promise;
  const submitted = await regionCustody(page, tripId, id);
  expect(submitted.attempts).toBe(1);
  await name.fill('Newer Region C');
  await expect.poll(async () => (await regionCustody(page, tripId, id)).generation).toBeGreaterThan(submitted.generation);
  drop.resolve();

  await page.reload();
  const recovery = page.locator('[data-structure-recovery]');
  await expect(recovery.getByRole('button', { name: 'Check server', exact: true })).toBeVisible();
  const operations = page.waitForResponse(response => response.request().method() === 'GET' && response.url().includes(routePath + '/operations/'));
  await recovery.getByRole('button', { name: 'Check server', exact: true }).click();
  expect((await operations).status()).toBe(200);
  await expect(recovery.getByText('The earlier Trip save was confirmed.', { exact: false })).toBeVisible();
  await expect.poll(() => regionCustody(page, tripId, id)).toMatchObject({ name: 'Newer Region C', attempts: 0 });
  await recovery.locator('summary').click();
  await expect(recovery.locator('pre')).toContainText('Newer Region C');
  expect(writes).toEqual(['POST', 'PATCH']);
  await page.screenshot({ path: process.env.WAYFARER_STRUCTURE_SCREENSHOT ?? info.outputPath('typed-region-recovery.png'), fullPage: true });
});
