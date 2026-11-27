import { expect, test, type Page } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** Exposes actual durable state to bound asynchronous tab steps without sleeping or changing custody. */
async function notesGeneration(page: Page, tripId: string): Promise<{ desired: string; original: string | null } | null> {
  return page.evaluate(id => new Promise((resolve, reject) => {
    const connection = indexedDB.open('wayfarer-trip-metadata', 1);
    connection.onerror = () => reject(connection.error);
    connection.onsuccess = () => {
      const db = connection.result;
      const account = document.querySelector<HTMLElement>('#trip-editor-app')?.dataset.accountId;
      const entry = db.transaction('drafts').objectStore('drafts').get([account!, id]);
      entry.onerror = () => { db.close(); reject(entry.error); };
      entry.onsuccess = () => {
        const field = entry.result?.fields.notesHtml;
        db.close();
        resolve(field ? { desired: field.desired.notesHtml, original: field.original } : null);
      };
    };
  }), tripId);
}

const form = (page: Page) => page.locator('#trip-editor-metadata-form');
const notes = (page: Page) => form(page).getByRole('textbox', { name: 'Notes', exact: true });
const save = (page: Page, exit = false) => page.getByRole('button', {
  name: exit ? 'Save settings & Exit' : 'Save settings & Continue', exact: true
});

test('Trip receipts recover from My Trips without replacing a newer Notes generation', async ({ page, context }, testInfo) => {
  test.setTimeout(75_000);
  const config = loadSharedLayoutConfig();
  const tripId = process.env.WAYFARER_E2E_TRIP_ID!;
  const path = `/User/Trip/Edit/${tripId}`;
  const editor = `/api/trips/${tripId}/editor`;
  const metadata = `${editor}/metadata/conditional`;
  await context.route('**/tiles/**', route => route.abort());
  await page.goto('/Identity/Account/Login');
  await page.getByLabel('Username').fill(config.username);
  await page.getByLabel('Password').fill(config.password);
  await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')), page.getByRole('button', { name: 'Log in' }).click()]);
  await page.goto(path);
  await expect(form(page)).not.toHaveAttribute('inert', '');
  const token = await page.locator('#trip-editor-antiforgery input').inputValue();
  const { metadata: original } = await (await page.request.get(metadata)).json();
  const expected = original.originalValues.notesHtml;
  await notes(page).fill('Submitted B');
  await expect.poll(() => notesGeneration(page, tripId)).toEqual({ desired: '<p>Submitted B</p>', original: expected });

  const stale = await context.newPage();
  await stale.goto(path);
  await expect(notes(stale)).toHaveText('Submitted B');
  await expect(form(stale)).not.toHaveAttribute('inert', '');

  // Lose the reply only after the actual application confirms a committed receipt.
  const committed = Promise.withResolvers<void>();
  const drop = Promise.withResolvers<void>();
  const writes: unknown[] = [];
  context.on('request', request => {
    if (new URL(request.url()).pathname.startsWith(editor) && ['PATCH', 'PUT', 'POST'].includes(request.method()))
      writes.push(request.postDataJSON());
  });
  await page.route(`**${metadata}`, async route => {
    if (route.request().method() !== 'PATCH') return route.continue();
    const reply = await route.fetch();
    expect((await reply.json()).state).toBe('applied');
    committed.resolve();
    await drop.promise;
    await route.abort();
  }, { times: 1 });
  await save(page, true).click();
  await committed.promise;
  await notes(page).fill('Newer C before owner read');
  await expect.poll(() => notesGeneration(page, tripId)).toEqual({ desired: '<p>Newer C before owner read</p>', original: expected });
  drop.resolve();
  await expect(page.getByText('A previous Trip save is unconfirmed.', { exact: false })).toBeVisible();
  await expect(page).toHaveURL(new RegExp(path + '$'));

  await save(stale).click();
  await expect(stale.getByText('This draft changed in another tab.', { exact: false })).toBeVisible();
  await stale.getByRole('button', { name: 'Cancel / Reset', exact: true }).click();
  await expect.poll(() => notesGeneration(page, tripId)).toEqual({ desired: '<p>Newer C before owner read</p>', original: expected });

  // The authorized footprint of one mixed draft is checked before any of its fields can leave the browser.
  await form(page).getByLabel('Name', { exact: true }).fill('Mixed Name');
  await form(page).getByLabel('Cover Image URL').fill('https://example.test/held.jpg');
  await save(page).click();
  await expect(page.getByText('Recover the save before sending overlapping changes.', { exact: false }).first()).toBeVisible();
  expect(writes).toHaveLength(2); // Permit and first PATCH.
  const changed = await page.request.patch(metadata, {
    headers: { RequestVerificationToken: token },
    data: { expected: { notesHtml: '<p>Submitted B</p>' }, changes: { notesHtml: '<p>Fresh owner D</p>' } }
  });
  expect(changed.ok(), await changed.text()).toBeTruthy();

  const recovery = await context.newPage();
  await recovery.route(`**${editor}`, route => route.fulfill({ status: 503, json: {} }));
  await recovery.goto(path);
  await expect(recovery.getByText('Trip Editor unavailable')).toBeVisible();
  await recovery.getByRole('link', { name: 'Recover Trip saves and drafts' }).click();
  await expect(recovery.getByText('Newer C before owner read', { exact: false })).toBeVisible();

  const ownerRead = Promise.withResolvers<void>();
  const continueRead = Promise.withResolvers<void>();
  await recovery.route(`**${metadata}`, async route => {
    const reply = await route.fetch();
    ownerRead.resolve();
    await continueRead.promise;
    await route.fulfill({ response: reply });
  }, { times: 1 });
  await recovery.getByRole('button', { name: 'Recover Trip save', exact: true }).click();
  await ownerRead.promise;
  await notes(page).fill('Newest C during owner read');
  await expect.poll(() => notesGeneration(page, tripId)).toEqual({ desired: '<p>Newest C during owner read</p>', original: expected });
  continueRead.resolve();
  await expect(recovery.getByText('<p>Newest C during owner read</p>', { exact: true })).toBeVisible();
  await expect(recovery.getByText('<p>Fresh owner D</p>', { exact: true })).toBeVisible();
  await expect(recovery.getByText('The earlier Trip save was confirmed.', { exact: true })).toBeVisible();
  await recovery.locator('#trip-metadata-recovery').screenshot({
    path: process.env.WAYFARER_1019_RECOVERY_SCREENSHOT || testInfo.outputPath('my-trips-recovery.png')
  });
  await expect.poll(() => notesGeneration(page, tripId)).toEqual({ desired: '<p>Newest C during owner read</p>', original: expected });
  expect(writes).toHaveLength(2);

  // Editor reload independently restores the dirty generation and does not run the old Save & Exit continuation.
  await page.reload();
  await expect(notes(page)).toHaveText('Newest C during owner read');
  await expect(page.getByText('<p>Fresh owner D</p>', { exact: true })).toBeVisible();
  await expect(page.locator('.trip-editor-save-state').filter({ hasText: 'Unsaved changes' }).first()).toBeVisible();
  expect(writes).toHaveLength(2);

  await recovery.getByRole('button', { name: 'Use current Notes as comparison base', exact: true }).click();
  await expect.poll(() => notesGeneration(page, tripId)).toEqual({ desired: '<p>Newest C during owner read</p>', original: '<p>Fresh owner D</p>' });
  expect(writes).toHaveLength(2);
});
