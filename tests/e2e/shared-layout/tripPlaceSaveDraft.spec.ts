import { appendFileSync } from 'node:fs';
import { expect, test, type Page } from '@playwright/test';
import type { StructureAttempt, StructureDraft } from '../../../ClientApps/trip-editor/src/recovery/structureCustody';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

type PlaceDraft = Omit<StructureDraft, 'payload'> & {
  payload: { expected: object; changes: { name?: string; location?: { latitude: number } } };
};

/** Reads the real IndexedDB draft slots and reservations without changing custody. */
function placeCustody(page: Page, tripId: string, id: string) {
  return page.evaluate(({ tripId, id }) => new Promise<{
    draft: PlaceDraft | null; legacyScalar: PlaceDraft | null; attempts: StructureAttempt[];
  }>((resolve, reject) => {
    const open = indexedDB.open('wayfarer-trip-metadata', 3);
    open.onerror = () => reject(open.error);
    open.onsuccess = () => {
      const db = open.result;
      const account = document.querySelector<HTMLElement>('#trip-editor-app')!.dataset.accountId;
      const read = db.transaction('drafts').objectStore('drafts').get([account, tripId]);
      read.onerror = () => { db.close(); reject(read.error); };
      read.onsuccess = () => {
        const state = read.result?.structure;
        resolve({
          draft: state?.drafts[JSON.stringify(['place-save', 'place', id])] ?? null,
          legacyScalar: state?.drafts[JSON.stringify(['place-fields', 'place', id])] ?? null,
          attempts: state?.attempts ?? []
        });
        db.close();
      };
    };
  }), { tripId, id });
}

/** Reviews and discards the first draft before starting the second form's own generation. */
async function discardReviewedPlaceDraft(page: Page, latitude: number) {
  const recovery = page.getByRole('region', { name: 'Trip save recovery' });
  await recovery.getByRole('button', { name: 'Check server', exact: true }).click();
  await recovery.locator('summary').click();
  await expect(recovery.locator('pre')).toContainText(String(latitude));
  await recovery.getByRole('button', { name: 'Discard local draft', exact: true }).click();
}

