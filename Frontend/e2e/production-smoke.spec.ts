/**
 * ============================================================================
 * PRODUCTION SMOKE SUITE — real browser, LIVE deployment. No mocks, no fixtures.
 * ============================================================================
 *
 * Targets
 *   Frontend : https://nexora1-ai.vercel.app   (override: E2E_PROD_FRONTEND_URL)
 *   Backend  : https://nexora-fyjw.onrender.com (override: E2E_PROD_BACKEND_URL)
 *
 * Invocation (no npm script — run directly from Frontend/):
 *
 *   E2E_PROD_EMAIL='ops@example.com' \
 *   E2E_PROD_PASSWORD='********' \
 *   npx playwright test --config playwright.production.config.ts
 *
 * Optional:
 *   E2E_PROD_BUSINESS_UNIT='Acme Trading'  # organization NAME, only needed when
 *                                          # the account belongs to multiple
 *                                          # organizations (otherwise first is picked)
 *
 * Without E2E_PROD_EMAIL / E2E_PROD_PASSWORD every test SKIPS with a clear
 * message — the suite is always listable and runnable without secrets:
 *
 *   npx playwright test --config playwright.production.config.ts --list
 *
 * Safety contract: the suite is tenant-safe and non-destructive. It creates at
 * most ONE clearly-labelled test Lead. It does not commit participation or
 * promote an RFQ against live customer data; that mutation belongs to the
 * disposable governed-pilot fixture.
 *
 * Journey covered (serial — later tests reuse artifacts from earlier ones):
 *   01  Real UI login → dashboard shell renders, zero console errors on load.
 *   02  Leads list renders; the new "Ingested" column is feature-detected and
 *       soft-logged if not yet deployed (the single allowed conditional).
 *   03  Bulk upload of an in-memory .xlsx (3 line items, deterministic native
 *       parser layout) → batch reconciliation reaches a terminal state and
 *       never shows raw exception copy ("SocketException" / "ClamAV").
 *   04  Open the legacy /convert bookmark and prove it resolves to the governed
 *       Decision Workbench with no direct RFQ-creation control.
 *   05  API checks with the browser session's bearer token: AI-trust posture,
 *       /health and /ready both hard-200.
 * ============================================================================
 */

import { expect, test, type Page } from '@playwright/test';
import * as XLSX from 'xlsx';

const BACKEND_URL = process.env.E2E_PROD_BACKEND_URL ?? 'https://nexora-fyjw.onrender.com';
const email = process.env.E2E_PROD_EMAIL ?? '';
const password = process.env.E2E_PROD_PASSWORD ?? '';
const businessUnitName = process.env.E2E_PROD_BUSINESS_UNIT ?? '';
const hasCredentials = Boolean(email && password);

/** One tag per run; appears in file name, RFQ number, buyer, products and notes. */
const RUN_TAG = `E2E-SMOKE-${Date.now()}`;

// Serial-journey state (worker-local; resets wholesale on retry, which reruns
// the entire serial chain from test 01).
let smokeBatchId = '';
let smokeLeadId = 0;

// ─── Helpers ────────────────────────────────────────────────────────────────

/**
 * Real-UI login. Selector conventions mirror e2e/support/login.ts: the password
 * field sits next to a "Show password" reveal button, so it MUST be matched as
 * getByRole('textbox', { name: 'Password', exact: true }). The organization
 * chooser (multi-org accounts only) is a MUI Select, so it is driven by
 * clicking the combobox and picking an option — selectOption() does not apply.
 */
async function loginToProduction(page: Page): Promise<void> {
  await page.goto('/login');
  await page.evaluate(() => {
    localStorage.removeItem('token');
    localStorage.removeItem('userData');
  });
  await page.getByLabel('Email Address').fill(email);
  await page.getByRole('textbox', { name: 'Password', exact: true }).fill(password);
  await page.getByRole('button', { name: 'LOGIN' }).click();

  const continueButton = page.getByRole('button', { name: 'CONTINUE' });
  if (await continueButton.isVisible({ timeout: 3_000 }).catch(() => false)) {
    await page.getByRole('combobox', { name: 'Which organization?' }).click();
    const options = page.getByRole('option');
    if (businessUnitName) {
      await options.filter({ hasText: businessUnitName }).first().click();
    } else {
      await options.first().click();
    }
    await continueButton.click();
  }

  // Render free-tier cold starts make the first authenticated round-trip slow.
  await expect(page).toHaveURL(/\/dashboard(?:\?|$)/, { timeout: 45_000 });
  await expect.poll(() => page.evaluate(() => Boolean(localStorage.getItem('token')))).toBe(true);
}

/** The browser session's bearer token, exactly as axiosInstance uses it. */
async function bearerToken(page: Page): Promise<string> {
  const value = await page.evaluate(() => localStorage.getItem('token'));
  expect(value, 'authenticated session must have a token in localStorage').toBeTruthy();
  return value!;
}

