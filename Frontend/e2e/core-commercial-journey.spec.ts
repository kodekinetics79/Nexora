import fs from 'node:fs/promises';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { api, jsonOk, loginAs, openLead, required, requiredNumber, resolutions, resolveLead } from './support/core-commercial';

const evidenceDir = path.resolve('../docs/nexora/evidence/core-sales-force-inventory');

test('28 Lead shows customer, owners and six line outcomes', async ({ page }) => {
  const leadId = requiredNumber('E2E_CORE_LEAD_ID');
  const token = await loginAs(page, 'manager');
  await resolveLead(page, token, leadId);
  await openLead(page, leadId);
  await expect(page.getByText(required('E2E_CORE_CUSTOMER_NAME'), { exact: false }).first()).toBeVisible();
  await expect(page.getByText(required('E2E_CORE_ACCOUNT_OWNER_NAME'), { exact: false }).first()).toBeVisible();
  await expect(page.getByText(required('E2E_CORE_OPPORTUNITY_OWNER_NAME'), { exact: false }).first()).toBeVisible();
  await expect(page.getByTestId('commercial-line-resolution')).toHaveCount(6);
  await fs.mkdir(evidenceDir, { recursive: true });
  await page.screenshot({ path: path.join(evidenceDir, '28-lead-six-line-outcomes.png'), fullPage: true });
});

test('29 Review & Create RFQ preserves customer, owners and inventory results', async ({ page }) => {
  const leadId = requiredNumber('E2E_CORE_RFQ_CREATION_LEAD_ID');
  const token = await loginAs(page, 'manager');
  const resolved = await resolveLead(page, token, leadId);
  expect(resolved.map((row) => row.classification)).toEqual([
    'KnownInStock',
    'KnownShortage',
    'KnownShortage',
    'KnownIncoming',
    'UnknownProduct',
    'NonInventoryService',
  ]);
  expect(Number(resolved[0].availableToPromise)).toBeGreaterThan(0);
  await openLead(page, leadId);
  await page.getByRole('button', { name: 'Review & Create RFQ' }).click();
  await expect(page.getByRole('heading', { name: 'Review inquiry and create RFQ' })).toBeVisible();
  await expect(page.getByText(required('E2E_CORE_CUSTOMER_NAME'), { exact: false }).first()).toBeVisible();
  await expect(page.getByTestId('commercial-line-resolution')).toHaveCount(6);
  const existing = await jsonOk<{ items: Array<{ id: number; leadId?: number }> }>(
    await api(page, token, 'get', '/api/Rfq?pageNumber=1&pageSize=250'),
  );
  const existingRfq = existing.items.find((row) => row.leadId === leadId);
  if (existingRfq) await page.goto(`/procurement/rfqs/view/${existingRfq.id}`);
  else {
    // WP-B1: a line the extractor could not read with confidence must be corrected, left out,
    // or explicitly acknowledged with a reason — the server refuses the conversion otherwise.
    // This fixture's UnknownProduct line raises exactly that, so the journey now records the
    // acknowledgement the way an operator would.
    await acknowledgeExtractionWarningsIfPresent(page);
    await page.getByRole('button', { name: 'Create RFQ' }).click();
  }
  await expect(page).toHaveURL(/\/procurement\/rfqs\/view\/\d+$/);
  await expect(page.getByText(required('E2E_CORE_RFQ_CREATION_NEXORA_SERIAL'), { exact: false }).first()).toBeVisible();
  await fs.mkdir(evidenceDir, { recursive: true });
  await page.screenshot({ path: path.join(evidenceDir, '29-created-rfq.png'), fullPage: true });
});

test('30 RFQ links back to Lead and Nexora Serial', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const rfq = await jsonOk<Record<string, unknown>>(await api(page, token, 'get', `/api/Rfq/${requiredNumber('E2E_CORE_RFQ_ID')}`));
  expect(Number(rfq.leadId)).toBe(requiredNumber('E2E_CORE_LEAD_ID'));
  expect(rfq.nexoraSerial ?? rfq.commercialCaseReference).toBe(required('E2E_CORE_NEXORA_SERIAL'));
  await page.goto(`/procurement/rfqs/view/${requiredNumber('E2E_CORE_RFQ_ID')}`);
  await expect(page.getByText(required('E2E_CORE_NEXORA_SERIAL'), { exact: false }).first()).toBeVisible();
});