test('Place Save shares one draft generation across combined and scalar routes', async ({ page, context }) => {
  test.setTimeout(60_000);
  const config = loadSharedLayoutConfig();
  const tripId = process.env.WAYFARER_E2E_TRIP_ID!;
  const editor = `/User/Trip/Edit/${tripId}`;
  const endpoint = `/api/trips/${tripId}/editor`;
  await context.route('**/tiles/**', route => route.abort());
  await page.goto('/Identity/Account/Login');
  await page.getByLabel('Username').fill(config.username);
  await page.getByLabel('Password').fill(config.password);
  await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')),
    page.getByRole('button', { name: 'Log in' }).click()]);
  await page.goto(editor);
  const card = page.locator('[data-place-id]').first();
  await expect(card).toBeVisible();
  const id = (await card.getAttribute('data-place-id'))!;
  const open = (actor: Page) => actor.locator(`[data-place-id="${id}"]`).getByRole('button', { name: 'Edit', exact: true }).click();
  const input = (actor: Page, label: string) => actor.locator('#trip-editor-place-form').getByLabel(label, { exact: true });
  const save = (actor: Page) => actor.getByRole('button', { name: 'Save Place', exact: true });
  const readDrafts = () => placeCustody(page, tripId, id);
  const readSaved = async () => {
    const response = await page.request.get(endpoint);
    expect(response.ok()).toBe(true);
    return (await response.json()).placesById[id];
  };
  const requests: string[] = [];
  context.on('request', request => {
    const path = new URL(request.url()).pathname;
    if (path.startsWith(endpoint + '/') && !['GET', 'HEAD'].includes(request.method())) requests.push(`${request.method()} ${path}`);
  });

  await open(page);
  const saved = await readSaved();
  const originalLatitude = await input(page, 'Latitude').inputValue();
  await input(page, 'Latitude').fill('37.91');
  await expect.poll(async () => (await readDrafts()).draft?.payload.changes.location?.latitude).toBe(37.91);
  const other = await context.newPage();
  await other.goto(editor);
  await discardReviewedPlaceDraft(other, 37.91);
  await expect.poll(async () => (await readDrafts()).draft).toBeNull();
  await open(other);
  await input(other, 'Latitude').fill('37.92');
  await input(other, 'Name').fill('Newer tab B Place');
  await expect.poll(readDrafts).toMatchObject({
    draft: { route: { kind: 'place-save' }, payload: { changes: { name: 'Newer tab B Place', location: { latitude: 37.92 } } } },
    legacyScalar: null, attempts: []
  });
  const newer = await readDrafts();

  await input(page, 'Latitude').fill('37.93');
  const stale = page.getByRole('alert').filter({ hasText: 'This draft is out of date.' });
  await expect(stale).toBeVisible();
  await input(page, 'Latitude').fill(originalLatitude);
  await input(page, 'Name').fill('Stale tab A Place');
  await save(page).click();
  await expect(save(page)).toBeEnabled();
  await expect(stale).toContainText('Review the retained draft in Trip save recovery');
  await expect(input(page, 'Name')).toHaveValue('Stale tab A Place');
  await expect(input(page, 'Latitude')).toHaveValue(originalLatitude);
  await expect(input(other, 'Name')).toHaveValue('Newer tab B Place');
  await expect(input(other, 'Latitude')).toHaveValue('37.92');
  expect(await readSaved()).toEqual(saved);
  expect(await readDrafts()).toEqual(newer);
  expect(requests).toEqual([]);

  // The current form can revert its latitude and keep the name in the same durable slot.
  await input(other, 'Latitude').fill(originalLatitude);
  const path = `${endpoint}/places/${id}/conditional/receipt`;
  const payload = { expected: { regionId: saved.regionId, name: saved.name }, changes: { name: 'Newer tab B Place' } };
  await expect.poll(readDrafts).toMatchObject({
    draft: { route: { kind: 'place-fields', method: 'PATCH', path }, payload, generation: newer.draft!.generation + 1 },
    legacyScalar: null, attempts: []
  });
  const scalar = await readDrafts();
  let reservation: StructureAttempt | undefined;
  await other.route(`**${path}`, async route => {
    [reservation] = (await readDrafts()).attempts;
    await route.continue();
  }, { times: 1 });
  const permit = other.waitForResponse(response => new URL(response.url()).pathname === path + '/operations');
  const applied = other.waitForResponse(response => new URL(response.url()).pathname === path);
  await save(other).click();
  const [permitResponse, mutationResponse] = await Promise.all([permit, applied]);
  expect(permitResponse.status()).toBe(200);
  expect(mutationResponse.status()).toBe(200);
  const issued = await permitResponse.json();
  const mutation = mutationResponse.request();
  const proof = (await mutation.allHeaders())['x-wayfarer-trip-attempt'];
  expect(mutation.postDataJSON()).toEqual({ receiptContract: issued.contract, ...payload });
  expect(reservation).toMatchObject({ permit: issued, route: scalar.draft!.route, generation: scalar.draft!.generation, body: mutation.postData() });
  expect(proof).toBe(issued.proof);
  expect(await mutationResponse.json()).toMatchObject({ state: 'applied', operationId: issued.operationId });
  const actual = await readSaved();
  expect(actual).toMatchObject({ name: payload.changes.name, location: saved.location });
  await expect.poll(readDrafts).toEqual({ draft: null, legacyScalar: null, attempts: [] });
  expect(requests).toEqual([`POST ${path}/operations`, `PATCH ${path}`]);
  if (process.env.STAGING_WITNESS) appendFileSync(process.env.STAGING_WITNESS, JSON.stringify({
    phase: 'current-place-changed-save-route', requests, combined: newer.draft, scalar: scalar.draft,
    operationId: issued.operationId, frozenBody: reservation!.body, proofMatched: true,
    savedAfter: { name: actual.name, location: actual.location }, settled: await readDrafts()
  }) + '\n');
});
