import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { createHmac } from 'node:crypto';
import { requireEnv } from './support/environment';

const apiURL = process.env.E2E_API_URL ?? '';
const journeyId = Date.now().toString(36);
const journeyTenant = {
  name: process.env.E2E_VISIBLE_TENANT_NAME ?? `Visible Certification ${journeyId}`,
  email: process.env.E2E_VISIBLE_TENANT_EMAIL ?? `admin-${journeyId}@visible-certification.example`,
  password: process.env.E2E_VISIBLE_TENANT_PASSWORD ?? 'Visible-Certification-2026!',
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

const decodeBase32 = (value: string): Buffer => {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  const bits = value.toUpperCase().replace(/[^A-Z2-7]/g, '')
    .split('').map((character) => alphabet.indexOf(character).toString(2).padStart(5, '0')).join('');
  return Buffer.from((bits.match(/.{8}/g) ?? []).map((byte) => Number.parseInt(byte, 2)));
};

const currentTotp = (secret: string, now = Date.now()): string => {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(now / 30_000)));
  const digest = createHmac('sha1', decodeBase32(secret)).update(counter).digest();
  const offset = digest[digest.length - 1] & 0x0f;
  const value = (digest.readUInt32BE(offset) & 0x7fffffff) % 1_000_000;
  return value.toString().padStart(6, '0');
};

// Treat the process-start step as already spent: a prior interrupted certification process may
// have submitted it, and the server correctly remembers that replay fence.
const lastSubmittedTotpStep = new Map<string, number>();

const nextUnusedTotp = async (page: Page, secret: string): Promise<string> => {
  let now = Date.now();
  let step = Math.floor(now / 30_000);
  const lastStep = lastSubmittedTotpStep.get(secret) ?? Math.floor(Date.now() / 30_000);
  if (step <= lastStep) {
    // The server intentionally fences replay of a TOTP time step across sessions. Serial
    // Playwright tests create fresh browser contexts faster than the 30-second window, so wait
    // visibly for the next genuine code instead of weakening the replay control or using mocks.
    await page.waitForTimeout(((lastStep + 1) * 30_000) - now + 500);
    now = Date.now();
    step = Math.floor(now / 30_000);
  }
  lastSubmittedTotpStep.set(secret, step);
  return currentTotp(secret, now);
};

