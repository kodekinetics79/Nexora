import AxeBuilder from '@axe-core/playwright';
import type { AxeResults, Result } from 'axe-core';
import { expect, test, type Page } from '@playwright/test';
import { credentialsFor } from './support/environment';
import { loginThroughUi } from './support/login';

/**
 * WCAG 2.1 AA automated conformance smoke test.
 *
 * Axe catches roughly a third of WCAG issues, so this is a regression gate, not
 * a VPAT substitute — manual keyboard/screen-reader passes still required. The
 * gate is set at zero *critical* violations so it stays green against the
 * pre-existing serious/moderate backlog on pages outside the layout shell,
 * while blocking anything that outright locks a user out.
 */

const WCAG_AA_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'];

const scan = (page: Page): Promise<AxeResults> =>
  new AxeBuilder({ page }).withTags(WCAG_AA_TAGS).analyze();

/** Readable failure output — axe's raw result object is unreadable in CI logs. */
const describeViolations = (violations: Result[]): string => {
  if (violations.length === 0) return 'no violations';
  return violations
    .map((violation) => {
      const targets = violation.nodes
        .slice(0, 3)
        .map((node) => node.target.join(' '))
        .join(' | ');
      return `[${violation.impact}] ${violation.id} (${violation.nodes.length} node(s)): ${violation.help}\n    ${targets}`;
    })
    .join('\n');
};

const criticalOnly = (violations: Result[]): Result[] =>
  violations.filter((violation) => violation.impact === 'critical');

