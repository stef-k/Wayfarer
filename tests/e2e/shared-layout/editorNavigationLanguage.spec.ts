import { expect, test, type Locator } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

const savedCamera = { lat: '37.980000', lng: '23.730000', zoom: '12' };

/** Reads the coordinates recorded by the adapter from the real Leaflet map on moveend. */
const camera = (map: Locator) => map.evaluate(element => ({
  lat: element.dataset.tripEditorMapLat,
  lng: element.dataset.tripEditorMapLng,
  zoom: element.dataset.tripEditorMapZoom
}));

test('Area 1 Greek animated Editor navigation and dirty Close', async ({ page }) => {
  const writes: string[] = [];
  const { username, password } = loadSharedLayoutConfig();
  const tripId = process.env.WAYFARER_E2E_TRIP_ID!;
  page.on('request', request => {
    if (new URL(request.url()).pathname.startsWith(`/api/trips/${tripId}/editor`) && request.method() !== 'GET') {
      writes.push(request.method());
    }
  });
  await page.setViewportSize({ width: 1536, height: 1024 });
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await page.goto('/Identity/Account/Login');
  await page.getByLabel('Username').fill(username);
  await page.getByLabel('Password').fill(password);
  await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Account/Login')),
    page.getByRole('button', { name: 'Log in' }).click()]);

  // Widen the loaded geometry just enough to animate from the fixture's saved zoom.
  await page.route(`**/api/trips/${tripId}/editor`, async route => {
    const response = await route.fetch();
    const state = await response.json();
    const place = state.placesById[Object.keys(state.placesById)[0]];
    const region = state.regionsById[place.regionId];
    region.center = { latitude: 37.79, longitude: 23.3 };
    place.location = { latitude: 38.17, longitude: 24.16 };
    await route.fulfill({ response, json: state });
  });
  await page.goto(`/User/Trip/Edit/${tripId}?lat=37.98&lng=23.73&zoom=13`);
  await page.getByRole('button', { name: 'Language', exact: true }).click();
  await page.locator('#language-dialog input[value="el-GR"]').check();
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('lang', 'el-GR');

  const map = page.locator('.trip-editor-map.leaflet-container');
  const pane = map.locator('.leaflet-map-pane');
  const toolbar = page.locator('.trip-editor-toolbar');
  const status = toolbar.getByRole('status');
  const docked = page.locator('.trip-editor-surface--docked');
  const draft = docked.getByRole('textbox', { name: 'Όνομα', exact: true });
  const latitude = docked.getByRole('spinbutton', { name: 'Γεωγραφικό πλάτος κέντρου' });
  await latitude.fill('40');
  await draft.fill('Unsaved browser title');
  await toolbar.getByRole('button', { name: 'Εστίαση στο ενεργό στοιχείο' }).click();
  await expect(map).toHaveAttribute('data-trip-editor-map-zoom', savedCamera.zoom);
  await expect(pane).not.toHaveClass(/leaflet-zoom-anim/);

  await recenterDuringFit(toolbar);
  await expect(status).toHaveText('Επαναφέρθηκε η αποθηκευμένη προβολή του ταξιδιού');
  await expect(pane).not.toHaveClass(/leaflet-zoom-anim/);
  const atAnnouncement = JSON.parse((await status.getAttribute('data-camera-at-announcement'))!);
  console.info('Recenter camera at announcement', atAnnouncement);
  expect(atAnnouncement).toEqual({ ...savedCamera, animating: false });
  expect(await camera(map)).toEqual(savedCamera);
  await expect(draft).toHaveValue('Unsaved browser title');
  await expect(latitude).toHaveValue('40');

  const close = page.locator('.trip-editor-surface--docked').getByRole('button', { name: 'Κλείσιμο', exact: true });
  await close.click();
  const dialog = page.getByRole('dialog', { name: 'Απόρριψη αλλαγών;' });
  await expect(dialog).toContainText('Να απορριφθούν οι μη αποθηκευμένες αλλαγές και να κλείσει ο επεξεργαστής;');
  await page.keyboard.press('Escape');
  await expect(close).toBeFocused();
  await expect(draft).toHaveValue('Unsaved browser title');
  await close.click();
  await dialog.getByRole('button', { name: 'Απόρριψη', exact: true }).click();
  await expect(draft).toHaveCount(0);
  await page.getByRole('button', { name: 'Επεξεργασία ταξιδιού', exact: true }).click();
  await expect(draft).toHaveValue('Shared layout browser Trip');
  await expect(latitude).toHaveValue('37.98');
  expect(writes).toEqual([]);
});

/** Triggers Recenter in the frame that starts Fit All's actual Leaflet zoom. */
async function recenterDuringFit(toolbar: Locator): Promise<void> {
  const recenter = toolbar.getByRole('button', { name: 'Επαναφορά αποθηκευμένης προβολής' });
  const recenterButton = await recenter.elementHandle();
  await toolbar.getByRole('button', { name: 'Προβολή όλων στον χάρτη' }).evaluate((fit, button) => new Promise<void>((resolve, reject) => {
    const map = document.querySelector<HTMLElement>('.trip-editor-map.leaflet-container')!;
    const pane = map.querySelector('.leaflet-map-pane')!;
    const status = fit.closest('.trip-editor-toolbar')!.querySelector<HTMLElement>('[role="status"]')!;
    const announced = new MutationObserver(() => {
      if (status.textContent !== 'Επαναφέρθηκε η αποθηκευμένη προβολή του ταξιδιού') return;
      announced.disconnect();
      status.dataset.cameraAtAnnouncement = JSON.stringify({
        lat: map.dataset.tripEditorMapLat, lng: map.dataset.tripEditorMapLng, zoom: map.dataset.tripEditorMapZoom,
        animating: pane.classList.contains('leaflet-zoom-anim')
      });
    });
    announced.observe(status, { characterData: true, childList: true, subtree: true });

    const started = new MutationObserver(() => {
      if (!pane.classList.contains('leaflet-zoom-anim')) return;
      started.disconnect();
      clearTimeout(timeout);
      button!.click();
      resolve();
    });
    const timeout = setTimeout(() => {
      started.disconnect();
      announced.disconnect();
      reject(new Error('Fit All did not start an animated Leaflet zoom'));
    }, 2000);
    started.observe(pane, { attributes: true, attributeFilter: ['class'] });
    (fit as HTMLButtonElement).click();
  }), recenterButton);
}
