import { expect, test, type Page, type CDPSession } from '@playwright/test';
import { buildMapEmbed } from '../../../wwwroot/js/embedSharing.js';
import { loadSharedLayoutConfig } from '../shared-layout/sharedLayoutConfig';

// A reserved test hostname points at the established local HTTPS host, never a deployment hostname.
test.use({ ignoreHTTPSErrors: true, permissions: ['local-network-access', 'clipboard-read', 'clipboard-write'],
  launchOptions: { args: ['--host-resolver-rules=MAP wayfarer.example.test 127.0.0.1'] } });

/** Native Chromium input reaches the actual iframe; no touch/wheel dispatch in page JavaScript. */
const swipe = async (cdp: CDPSession, two: boolean, pinch = false) => {
  const points = (step: number) => two
    ? [{ x: 160 - (pinch ? step * 7 : 0), y: 430 - step * 15, id: 1 },
      { x: 220 + (pinch ? step * 7 : 0), y: 430 - step * 15, id: 2 }]
    : [{ x: 190, y: 430 - step * 20, id: 1 }];
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: points(0) });
  for (let step = 1; step <= 8; step++) {
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: points(step) });
    await new Promise(resolve => setTimeout(resolve, 30));
  }
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
};

/** Mount production-generated markup in a cross-origin, genuinely scrollable, script-free parent. */
const mountEmbed = async (page: Page, origin: string) => {
  await page.goto(`${origin}/Public/Trips`);
  const path = await page.locator('a[href^="/Public/Trips/"]').evaluateAll(links =>
    links.map(link => link.getAttribute('href')).find(href => /\/Public\/Trips\/[\da-f-]{36}$/i.test(href ?? '')));
  expect(path, 'An existing public Trip is required; this test does not mutate fixtures.').toBeTruthy();
  const output = buildMapEmbed({ kind: 'trip', id: path!.split('/').at(-1), title: 'Wayfarer public Trip' }, origin);
  await page.route('https://embed-host.example.test/', route => route.fulfill({ contentType: 'text/html',
    body: `<!doctype html><meta name="viewport" content="width=device-width,initial-scale=1"><div style="height:120px">Before map</div>${output.html}<div style="height:1800px">After map</div>` }));
  await page.goto('https://embed-host.example.test/');
  const frame = page.frameLocator('iframe');
  await expect(frame.getByRole('link', { name: 'Open full view', exact: true })).toBeVisible();
  return { frame, path: path!, child: page.frames().find(item => item.url().startsWith(output.url))! };
};

test('generated Trip iframe cooperates with desktop scrolling and retains explicit controls and keyboard escape', async ({ page, baseURL }) => {
  const { frame, path, child } = await mountEmbed(page, baseURL!);
  const zoom = frame.locator('.leaflet-control-custom span').first();
  const initialZoom = await zoom.textContent();
  const initialUrl = child.url();
  await page.mouse.move(700, 430);
  await page.mouse.wheel(0, 350);
  await expect.poll(() => page.evaluate(() => scrollY)).toBeGreaterThan(300);
  await expect(zoom).toHaveText(initialZoom!);
  expect(child.url()).toBe(initialUrl);
  await page.evaluate(() => scrollTo(0, 0));
  await page.keyboard.down('Control');
  await page.mouse.wheel(0, -250);
  await page.keyboard.up('Control');
  await expect(zoom).not.toHaveText(initialZoom!);
  const intentionalZoom = await zoom.textContent();
  await page.mouse.wheel(0, 200);
  await expect.poll(() => page.evaluate(() => scrollY)).toBeGreaterThan(150);
  await expect(zoom).toHaveText(intentionalZoom!);
  await page.evaluate(() => scrollTo(0, 0));
  await frame.getByRole('button', { name: 'Zoom in', exact: true }).click();
  await expect(zoom).not.toHaveText(intentionalZoom!);
  await frame.getByRole('button', { name: 'Zoom out', exact: true }).click();
  await expect(zoom).toHaveText(intentionalZoom!);
  const beforeDrag = child.url();
  await page.mouse.move(700, 430);
  await page.mouse.down();
  await page.mouse.move(760, 460, { steps: 8 });
  await page.mouse.up();
  await expect.poll(() => child.url()).not.toBe(beforeDrag);
  const escape = frame.getByRole('link', { name: 'Open full view', exact: true });
  await escape.focus();
  const popup = page.waitForEvent('popup');
  await page.keyboard.press('Enter');
  const fullView = await popup;
  await fullView.waitForURL(`${baseURL}${path}`);
  await expect(fullView.locator('#trip-view')).toHaveAttribute('data-embed', 'false');
  console.log('Desktop: parent wheel scrolling before/after Ctrl-wheel; zoom controls, mouse drag, keyboard full view passed.');
});

