import { expect, test, type Page } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

type NavbarGeometry = Record<string, { left: number; right: number; width: number } | null> & {
  clientWidth: number;
  scrollWidth: number;
  firstOverflowingOwner: string | null;
};

test.describe('shared standard-layout navbar', () => {
  test('exposes the authenticated navbar overflow at the zoom-equivalent width', async ({ page }) => {
    await page.setViewportSize({ width: 600, height: 900 });
    await signIn(page);

    const navbar = page.locator('#mainNavbar');
    const collapse = navbar.locator('.navbar-collapse');
    const toggler = navbar.locator('.navbar-toggler');
    // The supported local fixture is the widest available authenticated User-role fixture.
    await expect(navbar.locator('.nav-user-btn')).toBeAttached();
    await expect(navbar.getByRole('button', { name: 'Logout' })).toBeAttached();
    await expect(collapse).toBeVisible();
    await expect(toggler).toBeHidden();

    const geometry = await measureNavbar(page);
    expect(
      geometry.scrollWidth,
      `The mounted authenticated navbar must fit the document. Geometry: ${JSON.stringify(geometry)}`
    ).toBeLessThanOrEqual(geometry.clientWidth);
  });
});

/** Signs in through the mounted Identity page using the ignored local verification fixture. */
async function signIn(page: Page): Promise<void> {
  const config = loadSharedLayoutConfig();
  await page.goto('/Identity/Account/Login');
  await page.getByLabel('Username').fill(config.username);
  await page.getByLabel('Password').fill(config.password);
  await Promise.all([
    page.waitForURL(url => !url.pathname.endsWith('/Account/Login')),
    page.getByRole('button', { name: 'Log in' }).click()
  ]);
}

/** Captures every owner needed to diagnose the first mounted horizontal overflow. */
async function measureNavbar(page: Page): Promise<NavbarGeometry> {
  return page.evaluate(() => {
    const navbar = document.querySelector<HTMLElement>('#mainNavbar');
    if (!navbar) throw new Error('The shared navbar did not mount.');
    const rect = (selector: string) => {
      const element = navbar.querySelector<HTMLElement>(selector);
      if (!element) return null;
      const bounds = element.getBoundingClientRect();
      return { left: bounds.left, right: bounds.right, width: bounds.width };
    };
    const navbarBounds = navbar.getBoundingClientRect();
    const clientWidth = document.documentElement.clientWidth;
    const firstOverflowingOwner = [...navbar.querySelectorAll<HTMLElement>('*')]
      .find(element => element.getBoundingClientRect().right > clientWidth + 0.5);

    return {
      clientWidth,
      scrollWidth: document.documentElement.scrollWidth,
      navbar: { left: navbarBounds.left, right: navbarBounds.right, width: navbarBounds.width },
      container: rect('.container-fluid'),
      brand: rect('.navbar-brand'),
      collapseOwner: rect('.navbar-collapse'),
      primaryNavigation: rect('.navbar-collapse > .navbar-nav:first-child'),
      accountNavigation: rect('.navbar-collapse > .navbar-nav:last-child'),
      usernameRoleButton: rect('.nav-user-btn'),
      toggler: rect('.navbar-toggler'),
      firstOverflowingOwner: firstOverflowingOwner
        ? `${firstOverflowingOwner.tagName.toLowerCase()}${firstOverflowingOwner.id ? `#${firstOverflowingOwner.id}` : ''}.${[...firstOverflowingOwner.classList].join('.')}`
        : null
    };
  });
}