/**
 * Builds a real .xlsx in memory (SheetJS is a first-party dependency) using the
 * exact column aliases the backend's deterministic NativeSpreadsheetParser
 * recognizes (rfqno / buyer / productname / quantity / part number — see
 * Backend .../Services/DocumentIntelligence/NativeSpreadsheetParser.cs), so the
 * upload takes the native-parser path with 3 line items on one logical inquiry.
 */
function buildSmokeWorkbook() {
  const rows = [
    ['rfqno', 'buyer', 'productname', 'quantity', 'part number'],
    [RUN_TAG, `${RUN_TAG} Buyer`, `${RUN_TAG} Widget Alpha`, 4, `${RUN_TAG}-PN-001`],
    [RUN_TAG, `${RUN_TAG} Buyer`, `${RUN_TAG} Widget Bravo`, 2, `${RUN_TAG}-PN-002`],
    [RUN_TAG, `${RUN_TAG} Buyer`, `${RUN_TAG} Widget Charlie`, 7, `${RUN_TAG}-PN-003`],
  ];
  const workbook = XLSX.utils.book_new();
  XLSX.utils.book_append_sheet(workbook, XLSX.utils.aoa_to_sheet(rows), 'RFQ');
  return XLSX.write(workbook, { type: 'buffer', bookType: 'xlsx' }) as Buffer;
}

// ─── The journey ────────────────────────────────────────────────────────────