test.describe('WCAG 2.1 AA — axe scan', () => {
  test.describe('unauthenticated', () => {
    // The shared storageState would bounce us straight to /dashboard.
    test.use({ storageState: { cookies: [], origins: [] } });

    test('login page has no critical accessibility violations', async ({ page }) => {
      await page.goto('/login');

      // Regression guards for the fixes this spec was added alongside.
      await expect(page).toHaveTitle('Sign In | NEXORA');
      await expect(page.getByRole('heading', { level: 1, name: /welcome back/i })).toBeVisible();
      await expect(page.getByLabel('Email Address')).toHaveAttribute('type', 'email');
      await expect(page.getByLabel('Email Address')).toHaveAttribute('autocomplete', 'username');
      await expect(page.getByRole('textbox', { name: 'Password', exact: true })).toHaveAttribute('autocomplete', 'current-password');
      await expect(page.getByRole('button', { name: 'Show password' })).toBeVisible();

      const results = await scan(page);
      expect(criticalOnly(results.violations), describeViolations(results.violations)).toEqual([]);
    });
  });

  test.describe('authenticated', () => {
    test('dashboard has no critical accessibility violations', async ({ page }) => {
      await page.goto('/dashboard');
      await expect(page).toHaveTitle('Dashboard | NEXORA');
      await expect(page.getByRole('main')).toBeVisible();

      const results = await scan(page);
      expect(criticalOnly(results.violations), describeViolations(results.violations)).toEqual([]);
    });

    test('layout shell exposes skip link, landmarks and current-page state', async ({ page }) => {
      await page.goto('/dashboard');
      await expect(page.getByRole('main')).toHaveAttribute('id', 'main-content');
      await expect(page.getByRole('navigation', { name: 'Main' })).toBeAttached();

      // SC 2.4.1 — the skip link is the first thing Tab reaches, and it moves
      // real focus into <main>. Start from a known state (a fresh document load
      // leaves focus on <body>).
      await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
      await page.keyboard.press('Tab');
      const skipLink = page.getByRole('link', { name: 'Skip to main content' });
      await expect(skipLink).toBeFocused();
      await skipLink.press('Enter');
      await expect(page.getByRole('main')).toBeFocused();

      // SC 4.1.2 — the sidebar advertises the current page programmatically.
      await expect(page.locator('[aria-current="page"]').first()).toBeAttached();

      // SC 2.1.1 — the account menu used to be a click-only <Box>; it is now a
      // real button that opens from the keyboard. (Once the menu is open MUI's
      // Modal marks the rest of the page aria-hidden, so the trigger is no
      // longer queryable by role — assert on the menu that opened instead.)
      const accountButton = page.getByRole('button', { name: /^Account menu/ });
      await expect(accountButton).toHaveAttribute('aria-expanded', 'false');
      await accountButton.press('Enter');
      await expect(page.getByRole('menu')).toBeVisible();
      await expect(page.getByRole('menuitem', { name: 'Log Out Session' })).toBeVisible();
    });

    /**
     * The core journey — lead to RFQ to quote — was outside this gate entirely. It covered
     * /dashboard, the two "all" lists and /security/users, so every screen a salesperson
     * actually spends the day on could regress without the gate noticing. These are the
     * list surfaces changed by the enterprise-polish pass; they share the DataGrid shell
     * with /procurement/rfqs/all, which this spec already covers.
     */
    for (const [route, title] of [
      ['/procurement/rfqs/draft', 'Draft RFQs | NEXORA'],
      ['/procurement/rfqs/outstanding', 'Outstanding RFQs | NEXORA'],
      ['/sales/quotes', 'Quotes | NEXORA'],
      ['/analytics/deadlines', 'Deadline Board | NEXORA'],
      // The landing screen and the directory that replaced the 69-row rail. The first screen after
      // sign-in is the one that can least afford an accessibility regression.
      ['/inbox', 'Inbox | NEXORA'],
      ['/advanced', 'All Screens | NEXORA'],
    ] as const) {
      test(`${route} has no critical accessibility violations`, async ({ page }) => {
        await page.goto(route);
        await expect(page).toHaveTitle(title);
        await expect(page.getByRole('main')).toBeVisible();

        const results = await scan(page);
        expect(criticalOnly(results.violations), describeViolations(results.violations)).toEqual([]);
      });
    }

    test('each route gets a distinct, meaningful document title', async ({ page }) => {
      await page.goto('/dashboard');
      await expect(page).toHaveTitle('Dashboard | NEXORA');

      await page.goto('/procurement/leads/all');
      await expect(page).toHaveTitle('All Inquiries | NEXORA');

      await page.goto('/procurement/rfqs/all');
      await expect(page).toHaveTitle('All RFQs | NEXORA');

      await page.goto('/security/users');
      await expect(page).toHaveTitle('Users | NEXORA');
    });
  });
});

test.describe('login form error handling', () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  test('a failed sign-in is announced and linked to the fields', async ({ page }) => {
    const credentials = credentialsFor('manager');
    expect(credentials, 'fixture credentials are required for this spec').not.toBeNull();

    await page.goto('/login');
    await page.getByLabel('Email Address').fill(credentials!.email);
    await page.getByRole('textbox', { name: 'Password', exact: true }).fill('definitely-the-wrong-password');
    await page.getByRole('button', { name: 'LOGIN' }).click();

    const alert = page.getByRole('alert');
    await expect(alert).toBeVisible();
    const alertId = await alert.getAttribute('id');
    expect(alertId).toBe('login-error');

    // SC 3.3.1 — the invalid fields point at the message describing the failure.
    await expect(page.getByLabel('Email Address')).toHaveAttribute('aria-invalid', 'true');
    await expect(page.getByLabel('Email Address')).toHaveAttribute('aria-describedby', 'login-error');
    await expect(page.getByRole('textbox', { name: 'Password', exact: true })).toHaveAttribute('aria-invalid', 'true');
  });

  test('the fixture login still works end to end', async ({ page }) => {
    const credentials = credentialsFor('manager');
    expect(credentials, 'fixture credentials are required for this spec').not.toBeNull();
    await loginThroughUi(page, credentials!);
    await expect(page).toHaveTitle('Dashboard | NEXORA');
  });
});
