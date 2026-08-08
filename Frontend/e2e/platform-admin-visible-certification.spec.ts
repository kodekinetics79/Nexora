import { expect, test, type Page } from '@playwright/test';
import { requireEnv } from './support/environment';

const apiURL = process.env.E2E_API_URL ?? '';
const journeyId = Date.now().toString(36);
const journeyTenant = {
  name: process.env.E2E_VISIBLE_TENANT_NAME ?? `Visible Certification ${journeyId}`,
  email: `admin-${journeyId}@visible-certification.example`,
  password: 'Visible-Certification-2026!',
};

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

function observeBrowserAndApiFailures(page: Page) {
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

  return () => {
    expect(serverErrors, 'Platform APIs returned unexpected HTTP 5xx responses.').toEqual([]);
    expect(failedApiRequests, 'Platform API network requests failed.').toEqual([]);
    expect(browserErrors, 'The Platform Admin emitted browser console/page errors.').toEqual([]);
  };
}

async function fillVisible(field: ReturnType<Page['getByLabel']>, value: string): Promise<void> {
  await field.click();
  await field.pressSequentially(value);
}

async function confirmReason(page: Page, dialogTitle: string, reason: string, action: string): Promise<void> {
  const dialog = page.getByRole('dialog', { name: dialogTitle });
  await fillVisible(dialog.getByRole('textbox'), reason);
  await dialog.getByRole('button', { name: action, exact: true }).click();
}

