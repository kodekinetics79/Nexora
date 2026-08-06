import { expect, test, type Page } from '@playwright/test';
import { loginThroughUi } from './support/login';
import { requireEnv } from './support/environment';

/**
 * Phase 1 base journey, in a real browser against a real backend, worker and PostgreSQL:
 *
 *   qualified Lead → correct a hard warning → acknowledge a soft warning → exclude a line
 *   → convert to exactly ONE RFQ → mark lines Quote / NoQuote / Pending
 *   → prepare exactly ONE Customer Quote Draft → prove no duplicates → prove denials
 *
 * Seeded by `GoldenCommercialJourneySeeder`, which creates only STARTING CONDITIONS. Every
 * decision below — the correction, the acknowledgement, the exclusion, the participation, the
 * draft — is made here, through the UI or the real API. Nothing asserted was seeded.
 */

const env = () => requireEnv('Phase 1 base journey',
  'E2E_API_URL', 'E2E_GOLDEN_SALES_EMAIL', 'E2E_GOLDEN_OUTSIDER_EMAIL',
  'E2E_GOLDEN_PASSWORD', 'E2E_GOLDEN_LEAD_ID', 'E2E_GOLDEN_FOREIGN_LEAD_ID',
  'E2E_GOLDEN_TENANT_A');

async function token(page: Page): Promise<string> {
  const value = await page.evaluate(() => localStorage.getItem('token'));
  if (!value) throw new Error('Authenticated session carries no access token.');
  return value;
}

async function api(page: Page, bearer: string, method: string, path: string, body?: unknown) {
  const { E2E_API_URL } = env();
  return page.request.fetch(`${E2E_API_URL}${path}`, {
    method,
    headers: { Authorization: `Bearer ${bearer}`, 'Content-Type': 'application/json' },
    data: body === undefined ? undefined : JSON.stringify(body),
  });
}

async function rfqCountForLead(page: Page, bearer: string, leadId: number): Promise<number> {
  const response = await api(page, bearer, 'get', '/api/Rfq?pageNumber=1&pageSize=250');
  expect(response.ok()).toBeTruthy();
  const payload = await response.json();
  return (payload.items ?? []).filter((row: { leadId?: number }) => row.leadId === leadId).length;
}

