import { expect, test, type Page } from '@playwright/test';
import { loadSharedLayoutConfig } from './sharedLayoutConfig';

type NavbarGeometry = Record<string, { left: number; right: number; width: number } | null> & {
  clientWidth: number;
  scrollWidth: number;
  firstOverflowingOwner: string | null;
};

test.describe('shared standard-layout navbar', () => {
  test('keeps the authenticated navbar coherent across desktop, zoom-equivalent, and phone widths', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await signIn(page);

    const navbar = page.locator('#mainNavbar');
    const collapse = navbar.locator('.navbar-collapse');
    const toggler = navbar.locator('.navbar-toggler');
    // The supported local fixture is the widest available authenticated User-role fixture.
    await expect(navbar.locator('.nav-user-btn')).toBeAttached();
    await expect(navbar.getByRole('button', { name: 'Logout' })).toBeAttached();
    await expect(collapse).toBeVisible();
    await expect(toggler).toBeHidden();
    await expectContained(page);

    const accountButton = navbar.locator('.nav-user-btn');
    await accountButton.focus();
    await expect(accountButton).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(accountButton).toHaveAttribute('aria-expanded', 'true');
    await expect(navbar.locator('.user-menu')).toBeVisible();
    await page.keyboard.press('Escape');

    await page.setViewportSize({ width: 640, height: 300 });
    await expect(collapse).toBeHidden();
    await expect(toggler).toBeVisible();
    await expect(toggler).toHaveClass(/collapsed/);
    await expect(toggler).toHaveAttribute('aria-expanded', 'false');
    await expect(navbar.getByRole('link', { name: 'Home' })).toBeHidden();
    await expectContained(page);

    await toggler.focus();
    await expect(toggler).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(collapse).toHaveClass(/show/);
    await expect(toggler).not.toHaveClass(/collapsed/);
    await expect(toggler).toHaveAttribute('aria-expanded', 'true');
    await expectStandardControls(navbar);
    await expectOpenedMenuContained(page);

    await accountButton.focus();
    await page.keyboard.press('Enter');
    await expect(accountButton).toHaveAttribute('aria-expanded', 'true');
    const accountItems = navbar.locator('.user-menu a');
    await expect(accountItems.first()).toBeVisible();
    await page.keyboard.press('Tab');
    await expect(accountItems.first()).toBeFocused();
    await page.keyboard.press('Escape');

    await toggler.focus();
    await page.keyboard.press('Space');
    await expect(collapse).toBeHidden();
    await expect(toggler).toHaveAttribute('aria-expanded', 'false');
    await page.keyboard.press('Enter');
    await expect(collapse).toBeVisible();
    await expect(collapse).toHaveClass(/show/);
    await expect(toggler).toHaveAttribute('aria-expanded', 'true');

    await page.setViewportSize({ width: 390, height: 844 });
    await expectStandardControls(navbar);
    await expectContained(page);
    await expectOpenedMenuContained(page);
    await toggler.focus();
    await page.keyboard.press('Enter');
    await expect(collapse).toBeHidden();
    await expectContained(page);
  });

  test('keeps the anonymous Login and Register controls contained', async ({ page }) => {
    await page.setViewportSize({ width: 640, height: 500 });
    await page.goto('/Home/Privacy');
    const navbar = page.locator('#mainNavbar');
    await navbar.locator('.navbar-toggler').focus();
    await page.keyboard.press('Enter');
    await expect(navbar.getByRole('link', { name: 'Register' })).toBeVisible();
    await expect(navbar.getByRole('link', { name: 'Login' })).toBeVisible();
    await expectContained(page);
  });

  test('uses one stable native Bootstrap collapse owner', async ({ page }) => {
    await page.goto('/Home/Privacy');
    const toggler = page.locator('#mainNavbar .navbar-toggler');
    await expect(toggler).toHaveAttribute('data-bs-target', '#mainNavbarCollapse');
    await expect(toggler).toHaveAttribute('aria-controls', 'mainNavbarCollapse');
    await expect(page.locator('#mainNavbarCollapse')).toHaveCount(1);
  });
});

/** Verifies all controls supplied by the supported authenticated fixture remain exposed. */
async function expectStandardControls(navbar: ReturnType<Page['locator']>): Promise<void> {
  for (const name of ['Home', 'Trips', 'Docs', 'Mobile', 'Privacy']) {
    await expect(navbar.getByRole('link', { name, exact: true }).first()).toBeVisible();
  }
  await expect(navbar.locator('#themeToggle')).toBeVisible();
  await expect(navbar.locator('.nav-user-btn')).toBeVisible();
  await expect(navbar.getByRole('button', { name: 'Logout' })).toBeVisible();
}

/** Verifies the document and every shared-navbar owner remain inside the CSS viewport. */
async function expectContained(page: Page): Promise<void> {
  const geometry = await measureNavbar(page);
  expect(geometry.scrollWidth, `Navbar geometry: ${JSON.stringify(geometry)}`).toBeLessThanOrEqual(geometry.clientWidth);
  expect(geometry.firstOverflowingOwner, `Navbar geometry: ${JSON.stringify(geometry)}`).toBeNull();
}

/** Verifies the opened collapse owns bounded vertical overflow without widening the page. */
async function expectOpenedMenuContained(page: Page): Promise<void> {
  const geometry = await page.locator('#mainNavbarCollapse').evaluate(element => {
    const bounds = element.getBoundingClientRect();
    const style = getComputedStyle(element);
    return {
      bottom: bounds.bottom,
      clientHeight: element.clientHeight,
      scrollHeight: element.scrollHeight,
      viewportHeight: innerHeight,
      overflowY: style.overflowY
    };
  });
  expect(geometry.bottom).toBeLessThanOrEqual(geometry.viewportHeight + 1);
  if (geometry.viewportHeight <= 500) {
    expect(geometry.scrollHeight).toBeGreaterThan(geometry.clientHeight);
  } else {
    expect(geometry.scrollHeight).toBeGreaterThanOrEqual(geometry.clientHeight);
  }
  expect(geometry.overflowY).toBe('auto');
  await expectContained(page);
}

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