test('Timeline settings copies matching output and a wrapped header does not consume parent scrolling', async ({ page, baseURL }) => {
  const config = loadSharedLayoutConfig();
  await page.goto(`${baseURL}/Identity/Account/Login`);
  await page.getByLabel('Username').fill(config.username);
  await page.getByLabel('Password').fill(config.password);
  await Promise.all([page.waitForURL(url => !url.pathname.endsWith('/Login')),
    page.getByRole('button', { name: 'Log in' }).click()]);
  await page.goto(`${baseURL}/User/Settings`);
  test.skip(await page.locator('[data-embed-format="url"]').count() === 0, 'The established account has no public Timeline.');
  await page.getByRole('button', { name: 'Copy embed URL', exact: true }).click();
  const url = await page.evaluate(() => navigator.clipboard.readText());
  await page.getByRole('button', { name: 'Copy embed HTML', exact: true }).click();
  const html = await page.evaluate(() => navigator.clipboard.readText());
  expect(html).toContain(`src="${url}"`);
  expect(url).toMatch(new RegExp(`^${baseURL}/Public/Users/Timeline/`));
  await page.route('https://embed-host.example.test/', route => route.fulfill({ contentType: 'text/html',
    body: `<!doctype html><meta name="viewport" content="width=device-width,initial-scale=1"><div style="height:120px">Before</div>${html}<div style="height:1800px">After</div>` }));
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('https://embed-host.example.test/');
  const frame = page.frameLocator('iframe');
  const escape = frame.getByRole('link', { name: 'Open full view', exact: true });
  await expect(escape).toBeVisible();
  await expect(escape).toHaveAttribute('href', new URL(url).pathname.replace(/\/embed$/, ''));
  const child = page.frames().find(item => item.url().startsWith(url))!;
  await expect.poll(() => child.evaluate(() => document.documentElement.scrollHeight - innerHeight)).toBe(0);
  const zoom = frame.locator('.leaflet-control-custom span').first();
  const initialZoom = await zoom.textContent();
  await page.mouse.move(190, 430);
  await page.mouse.wheel(0, 250);
  await expect.poll(() => page.evaluate(() => scrollY)).toBeGreaterThan(200);
  await expect(zoom).toHaveText(initialZoom!);
  console.log('Timeline: real settings clipboard URL/HTML match; wrapped header fits iframe; ordinary wheel reaches parent.');
});

test('mounted mobile iframe allows single-touch page scroll, two-touch pan/zoom, and one-tap escape', async ({ browser, baseURL }) => {
  const context = await browser.newContext({ ignoreHTTPSErrors: true, permissions: ['local-network-access'],
    viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  try {
    const page = await context.newPage();
    const { frame, child, path } = await mountEmbed(page, baseURL!);
    const cdp = await context.newCDPSession(page);
    const zoom = frame.locator('.leaflet-control-custom span').first();
    const originalUrl = child.url();
    const originalZoom = await zoom.textContent();
    await swipe(cdp, false);
    await expect.poll(() => page.evaluate(() => scrollY)).toBeGreaterThan(100);
    expect(child.url()).toBe(originalUrl);
    await page.evaluate(() => scrollTo(0, 0));
    await swipe(cdp, true);
    await expect.poll(() => child.url()).not.toBe(originalUrl);
    await expect(zoom).toHaveText(originalZoom!);
    await swipe(cdp, true, true);
    await expect(zoom).not.toHaveText(originalZoom!);
    const manipulatedUrl = child.url();
    await swipe(cdp, false);
    await expect.poll(() => page.evaluate(() => scrollY)).toBeGreaterThan(100);
    expect(child.url()).toBe(manipulatedUrl);
    await page.evaluate(() => scrollTo(0, 0));
    await frame.getByRole('button', { name: 'Zoom in', exact: true }).tap();
    await expect.poll(() => child.url()).not.toBe(manipulatedUrl);
    const popup = page.waitForEvent('popup');
    await frame.getByRole('link', { name: 'Open full view', exact: true }).tap();
    await (await popup).waitForURL(`${baseURL}${path}`);
    console.log('Mobile Chromium emulation: parent single-touch scrolling before/after two-touch pan and pinch; zoom tap and full-view tap passed.');
  } finally { await context.close(); }
});