test.describe.serial('Production smoke — live customer journey', () => {
  test.skip(
    !hasCredentials,
    'Production smoke skipped: set E2E_PROD_EMAIL and E2E_PROD_PASSWORD to run against the live deployment.',
  );

  test('01 login via the real UI renders the dashboard shell with no console errors', async ({ page }) => {
    const consoleErrors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error') consoleErrors.push(message.text());
    });
    page.on('pageerror', (error) => consoleErrors.push(`pageerror: ${error.message}`));

    await loginToProduction(page);

    // Dashboard shell: the main navigation landmark (MainLayout renders
    // component="nav" aria-label="Main") plus the page heading.
    await expect(page.getByRole('navigation', { name: 'Main' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
    expect(consoleErrors, 'no console errors from login through dashboard load').toEqual([]);
  });

  test('02 leads list renders; "Ingested" column is feature-detected', async ({ page }, testInfo) => {
    await loginToProduction(page);
    await page.goto('/procurement/leads/all');

    await expect(page.getByRole('heading', { name: 'Leads', exact: true })).toBeVisible();
    const grid = page.getByRole('grid');
    await expect(grid).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole('columnheader', { name: 'Nexora Serial' })).toBeVisible();

    // Feature detection — the SINGLE allowed conditional in this suite. The
    // "Ingested" column (with its "Loaded after deadline" badge) may not be in
    // the live build yet: soft-log instead of failing when it is absent.
    const ingestedHeader = page.getByRole('columnheader', { name: 'Ingested', exact: true });
    if (await ingestedHeader.count() === 0) {
      const note = 'The "Ingested" column is not present on the live Leads grid — feature not deployed yet (or column virtualized out of the viewport). Soft-logged, not failed.';
      testInfo.annotations.push({ type: 'feature-not-deployed', description: note });
      console.log(`[production-smoke] ${note}`);
    } else {
      await expect(ingestedHeader).toBeVisible();
    }
  });

  test('03 bulk upload reconciles to a terminal state without raw exception copy', async ({ page }) => {
    // Real extraction on live infra: allow for queueing + polling backoff.
    test.setTimeout(360_000);
    await loginToProduction(page);
    await page.goto('/procurement/leads/manual-upload');

    await page.locator('input[type="file"]').setInputFiles({
      name: `${RUN_TAG}.xlsx`,
      mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
      buffer: buildSmokeWorkbook(),
    });
    await expect(page.getByText(`${RUN_TAG}.xlsx`)).toBeVisible();

    const uploadResponse = page.waitForResponse((candidate) =>
      candidate.request().method() === 'POST'
      && candidate.url().includes('/api/Extraction/upload'));
    await page.getByRole('button', { name: 'Queue for reconciliation' }).click();
    const upload = await uploadResponse;
    expect(upload.status(), 'governed upload is accepted for processing').toBe(202);

    const uploadBody = await upload.json() as { batchId?: string; jobs?: unknown[] };
    expect(uploadBody.batchId, 'upload returns a batch reference').toBeTruthy();
    smokeBatchId = uploadBody.batchId!;

    // On success the tray auto-navigates here; on an all-held upload it stays
    // put with a "View batch" link. Direct navigation is honest either way —
    // the batch exists and this is its canonical screen.
    await page.goto(`/procurement/leads/ingestion/${encodeURIComponent(smokeBatchId)}`);
    await expect(page.getByRole('heading', { name: 'Batch reconciliation' })).toBeVisible();

    // Terminal state: every occurrence classified ("Processing complete:"), or
    // the product's honest infrastructure-hold outage notice. Both are
    // acceptable end states for a live smoke; a raw exception dump is not.
    const terminal = page
      .getByText(/Processing complete:/)
      .or(page.getByText('Malware scanning is offline'));
    await expect(terminal.first()).toBeVisible({ timeout: 300_000 });

    // The uploaded file (E2E-SMOKE-labeled) is visible on the reconciliation screen.
    await expect(page.getByText(`${RUN_TAG}.xlsx`).first()).toBeVisible();

    // The owner's bug class: backend exception text leaking into product copy.
    const bodyText = await page.locator('body').innerText();
    expect(bodyText, 'no raw SocketException copy on the reconciliation screen').not.toContain('SocketException');
    expect(bodyText, 'no raw ClamAV copy on the reconciliation screen').not.toContain('ClamAV');

    // Record the lead this batch materialized (if any) for the convert step.
    const token = await bearerToken(page);
    const batch = await page.request.get(
      `${BACKEND_URL}/api/LeadIngestion/batches/${encodeURIComponent(smokeBatchId)}`,
      { headers: { Authorization: `Bearer ${token}` } },
    );
    expect(batch.status(), 'reconciliation read model answers for the batch').toBe(200);
    const batchBody = await batch.json() as { items?: Array<{ leadId?: number | null }> };
    smokeLeadId = (batchBody.items ?? [])
      .map((item) => item.leadId ?? 0)
      .find((leadId) => leadId > 0) ?? 0;
    console.log(`[production-smoke] batch ${smokeBatchId} → lead ${smokeLeadId || '(none materialized)'}`);
  });

  test('04 legacy conversion bookmark resolves to the governed Decision Workbench', async ({ page }) => {
    test.setTimeout(180_000);
    await loginToProduction(page);
    const token = await bearerToken(page);

    // Target lead: the one step 03 created when it materialized, otherwise the
    // newest lead this account can see (explicit max-id so we do not depend on
    // the API's default ordering).
    let targetLeadId = smokeLeadId;
    if (!targetLeadId) {
      const leads = await page.request.get(
        `${BACKEND_URL}/api/Lead?pageNumber=1&pageSize=50`,
        { headers: { Authorization: `Bearer ${token}` } },
      );
      expect(leads.status(), 'lead list API answers').toBe(200);
      const body = await leads.json() as { items?: Array<{ id: number }> };
      targetLeadId = (body.items ?? []).reduce((max, lead) => Math.max(max, lead.id), 0);
    }
    expect(targetLeadId, 'a lead is available to exercise the governed decision flow').toBeGreaterThan(0);
    console.log(`[production-smoke] opening governed workbench for lead ${targetLeadId}${targetLeadId === smokeLeadId ? ' (created by this run)' : ' (newest existing lead)'}`);

    await page.goto(`/procurement/leads/${targetLeadId}/convert`);
    await expect(page).toHaveURL(new RegExp(`/procurement/leads/${targetLeadId}/workbench$`), { timeout: 30_000 });
    await expect(page.getByText('Decision workbench', { exact: true }).first()).toBeVisible({ timeout: 60_000 });
    await expect(page.getByRole('tab', { name: /^1\. Evidence:/ })).toBeVisible();
    await expect(page.getByRole('tab', { name: /^2\. Review transformation:/ })).toBeVisible();
    await expect(page.getByRole('tab', { name: /^3\. Fit & Participation:/ })).toBeVisible();
    await expect(page.getByRole('tab', { name: /^4\. Promote:/ })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Create RFQ', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /Qualify & Create RFQ/i })).toHaveCount(0);
    console.log(`[production-smoke] legacy bookmark resolved to governed workbench for lead ${targetLeadId}; no direct RFQ action was exposed.`);
  });

  test('05 governance and health APIs answer for the browser session', async ({ page }) => {
    await loginToProduction(page);
    const token = await bearerToken(page);
    const authHeaders = { Authorization: `Bearer ${token}` };

    // AI trust posture: external processing is an authorized, governed stance.
    const aiTrust = await page.request.get(
      `${BACKEND_URL}/api/platform-governance/ai-trust`,
      { headers: authHeaders },
    );
    expect(aiTrust.status(), 'AI trust center view is readable with the session token').toBe(200);
    const view = await aiTrust.json() as {
      policy?: { externalProcessingAllowed?: boolean };
      inferencePosture?: string;
    };
    expect(view.policy?.externalProcessingAllowed, 'externalProcessingAllowed is true').toBe(true);
    expect(view.inferencePosture, 'deployment posture is ExternalAuthorized').toBe('ExternalAuthorized');

    // A pilot-ready deployment must be both alive and ready. A closed evidence-storage
    // gate is a release blocker, not a passing smoke outcome.
    const health = await page.request.get(`${BACKEND_URL}/health`);
    expect(health.status(), '/health is 200').toBe(200);

    const ready = await page.request.get(`${BACKEND_URL}/ready`);
    expect(ready.status(), '/ready is 200').toBe(200);
    console.log(`[production-smoke] /health=${health.status()} /ready=${ready.status()}`);
  });
});