async function signInAsPlatformAdmin(page: Page, actor: 'maker' | 'checker' = 'maker'): Promise<void> {
  const credentials = actor === 'maker' ? (() => {
    const value = requireEnv('Visible Google Chrome Platform Admin certification', 'E2E_PLATFORM_ADMIN_EMAIL', 'E2E_PLATFORM_ADMIN_PASSWORD', 'E2E_PLATFORM_ADMIN_TOTP_SECRET');
    return { email: value.E2E_PLATFORM_ADMIN_EMAIL, password: value.E2E_PLATFORM_ADMIN_PASSWORD, secret: value.E2E_PLATFORM_ADMIN_TOTP_SECRET };
  })() : (() => {
    const value = requireEnv('Visible Google Chrome Platform checker certification', 'E2E_PLATFORM_CHECKER_EMAIL', 'E2E_PLATFORM_CHECKER_PASSWORD', 'E2E_PLATFORM_CHECKER_TOTP_SECRET');
    return { email: value.E2E_PLATFORM_CHECKER_EMAIL, password: value.E2E_PLATFORM_CHECKER_PASSWORD, secret: value.E2E_PLATFORM_CHECKER_TOTP_SECRET };
  })();

  await page.goto('/platform/login');
  await page.evaluate(() => sessionStorage.clear());
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Platform Console' })).toBeVisible();

  const email = page.getByRole('textbox', { name: 'Email' });
  await email.click();
  await email.pressSequentially(credentials.email);
  const password = page.getByLabel('Password');
  await password.click();
  await password.pressSequentially(credentials.password);
  await page.getByRole('button', { name: 'Enter Control Plane' }).click();

  const verification = page.getByLabel('6-digit authenticator code');
  const overview = page.getByRole('heading', { name: 'Platform Overview' });
  await expect(verification.or(overview)).toBeVisible({ timeout: 20_000 });
  if (await verification.isVisible()) {
    await verification.fill(await nextUnusedTotp(page, credentials.secret));
    await page.getByRole('button', { name: 'Verify and enter' }).click();
  }

  await expect(overview).toBeVisible({ timeout: 20_000 });
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

async function platformBearer(page: Page): Promise<string> {
  return page.evaluate(() => {
    const token = sessionStorage.getItem('nexora_platform_token');
    if (!token) throw new Error('The authenticated platform session did not contain a bearer token.');
    return token;
  });
}

async function expectApiOk(response: APIResponse, action: string) {
  if (!response.ok()) {
    throw new Error(`${action} failed with HTTP ${response.status()}: ${await response.text()}`);
  }
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

  test('shares a live platform session across Chrome tabs and propagates server-authoritative logout', async ({ page }) => {
    test.setTimeout(120_000);
    const assertFirstTabHasNoFailures = observeBrowserAndApiFailures(page);
    await signInAsPlatformAdmin(page);
    const issuedToken = await platformBearer(page);

    // Platform tokens remain tab-session scoped at rest. The second real Chrome tab has
    // empty sessionStorage and asks the already authenticated same-origin tab for the live
    // session through the nonce-targeted BroadcastChannel handshake.
    const secondTab = await page.context().newPage();
    await secondTab.addInitScript(() => {
      (window as Window & { __initialPlatformToken?: string | null }).__initialPlatformToken =
        sessionStorage.getItem('nexora_platform_token');
    });
    const assertSecondTabHasNoFailures = observeBrowserAndApiFailures(secondTab);
    await secondTab.goto('/platform/overview');
    expect(await secondTab.evaluate(() =>
      (window as Window & { __initialPlatformToken?: string | null }).__initialPlatformToken),
    'A newly-opened Chrome tab must begin without a persisted platform bearer.').toBeNull();
    await expect(secondTab.getByRole('heading', { name: 'Platform Overview' })).toBeVisible({ timeout: 20_000 });
    await expect.poll(() => secondTab.evaluate(() => sessionStorage.getItem('nexora_platform_token')))
      .toBe(issuedToken);

    for (const tab of [page, secondTab]) {
      expect(await tab.evaluate(() => localStorage.getItem('nexora_platform_token')),
        'The platform bearer must never be persisted in localStorage.').toBeNull();
    }

    // Logout is performed through the real UI. The first tab revokes the database-backed
    // session and broadcasts only the local clear occurrence; the second tab must return to
    // the login screen. Replaying the captured token against the real API must independently
    // fail, proving the browser bridge cannot bypass server revocation authority.
    await page.getByRole('button', { name: 'Sign out of platform console' }).click();
    await expect(page.getByRole('heading', { name: 'Platform Console' })).toBeVisible({ timeout: 20_000 });
    await expect(secondTab.getByRole('heading', { name: 'Platform Console' })).toBeVisible({ timeout: 20_000 });
    await expect.poll(() => secondTab.evaluate(() => sessionStorage.getItem('nexora_platform_token'))).toBeNull();

    const revoked = await page.request.get(new URL('/api/platform/tenants', apiURL).toString(), {
      headers: { Authorization: `Bearer ${issuedToken}` },
    });
    expect(revoked.status(), 'A platform JWT replayed after UI logout must be rejected by the session ledger.').toBe(401);

    await secondTab.close();
    assertFirstTabHasNoFailures();
    assertSecondTabHasNoFailures();
  });

  test('saves, resumes, edits, and reloads a first-field provisioning draft', async ({ page }) => {
    const assertNoFailures = observeBrowserAndApiFailures(page);
    const draftName = `Visible Draft ${journeyId}`;
    await signInAsPlatformAdmin(page);
    await page.getByRole('link', { name: 'Tenants', exact: true }).click();
    await page.getByRole('button', { name: 'Create Company' }).click();

    let wizard = page.getByRole('dialog', { name: 'Create a company workspace' });
    await fillVisible(wizard.getByLabel('Organization name'), draftName);
    await wizard.getByRole('button', { name: 'Save draft' }).click();
    await expect(page.getByText(new RegExp(`Draft .*${draftName}.* saved`))).toBeVisible();
    await wizard.getByRole('button', { name: 'Cancel', exact: true }).click();

    await page.reload();
    await page.getByRole('button', { name: 'Create Company' }).click();
    wizard = page.getByRole('dialog', { name: 'Create a company workspace' });
    await wizard.getByRole('button', { name: 'Resume one' }).click();
    const draftRow = wizard.getByText(draftName, { exact: true }).locator('..').locator('..');
    await draftRow.getByRole('button', { name: 'Resume', exact: true }).click();
    await expect(wizard.getByLabel('Organization name')).toHaveValue(draftName);
    await fillVisible(wizard.getByLabel('Industry'), 'Aerospace');
    await wizard.getByRole('button', { name: 'Update draft' }).click();
    await expect(page.getByText(new RegExp(`Draft .*${draftName}.* saved`))).toBeVisible();
    await wizard.getByRole('button', { name: 'Cancel', exact: true }).click();

    await page.reload();
    await page.getByRole('button', { name: 'Create Company' }).click();
    wizard = page.getByRole('dialog', { name: 'Create a company workspace' });
    await wizard.getByRole('button', { name: 'Resume one' }).click();
    await wizard.getByText(draftName, { exact: true }).locator('..').locator('..')
      .getByRole('button', { name: 'Resume', exact: true }).click();
    await expect(wizard.getByLabel('Industry')).toHaveValue('Aerospace');
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

  test('rejects incomplete cutover then certifies statement, invoice, and full payment with two MFA Owners', async ({ page }) => {
    test.setTimeout(360_000);
    const assertNoFailures = observeBrowserAndApiFailures(page);
    const now = new Date();
    const period = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - 1, 1))
      .toISOString().slice(0, 7);
    const api = (path: string) => new URL(path, apiURL).toString();

    await signInAsPlatformAdmin(page, 'maker');
    await page.getByRole('link', { name: 'Tenants', exact: true }).click();
    await fillVisible(page.getByLabel('Search tenants'), journeyTenant.name);
    await page.getByRole('row').filter({ hasText: journeyTenant.name })
      .getByText(journeyTenant.name, { exact: true }).click();
    const tenantId = new URL(page.url()).pathname.split('/').at(-1);
    expect(tenantId, 'The tenant-detail URL must carry the authoritative tenant id.').toBeTruthy();

    let token = await platformBearer(page);
    const headers = () => ({ Authorization: `Bearer ${token}` });
    const termsResponse = await page.request.put(
      api(`/api/platform/billing/tenants/${tenantId}/commercial-terms`),
      {
        headers: headers(),
        data: {
          billingMode: 'Billable', billingModeReason: null, trialEndsOn: null,
          billingStartsOn: `${period}-01T00:00:00.000Z`,
        },
      },
    );
    await expectApiOk(termsResponse, 'Closed-period billing-start correction');
    const rateCardResponse = await page.request.post(api('/api/platform/billing/rate-cards'), {
      headers: headers(),
      data: {
        code: `visible-certified-${journeyId}`,
        currency: 'USD',
        effectiveFromUtc: '2020-01-01T00:00:00.000Z',
        effectiveToUtc: null,
        isActive: true,
        lines: [
          { meterKey: 'documents', includedQuantity: 0, unitPrice: 1, unit: 'document', tierNote: null },
          { meterKey: 'ai.tokens.external', includedQuantity: 0, unitPrice: 0.01, unit: '1K tokens', tierNote: null },
          { meterKey: 'seats', includedQuantity: 0, unitPrice: 1, unit: 'seat', tierNote: null },
        ],
      },
    });
    await expectApiOk(rateCardResponse, 'Certified rate-card creation');
    const rateCard = await rateCardResponse.json() as { id: number | string };
    const pinResponse = await page.request.put(api(`/api/platform/billing/tenants/${tenantId}/rate-card`), {
      headers: headers(), data: { rateCardId: Number(rateCard.id), reason: 'Visible paid-journey certification' },
    });
    await expectApiOk(pinResponse, 'Tenant rate-card pin');
    const proposalResponse = await page.request.post(
      api(`/api/platform/billing/tenants/${tenantId}/document-coverage/proposals`),
      { headers: headers(), data: { period, midPeriodCutoverUtc: null, reason: 'Visible coverage certification proposal' } },
    );
    await expectApiOk(proposalResponse, 'Document-coverage proposal');
    const taxJurisdictionCode = `US-VISIBLE-${journeyId}`;
    const taxRuleResponse = await page.request.post(api('/api/platform/billing/tax-rules'), {
      headers: headers(),
      data: {
        jurisdictionCode: taxJurisdictionCode, buyerCountryCode: 'US', currency: 'USD',
        treatment: 'Certification zero-rated', ratePercent: 0,
        legalAuthorityReference: `Isolated visible-browser certification rule ${journeyId}; not production tax advice`,
        evidenceSha256: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
        effectiveFromUtc: '2020-01-01T00:00:00.000Z', effectiveToUtc: null,
      },
    });
    await expectApiOk(taxRuleResponse, 'Tax-rule maker proposal');
    const taxRule = await taxRuleResponse.json() as { id: number | string };

    await signInAsPlatformAdmin(page, 'checker');
    token = await platformBearer(page);
    const taxApprovalResponse = await page.request.post(
      api(`/api/platform/billing/tax-rules/${taxRule.id}/approve`), { headers: headers() });
    await expectApiOk(taxApprovalResponse, 'Independent tax-rule approval');
    const approvalResponse = await page.request.post(
      api(`/api/platform/billing/tenants/${tenantId}/document-coverage/approve`),
      { headers: headers(), data: { period, reason: 'Independent visible coverage approval' } },
    );
    expect(approvalResponse.status(), 'A cutover with no canonical event evidence must fail closed.').toBe(409);
    expect(await approvalResponse.text()).toContain('Every canonical document event');

    await signInAsPlatformAdmin(page, 'maker');
    await page.goto(`/platform/tenants/${tenantId}?tab=commercial`);
    await page.getByLabel('Period').fill(period);
    await expect(page.getByText('Ready to finalize', { exact: true })).toBeVisible({ timeout: 20_000 });

    const computeResponse = page.waitForResponse((response) =>
      response.url().endsWith('/api/platform/billing/statements/compute') && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'Compute', exact: true }).click();
    const computed = await computeResponse;
    expect(computed.ok(), `Statement compute failed with HTTP ${computed.status()}.`).toBeTruthy();
    const statement = await computed.json() as { id: number | string; period: string };
    await expect(page.getByRole('dialog', { name: 'Statement review' })).toBeVisible();
    await page.getByRole('dialog', { name: 'Statement review' }).getByRole('button', { name: 'Close' }).click();

    await signInAsPlatformAdmin(page, 'checker');
    await page.goto(`/platform/tenants/${tenantId}?tab=commercial`);
    const statementRow = page.getByRole('row').filter({ hasText: statement.period });
    await statementRow.getByRole('button', { name: 'Finalize', exact: true }).click();
    const finalizeStatement = page.waitForResponse((response) =>
      response.url().endsWith(`/api/platform/billing/statements/${statement.id}/finalize`));
    await page.getByRole('dialog', { name: 'Finalize this statement' })
      .getByRole('button', { name: 'Finalize permanently' }).click();
    await expectApiOk(await finalizeStatement, 'Independent statement finalization');

    await signInAsPlatformAdmin(page, 'maker');
    await page.goto(`/platform/tenants/${tenantId}?tab=commercial`);
    await page.getByRole('button', { name: 'Create invoice' }).click();
    const invoiceDialog = page.getByRole('dialog', { name: 'Create subscription invoice' });
    await invoiceDialog.getByLabel('Final statement').click();
    await page.getByRole('option', { name: new RegExp(statement.period) }).click();
    await fillVisible(invoiceDialog.getByLabel('Seller legal name'), 'Nexora Visible Certification');
    await fillVisible(invoiceDialog.getByLabel('Seller tax number'), 'US-VISIBLE-2026');
    await fillVisible(invoiceDialog.getByLabel('Tax jurisdiction code'), taxJurisdictionCode);
    await fillVisible(invoiceDialog.getByLabel('Tax treatment'), 'Certification zero-rated');
    const createInvoice = page.waitForResponse((response) =>
      response.url().endsWith('/api/platform/billing/invoices') && response.request().method() === 'POST');
    await invoiceDialog.getByRole('button', { name: 'Create draft' }).click();
    const invoiceResponse = await createInvoice;
    expect(invoiceResponse.ok(), `Invoice creation failed with HTTP ${invoiceResponse.status()}.`).toBeTruthy();
    const invoice = await invoiceResponse.json() as { id: number | string; invoiceNumber: string; totalAmount: number };

    await signInAsPlatformAdmin(page, 'checker');
    await page.goto(`/platform/tenants/${tenantId}?tab=commercial`);
    const invoiceRow = page.getByRole('row').filter({ hasText: invoice.invoiceNumber });
    await invoiceRow.getByRole('button', { name: 'Finalize', exact: true }).click();
    const finalizeInvoice = page.waitForResponse((response) =>
      response.url().endsWith(`/api/platform/billing/invoices/${invoice.id}/finalize`));
    await page.getByRole('dialog', { name: 'Finalize invoice' })
      .getByRole('button', { name: 'Finalize invoice' }).click();
    const finalizedInvoiceResponse = await finalizeInvoice;
    await expectApiOk(finalizedInvoiceResponse, 'Independent invoice finalization');
    const finalizedInvoice = await finalizedInvoiceResponse.json() as {
      id: number | string; invoiceNumber: string; totalAmount: number;
    };

    await signInAsPlatformAdmin(page, 'maker');
    await page.goto(`/platform/tenants/${tenantId}?tab=commercial`);
    const paidRow = page.getByRole('row').filter({ hasText: finalizedInvoice.invoiceNumber });
    await paidRow.getByRole('button', { name: 'Record payment' }).click();
    const paymentDialog = page.getByRole('dialog', { name: 'Record payment' });
    await fillVisible(paymentDialog.getByLabel('Amount'), String(finalizedInvoice.totalAmount));
    await fillVisible(paymentDialog.getByLabel('External payment reference'), `VISIBLE-${journeyId}`);
    const paymentResponse = page.waitForResponse((response) =>
      response.url().endsWith(`/api/platform/billing/invoices/${invoice.id}/payments`));
    await paymentDialog.getByRole('button', { name: 'Post' }).click();
    expect((await paymentResponse).ok(), 'Full payment posting must succeed.').toBeTruthy();
    await expect(paidRow.getByText('Paid', { exact: true })).toBeVisible();
    assertNoFailures();
  });

  test('blocks lifecycle before authoritative activation and exports tenant data', async ({ page }) => {
    const assertNoFailures = observeBrowserAndApiFailures(page);
    await signInAsPlatformAdmin(page);
    await page.getByRole('link', { name: 'Tenants', exact: true }).click();
    await fillVisible(page.getByLabel('Search tenants'), journeyTenant.name);
    await page.getByRole('row').filter({ hasText: journeyTenant.name }).getByText(journeyTenant.name, { exact: true }).click();
    await page.getByRole('tab', { name: 'Lifecycle' }).click();

    await expect(page.getByText('Provisioning', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Suspend tenant' })).toHaveCount(0);
    await expect(page.getByText('NotScheduled', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Schedule deletion' })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Purge tenant records' })).toBeDisabled();

    // A governed export remains available while activation is blocked so the operator can
    // return the submitted tenant data without first enabling customer access.
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

  test('changes plan and shows the new server-owned entitlements from the real catalogue', async ({ page }) => {
    const assertNoFailures = observeBrowserAndApiFailures(page);
    await signInAsPlatformAdmin(page);
    await page.getByRole('link', { name: 'Tenants', exact: true }).click();
    await fillVisible(page.getByLabel('Search tenants'), journeyTenant.name);
    await page.getByRole('row').filter({ hasText: journeyTenant.name }).getByText(journeyTenant.name, { exact: true }).click();
    await page.getByRole('tab', { name: 'Commercial' }).click();
    await page.getByRole('button', { name: 'Change plan' }).click();
    const changePlan = page.getByRole('dialog', { name: 'Change plan' });
    await changePlan.getByRole('combobox', { name: 'Plan' }).click();
    await page.getByRole('option', { name: 'Enterprise (enterprise)', exact: true }).click();
    await fillVisible(changePlan.getByLabel('Reason'), 'Visible certification plan upgrade');
    const planChangeResponse = page.waitForResponse((response) =>
      response.url().endsWith('/plan') && response.request().method() === 'PUT');
    await changePlan.getByRole('button', { name: 'Assign plan' }).click();
    await expectApiOk(await planChangeResponse, 'Tenant plan change');

    const plansResponse = page.waitForResponse((response) =>
      response.url().endsWith('/api/platform/plans') && response.request().method() === 'GET');
    await page.getByRole('tab', { name: 'Entitlements' }).click();
    expect((await plansResponse).ok(), 'The real plan catalogue must be readable.').toBeTruthy();
    await expect(page.getByRole('heading', { name: 'Enterprise entitlements' })).toBeVisible();
    await expect(page.getByText('Concurrent extractions')).toBeVisible();
    await expect(page.getByText('Documents per month')).toBeVisible();
    await expect(page.getByText('Seats', { exact: true })).toBeVisible();
    await expect(page.getByText(/contracted plan capacity, not live consumption/i)).toBeVisible();
    assertNoFailures();
  });

  test('fails closed on tenant login and activation while required control evidence is absent', async ({ page }) => {
    const assertNoFailures = observeBrowserAndApiFailures(page);
    await signInAsPlatformAdmin(page);
    await page.getByRole('link', { name: 'Tenants', exact: true }).click();
    await fillVisible(page.getByLabel('Search tenants'), journeyTenant.name);
    await page.getByRole('row').filter({ hasText: journeyTenant.name }).getByText(journeyTenant.name, { exact: true }).click();
    const tenantId = new URL(page.url()).pathname.split('/').filter(Boolean).at(-1);
    expect(tenantId, 'The selected tenant URL must expose its durable identifier.').toMatch(/^\d+$/);

    const assetsResponse = page.waitForResponse((response) =>
      response.url().endsWith('/data-assets') && response.request().method() === 'GET');
    const decisionResponse = page.waitForResponse((response) =>
      response.url().endsWith('/data-assets/activation-data-decision') && response.request().method() === 'GET');
    const activationResponse = page.waitForResponse((response) =>
      response.url().endsWith('/activation/decision') && response.request().method() === 'GET');
    const recoveryResponse = page.waitForResponse((response) =>
      response.url().endsWith('/data-recovery/evidence') && response.request().method() === 'GET');
    const deletionResponse = page.waitForResponse((response) =>
      response.url().endsWith('/data-recovery/deletion-certification') && response.request().method() === 'GET');
    await page.getByRole('tab', { name: 'Data & storage' }).click();

    expect((await assetsResponse).ok(), 'The Owner-only tenant data-asset registry must be readable.').toBeTruthy();
    expect((await decisionResponse).ok(), 'The activation data decision must be readable.').toBeTruthy();
    expect((await activationResponse).ok(), 'The authoritative tenant activation decision must be readable.').toBeTruthy();
    expect((await recoveryResponse).ok(), 'The immutable recovery evidence list must be readable.').toBeTruthy();
    expect((await deletionResponse).ok(), 'The deletion-certification decision must be readable.').toBeTruthy();
    await expect(page.getByText('Authoritative tenant activation')).toBeVisible();
    await expect(page.getByText(/tenant-activation\/2026-08-08\.v1/)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Activate tenant' })).toBeVisible();
    await expect(page.getByText('Activation data decision')).toBeVisible();
    await expect(page.getByText('Decision boundary')).toBeVisible();
    await expect(page.getByText(/does not activate the tenant/i)).toBeVisible();
    await expect(page.getByText('Recovery and non-resurrection evidence')).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Deletion certification', exact: true })).toBeVisible();
    await expect(page.getByText('Certification boundary')).toBeVisible();
    await expect(page.getByText(/unknown.*blocker|registered boundaries.*known to Nexora/i)).toBeVisible();
    await expect(page.getByText(/console never claims it performed a backup or restore/i)).toBeVisible();

    // A password-path founding admin is persisted during provisioning. It must still receive no
    // tenant token until this policy passes; this was a real activation-bypass defect.
    const tenantLogin = await page.request.post(new URL('/api/Auth/Login', apiURL).toString(), {
      data: { email: journeyTenant.email, password: journeyTenant.password },
    });
    expect(tenantLogin.status(), 'An unactivated tenant administrator must be denied a token.').toBe(403);
    expect(await tenantLogin.text()).toMatch(/still provisioning.*authoritative activation/i);

    const activation = await page.request.post(
      new URL(`/api/platform/tenants/${tenantId}/activation`, apiURL).toString(),
      { headers: { Authorization: `Bearer ${await platformBearer(page)}` } },
    );
    expect(activation.status(), 'Activation without truthful control artifacts must fail closed.').toBe(409);
    const activationBody = await activation.text();
    expect(activationBody).toContain('security.privileged-mfa-policy');
    expect(activationBody).toContain('data.residency-isolation');
    expect(activationBody).toContain('integrations.mandatory');
    assertNoFailures();
  });

  test('places and releases a legal hold while destructive actions remain unavailable', async ({ page }) => {
    test.setTimeout(120_000);
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
    await expect(page.getByRole('button', { name: 'Erase personal data' })).toBeDisabled();

    // The UI fails closed, while this browser-context call proves the server independently
    // refuses the same destructive operation. It uses the real platform session and API.
    let token = await page.evaluate(() => sessionStorage.getItem('nexora_platform_token'));
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
    expect(denial.status(), 'The server must independently refuse erasure.').toBe(409);
    expect(await denial.text()).toMatch(/cannot be erased while the tenant is Provisioning/i);

    await signInAsPlatformAdmin(page, 'checker');
    await page.goto(`/platform/tenants/${tenantId}?tab=lifecycle`);
    token = await page.evaluate(() => sessionStorage.getItem('nexora_platform_token'));
    const checkerHoldAlert = page.getByRole('alert').filter({ hasText: 'Legal hold active' });
    await checkerHoldAlert.getByRole('button', { name: 'Release hold' }).click();
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

    await expect(page.getByText('Provisioning', { exact: true })).toBeVisible();
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

  test('recovers one explicitly authorized real platform dead-letter through visible Chrome', async ({ page }) => {
    const tenantId = process.env.E2E_PLATFORM_DLQ_TENANT_ID;
    const itemId = process.env.E2E_PLATFORM_DLQ_ITEM_ID;
    test.skip(!tenantId || !itemId,
      'Requires an isolated real dead-letter fixture via E2E_PLATFORM_DLQ_TENANT_ID and E2E_PLATFORM_DLQ_ITEM_ID.');
    const assertNoFailures = observeBrowserAndApiFailures(page);
    await signInAsPlatformAdmin(page);
    await page.goto(`/platform/pipeline?tenant=${tenantId}`);
    await page.getByRole('tab', { name: /Dead-Letter/ }).click();

    const row = page.getByRole('row').filter({ hasText: itemId! });
    await expect(row).toHaveCount(1);
    await row.getByRole('button', { name: 'Recover' }).click();
    const dialog = page.getByRole('dialog', { name: 'Recover dead-letter item' });
    await expect(dialog).toContainText(`Tenant:`);
    await expect(dialog).toContainText(`(${tenantId})`);
    await expect(dialog).toContainText('Queue: extraction');
    await expect(dialog).toContainText(`Item: ${itemId}`);
    await expect(dialog.getByLabel('Idempotency key')).toHaveValue(new RegExp(`^platform-dlq-${itemId}-`));
    await dialog.getByLabel('Reason').fill(
      'Visible Chrome recovery after immutable evidence and dependency verification.',
    );
    const recovery = page.waitForResponse((response) =>
      response.url().endsWith(`/api/platform/tenants/${tenantId}/dead-letters/recover`)
      && response.request().method() === 'POST');
    await dialog.getByRole('button', { name: 'Queue governed retry' }).click();
    expect((await recovery).status()).toBe(200);
    await expect(page.getByText(/queued for governed retry.*Audit evidence refreshed/i)).toBeVisible();
    await expect(row).toHaveCount(0);
    assertNoFailures();
  });
});
