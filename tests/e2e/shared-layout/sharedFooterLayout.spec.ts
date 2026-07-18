import { expect, test, type Locator, type Page } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

const config = loadSharedLayoutConfig();
const footerLinks = '.site-footer__link';

test.describe('shared standard-layout footer', () => {
  for (const [name, viewport] of Object.entries({ desktop: { width: 1440, height: 900 }, tablet: { width: 768, height: 1024 }, mobile: { width: 375, height: 667 } })) {
    test(`keeps the short public page contained at ${name}`, async ({ page }) => {
      await page.setViewportSize(viewport);
      await page.goto('/Home/Privacy');
      await expectStandardFooter(page);
      await expect(page.locator(footerLinks)).toHaveCount(4);
      await expectMatchingColors(page, 'light');
    });
  }

  for (const theme of ['light', 'dark'] as const) {
    test(`matches footer link colors across ${theme} theme interaction states`, async ({ page }) => {
      await page.addInitScript(value => localStorage.setItem('theme', value), theme);
      await page.goto('/Home/Privacy');
      await expect(page.locator('body')).toHaveAttribute('data-bs-theme', theme);
      await expectMatchingColors(page, theme);
      await expectMatchingInteractionColors(page, 'hover');
      await expectMatchingInteractionColors(page, 'focus');
    });
  }

  test('scrolls normal long content without the footer overlaying it', async ({ page }) => {
    await page.goto('/Home/Privacy');
    await page.locator('.site-content main').evaluate(main => {
      const probe = document.createElement('div');
      probe.textContent = 'Shared layout scroll probe';
      probe.style.height = '220vh';
      main.append(probe);
    });
    const documentHeight = await page.evaluate(() => document.documentElement.scrollHeight);
    expect(documentHeight).toBeGreaterThan(await page.evaluate(() => innerHeight));
    await page.evaluate(() => scrollTo(0, document.documentElement.scrollHeight));
    await expect(page.locator('.site-footer')).toBeInViewport();
    const footerDocumentBottom = await page.locator('.site-footer').evaluate(element => element.getBoundingClientRect().bottom + scrollY);
    expect(footerDocumentBottom).toBeCloseTo(documentHeight, 0);
  });

  test('keeps the authenticated trip shell compact without restoring the viewer footer calculation', async ({ page }) => {
    await signIn(page);
    await page.goto('/User/Trip');
    await expectStandardFooter(page);
    expect((await page.locator('.site-footer').boundingBox())!.height).toBeLessThan(100);

    const viewerLink = page.locator('a[href^="/User/Trip/View/"]').first();
    await expect(viewerLink).toBeVisible();
    await viewerLink.click();
    await expect(page.locator('#trip-view')).toBeVisible();
    await expect(page.locator('.site-footer')).toBeVisible();
    const viewerBox = await page.locator('#trip-view').boundingBox();
    const viewerFooterBox = await page.locator('.site-footer').boundingBox();
    expect(viewerBox!.y + viewerBox!.height).toBeCloseTo(viewerFooterBox!.y, 0);
  });
});

async function signIn(page: Page): Promise<void> {
  await page.goto('/Identity/Account/Login?ReturnUrl=%2FUser%2FTrip');
  await page.getByLabel('Username').fill(config.username);
  await page.getByLabel('Password').fill(config.password);
  await Promise.all([
    page.waitForURL(url => !url.pathname.includes('/Identity/Account/Login')),
    page.getByRole('button', { name: 'Log in' }).click()
  ]);
}

async function expectStandardFooter(page: Page): Promise<void> {
  const footer = page.locator('.site-footer');
  await expect(footer).toBeVisible();
  expect(await footer.evaluate(element => getComputedStyle(element).position)).not.toBe('fixed');
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(await page.evaluate(() => innerWidth));
  const footerBox = await footer.boundingBox();
  expect(footerBox!.y + footerBox!.height).toBeGreaterThanOrEqual((await page.evaluate(() => innerHeight)) - 1);
}

async function expectMatchingColors(page: Page, theme: string): Promise<void> {
  const colors = await page.locator(footerLinks).evaluateAll(links => links.map(link => getComputedStyle(link).color));
  expect(colors, `${theme} default footer colors`).toEqual([colors[0], colors[0], colors[0], colors[0]]);
}

async function expectMatchingInteractionColors(page: Page, interaction: 'hover' | 'focus'): Promise<void> {
  const colors: string[] = [];
  for (const link of await page.locator(footerLinks).all()) {
    if (interaction === 'hover') await link.hover();
    else await focusWithKeyboard(page, link);
    colors.push(await link.evaluate(element => getComputedStyle(element).color));
  }
  expect(colors, `${interaction} footer colors`).toEqual([colors[0], colors[0], colors[0], colors[0]]);
}

async function focusWithKeyboard(page: Page, link: Locator): Promise<void> {
  for (let attempt = 0; attempt < 40; attempt++) {
    if (await link.evaluate(element => document.activeElement === element)) return;
    await page.keyboard.press('Tab');
  }
  throw new Error('Footer link was not reachable by keyboard navigation.');
}
