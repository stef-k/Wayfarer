import { createHash } from 'node:crypto';
import { readFile, writeFile } from 'node:fs/promises';
import { expect, test, type Page } from '@playwright/test';
import type { Custody } from '../../../ClientApps/trip-editor/src/recovery/custody';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

const trip = process.env.WAYFARER_E2E_TRIP_ID!;
const editor = `/api/trips/${trip}/editor`;
const metadata = `${editor}/metadata/conditional`;
const editorUrl = `/User/Trip/Edit/${trip}`;
const v1 = 'trip-metadata-receipt-1';
const v2 = 'trip-metadata-receipt-2';
const form = (page: Page) => page.locator('#trip-editor-metadata-form');

/** Uses a real owner session and its corresponding antiforgery proof. */
async function signIn(page: Page): Promise<string> {
  const { username, password } = loadSharedLayoutConfig();
  await page.context().route('**/tiles/**', route => route.abort());
  await page.goto('/Identity/Account/Login');
  await page.getByLabel('Username').fill(username);
  await page.getByLabel('Password').fill(password);
  await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')), page.getByRole('button', { name: 'Log in' }).click()]);
  await page.goto('/User/Trip');
  return page.locator('input[name="__RequestVerificationToken"]').first().inputValue();
}

/** Issues exactly the frozen bytes using independently framed SHA-256. */
async function prepare(page: Page, token: string, subject: string, url: string, kind: string, contract: string, payload: object) {
  const body = JSON.stringify({ receiptContract: contract, ...payload });
  const hash = createHash('sha256');
  for (const value of [contract, kind, 'web', subject, trip, body]) {
    const bytes = Buffer.from(value);
    const frame = Buffer.alloc(4);
    frame.writeUInt32BE(bytes.length);
    hash.update(frame).update(bytes);
  }
  const issued = await page.request.post(url + '/operations', {
    data: { contract, requestDigest: 'sha256:' + hash.digest('hex') }, headers: { RequestVerificationToken: token }
  });
  expect(issued.ok(), await issued.text()).toBeTruthy();
  return { permit: await issued.json(), body };
}

/** Dispatches one prepared cookie operation without going through a second UI writer. */
async function dispatch(page: Page, token: string, url: string, method: string, attempt: Awaited<ReturnType<typeof prepare>>) {
  const reply = await page.request.fetch(url, { method, data: attempt.body, headers: {
    RequestVerificationToken: token, 'Content-Type': 'application/json', 'X-Wayfarer-Trip-Attempt': attempt.permit.proof
  } });
  expect(reply.ok(), await reply.text()).toBeTruthy();
  return reply.json();
}

/** Reads the account/Trip record from Chromium's real object store. */
async function custody(page: Page, subject: string): Promise<Custody> {
  return page.evaluate(({ account, id }) => new Promise((resolve, reject) => {
    const request = indexedDB.open('wayfarer-trip-metadata', 2);
    request.onerror = () => reject(request.error);
    request.onsuccess = () => {
      const db = request.result;
      const entry = db.transaction('drafts').objectStore('drafts').get([account, id]);
      entry.onsuccess = () => { db.close(); resolve(entry.result); };
      entry.onerror = () => { db.close(); reject(entry.error); };
    };
  }), { account: subject, id: trip });
}

test.beforeAll(async () => {
  const destination = process.env.WAYFARER_1019_CONTROLS_SERVING;
  if (!destination) return;
  const paths = ['../host/Wayfarer.dll', '../host/Wayfarer.pdb', 'ClientApps/trip-editor/src/recovery/custody.ts',
    'ClientApps/trip-editor/src/recovery/receiptApi.ts', 'ClientApps/trip-editor/src/components/MetadataEditor.vue',
    'tests/e2e/shared-layout/tripControlRecovery.spec.ts'];
  const hashes = Object.fromEntries(await Promise.all(paths.map(async path =>
    [path, createHash('sha256').update(await readFile(path)).digest('hex')])));
  await writeFile(destination, JSON.stringify({ run: process.env.WAYFARER_E2E_RUN_ID, hashes }, null, 2));
});

