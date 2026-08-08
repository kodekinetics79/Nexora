import { expect, test, type Page } from '@playwright/test';
import { requireEnv } from './support/environment';

const apiURL = process.env.E2E_API_URL ?? '';

const platformTabs = [
  { name: 'Overview', path: '/platform/overview', heading: 'Platform Overview' },
  { name: 'Tenants', path: '/platform/tenants', heading: 'Tenants' },
  { name: 'Pipeline', path: '/platform/pipeline', heading: 'Extraction Pipeline' },
  { name: 'Plans', path: '/platform/plans', heading: 'Plans' },
  { name: 'Users', path: '/platform/users', heading: 'Platform Users' },
  { name: 'Billing', path: '/platform/billing', heading: 'Billing' },
  { name: 'Support', path: '/platform/support', heading: 'Support' },
  { name: 'Email', path: '/platform/email', heading: 'Email' },
  { name: 'Security', path: '/platform/security', heading: 'Security' },
  { name: 'Audit Log', path: '/platform/audit', heading: 'Audit Log' },
] as const;

async function signInAsPlatformAdmin(page: Page): Promise<void> {
  const credentials = requireEnv(
    'Visible Google Chrome Platform Admin certification',
    'E2E_PLATFORM_ADMIN_EMAIL',
    'E2E_PLATFORM_ADMIN_PASSWORD',
  );

  await page.goto('/platform/login');
  await page.evaluate(() => sessionStorage.clear());
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Platform Console' })).toBeVisible();

  const email = page.getByRole('textbox', { name: 'Email' });
  await email.click();
  await email.pressSequentially(credentials.E2E_PLATFORM_ADMIN_EMAIL);
  const password = page.getByLabel('Password');
  await password.click();
  await password.pressSequentially(credentials.E2E_PLATFORM_ADMIN_PASSWORD);
  await page.getByRole('button', { name: 'Enter Control Plane' }).click();

  await expect(page.getByRole('heading', { name: 'Platform Overview' })).toBeVisible({ timeout: 20_000 });
  await expect(page.getByText(/scope=platform/i).first()).toBeVisible();
}

test.describe('visible Google Chrome Platform Admin certification', () => {
  test('real control plane opens every operator tab without browser or server failures', async ({ page, request }) => {
    expect(
      process.env.E2E_FIXTURE_MODE,
      'Certification must set E2E_FIXTURE_MODE=false; fixture APIs are forbidden in this lane.',
    ).toBe('false');

    const health = await request.get(new URL('/health', apiURL).toString());
    expect(health.ok(), `Real backend health check failed with HTTP ${health.status()}.`).toBeTruthy();

    const browserErrors: string[] = [];
    const failedApiRequests: string[] = [];
    const serverErrors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error') browserErrors.push(message.text());
    });
    page.on('pageerror', (error) => browserErrors.push(error.message));
    page.on('requestfailed', (request) => {
      if (request.url().startsWith(apiURL)) {
        failedApiRequests.push(`${request.method()} ${request.url()}: ${request.failure()?.errorText ?? 'failed'}`);
      }
    });
    page.on('response', (response) => {
      if (response.url().startsWith(apiURL) && response.status() >= 500) {
        serverErrors.push(`${response.request().method()} ${response.url()}: HTTP ${response.status()}`);
      }
    });

    await signInAsPlatformAdmin(page);

    for (const tab of platformTabs) {
      await page.getByRole('link', { name: tab.name, exact: true }).click();
      await expect(page).toHaveURL(new RegExp(`${tab.path.replaceAll('/', '\\/')}$`));
      await expect(page.getByRole('heading', { name: tab.heading, exact: true })).toBeVisible();
    }

    expect(serverErrors, 'Platform APIs returned unexpected HTTP 5xx responses.').toEqual([]);
    expect(failedApiRequests, 'Platform API network requests failed.').toEqual([]);
    expect(browserErrors, 'The Platform Admin emitted browser console/page errors.').toEqual([]);
  });
});