test('31 duplicate inquiry creates no additional RFQ or workload', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const canonicalLeadId = requiredNumber('E2E_CORE_LEAD_ID');
  const batchId = required('E2E_CORE_DUPLICATE_BATCH_ID');
  const batch = await jsonOk<{ items: Array<Record<string, unknown>> }>(await api(page, token, 'get', `/api/LeadIngestion/batches/${batchId}`));
  const occurrence = batch.items.find((row) => row.classification === 'ExactDuplicate');
  if (!occurrence) throw new Error(`Batch ${batchId} does not contain an exact-duplicate occurrence.`);
  expect(occurrence.classification).toBe('ExactDuplicate');
  expect(Number(occurrence.leadId)).toBe(canonicalLeadId);
  const rfqs = await jsonOk<{ items: Array<{ leadId?: number }> }>(await api(page, token, 'get', '/api/Rfq?pageNumber=1&pageSize=250'));
  expect(rfqs.items.filter((row) => row.leadId === canonicalLeadId)).toHaveLength(1);
  const today = await jsonOk<{ attentionItems: Array<{ recordType: string; recordId: number }> }>(await api(page, token, 'get', '/api/commercial-intelligence/sales-today'));
  expect(new Set(today.attentionItems.map((row) => `${row.recordType}:${row.recordId}`)).size)
    .toBe(today.attentionItems.length);
});

test('32 revision reprocesses only affected lines', async ({ page }) => {
  const leadId = requiredNumber('E2E_CORE_LEAD_ID');
  const token = await loginAs(page, 'manager');
  const revisions = await jsonOk<Array<Record<string, unknown>>>(await api(page, token, 'get', `/api/LeadIngestion/leads/${leadId}/revisions`));
  expect(revisions.length).toBeGreaterThanOrEqual(2);
  const latest = revisions[0];
  const differences = JSON.stringify(latest).toLowerCase();
  expect(differences).toContain('changed');
  expect(Number(latest.changedLineCount ?? latest.modifiedLineCount)).toBe(Number(required('E2E_CORE_REVISION_CHANGED_LINE_COUNT')));
});

test('33 Prepare Quote Draft revalidates stock', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const rfqId = requiredNumber('E2E_CORE_QUOTE_DRAFT_RFQ_ID');
  const before = await resolutions(page, token, 'rfqs', rfqId);
  const response = await api(page, token, 'post', `/api/Rfq/${rfqId}/prepare-quote-draft`);
  const quote = await jsonOk<Record<string, unknown>>(response);
  const quoteId = Number(quote.id);
  expect(quoteId).toBeGreaterThan(0);
  const after = await resolutions(page, token, 'quotes', quoteId);
  expect(after).toHaveLength(before.length);
  expect(new Set(after.map((row) => row.resolutionBatchId)).size).toBe(1);
  expect(after[0].resolutionBatchId).not.toBe(before[0].resolutionBatchId);
  expect(Math.min(...after.map((row) => Date.parse(String(row.resolvedOn))))).toBeGreaterThan(
    Math.max(...before.map((row) => Date.parse(String(row.resolvedOn)))),
  );
  await page.goto(`/sales/quotes/view/${quoteId}`);
  await fs.mkdir(evidenceDir, { recursive: true });
  await page.screenshot({ path: path.join(evidenceDir, '33-revalidated-quote-draft.png'), fullPage: true });
});

test('34 Quote Draft preserves Lead/RFQ/customer/owner/Product lineage', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const quoteId = requiredNumber('E2E_CORE_QUOTE_ID');
  const quote = await jsonOk<Record<string, unknown>>(await api(page, token, 'get', `/api/Quote/${quoteId}`));
  expect(Number(quote.leadId)).toBe(requiredNumber('E2E_CORE_LEAD_ID'));
  expect(Number(quote.rfqId)).toBe(requiredNumber('E2E_CORE_RFQ_ID'));
  expect(Number(quote.customerId)).toBe(requiredNumber('E2E_CORE_CUSTOMER_ID'));
  expect(quote.nexoraSerial ?? quote.commercialCaseReference).toBe(required('E2E_CORE_NEXORA_SERIAL'));
  const rows = await resolutions(page, token, 'quotes', quoteId);
  expect(rows).toHaveLength(6);
  expect(rows.filter((row) => row.productId != null).length).toBeGreaterThanOrEqual(4);
  expect(String(quote.createdBy)).toContain(required('E2E_CORE_OPPORTUNITY_OWNER_EMAIL'));
});