test('Trip controls retain a v1 attempt through blocked custody migration', async ({ page, context }) => {
  test.setTimeout(60_000);
  const token = await signIn(page);
  // The login and blank server document must not mount the version-two My Trips app before seeding v1.
  await page.goto('/Home/Privacy');
  await page.evaluate(() => new Promise<void>((resolve, reject) => {
    const deletion = indexedDB.deleteDatabase('wayfarer-trip-metadata');
    deletion.onsuccess = () => resolve();
    deletion.onerror = () => reject(deletion.error);
  }));
  const snapshot = await (await page.request.get(metadata)).json();
  const old = snapshot.metadata.originalValues.notesHtml;
  const desired = '<p>V1 retained Notes</p>';
  const frozen = await prepare(page, token, snapshot.subjectId, metadata, 'metadata', v1,
    { expected: { notesHtml: old }, changes: { notesHtml: desired } });
  await page.evaluate(({ account, id, old, desired, frozen }) => new Promise<void>((resolve, reject) => {
    const request = indexedDB.open('wayfarer-trip-metadata', 1);
    request.onupgradeneeded = () => request.result.createObjectStore('drafts', { keyPath: ['account', 'tripId'] }).createIndex('account', 'account');
    request.onerror = () => reject(request.error);
    request.onsuccess = () => {
      const db = request.result;
      Object.assign(window, { v1Custody: db });
      const tx = db.transaction('drafts', 'readwrite');
      tx.oncomplete = () => resolve();
      tx.onabort = () => reject(tx.error);
      tx.objectStore('drafts').put({ account, tripId: id, generation: 1,
        fields: { notesHtml: { desired: { notesHtml: desired }, baseline: { notesHtml: old ?? '' }, original: old, generation: 1 } },
        attempts: [{ ...frozen, submitted: { notesHtml: 1 } }], outcomes: {}, review: [] });
    };
  }), { account: snapshot.subjectId, id: trip, old, desired, frozen });
  expect((await dispatch(page, token, metadata, 'PATCH', frozen)).state).toBe('applied');

  const newer = await context.newPage();
  const writes: string[] = [];
  newer.on('request', request => { if (['PUT', 'PATCH', 'POST'].includes(request.method())) writes.push(request.method()); });
  await newer.goto(editorUrl);
  await expect(newer.getByText('Close older Trip tabs', { exact: false })).toBeVisible();
  await expect(form(newer)).toHaveAttribute('inert', '');
  expect(writes).toEqual([]);
  await page.evaluate(() => (window as unknown as { v1Custody: IDBDatabase }).v1Custody.close());

  const ownerRead = newer.waitForResponse(response => new URL(response.url()).pathname === metadata
    && !new URL(response.url()).search && response.request().method() === 'GET');
  await newer.reload();
  await ownerRead;
  await expect(newer.getByText('The earlier Trip save was confirmed.', { exact: true })).toBeVisible();
  await expect(form(newer).getByRole('textbox', { name: 'Notes', exact: true })).toHaveText('V1 retained Notes');
  expect((await custody(newer, snapshot.subjectId)).attempts).toEqual([]);
  expect(writes).toEqual([]);
  const reopenOld = await page.evaluate(() => new Promise<string>(resolve => {
    const request = indexedDB.open('wayfarer-trip-metadata', 1);
    request.onerror = () => resolve(request.error!.name);
    request.onsuccess = () => { request.result.close(); resolve('opened'); };
  }));
  expect(reopenOld).toBe('VersionError');
});

