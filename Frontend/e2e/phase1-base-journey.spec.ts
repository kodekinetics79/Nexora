import path from 'node:path';
import { expect, test, type Locator, type Page } from '@playwright/test';
import { loginThroughUi } from './support/login';
import { requireEnv } from './support/environment';

/** Positive pilot outcomes use visible controls. page.request is used only for explicit negative
 * authorization and idempotent replay boundary assertions. */
const env = () => requireEnv('Governed Lead pilot gate',
  'E2E_API_URL', 'E2E_GOLDEN_SALES_EMAIL', 'E2E_GOLDEN_OUTSIDER_EMAIL',
  'E2E_GOLDEN_MANAGER_EMAIL', 'E2E_GOLDEN_DENIED_EMAIL',
  'E2E_GOLDEN_PASSWORD', 'E2E_GOLDEN_PARTIAL_BID_LEAD_ID',
  'E2E_GOLDEN_FULL_BID_LEAD_ID', 'E2E_GOLDEN_FULL_NO_BID_LEAD_ID',
  'E2E_GOLDEN_TENANT_A', 'E2E_GOLDEN_TENANT_B');

let partialRfqId = 0;
let partialRfqNumber = '';
let partialQuoteId = 0;
const partialAmendment = path.resolve('e2e/fixtures/golden-partial-amendment.csv');

async function token(page: Page): Promise<string> {
  const value = await page.evaluate(() => localStorage.getItem('token'));
  if (!value) throw new Error('Authenticated session carries no access token.');
  return value;
}

async function readApi(page: Page, bearer: string | null, path: string) {
  const headers: Record<string, string> = {};
  if (bearer) headers.Authorization = `Bearer ${bearer}`;
  return page.request.get(`${env().E2E_API_URL}${path}`, { headers });
}

async function commandApi(page: Page, bearer: string, path: string, body: unknown, idempotencyKey: string) {
  return page.request.post(`${env().E2E_API_URL}${path}`, {
    data: body,
    headers: { Authorization: `Bearer ${bearer}`, 'Idempotency-Key': idempotencyKey },
  });
}

async function rfqCountForLead(page: Page, bearer: string, leadId: number): Promise<number> {
  const response = await readApi(page, bearer, '/api/Rfq?pageNumber=1&pageSize=250');
  expect(response.ok(), await response.text()).toBeTruthy();
  const payload = await response.json();
  return (payload.items ?? []).filter((row: { leadId?: number }) => row.leadId === leadId).length;
}

async function selectOption(page: Page, combobox: Locator, name: string | RegExp) {
  await combobox.click();
  await page.getByRole('option', { name }).first().click();
}

async function openFreshWorkbench(page: Page, leadId: number, expectedParticipationStatus = 'NONE') {
  const bearer = await token(page);
  expect(await rfqCountForLead(page, bearer, leadId)).toBe(0);
  const response = await readApi(page, bearer, `/api/leads/${leadId}/decision-workbench`);
  expect(response.ok(), await response.text()).toBeTruthy();
  const fixture = await response.json() as {
    participationStatus: string;
    participationVersion?: number | null;
    verificationStatus: string;
    sourceCoverage?: { coveredLines: number; totalLines: number } | null;
    evidence: Array<{ sourceAvailable: boolean }>;
    lines: Array<{ verificationStatus: string }>;
    reasonCodes: Array<{ appliesTo: string[] }>;
  };
  expect(fixture.lines).toHaveLength(6);
  expect(fixture.participationStatus).toBe(expectedParticipationStatus);
  if (expectedParticipationStatus === 'NONE') expect(fixture.participationVersion).toBeNull();
  expect(fixture.verificationStatus).toBe('VERIFIED');
  expect(fixture.sourceCoverage).toEqual({ coveredLines: 6, totalLines: 6 });
  expect(fixture.evidence.length).toBeGreaterThan(0);
  expect(fixture.evidence.every((item) => item.sourceAvailable)).toBeTruthy();
  expect(fixture.lines.every((line) => line.verificationStatus === 'VERIFIED')).toBeTruthy();
  expect(fixture.reasonCodes.some((reason) => reason.appliesTo.includes('NoBid')),
    'fixture must expose a governed no-bid reason').toBeTruthy();

  await page.goto(`/procurement/leads/${leadId}/workbench`);
  await expect(page.getByRole('heading', { name: 'Source evidence' })).toBeVisible();
  await page.getByRole('tab', { name: '3. Fit & Participation' }).click();
  await expect(page.getByRole('heading', { name: 'Fit assessment' })).toBeVisible();
}

