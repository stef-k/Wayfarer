import { expect, test, type Page } from '@playwright/test';
import type { Custody } from '../../../ClientApps/trip-editor/src/recovery/custody';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** Reads custody without waiting on the Editor's completion callback. */
async function durableDraft(page: Page, tripId: string): Promise<Custody | null> {
  return page.evaluate(id => new Promise((resolve, reject) => {
    const connection = indexedDB.open('wayfarer-trip-metadata', 1);
    connection.onerror = () => reject(connection.error);
    connection.onsuccess = () => {
      const db = connection.result;
      const account = document.querySelector<HTMLElement>('#trip-editor-app')?.dataset.accountId;
      const entry = db.transaction('drafts').objectStore('drafts').get([account!, id]);
      entry.onerror = () => { db.close(); reject(entry.error); };
      entry.onsuccess = () => {
        db.close();
        resolve(entry.result ?? null);
      };
    };
  }), tripId);
}

/** Reads the exact comparison base alongside the authored Name. */
async function draftName(page: Page, tripId: string) {
  const field = (await durableDraft(page, tripId))?.fields.name;
  return field ? { desired: field.desired.name, original: field.original } : null;
}

/** Lets the real settlement commit before the Editor receives its completion event. */
async function delaySettlementCallback(page: Page): Promise<void> {
  await page.evaluate(() => {
    const put = IDBObjectStore.prototype.put;
    IDBObjectStore.prototype.put = function (value, key) {
      if (this.name === 'drafts' && !value.attempts.length && !Object.keys(value.fields).length) {
        IDBObjectStore.prototype.put = put;
        const tx = this.transaction;
        const complete = tx.oncomplete!;
        tx.oncomplete = event => {
          Object.assign(window, { releaseReviewRecovery: () => complete.call(tx, event) });
        };
      }
      return put.call(this, value, key);
    };
  });
}

const form = (page: Page) => page.locator('#trip-editor-metadata-form');
const name = (page: Page) => form(page).getByLabel('Name', { exact: true });

/** Loses the transport reply after the real mutation commits. */
async function dropCommittedReply(page: Page, metadata: string): Promise<void> {
  await page.route(`**${metadata}`, async route => {
    if (route.request().method() !== 'PATCH') return route.continue();
    const response = await route.fetch();
    expect((await response.json()).state).toBe('applied');
    await route.abort();
  }, { times: 1 });
}

/** Fails only the buffered write in this tab, after the normal generation check. */
async function interruptBufferedWrite(page: Page): Promise<void> {
  await page.evaluate(() => {
    const put = IDBObjectStore.prototype.put;
    IDBObjectStore.prototype.put = function (value, key) {
      if (this.name === 'drafts' && value.fields?.name?.desired?.name === 'Newer C')
        throw new DOMException('Interrupted draft write', 'QuotaExceededError');
      return put.call(this, value, key);
    };
  });
}