test('Phase 1 — Lead converts to exactly one RFQ and one Customer Quote Draft', async ({ page }) => {
  const {
    E2E_GOLDEN_SALES_EMAIL: email,
    E2E_GOLDEN_PASSWORD: password,
    E2E_GOLDEN_TENANT_A: businessUnitId,
    E2E_GOLDEN_LEAD_ID: leadIdRaw,
  } = env();
  const leadId = Number(leadIdRaw);

  await loginThroughUi(page, { email, password, businessUnitId });
  const bearer = await token(page);

  // Nothing exists yet — otherwise "exactly one" later proves nothing.
  expect(await rfqCountForLead(page, bearer, leadId)).toBe(0);

  // ---------------------------------------------------------------- named owner
  const leadResponse = await api(page, bearer, 'get', `/api/Lead/${leadId}`);
  expect(leadResponse.ok()).toBeTruthy();
  const lead = await leadResponse.json();
  // Routing ran through the real engine during seeding; a named owner must have resulted.
  expect(lead.assignedToFullName ?? lead.accountOwnerName).toBeTruthy();

  // ---------------------------------------------------------------- the six lines
  const previewResponse = await api(page, bearer, 'get', `/api/intelligence/leads/${leadId}/conversion-preview`);
  expect(previewResponse.ok()).toBeTruthy();
  const preview = await previewResponse.json();
  expect(preview.items).toHaveLength(6);

  const hard = preview.items.find((i: any) => (i.attentionReason ?? '').includes('Quantity missing'));
  const soft = preview.items.find((i: any) => (i.attentionReason ?? '').includes('No catalog match'));
  expect(hard, 'seed must provide one hard-blocked line').toBeTruthy();
  expect(soft, 'seed must provide one soft-warning line').toBeTruthy();
  const clean = preview.items.filter((i: any) => !i.needsAttention);
  expect(clean.length).toBeGreaterThanOrEqual(4);
  const [toExclude, toQuote, toNoQuote, toPending] = clean;

  // ---------------------------------------------------------------- hard warning cannot be waived
  const waiveAttempt = await api(page, bearer, 'post', `/api/intelligence/leads/${leadId}/convert`, {
    acknowledgeAllWarnings: true,
    warningAcknowledgementReason: 'Attempting to wave through a missing quantity',
    items: preview.items.map((i: any) => ({ leadItemId: i.leadItemId, include: true, acknowledgeWarning: true })),
  });
  expect(waiveAttempt.status(), 'a missing quantity must never be acknowledgeable').toBe(409);
  expect(await waiveAttempt.text()).toContain('correct them on the lead first');
  expect(await rfqCountForLead(page, bearer, leadId)).toBe(0);

  // ---------------------------------------------------------------- correct the hard warning
  // Through the Review & Create RFQ screen, as an operator would.
  await page.goto(`/procurement/leads/${leadId}/convert`);
  await expect(page.getByRole('heading', { name: /Review inquiry and create RFQ/i })).toBeVisible();
  await page.screenshot({ path: 'test-results/phase1-01-convert-page.png', fullPage: true });

  // The acknowledgement control is present because lines are flagged, and Create RFQ is refused
  // until it is satisfied.
  const acknowledge = page.getByRole('checkbox', { name: 'Acknowledge the flagged lines and convert anyway' });
  await expect(acknowledge).toBeVisible();
  await expect(page.getByRole('button', { name: /^Create RFQ$/ })).toBeDisabled();

  await acknowledge.check();
  const reason = page.getByLabel('Why are you going ahead?');
  await reason.fill('bad');           // below the 5-character floor
  await expect(page.getByRole('button', { name: /^Create RFQ$/ })).toBeDisabled();
  await reason.fill('Part confirmed against the buyer’s drawing pack by phone');
  await page.screenshot({ path: 'test-results/phase1-02-acknowledged.png', fullPage: true });

  // ---------------------------------------------------------------- convert (API: exact control
  // over which line is corrected and which is excluded, with the same governance the UI posts)
  const convert = await api(page, bearer, 'post', `/api/intelligence/leads/${leadId}/convert`, {
    acknowledgeAllWarnings: true,
    warningAcknowledgementReason: 'Part confirmed against the buyer drawing pack by phone',
    notes: 'Phase 1 base journey',
    items: [
      { leadItemId: hard.leadItemId, include: true, quantity: 25, acknowledgeWarning: true },
      { leadItemId: soft.leadItemId, include: true, acknowledgeWarning: true },
      { leadItemId: toExclude.leadItemId, include: false },
      { leadItemId: toQuote.leadItemId, include: true },
      { leadItemId: toNoQuote.leadItemId, include: true },
      { leadItemId: toPending.leadItemId, include: true },
    ],
  });
  expect(convert.status(), await convert.text()).toBe(200);
  const rfqId = (await convert.json()).rfqId as number;
  expect(rfqId).toBeGreaterThan(0);

  // EXACTLY ONE RFQ, and the excluded line did not travel.
  expect(await rfqCountForLead(page, bearer, leadId)).toBe(1);

  // Replaying convert must return the SAME RFQ, never mint a second.
  const replay = await api(page, bearer, 'post', `/api/intelligence/leads/${leadId}/convert`, { items: [] });
  expect(replay.ok()).toBeTruthy();
  expect((await replay.json()).rfqId).toBe(rfqId);
  expect(await rfqCountForLead(page, bearer, leadId)).toBe(1);

  // ---------------------------------------------------------------- RFQ workspace
  await page.goto(`/procurement/rfqs/view/${rfqId}`);
  await expect(page).toHaveURL(new RegExp(`/procurement/rfqs/view/${rfqId}$`));
  await page.screenshot({ path: 'test-results/phase1-03-rfq.png', fullPage: true });

  const rfqResponse = await api(page, bearer, 'get', `/api/Rfq/${rfqId}?businessUnitId=${businessUnitId}`);
  expect(rfqResponse.ok()).toBeTruthy();
  const rfq = await rfqResponse.json();
  expect(rfq.rfqitems).toHaveLength(5);                       // 6 seeded, 1 excluded
  expect(rfq.rfqitems.every((i: any) => i.participationDecision === 'Pending')).toBeTruthy();

  const quoteLine = rfq.rfqitems.find((i: any) => i.quantity === toQuote.quantity) ?? rfq.rfqitems[0];
  const noQuoteLine = rfq.rfqitems.find((i: any) => i.id !== quoteLine.id);

  // ---------------------------------------------------------------- participation
  // No-Quote without a reason is refused.
  const badDecline = await api(page, bearer, 'post', `/api/Rfq/${rfqId}/lines/${noQuoteLine.id}/participation`,
    { decision: 'NoQuote' });
  expect(badDecline.status(), 'a decline without a reason must be refused').toBe(400);

  expect((await api(page, bearer, 'post', `/api/Rfq/${rfqId}/lines/${quoteLine.id}/participation`,
    { decision: 'Quote' })).ok()).toBeTruthy();
  expect((await api(page, bearer, 'post', `/api/Rfq/${rfqId}/lines/${noQuoteLine.id}/participation`,
    { decision: 'NoQuote', reason: 'Obsolete Alstom part, no supplier source' })).ok()).toBeTruthy();
  // The remaining lines stay Pending on purpose.

  await page.reload();
  await expect(page.getByText('No-Quote').first()).toBeVisible();
  await page.screenshot({ path: 'test-results/phase1-04-participation.png', fullPage: true });

  // ---------------------------------------------------------------- Quote Draft
  const draft = await api(page, bearer, 'post', `/api/Rfq/${rfqId}/prepare-quote-draft`);
  expect(draft.status(), await draft.text()).toBe(200);
  const quote = await draft.json();

  // ONLY the Quote-selected line travelled.
  expect(quote.quoteItems).toHaveLength(1);
  expect(quote.quoteItems[0].rfqItemId).toBe(quoteLine.id);
  // and the buyer's commercial identity is visible to whoever prices it
  expect(quote.quoteItems[0].requestedManufacturerPartNumber).toBeTruthy();
  expect(quote.quoteItems[0].requestedManufacturerName).toBeTruthy();

  await page.goto(`/sales/quotes/view/${quote.id}`);
  await page.screenshot({ path: 'test-results/phase1-05-quote-draft.png', fullPage: true });

  // Double-click / refresh must not mint a second quote.
  const draftAgain = await api(page, bearer, 'post', `/api/Rfq/${rfqId}/prepare-quote-draft`);
  expect(draftAgain.ok()).toBeTruthy();
  expect((await draftAgain.json()).id).toBe(quote.id);

  const quotes = await (await api(page, bearer, 'get', '/api/Quote?pageNumber=1&pageSize=250')).json();
  expect((quotes.items ?? []).filter((q: any) => q.rfqId === rfqId)).toHaveLength(1);
});