async function saveFitThroughControls(page: Page) {
  const panel = page.locator('section[aria-labelledby="fit-assessment-heading"]');
  const choices = panel.getByRole('combobox');
  const count = await choices.count();
  expect(count, 'five governed criteria plus one overall decision').toBe(6);
  for (let index = 0; index < count - 1; index += 1)
    await selectOption(page, choices.nth(index), 'Pass');
  await selectOption(page, choices.nth(count - 1), 'Fit to bid');
  await panel.getByLabel('Assessment rationale').fill(
    'Commercial reviewer confirmed eligibility, capability, delivery, compliance, and commercials.');
  await panel.getByRole('button', { name: 'Save fit assessment' }).click();
  await expect(page.getByText('Fit assessment saved against this Lead revision.')).toBeVisible();
}

async function markAllBidThroughControls(page: Page) {
  await page.getByRole('spinbutton', { name: 'Quantity for line 00010' }).fill('25');
  await page.getByRole('checkbox', { name: 'Select all rows' }).check();
  await page.getByRole('button', { name: 'Mark Bid' }).click();
  const warning = page.getByRole('dialog', { name: 'Acknowledge line warning' });
  if (await warning.isVisible()) {
    await warning.getByLabel('Human review note').fill(
      'Reviewer checked source evidence and confirmed the corrected commercial values.');
    await warning.getByRole('button', { name: 'Acknowledge and mark Bid' }).click();
  }
  await page.getByRole('button', { name: 'Clear selection' }).click();
}

async function chooseNoBidReason(page: Page, dialog: Locator, note: string) {
  await selectOption(page, dialog.getByRole('combobox', { name: 'Governed reason' }), /.+/);
  await dialog.getByLabel('Decision note (optional)').fill(note);
  await dialog.getByRole('button', { name: 'Apply decision' }).click();
}

async function uploadAndWaitForReconciliation(page: Page, file: string): Promise<string> {
  await page.goto('/procurement/leads/intelligence');
  await page.locator('input[type="file"]').setInputFiles(file);
  await page.getByRole('button', { name: 'Queue for reconciliation' }).click();
  await expect(page).toHaveURL(/\/procurement\/leads\/ingestion\/[0-9a-f-]+$/i);
  const batchId = page.url().split('/').at(-1);
  expect(batchId, 'reconciliation route must carry the durable batch id').toBeTruthy();
  await expect(page.getByRole('heading', { name: 'Batch reconciliation' })).toBeVisible();
  await expect(page.getByText(/Processing complete/)).toBeVisible({ timeout: 90_000 });
  return batchId!;
}

async function markPartNoBidThroughControls(page: Page, part: string) {
  const row = page.getByRole('row').filter({ hasText: part });
  await expect(row).toHaveCount(1);
  await row.getByRole('checkbox').check();
  await page.getByRole('button', { name: 'Mark No-bid…' }).click();
  await chooseNoBidReason(page, page.getByRole('dialog', { name: /No-bid for 1 line/ }),
    'This obsolete part is outside the approved commercial scope.');
}

async function markAllNoBidThroughControls(page: Page) {
  await page.getByRole('checkbox', { name: 'Select all rows' }).check();
  await page.getByRole('button', { name: 'Mark No-bid…' }).click();
  await chooseNoBidReason(page, page.getByRole('dialog', { name: /No-bid for 6 lines/ }),
    'Customer request is outside the approved commercial scope for this bid cycle.');
}