/** Verifies that loss of current access leaves the earlier attempt and authored generation recoverable. */
async function verifyInaccessibleDraft(page: Page, tripId: string, before: Custody): Promise<void> {
  await expect(form(page)).toHaveCount(0);
  await expect(page.getByText('Trip Editor unavailable')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Recover Trip saves and drafts' })).toBeVisible();
  const retained = await durableDraft(page, tripId);
  expect(retained?.generation).toBe(before.generation);
  expect(retained?.fields).toEqual(before.fields);
  expect(retained?.attempts).toEqual(before.attempts);
  expect(retained?.outcomes[before.attempts[0].permit.operationId]?.evidence.state).toBe('applied');
}

/** Advances custody in another tab and explicitly reopens it after the buffered write failed. */
async function reopenAfterStorageFailure(page: Page, tripId: string, metadata: string, submitted: string, inaccessible: boolean): Promise<void> {
  await expect(page.getByText('Browser storage could not retain this edit.', { exact: false })).toBeVisible();
  await expect(name(page)).toHaveValue('Newer C');
  await expect.poll(() => draftName(page, tripId)).toBeNull();

  const other = await page.context().newPage();
  await other.goto(page.url());
  await expect(form(other)).not.toHaveAttribute('inert', '');
  await name(other).fill('Other tab D');
  const otherDraft = { desired: 'Other tab D', original: submitted };
  await expect.poll(() => draftName(other, tripId)).toEqual(otherDraft);
  await name(page).fill('Later E');
  const reopen = page.getByRole('button', { name: 'Reopen latest draft', exact: true });
  await expect(reopen).toBeEnabled();
  if (inaccessible) {
    // Give the 404 a real, recoverable attempt that it must leave in custody.
    await dropCommittedReply(other, metadata);
    await other.getByRole('button', { name: 'Save settings & Continue', exact: true }).click();
    await expect(other.getByRole('button', { name: 'Recover Trip save', exact: true })).toBeEnabled();
    await page.route(`**${metadata}`, route => route.request().method() === 'GET'
      ? route.fulfill({ status: 404 }) : route.continue());
  }
  const before = (await durableDraft(page, tripId))!;
  expect(before.attempts).toHaveLength(inaccessible ? 1 : 0);
  await reopen.click();
  if (inaccessible) await verifyInaccessibleDraft(page, tripId, before);
  else {
    await expect(name(page)).toHaveValue('Other tab D');
    await expect(form(page)).not.toHaveAttribute('inert', '');
    expect((await durableDraft(page, tripId))?.generation).toBe(before.generation);
  }
  expect(await draftName(page, tripId)).toEqual(otherDraft);
}

for (const scenario of ['retains input on reload', 'fences a newer tab', 'reopens after storage failure', 'hides access loss after storage failure']) {
  const competingTab = scenario === 'fences a newer tab';
  const inaccessible = scenario === 'hides access loss after storage failure';
  const storageFailure = scenario === 'reopens after storage failure' || inaccessible;
  test(`Editor receipt settlement ${scenario}`, async ({ page, context }) => {
    test.setTimeout(45_000);
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
    const original = await name(page).inputValue();
    const submitted = `Submitted B (${scenario})`;

    // Drop a real committed reply and count any further mutation dispatches.
    const writes: string[] = [];
    context.on('request', request => {
      if (new URL(request.url()).pathname.startsWith(editor) && ['PATCH', 'PUT', 'POST', 'DELETE'].includes(request.method()))
        writes.push(request.method());
    });
    await dropCommittedReply(page, metadata);
    await name(page).fill(submitted);
    await page.getByRole('button', { name: 'Save settings & Exit', exact: true }).click();
    const recover = page.getByRole('button', { name: 'Recover Trip save', exact: true });
    await expect(recover).toBeEnabled();

    await delaySettlementCallback(page);
    await recover.click();
    await page.waitForFunction(() => 'releaseReviewRecovery' in window);
    await expect.poll(() => draftName(page, tripId)).toBeNull();
    await name(page).fill('Newer C');
    if (competingTab) {
      const other = await context.newPage();
      await other.goto(path);
      await expect(form(other)).not.toHaveAttribute('inert', '');
      await name(other).fill('Other tab D');
      await expect.poll(() => draftName(other, tripId)).toEqual({ desired: 'Other tab D', original: submitted });
    }

    if (storageFailure) await interruptBufferedWrite(page);
    await page.evaluate(() => (window as unknown as { releaseReviewRecovery: () => void }).releaseReviewRecovery());
    if (storageFailure) {
      await reopenAfterStorageFailure(page, tripId, metadata, submitted, inaccessible);
    } else if (competingTab) {
      await expect(page.getByText('This draft changed in another tab.', { exact: false })).toBeVisible();
      await expect(name(page)).toHaveValue('Newer C');
      await expect.poll(() => draftName(page, tripId)).toEqual({ desired: 'Other tab D', original: submitted });
    } else {
      await expect(page.getByText('The earlier Trip save was confirmed.', { exact: true })).toBeVisible();
      await expect(name(page)).toHaveValue('Newer C');
      await expect.poll(() => draftName(page, tripId)).toEqual({ desired: 'Newer C', original });
      await expect(page.getByRole('button', { name: 'Use current Name as comparison base', exact: true })).toBeVisible();
      await page.reload();
      await expect(name(page)).toHaveValue('Newer C');
      await expect(page.getByRole('button', { name: 'Use current Name as comparison base', exact: true })).toBeVisible();
      await expect.poll(() => draftName(page, tripId)).toEqual({ desired: 'Newer C', original });
    }
    await expect(page).toHaveURL(new RegExp(path + '$'));
    expect(writes).toEqual(inaccessible ? ['POST', 'PATCH', 'POST', 'PATCH'] : ['POST', 'PATCH']);
  });
}