test('Trip controls recover the lost middle branch and retain later tags and private sharing intent', async ({ page, context }, testInfo) => {
  test.setTimeout(75_000);
  const token = await signIn(page);
  const read = async () => (await page.request.get(metadata + '?contract=' + v2)).json();
  const snapshot = await read();
  const subject = snapshot.subjectId;
  const tagUrl = editor + '/tags/conditional';
  const uniqueTag = 'Trip receipt ' + trip.replaceAll('-', '').slice(0, 12);
  await page.goto(editorUrl);
  await expect(form(page)).not.toHaveAttribute('inert', '');
  const add = async (name: string) => {
    const input = form(page).getByLabel('Add tag', { exact: true });
    await input.fill(name);
    await input.press('Enter');
  };
  const committed = Promise.withResolvers<void>();
  const drop = Promise.withResolvers<void>();
  await page.route(`**${tagUrl}`, async route => {
    const response = await route.fetch();
    const result = await response.json();
    expect(result.state).toBe('applied');
    expect(result.tagResult.createdCatalogue[0].id).toBe(result.tagResult.resultingTags[0].id);
    committed.resolve();
    await drop.promise;
    await route.abort();
  }, { times: 1 });
  const writes: string[] = [];
  context.on('request', request => {
    if (['PATCH', 'PUT', 'POST'].includes(request.method()) && new URL(request.url()).pathname.startsWith(editor))
      writes.push(new URL(request.url()).pathname);
  });
  try {
    await form(page).getByLabel('Name', { exact: true }).fill('Mixed metadata title');
    await form(page).getByLabel('Cover Image URL').fill('https://example.test/trip-controls.png');
    await add(uniqueTag);
    await form(page).getByLabel('Show visit progress on public trip').check();
    await page.getByRole('button', { name: 'Save settings & Exit', exact: true }).click();
    await committed.promise;
    await add('Newer tag draft');
    await expect.poll(async () => (await custody(page, subject)).fields.tags?.desired.tags).toEqual([uniqueTag, 'Newer tag draft']);
    drop.resolve();
    await expect(page.getByText('A previous Trip save is unconfirmed.', { exact: false }).first()).toBeVisible();
    const privacy = await prepare(page, token, subject, metadata, 'metadata', v2,
      { expected: { isPublic: true }, changes: { isPublic: false } });
    await dispatch(page, token, metadata, 'PATCH', privacy);

    const recovery = await context.newPage();
    await recovery.goto('/User/Trip');
    const panel = recovery.locator('#trip-metadata-recovery');
    await panel.getByRole('button', { name: 'Recover Trip save', exact: true }).click();
    await expect(panel.getByText('The earlier Trip save was confirmed.', { exact: true })).toBeVisible();
    await expect(panel.getByText('Newer tag draft', { exact: false })).toBeVisible();
    await expect(panel.getByRole('button', { name: 'Use current Tags as comparison base' })).toBeVisible();
    await expect(panel.getByRole('button', { name: /Use current Share Progress as comparison base/i })).toBeVisible();
    const retained = await custody(page, subject);
    expect(retained.attempts).toEqual([]);
    expect(retained.fields.tags?.original).toEqual([]);
    expect(retained.fields.shareProgressEnabled?.original).toEqual({ isPublic: true, shareProgressEnabled: false });
    expect(retained.fields.shareProgressEnabled?.desired.shareProgressEnabled).toBe(true);
    expect(await read()).toMatchObject({ metadata: { name: 'Mixed metadata title', isPublic: false, shareProgressEnabled: false } });
    const viewer = await context.newPage();
    await viewer.goto(`/User/Trip/View/${trip}`);
    const sharing = viewer.locator('#viewer-sharing-recovery');
    await expect(sharing.getByText('Retained sharing choice (Enabled).', { exact: false })).toBeVisible();
    await expect(sharing.locator('[data-submit]')).toBeHidden();
    await expect(page).toHaveURL(new RegExp(editorUrl + '$'));
    expect(writes).toEqual([metadata + '/operations', metadata, tagUrl + '/operations', tagUrl]);
    await panel.screenshot({ path: process.env.WAYFARER_1019_CONTROLS_SCREENSHOT || testInfo.outputPath('controls-recovery.png') });
  } finally {
    drop.resolve();
    const cleanup = await prepare(page, token, subject, tagUrl, 'tags-replace', v2, { tags: [] });
    await dispatch(page, token, tagUrl, 'PUT', cleanup);
  }
});