async function commitBidScopeAndPromote(page: Page, leadId: number, approved: number): Promise<number> {
  await page.getByRole('button', { name: 'Commit participation' }).click();
  const review = page.getByRole('dialog', { name: 'Commit participation scope' });
  await expect(review.getByText(new RegExp(`committing ${approved} line`))).toBeVisible();
  const commitResponsePromise = page.waitForResponse((response) =>
    response.request().method() === 'PUT'
    && response.url().endsWith(`/api/leads/${leadId}/participation`));
  await review.getByRole('button', { name: 'Commit exact scope' }).click();
  const commitResponse = await commitResponsePromise;
  expect(commitResponse.ok(), await commitResponse.text()).toBeTruthy();
  await expect(page.getByRole('heading', { name: 'RFQ promotion' })).toBeVisible();
  const promotionResponsePromise = page.waitForResponse((response) =>
    response.request().method() === 'POST'
    && response.url().endsWith(`/api/leads/${leadId}/promote-to-rfq`));
  await page.getByRole('button', { name: `Promote ${approved} lines to RFQ` }).click();
  const promotionResponse = await promotionResponsePromise;
  expect(promotionResponse.ok(), await promotionResponse.text()).toBeTruthy();
  await expect(page).toHaveURL(/\/procurement\/rfqs\/view\/\d+$/);
  const rfqId = Number(page.url().split('/').at(-1));
  expect(rfqId).toBeGreaterThan(0);
  await expect(page.getByText(`${approved} approved lines promoted to one RFQ.`)).toBeVisible();
  expect(await rfqCountForLead(page, await token(page), leadId)).toBe(1);

  // Re-enter through the operator route. The durable receipt replaces the creation action, and
  // the read-only RFQ count proves this visible replay cannot create a second formal RFQ.
  await page.goto(`/procurement/leads/${leadId}/workbench`);
  await expect(page.getByText('Already promoted', { exact: true })).toBeVisible();
  await expect(page.getByText(new RegExp(`promoted ${approved} approved line`))).toBeVisible();
  await page.getByRole('tab', { name: '4. Promote' }).click();
  await expect(page.getByRole('heading', { name: 'RFQ promotion' })).toBeVisible();
  await expect(page.getByRole('button', { name: /Promote .* to RFQ/ })).toHaveCount(0);
  expect(await rfqCountForLead(page, await token(page), leadId)).toBe(1);
  return rfqId;
}

