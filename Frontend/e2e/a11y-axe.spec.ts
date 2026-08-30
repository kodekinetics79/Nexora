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
 * release gate is set at zero serious or critical violations. Moderate/minor
 * findings remain visible in the failure message when a release-blocking issue
 * exists, while manual keyboard/screen-reader review covers what Axe cannot.
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

const releaseBlocking = (violations: Result[]): Result[] =>
  violations.filter((violation) => violation.impact === 'critical' || violation.impact === 'serious');

test.describe('WCAG 2.1 AA — axe scan', () => {
  test.describe('unauthenticated', () => {
    // The shared storageState would bounce us straight to /dashboard.
    test.use({ storageState: { cookies: [], origins: [] } });

    test('login page has no serious or critical accessibility violations', async ({ page }) => {
      await page.goto('/login');

      // Regression guards for the fixes this spec was added alongside.
      await expect(page).toHaveTitle('Sign In | NEXORA');
      await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible();
      await expect(page.getByLabel('Email Address')).toHaveAttribute('type', 'email');
      await expect(page.getByLabel('Email Address')).toHaveAttribute('autocomplete', 'username');
      await expect(page.getByRole('textbox', { name: 'Password', exact: true })).toHaveAttribute('autocomplete', 'current-password');
      await expect(page.getByRole('button', { name: 'Show password' })).toBeVisible();

      const results = await scan(page);
      expect(releaseBlocking(results.violations), describeViolations(results.violations)).toEqual([]);
    });

    test('mobile login keeps identity, the primary action, and usable landmarks above the fold', async ({ page }) => {
      await page.setViewportSize({ width: 390, height: 640 });
      await page.goto('/login');

      const workflow = page.getByRole('region', { name: 'Nexora evidence-to-cash workflow' });
      await expect(workflow).toBeHidden();

      const main = page.getByRole('main');
      await expect(main).toHaveAttribute('id', 'main-content');
      await expect(main.getByText('NEXORA', { exact: true })).toBeVisible();
      await expect(main.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible();
      await expect(main.getByLabel('Email address')).toBeVisible();
      await expect(main.getByRole('textbox', { name: 'Password', exact: true })).toBeVisible();
      const signIn = main.getByRole('button', { name: 'Sign in' });
      await expect(signIn).toBeVisible();
      await expect(main.getByRole('button', { name: 'Switch to dark theme' })).toBeVisible();

      const mobileGeometry = await page.evaluate(() => {
        const targets = [
          document.querySelector('button[aria-label="Switch to dark theme"]'),
          document.querySelector('button[aria-label="Show password"]'),
        ].filter((element): element is Element => element !== null);

        return {
          noHorizontalOverflow: document.documentElement.scrollWidth <= window.innerWidth,
          signInAboveFold: (() => {
            const button = document.querySelector('button[type="submit"]');
            if (!button) return false;
            const rect = button.getBoundingClientRect();
            return rect.top >= 0 && rect.bottom <= window.innerHeight;
          })(),
          touchTargets: targets.map((element) => {
            const rect = element.getBoundingClientRect();
            return { width: rect.width, height: rect.height };
          }),
        };
      });

      expect(mobileGeometry.noHorizontalOverflow).toBe(true);
      expect(mobileGeometry.signInAboveFold).toBe(true);
      expect(mobileGeometry.touchTargets).toHaveLength(2);
      for (const target of mobileGeometry.touchTargets) {
        expect(target.width).toBeGreaterThanOrEqual(44);
        expect(target.height).toBeGreaterThanOrEqual(44);
      }

      // Guard the exact hand-off between the compact mobile brand and the
      // evidence panel. A mismatched 600px boundary once hid both.
      await page.setViewportSize({ width: 600, height: 800 });
      await page.reload();
      await expect(
        page.getByRole('region', { name: 'Nexora evidence-to-cash workflow' }).getByText('NEXORA', { exact: true }),
      ).toBeVisible();
    });
  });

  test.describe('authenticated', () => {
    test('dashboard has no serious or critical accessibility violations', async ({ page }) => {
      await page.goto('/dashboard');
      await expect(page).toHaveTitle('Dashboard | NEXORA');
      await expect(page.getByRole('main')).toBeVisible();

      const results = await scan(page);
      expect(releaseBlocking(results.violations), describeViolations(results.violations)).toEqual([]);
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
      ['/procurement/rfqs/outstanding', 'Assigned Leads | NEXORA'],
      ['/sales/quotes', 'Quotes | NEXORA'],
      ['/analytics/deadlines', 'Deadline Board | NEXORA'],
      // The landing screen and the directory that replaced the 69-row rail. The first screen after
      // sign-in is the one that can least afford an accessibility regression.
      ['/inbox', 'Inbox | NEXORA'],
      ['/advanced', 'All Screens | NEXORA'],
    ] as const) {
      test(`${route} has no serious or critical accessibility violations`, async ({ page }) => {
        await page.goto(route);
        await expect(page).toHaveTitle(title);
        await expect(page.getByRole('main')).toBeVisible();

        const results = await scan(page);
        expect(releaseBlocking(results.violations), describeViolations(results.violations)).toEqual([]);
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
    await page.getByRole('button', { name: 'Sign in' }).click();

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
    // Landing is the work queue, not a dashboard — see landingRoute.test.ts, which pins /inbox
    // for every role including one with no modules at all.
    await expect(page).toHaveTitle('Inbox | NEXORA');
  });
});