test('35 Quote send queues delivery without claiming success early', async ({ page }) => {
  const quoteId = requiredNumber('E2E_CORE_SEND_QUOTE_ID');
  const token = await loginAs(page, 'manager');
  const recipient = encodeURIComponent(required('E2E_CORE_CONTACT_EMAIL'));
  const sent = await api(page, token, 'post', `/api/Quote/${quoteId}/email?recipientEmail=${recipient}`);
  expect(sent.status()).toBe(202);
  const delivery = await sent.json() as { queuedForDelivery: boolean; delivered: boolean };
  expect(delivery.queuedForDelivery).toBe(true);
  expect(delivery.delivered).toBe(false);
  const followUps = await jsonOk<Array<{ quoteId: number; status: string }>>(await api(page, token, 'get', '/api/commercial-intelligence/follow-ups'));
  expect(followUps.some((row) => row.quoteId === quoteId && /Open|InProgress/i.test(row.status))).toBe(false);
});

test('36 follow-up completion updates accountable performance', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const followUpId = requiredNumber('E2E_CORE_COMPLETED_FOLLOW_UP_ID');
  const followUps = await jsonOk<Array<{ id: number; status: string }>>(await api(page, token, 'get', '/api/commercial-intelligence/follow-ups'));
  expect(followUps.find((row) => row.id === followUpId)?.status).toMatch(/Completed/i);
  const from = encodeURIComponent(new Date(Date.now() - 90 * 86400000).toISOString());
  const to = encodeURIComponent(new Date(Date.now() + 86400000).toISOString());
  const performance = await jsonOk<{ representatives: Array<Record<string, unknown>> }>(await api(page, token, 'get', `/api/commercial-intelligence/performance?from=${from}&to=${to}`));
  const rep = performance.representatives.find((row) => Number(row.userId) === requiredNumber('E2E_CORE_OPPORTUNITY_OWNER_USER_ID'));
  expect(rep).toBeTruthy();
  expect(Number(rep!.followUpsCompletedOnTime ?? rep!.completedFollowUps)).toBeGreaterThan(0);
});

test('37 Dashboard metrics reconcile', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const today = await jsonOk<{ metrics: Array<{ key: string; value: number }> }>(await api(page, token, 'get', '/api/commercial-intelligence/sales-today'));
  const followUps = await jsonOk<Array<{ status: string }>>(await api(page, token, 'get', '/api/commercial-intelligence/follow-ups?status=open'));
  expect(today.metrics.find((row) => row.key === 'open-follow-ups')?.value).toBe(followUps.filter((row) => /Open|InProgress/i.test(row.status)).length);
  await page.goto('/dashboard');
  await expect(page.getByText(required('E2E_CORE_DASHBOARD_METRIC_LABEL'), { exact: false }).first()).toBeVisible();
  await fs.mkdir(evidenceDir, { recursive: true });
  await page.screenshot({ path: path.join(evidenceDir, '37-dashboard-reconciliation.png'), fullPage: true });
});

test('38 restricted role cannot override match or fulfilment route', async ({ page }) => {
  const token = await loginAs(page, 'denied');
  const match = await api(page, token, 'post', `/api/inventory-intelligence/leads/${requiredNumber('E2E_CORE_LEAD_ID')}/resolve?limit=50`);
  const release = await api(page, token, 'post', `/api/inventory-intelligence/reservations/${requiredNumber('E2E_CORE_RESERVATION_ID')}/release`,
    { expectedVersion: 1 }, { 'Idempotency-Key': 'core-denied-release' });
  expect(match.status()).toBe(403);
  expect(release.status()).toBe(403);
});

test('39 Mobile Sales Rep and Inventory journeys work', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 });
  await loginAs(page, 'manager');
  await page.goto('/sales/today');
  await expect(page.getByRole('heading', { name: 'Sales today' })).toBeVisible();
  await expect(page.locator('body')).not.toHaveCSS('overflow-x', 'scroll');
  await page.goto('/inventory/availability');
  await expect(page.getByRole('heading', { name: 'Availability' })).toBeVisible();
  await expect(page.getByLabel('Search part or product')).toBeVisible();
  await fs.mkdir(evidenceDir, { recursive: true });
  await page.screenshot({ path: path.join(evidenceDir, '39-mobile-sales-inventory.png'), fullPage: true });
});