test.describe.serial('governed commercial outcomes through visible controls', () => {
  test.beforeEach(async ({ page }) => {
    const values = env();
    await loginThroughUi(page, {
      email: values.E2E_GOLDEN_MANAGER_EMAIL,
      password: values.E2E_GOLDEN_PASSWORD,
      businessUnitId: values.E2E_GOLDEN_TENANT_A,
    });
  });

  test('sales representative may prepare a draft but cannot commit participation', async ({ page }) => {
    const values = env();
    const leadId = Number(values.E2E_GOLDEN_PARTIAL_BID_LEAD_ID);
    await loginThroughUi(page, {
      email: values.E2E_GOLDEN_SALES_EMAIL,
      password: values.E2E_GOLDEN_PASSWORD,
      businessUnitId: values.E2E_GOLDEN_TENANT_A,
    });
    await openFreshWorkbench(page, leadId);
    await saveFitThroughControls(page);
    await markAllBidThroughControls(page);

    const draftRequestPromise = page.waitForRequest((request) =>
      request.method() === 'PUT' && request.url().endsWith(`/api/leads/${leadId}/participation`));
    const draftResponsePromise = page.waitForResponse((response) =>
      response.request().method() === 'PUT'
      && response.url().endsWith(`/api/leads/${leadId}/participation`));
    await page.getByRole('button', { name: 'Save draft for manager review' }).click();
    const draftRequest = await draftRequestPromise;
    const draftResponse = await draftResponsePromise;
    expect(draftResponse.ok(), await draftResponse.text()).toBeTruthy();
    await expect(page.getByText('Stage 3 of 4 · Participation draft')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Commit participation' })).toBeDisabled();

    const draftPayload = draftRequest.postDataJSON() as Record<string, unknown>;
    const commitAttempt = await page.request.put(draftRequest.url(), {
      data: { ...draftPayload, commit: true },
      headers: { Authorization: `Bearer ${await token(page)}` },
    });
    expect(commitAttempt.status()).toBe(403);
  });

  test('partial bid promotes only the five approved lines', async ({ page }) => {
    const leadId = Number(env().E2E_GOLDEN_PARTIAL_BID_LEAD_ID);
    await openFreshWorkbench(page, leadId, 'DRAFT');
    await saveFitThroughControls(page);
    await markAllBidThroughControls(page);
    await markPartNoBidThroughControls(page, 'GOLD-NOQT-0005');
    partialRfqId = await commitBidScopeAndPromote(page, leadId, 5);
    const rfq = await readApi(page, await token(page), `/api/Rfq/${partialRfqId}`);
    expect(rfq.ok(), await rfq.text()).toBeTruthy();
    partialRfqNumber = (await rfq.json()).rfqno;
    expect(partialRfqNumber).toBeTruthy();
  });

  test('full bid promotes all six approved lines', async ({ page }) => {
    const leadId = Number(env().E2E_GOLDEN_FULL_BID_LEAD_ID);
    await openFreshWorkbench(page, leadId);
    await saveFitThroughControls(page);
    await markAllBidThroughControls(page);
    await commitBidScopeAndPromote(page, leadId, 6);
  });

  test('full no-bid closes participation and exposes no promotion action', async ({ page }) => {
    const leadId = Number(env().E2E_GOLDEN_FULL_NO_BID_LEAD_ID);
    await openFreshWorkbench(page, leadId);
    await saveFitThroughControls(page);
    await markAllNoBidThroughControls(page);
    await page.getByRole('button', { name: 'Commit full no-bid' }).click();
    const dialog = page.getByRole('dialog', { name: 'Commit full no-bid' });
    await selectOption(page, dialog.getByRole('combobox', { name: 'Full no-bid reason' }), /.+/);
    await dialog.getByLabel('Decision note (optional)').fill(
      'Commercial owner confirmed the full no-bid decision for this request.');
    const commitResponsePromise = page.waitForResponse((response) =>
      response.request().method() === 'PUT'
      && response.url().endsWith(`/api/leads/${leadId}/participation`));
    await dialog.getByRole('button', { name: 'Commit full no-bid' }).click();
    const commitResponse = await commitResponsePromise;
    expect(commitResponse.ok(), await commitResponse.text()).toBeTruthy();
    await expect(page.getByText('Participation decision committed.')).toBeVisible();
    await expect(page.getByText('Full no-bid committed', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: /Promote .* to RFQ/ })).toHaveCount(0);
    expect(await rfqCountForLead(page, await token(page), leadId)).toBe(0);
  });

  test('promotion HTTP boundary replays to the one durable RFQ', async ({ page }) => {
    const leadId = Number(env().E2E_GOLDEN_PARTIAL_BID_LEAD_ID);
    expect(partialRfqId, 'partial-bid UI journey must run first').toBeGreaterThan(0);
    const bearer = await token(page);
    const workbenchResponse = await readApi(page, bearer, `/api/leads/${leadId}/decision-workbench`);
    expect(workbenchResponse.ok(), await workbenchResponse.text()).toBeTruthy();
    const workbench = await workbenchResponse.json() as {
      leadRevisionId: number;
      decisionVersion: number;
      participationVersion: number;
    };
    const replay = await commandApi(page, bearer, `/api/leads/${leadId}/promote-to-rfq`, {
      expectedLeadRevisionId: workbench.leadRevisionId,
      expectedDecisionVersion: workbench.decisionVersion,
      expectedParticipationVersion: workbench.participationVersion,
    }, `phase1-boundary-replay-${leadId}`);
    expect(replay.ok(), await replay.text()).toBeTruthy();
    expect((await replay.json()).rfqId).toBe(partialRfqId);
    expect(await rfqCountForLead(page, bearer, leadId)).toBe(1);
  });

  test('governed RFQ lineage prepares one idempotent Quote Draft', async ({ page }) => {
    expect(partialRfqId, 'partial-bid UI journey must run first').toBeGreaterThan(0);
    await page.goto('/procurement/rfqs/all');
    await page.getByPlaceholder('Search Nexora Serial, RFQ, customer or buyer').fill(partialRfqNumber);
    await expect(page.getByText(partialRfqNumber, { exact: true }).first()).toBeVisible();
    await page.goto(`/procurement/rfqs/view/${partialRfqId}`);
    await expect(page.getByText('Governed promotion receipt', { exact: true })).toBeVisible();
    await expect(page.getByText('Immutable Lead revision', { exact: true })).toBeVisible();
    await expect(page.getByText('Participation decision', { exact: true })).toBeVisible();
    await expect(page.getByText('Promotion receipt', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Open Lead decision record' })).toBeVisible();

    const bearer = await token(page);
    const rfqResponse = await readApi(page, bearer, `/api/Rfq/${partialRfqId}`);
    expect(rfqResponse.ok(), await rfqResponse.text()).toBeTruthy();
    const rfq = await rfqResponse.json() as { leadId: number; rfqitems: unknown[] };
    expect(rfq.leadId).toBe(Number(env().E2E_GOLDEN_PARTIAL_BID_LEAD_ID));
    expect(rfq.rfqitems).toHaveLength(5);

    await page.getByRole('button', { name: 'Prepare Quote Draft' }).click();
    await expect(page).toHaveURL(/\/sales\/quotes\/view\/\d+$/);
    partialQuoteId = Number(page.url().split('/').at(-1));
    expect(partialQuoteId).toBeGreaterThan(0);
    await expect(page.getByRole('button', { name: 'Open Source RFQ' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Open Canonical Lead' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Export PDF' })).toBeDisabled();

    // The operator repeats the same visible action from the RFQ. The service must return the
    // original draft rather than create a second commercial record.
    await page.goto(`/procurement/rfqs/view/${partialRfqId}`);
    await page.getByRole('button', { name: 'Prepare Quote Draft' }).click();
    await expect(page).toHaveURL(`/sales/quotes/view/${partialQuoteId}`);
    const quotesResponse = await readApi(page, await token(page), '/api/Quote?pageNumber=1&pageSize=250');
    expect(quotesResponse.ok(), await quotesResponse.text()).toBeTruthy();
    const quotes = await quotesResponse.json();
    expect((quotes.items ?? []).filter((quote: { rfqId?: number }) => quote.rfqId === partialRfqId)).toHaveLength(1);

    const quoteResponse = await readApi(page, await token(page), `/api/Quote/${partialQuoteId}`);
    expect(quoteResponse.ok(), await quoteResponse.text()).toBeTruthy();
    const quote = await quoteResponse.json() as { rfqId: number; leadId: number };
    expect(quote.rfqId).toBe(partialRfqId);
    expect(quote.leadId).toBe(Number(env().E2E_GOLDEN_PARTIAL_BID_LEAD_ID));

    // An unpriced draft must fail closed at the PDF boundary every time and direct the operator
    // to Commercial Review. Price attestation deliberately comes later: there are no complete
    // prices to attest yet. Neither refusal may create a second Quote Draft as a side effect.
    for (let attempt = 0; attempt < 2; attempt += 1) {
      const pdf = await readApi(page, await token(page), `/api/Quote/${partialQuoteId}/pdf`);
      expect(pdf.status()).toBe(409);
      const refusal = await pdf.json();
      expect(refusal.commercialReviewRequired).toBe(true);
      expect(refusal.priceAttestationRequired).not.toBe(true);
      expect(refusal.message).toContain('Commercial Review Required');
    }
  });

  test('customer amendment creates a new Lead revision and marks the Quote Draft stale', async ({ page }) => {
    expect(partialQuoteId, 'Quote Draft journey must run first').toBeGreaterThan(0);
    const bearer = await token(page);
    const beforeResponse = await readApi(
      page,
      bearer,
      `/api/leads/${Number(env().E2E_GOLDEN_PARTIAL_BID_LEAD_ID)}/decision-workbench`,
    );
    expect(beforeResponse.ok(), await beforeResponse.text()).toBeTruthy();
    const before = await beforeResponse.json() as { leadRevisionNumber: number };

    const batchId = await uploadAndWaitForReconciliation(page, partialAmendment);
    const batchResponse = await readApi(page, bearer, `/api/LeadIngestion/batches/${batchId}`);
    expect(batchResponse.ok(), await batchResponse.text()).toBeTruthy();
    const batch = await batchResponse.json() as {
      revisions: number;
      possibleMatches: number;
      items: Array<{ classification: string; leadId?: number | null; revisionNumber?: number | null }>;
    };
    expect(batch.revisions).toBe(1);
    expect(batch.possibleMatches).toBe(0);
    expect(batch.items).toContainEqual(expect.objectContaining({
      classification: 'Revision',
      leadId: Number(env().E2E_GOLDEN_PARTIAL_BID_LEAD_ID),
      revisionNumber: before.leadRevisionNumber + 1,
    }));

    const quoteResponse = await readApi(page, bearer, `/api/Quote/${partialQuoteId}`);
    expect(quoteResponse.ok(), await quoteResponse.text()).toBeTruthy();
    expect((await quoteResponse.json()).revisionImpact).toBe('DRAFT_STALE_REVIEW_REQUIRED');
    await page.goto(`/sales/quotes/view/${partialQuoteId}`);
    await expect(page.getByText('Customer Revision Received', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Mark review complete' })).toBeVisible();
    await page.getByRole('button', { name: 'Open Canonical Lead' }).click();
    await expect(page).toHaveURL(new RegExp(`/procurement/leads/view/${env().E2E_GOLDEN_PARTIAL_BID_LEAD_ID}$`));
    await expect(page.getByRole('heading', { name: 'Revision history' })).toBeVisible();
  });

  test('sales manager resolves managed scope and can inspect the assigned rep journey', async ({ page }) => {
    const values = env();
    await loginThroughUi(page, {
      email: values.E2E_GOLDEN_MANAGER_EMAIL,
      password: values.E2E_GOLDEN_PASSWORD,
      businessUnitId: values.E2E_GOLDEN_TENANT_A,
    });
    const bearer = await token(page);
    const today = await readApi(page, bearer, '/api/commercial-intelligence/sales-today');
    expect(today.ok(), await today.text()).toBeTruthy();
    expect((await today.json()).scope).toBe('managed_scope');
    const assigned = await readApi(page, bearer,
      `/api/leads/${Number(values.E2E_GOLDEN_PARTIAL_BID_LEAD_ID)}/decision-workbench`);
    expect(assigned.ok(), await assigned.text()).toBeTruthy();
    await page.goto(`/procurement/leads/${values.E2E_GOLDEN_PARTIAL_BID_LEAD_ID}/workbench`);
    await expect(page.getByText('Already promoted', { exact: true })).toBeVisible();
  });

  test('same-tenant restricted role is denied Quote Draft and PDF boundaries', async ({ page }) => {
    const values = env();
    expect(partialRfqId).toBeGreaterThan(0);
    expect(partialQuoteId).toBeGreaterThan(0);
    await loginThroughUi(page, {
      email: values.E2E_GOLDEN_DENIED_EMAIL,
      password: values.E2E_GOLDEN_PASSWORD,
      businessUnitId: values.E2E_GOLDEN_TENANT_A,
    });
    const bearer = await token(page);
    const prepare = await commandApi(page, bearer, `/api/Rfq/${partialRfqId}/prepare-quote-draft`, {},
      `phase1-denied-prepare-${partialRfqId}`);
    expect(prepare.status()).toBe(403);
    expect((await readApi(page, bearer, `/api/Quote/${partialQuoteId}/pdf`)).status()).toBe(403);
  });
});

test('pilot gate — anonymous and cross-tenant workbench access is non-disclosing', async ({ page }) => {
  const values = env();
  const leadId = Number(values.E2E_GOLDEN_PARTIAL_BID_LEAD_ID);
  const anonymous = await readApi(page, null, `/api/leads/${leadId}/decision-workbench`);
  expect(anonymous.status()).toBe(401);
  await loginThroughUi(page, {
    email: values.E2E_GOLDEN_OUTSIDER_EMAIL,
    password: values.E2E_GOLDEN_PASSWORD,
    businessUnitId: values.E2E_GOLDEN_TENANT_B,
  });
  const foreign = await readApi(page, await token(page), `/api/leads/${leadId}/decision-workbench`);
  expect(foreign.status()).toBe(404);

  const outsiderBearer = await token(page);
  const promotion = await commandApi(page, outsiderBearer, `/api/leads/${leadId}/promote-to-rfq`, {
    expectedLeadRevisionId: 1,
    expectedDecisionVersion: 1,
    expectedParticipationVersion: 1,
  }, `phase1-cross-tenant-${leadId}`);
  expect(promotion.status()).toBe(404);
  if (partialRfqId > 0) {
    expect((await readApi(page, outsiderBearer, `/api/Rfq/${partialRfqId}`)).status()).toBe(404);
  }
  if (partialQuoteId > 0) {
    expect((await readApi(page, outsiderBearer, `/api/Quote/${partialQuoteId}`)).status()).toBe(404);
  }
});