test('Phase 1 — cross-tenant and unauthorized access is denied', async ({ page }) => {
  const {
    E2E_GOLDEN_OUTSIDER_EMAIL: outsider,
    E2E_GOLDEN_PASSWORD: password,
    E2E_GOLDEN_LEAD_ID: leadIdRaw,
    E2E_GOLDEN_TENANT_B: tenantB,
  } = requireEnv('Phase 1 denial checks', 'E2E_GOLDEN_OUTSIDER_EMAIL', 'E2E_GOLDEN_PASSWORD',
    'E2E_GOLDEN_LEAD_ID', 'E2E_GOLDEN_TENANT_B', 'E2E_API_URL');

  // Unauthenticated first — no token at all.
  const anonymous = await page.request.fetch(`${env().E2E_API_URL}/api/Lead/${leadIdRaw}`, { method: 'GET' });
  expect(anonymous.status()).toBe(401);

  // Then a real user of the OTHER tenant.
  await loginThroughUi(page, { email: outsider, password, businessUnitId: tenantB });
  const bearer = await token(page);

  const foreignLead = await api(page, bearer, 'get', `/api/Lead/${leadIdRaw}`);
  expect([403, 404], `tenant B must not read tenant A's lead (got ${foreignLead.status()})`)
    .toContain(foreignLead.status());
});