test.describe.serial('visible Google Chrome Platform Admin certification', () => {
  test('real control plane opens every operator tab without browser or server failures', async ({ page, request }) => {
    expect(
      process.env.E2E_FIXTURE_MODE,
      'Certification must set E2E_FIXTURE_MODE=false; fixture APIs are forbidden in this lane.',
    ).toBe('false');

    const health = await request.get(new URL('/health', apiURL).toString());
    expect(health.ok(), `Real backend health check failed with HTTP ${health.status()}.`).toBeTruthy();

    const assertNoFailures = observeBrowserAndApiFailures(page);

    await signInAsPlatformAdmin(page);

    for (const tab of platformTabs) {
      await page.getByRole('link', { name: tab.name, exact: true }).click();
      await expect(page).toHaveURL(new RegExp(`${tab.path.replaceAll('/', '\\/')}$`));
      await expect(page.getByRole('heading', { name: tab.heading, exact: true })).toBeVisible();
    }

    assertNoFailures();
  });

  test('provisions a billable tenant through the durable eight-step worker', async ({ page }) => {
    const assertNoFailures = observeBrowserAndApiFailures(page);
    await signInAsPlatformAdmin(page);
    await page.getByRole('link', { name: 'Tenants', exact: true }).click();
    await page.getByRole('button', { name: 'Create Company' }).click();

    const wizard = page.getByRole('dialog', { name: 'Create a company workspace' });
    await fillVisible(wizard.getByLabel('Organization name'), journeyTenant.name);
    await fillVisible(wizard.getByLabel('Company contact email'), `contact-${journeyId}@visible-certification.example`);
    await fillVisible(wizard.getByLabel('Address line 1'), '100 Certification Way');
    await fillVisible(wizard.getByLabel('City'), 'New York');
    await wizard.getByLabel('Country of registration').click();
    await wizard.getByLabel('Country of registration').fill('United States');
    await wizard.getByLabel('Country of registration').press('ArrowDown');
    await wizard.getByLabel('Country of registration').press('Enter');
    await wizard.getByRole('button', { name: 'Next' }).click();

    await wizard.getByRole('combobox', { name: 'Plan', exact: true }).click();
    await page.getByRole('option', { name: 'Growth (growth)', exact: true }).click();
    await wizard.getByRole('combobox', { name: 'Rate card', exact: true }).click();
    await page.getByRole('option', { name: /standard-2026 · USD/ }).click();
    await fillVisible(wizard.getByLabel('Billing contact name'), 'Visible Billing');
    await fillVisible(wizard.getByLabel('Billing contact email'), `billing-${journeyId}@visible-certification.example`);
    await fillVisible(wizard.getByLabel('Account owner (internal)'), 'owner@nexora.local');
    await wizard.getByRole('button', { name: 'Next' }).click();

    await fillVisible(wizard.getByLabel('First name'), 'Visible');
    await fillVisible(wizard.getByLabel('Last name'), 'Administrator');
    await fillVisible(wizard.getByLabel('Work email'), journeyTenant.email);
    await wizard.getByLabel('Set a password now').check();
    await fillVisible(wizard.getByLabel('Initial password'), journeyTenant.password);
    await wizard.getByRole('button', { name: 'Next' }).click();
    await wizard.getByRole('button', { name: 'Create workspace' }).click();

    const progress = page.getByRole('dialog', { name: new RegExp(`Provisioning ${journeyTenant.name}`) });
    await expect(progress.getByText(/Every step committed/)).toBeVisible({ timeout: 45_000 });
    await expect(progress.getByText('8 of 8 steps')).toBeVisible();
    await progress.getByRole('button', { name: 'Close' }).click();

    await fillVisible(page.getByLabel('Search tenants'), journeyTenant.name);
    await expect(page.getByRole('row').filter({ hasText: journeyTenant.name })).toBeVisible();
    assertNoFailures();
  });

  test('proves reversible offboarding and blocks purge before retention elapses', async ({ page }) => {
    const assertNoFailures = observeBrowserAndApiFailures(page);
    await signInAsPlatformAdmin(page);
    await page.getByRole('link', { name: 'Tenants', exact: true }).click();
    await fillVisible(page.getByLabel('Search tenants'), journeyTenant.name);
    await page.getByRole('row').filter({ hasText: journeyTenant.name }).getByText(journeyTenant.name, { exact: true }).click();
    await page.getByRole('tab', { name: 'Lifecycle' }).click();

    await page.getByRole('button', { name: 'Suspend tenant' }).click();
    await confirmReason(page, 'Suspend tenant', 'Visible certification suspension', 'Suspend');
    await expect(page.getByRole('button', { name: 'Resume tenant' })).toBeVisible();

    await page.getByRole('button', { name: 'Resume tenant' }).click();
    await confirmReason(page, 'Resume tenant', 'Visible certification recovery', 'Resume');
    await expect(page.getByRole('button', { name: 'Suspend tenant' })).toBeVisible();

    await page.getByRole('button', { name: 'Suspend tenant' }).click();
    await confirmReason(page, 'Suspend tenant', 'Visible certification offboarding', 'Suspend');
    await page.getByRole('button', { name: 'Archive tenant' }).click();
    await confirmReason(page, 'Archive tenant', 'Visible certification archive decision', 'Archive');
    await expect(page.getByRole('button', { name: 'Schedule deletion' })).toBeEnabled();

    await page.getByRole('button', { name: 'Schedule deletion' }).click();
    const schedule = page.getByRole('dialog', { name: 'Schedule deletion' });
    await schedule.getByLabel('Retention window (days)').fill('7');
    await fillVisible(schedule.getByLabel('Why is this customer being deleted?'), 'Visible certification retention test');
    await schedule.getByRole('button', { name: 'Start the retention clock' }).click();
    await expect(page.getByText(/Deletion scheduled — purge allowed from/)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Purge tenant records' })).toBeDisabled();

    // Restore is the customer-safe recovery path. It must cancel the pending deletion
    // atomically before access can be resumed.
    await page.getByRole('button', { name: 'Restore to suspended' }).click();
    await confirmReason(page, 'Restore tenant', 'Customer cancellation received', 'Restore');
    await expect(page.getByText('NotScheduled', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Resume tenant' }).click();
    await confirmReason(page, 'Resume tenant', 'Customer access restored safely', 'Resume');
    await expect(page.getByRole('button', { name: 'Suspend tenant' })).toBeVisible();

    // Export is deliberately last so a contract failure does not hide the retention and
    // recovery evidence above. A 4xx here is still a failed certification, never an
    // expected denial for an Owner.
    const exportResponse = page.waitForResponse((response) =>
      response.url().includes('/offboarding/export') && response.request().method() === 'POST');
    const download = page.waitForEvent('download');
    await page.getByRole('button', { name: "Export this tenant's data" }).click();
    const exportDialog = page.getByRole('dialog', { name: "Export this tenant's data" });
    await fillVisible(exportDialog.getByLabel('Why is this being done?'), 'Customer offboarding export');
    await fillVisible(exportDialog.getByLabel(`Type ${journeyTenant.name} to confirm`), journeyTenant.name);
    await exportDialog.getByRole('button', { name: 'Generate export' }).click();
    expect((await exportResponse).ok(), 'The governed export request must succeed.').toBeTruthy();
    expect((await download).suggestedFilename()).toMatch(/\.json$/);
    await expect(page.getByText(/Export downloaded/)).toBeVisible();
    await expect(page.getByRole('columnheader', { name: 'SHA-256' })).toBeVisible();

    assertNoFailures();
  });

  test('places a legal hold, proves destructive denial, and releases it', async ({ page }) => {
    const assertNoFailures = observeBrowserAndApiFailures(page);
    await signInAsPlatformAdmin(page);
    await page.getByRole('link', { name: 'Tenants', exact: true }).click();
    await fillVisible(page.getByLabel('Search tenants'), journeyTenant.name);
    await page.getByRole('row').filter({ hasText: journeyTenant.name }).getByText(journeyTenant.name, { exact: true }).click();
    const tenantId = new URL(page.url()).pathname.split('/').filter(Boolean).at(-1);
    expect(tenantId, 'The selected tenant URL must expose its durable identifier.').toMatch(/^\d+$/);
    await page.getByRole('tab', { name: 'Lifecycle' }).click();

    await page.getByRole('button', { name: 'Place legal hold' }).click();
    const place = page.getByRole('dialog', { name: 'Place legal hold' });
    await place.getByLabel('Scope').fill('AllTenantData');
    await place.getByLabel('Authority').fill(`Legal counsel case ${journeyId}`);
    await place.getByLabel('Evidence reference').fill(`CASE-${journeyId}`);
    await fillVisible(place.getByLabel('Preservation reason'), 'Visible certification preservation order');
    const placementResponse = page.waitForResponse((response) =>
      response.url().endsWith(`/api/platform/tenants/${tenantId}/legal-holds`)
      && response.request().method() === 'POST');
    await place.getByRole('button', { name: 'Place hold', exact: true }).click();
    const placement = await placementResponse;
    expect(placement.ok(), `Legal-hold placement returned HTTP ${placement.status()}.`).toBeTruthy();

    const holdAlert = page.getByRole('alert').filter({ hasText: 'Legal hold active' });
    await expect(holdAlert).toContainText(`CASE-${journeyId}`);
    await page.getByRole('button', { name: 'Suspend tenant' }).click();
    await confirmReason(page, 'Suspend tenant', 'Suspend for governed erasure review', 'Suspend');
    await expect(page.getByRole('button', { name: 'Erase personal data' })).toBeDisabled();

    // The UI fails closed, while this browser-context call proves the server independently
    // refuses the same destructive operation. It uses the real platform session and API.
    const token = await page.evaluate(() => sessionStorage.getItem('nexora_platform_token'));
    expect(token, 'The platform browser session must retain its access token.').toBeTruthy();
    const denial = await page.request.post(
      new URL(`/api/platform/tenants/${tenantId}/offboarding/erase-personal-data`, apiURL).toString(),
      {
        headers: { Authorization: `Bearer ${token}` },
        data: {
          reason: 'Certification attempt must be refused by legal hold',
          confirmation: journeyTenant.name,
        },
      },
    );
    expect(denial.status(), 'Active legal hold must produce an explicit conflict.').toBe(409);
    expect(await denial.text()).toMatch(/blocked by an active legal hold/i);

    await holdAlert.getByRole('button', { name: 'Release hold' }).click();
    const releaseResponse = page.waitForResponse((response) =>
      response.url().includes(`/api/platform/tenants/${tenantId}/legal-holds/`)
      && response.url().endsWith('/release')
      && response.request().method() === 'POST');
    await confirmReason(page, 'Release legal hold', 'Counsel confirmed preservation duty ended', 'Release hold');
    const release = await releaseResponse;
    expect(release.ok(), `Legal-hold release returned HTTP ${release.status()}.`).toBeTruthy();
    await expect(page.getByRole('alert').filter({ hasText: 'Legal hold active' })).toHaveCount(0);

    const holds = await page.request.get(
      new URL(`/api/platform/tenants/${tenantId}/legal-holds`, apiURL).toString(),
      { headers: { Authorization: `Bearer ${token}` } },
    );
    expect(holds.ok(), 'Legal-hold receipt list must remain readable after release.').toBeTruthy();
    const holdReceipts = await holds.json() as Array<{
      evidenceReference: string;
      isActive: boolean;
      releasedOn: string | null;
    }>;
    expect(holdReceipts).toEqual(expect.arrayContaining([
      expect.objectContaining({ evidenceReference: `CASE-${journeyId}`, isActive: false }),
    ]));

    await page.getByRole('button', { name: 'Resume tenant' }).click();
    await confirmReason(page, 'Resume tenant', 'Legal-hold certification completed safely', 'Resume');
    await expect(page.getByRole('button', { name: 'Suspend tenant' })).toBeVisible();
    assertNoFailures();
  });

  test('governs tenant AI policy and an exact provider authorization as Owner', async ({ page }) => {
    const assertNoFailures = observeBrowserAndApiFailures(page);
    await signInAsPlatformAdmin(page);
    await page.getByRole('link', { name: 'Tenants', exact: true }).click();
    await fillVisible(page.getByLabel('Search tenants'), journeyTenant.name);
    await page.getByRole('row').filter({ hasText: journeyTenant.name }).getByText(journeyTenant.name, { exact: true }).click();
    await page.getByRole('tab', { name: 'AI governance' }).click();
    await expect(page.getByText('Owner authority only')).toBeVisible();

    await page.getByRole('button', { name: 'Edit policy' }).click();
    const policy = page.getByRole('dialog', { name: 'Edit tenant AI policy' });
    await policy.getByLabel('External processing allowed').check();
    await policy.getByLabel('Redaction required').check();
    await policy.getByLabel('Allowed provider').fill('certification-provider');
    await policy.getByLabel('Allowed model').fill('certification-model-v1');
    await policy.getByLabel('External dependency ceiling (%)').fill('10');
    await fillVisible(policy.getByLabel('Change reason'), 'Visible Owner AI governance certification');
    const policyResponse = page.waitForResponse((response) =>
      response.url().endsWith('/ai-policy') && response.request().method() === 'PUT');
    await policy.getByRole('button', { name: 'Save policy' }).click();
    expect((await policyResponse).ok(), 'The Owner AI policy mutation must succeed.').toBeTruthy();
    await expect(page.getByText('certification-provider / certification-model-v1')).toBeVisible();
    await expect(page.getByText('Redaction required; review')).toBeVisible();

    await page.getByRole('button', { name: 'Authorize provider' }).click();
    const authorization = page.getByRole('dialog', { name: 'Authorize external AI provider' });
    await authorization.getByLabel('Provider').fill('certification-provider');
    await authorization.getByLabel('Endpoint').fill('https://ai-certification.invalid/v1');
    await authorization.getByLabel('Model').fill('certification-model-v1');
    await authorization.getByLabel('Allowed purposes').fill('RfqExtraction');
    await fillVisible(authorization.getByLabel('Justification / approval reference'), 'Security approval CERT-AI-2026');
    const authorizeResponse = page.waitForResponse((response) =>
      response.url().endsWith('/ai-providers') && response.request().method() === 'POST');
    await authorization.getByRole('button', { name: 'Authorize provider' }).click();
    expect((await authorizeResponse).ok(), 'The exact provider authorization must succeed.').toBeTruthy();
    // The server deliberately canonicalizes authorization to the exact HTTPS origin;
    // path fragments never broaden or distinguish an egress trust boundary.
    const providerRow = page.getByRole('row').filter({ hasText: 'https://ai-certification.invalid' });
    await expect(providerRow).toContainText('Active');

    await providerRow.getByRole('button', { name: 'Revoke' }).click();
    const revokeResponse = page.waitForResponse((response) =>
      response.url().endsWith('/revoke') && response.request().method() === 'POST');
    await confirmReason(page, 'Revoke provider authorization', 'Certification cleanup and immediate fence', 'Revoke authorization');
    expect((await revokeResponse).ok(), 'Provider revocation must succeed.').toBeTruthy();
    await expect(providerRow).toContainText('Revoked');
    assertNoFailures();
  });
});