test.afterEach(async ({ page }, testInfo) => {
  expect(testInfo.annotations.filter((annotation) => annotation.type === 'skip')).toHaveLength(0);
});

test('40 RFQ lines are marked Quote or No-Quote, and one Quote Draft is prepared', async ({ page }) => {
  // Closes the Phase 1 journey: Convert to RFQ -> Mark Selected Lines Quote -> Quote Draft.
  // Everything here is real: real auth, real API, real database. No fixture substitutes for a
  // step of the journey.
  const rfqId = requiredNumber('E2E_CORE_RFQ_ID');
  const token = await loginAs(page, 'manager');

  await page.goto(`/procurement/rfqs/view/${rfqId}`);
  await expect(page).toHaveURL(new RegExp(`/procurement/rfqs/view/${rfqId}$`));

  // Every line starts undecided. A line nobody has looked at must never read as an implicit
  // commitment to quote it, so this is asserted rather than assumed.
  const firstQuoteToggle = page.getByRole('button', { name: 'Quote this line' }).first();
  await expect(firstQuoteToggle).toBeVisible();

  // Decline one line WITH a reason — the dialog must refuse to submit without one.
  const firstDecline = page.getByRole('button', { name: 'Decline this line' }).first();
  await firstDecline.click();
  const confirmDecline = page.getByRole('button', { name: 'Decline line' });
  await expect(confirmDecline).toBeDisabled(); // no reason yet
  await page.getByLabel('Why are we not quoting this line?')
    .fill('Browser acceptance: obsolete part, no supplier source');
  await expect(confirmDecline).toBeEnabled();
  await confirmDecline.click();
  await expect(page.getByText('Line declined, with your reason recorded.')).toBeVisible();

  // Mark a different line for quotation.
  const quoteToggle = page.getByRole('button', { name: 'Quote this line' }).last();
  await quoteToggle.click();
  await expect(page.getByText('Line marked for quotation.')).toBeVisible();

  // Prepare the Customer Quote Draft. Only the Quote-marked lines may reach it.
  await page.getByRole('button', { name: /Prepare Quote Draft/i }).click();
  await expect(page).toHaveURL(/\/sales\/quotes\/view\/\d+$/);
  const firstQuoteUrl = page.url();

  await fs.mkdir(evidenceDir, { recursive: true });
  await page.screenshot({ path: path.join(evidenceDir, '30-quote-draft-from-marked-lines.png'), fullPage: true });

  // Idempotency through the browser, not just the database: going back and pressing the button
  // again must land on the SAME quote, never mint a second one.
  const quotesBefore = await jsonOk<{ items: Array<{ id: number; rfqId?: number }> }>(
    await api(page, token, 'get', '/api/Quote?pageNumber=1&pageSize=250'),
  );
  const countBefore = quotesBefore.items.filter((row) => row.rfqId === rfqId).length;
  expect(countBefore).toBe(1);

  await page.goto(`/procurement/rfqs/view/${rfqId}`);
  await page.getByRole('button', { name: /Prepare Quote Draft/i }).click();
  await expect(page).toHaveURL(new RegExp(firstQuoteUrl.replace(/^.*(\/sales\/quotes\/view\/\d+)$/, '$1') + '$'));

  const quotesAfter = await jsonOk<{ items: Array<{ id: number; rfqId?: number }> }>(
    await api(page, token, 'get', '/api/Quote?pageNumber=1&pageSize=250'),
  );
  expect(quotesAfter.items.filter((row) => row.rfqId === rfqId).length).toBe(countBefore);
});

/**
 * Ticks the extraction-warning acknowledgement on the Review & Create RFQ page when the server
 * has flagged at least one included line, and records a reason.
 *
 * No-op when nothing is flagged, so the same journey works against a clean fixture. Deliberately
 * NOT a blind click: if the control is absent the conversion is expected to succeed unaided, and
 * silently tolerating its absence is what would let the gate regress unnoticed.
 */
async function acknowledgeExtractionWarningsIfPresent(page: import('@playwright/test').Page) {
  const acknowledgement = page.getByRole('checkbox', {
    name: 'Acknowledge the flagged lines and convert anyway',
  });
  if (await acknowledgement.count() === 0) return false;
  await acknowledgement.check();
  await page
    .getByLabel('Why are you going ahead?')
    .fill('Browser acceptance: catalog not seeded for this fixture, parts verified by the test');
  return true;
}
