import { expect, test, type Page } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

/** Reads the durable draft and original without waiting on the Editor's completion callback. */
async function draftName(page: Page, tripId: string): Promise<{ desired: string; original: string } | null> {
  return page.evaluate(id => new Promise((resolve, reject) => {
    const connection = indexedDB.open('wayfarer-trip-metadata', 1);
    connection.onerror = () => reject(connection.error);
    connection.onsuccess = () => {
      const db = connection.result;
      const account = document.querySelector<HTMLElement>('#trip-editor-app')?.dataset.accountId;
      const entry = db.transaction('drafts').objectStore('drafts').get([account!, id]);
      entry.onerror = () => { db.close(); reject(entry.error); };
      entry.onsuccess = () => {
        const field = entry.result?.fields.name;
        db.close();
        resolve(field ? { desired: field.desired.name, original: field.original } : null);
      };
    };
  }), tripId);
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

for (const competingTab of [false, true]) {
  test(`Editor receipt settlement ${competingTab ? 'fences a newer tab' : 'retains input and its original on reload'}`, async ({ page, context }) => {
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
    const submitted = competingTab ? 'Submitted B before another tab' : 'Submitted B';

    // Drop a real committed reply and count any further mutation dispatches.
    const writes: string[] = [];
    context.on('request', request => {
      if (new URL(request.url()).pathname.startsWith(editor) && ['PATCH', 'PUT', 'POST', 'DELETE'].includes(request.method()))
        writes.push(request.method());
    });
    await page.route(`**${metadata}`, async route => {
      if (route.request().method() !== 'PATCH') return route.continue();
      const response = await route.fetch();
      expect((await response.json()).state).toBe('applied');
      await route.abort();
    }, { times: 1 });
    await name(page).fill(submitted);
    await page.getByRole('button', { name: 'Save settings & Continue', exact: true }).click();
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

    await page.evaluate(() => (window as unknown as { releaseReviewRecovery: () => void }).releaseReviewRecovery());
    if (competingTab) {
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
    expect(writes).toEqual(['POST', 'PATCH']);
  });
}
