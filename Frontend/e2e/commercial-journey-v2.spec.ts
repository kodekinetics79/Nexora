import fs from 'node:fs/promises';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { api, jsonOk, loginAs, required, requiredNumber } from './support/core-commercial';

const evidenceDir = path.resolve('../docs/nexora/evidence/commercial-journey-v2');
const rfqId = () => requiredNumber('E2E_CORE_RFQ_ID');
const quoteId = () => requiredNumber('E2E_CORE_QUOTE_ID');

type Workbench = {
  nexoraSerial: string;
  lines: Array<{ id: number; demandLineId?: number | null; partNumber?: string | null; shortfallQuantity: number }>;
  solicitations: Array<{ id: number; supplierId: number; supplierName: string }>;
  offers: Array<{ id: number; rfqItemId: number; supplierName: string; version: number }>;
  awards: Array<{ id: number; rfqItemId: number; supplierName: string }>;
  customerQuoteDraft: { lines: Array<{ quoteItemId: number; rfqItemId: number }> } | null;
};

type SourcingCase = {
  id: number;
  version: number;
  rfqItemId: number;
  commercialDemandLineId: number;
  nexoraSerial: string;
  candidates: Array<{ supplierId: number; supplierName: string }>;
};

type QuoteDetail = {
  id: number;
  commercialCaseId: number;
  customerId: number;
  currencyId: number;
  version: number;
  quoteItems: Array<{ id: number; productId: number; itemDescription: string; quantity: number; unitPrice: number }>;
};

type ClientPoMatch = {
  header: {
    id: number;
    externalPoNumber: string;
    nexoraSerial: string;
    matchOutcome: string;
    discrepancyCount: number;
    customerOrderId?: number | null;
    customerOrderNumber?: string | null;
  };
  lines: Array<{ matchStatus: string; differences: string[] }>;
};

const commandHeaders = (key: string) => ({
  'Idempotency-Key': key,
  'X-Correlation-ID': key,
});

async function getWorkbench(page: Parameters<typeof api>[0], token: string): Promise<Workbench> {
  return jsonOk<Workbench>(await api(page, token, 'get', `/api/procurement/rfqs/${rfqId()}/workbench`));
}

async function ensureOutOfStockCase(page: Parameters<typeof api>[0], token: string): Promise<SourcingCase> {
  const workbench = await getWorkbench(page, token);
  const line = workbench.lines.find((item) => item.partNumber === required('E2E_CORE_OUT_OF_STOCK_PART'));
  expect(line, 'The acceptance RFQ must contain its persisted out-of-stock line.').toBeTruthy();
  return jsonOk<SourcingCase>(await api(page, token, 'post', '/api/procurement/sourcing-cases', {
    rfqId: rfqId(), rfqItemId: line!.id, searchLimit: 10, sourceEntireQuantity: false,
  }, commandHeaders('commercial-v2-case-oos')));
}

async function captureAndProjectOffers(page: Parameters<typeof api>[0], token: string) {
  const sourcingCase = await ensureOutOfStockCase(page, token);
  const workbench = await getWorkbench(page, token);
  expect(workbench.solicitations.length).toBeGreaterThanOrEqual(2);
  const selected = workbench.solicitations
    .filter((item) => ['Atlas Automation Partners', 'Meridian Process Equipment'].includes(item.supplierName));
  expect(selected).toHaveLength(2);

  const prices: Record<string, number> = {
    'Atlas Automation Partners': 446,
    'Meridian Process Equipment': 449,
  };
  const capturedIds: number[] = [];
  for (const solicitation of selected) {
    const slug = solicitation.supplierName.startsWith('Atlas') ? 'atlas' : 'meridian';
    const reference = `V2-${slug.toUpperCase()}-001`;
    const inbox = await jsonOk<Array<{ supplierQuoteId: number; supplierQuoteReference: string }>>(
      await api(page, token, 'get', '/api/supplier-quote-inbox?limit=200'),
    );
    let supplierQuoteId = inbox.find((item) => item.supplierQuoteReference === reference)?.supplierQuoteId;
    if (!supplierQuoteId) {
      const capture = await jsonOk<{ supplierQuoteId: number }>(await api(
        page, token, 'post', '/api/supplier-quote-inbox', {
          supplierId: solicitation.supplierId,
          supplierSolicitationId: solicitation.id,
          sourcingCaseId: sourcingCase.id,
          nexoraSerial: sourcingCase.nexoraSerial,
          supplierQuoteReference: reference,
          revisionNumber: 1,
          captureChannel: 'MANUAL',
          sourceDocumentId: null,
          sourceIdentity: `commercial-v2-${slug}-quote`,
          sourceSha256: slug === 'atlas' ? 'a'.repeat(64) : 'b'.repeat(64),
          currencyId: requiredNumber('E2E_CORE_CURRENCY_ID'),
          validUntil: new Date(Date.now() + 20 * 86_400_000).toISOString(),
          incoterms: 'DAP',
          freightAmount: slug === 'atlas' ? 36 : 24,
          taxAmount: 0,
          paymentTerms: 'Net 30',
          notes: 'Authorized Release 02 acceptance quote.',
          lines: [{
            lineNumber: 1,
            rfqItemId: sourcingCase.rfqItemId,
            commercialDemandLineId: sourcingCase.commercialDemandLineId,
            partNumber: required('E2E_CORE_OUT_OF_STOCK_PART'),
            manufacturer: 'Nexora Acceptance Controls',
            supplierPartNumber: `${slug.toUpperCase()}-CORE-OOS-300`,
            description: 'Known transmitter with supplier history and zero ATP',
            quantity: 12,
            availableQuantity: 12,
            unitOfMeasure: 'EA',
            unitPrice: prices[solicitation.supplierName],
            minimumOrderQuantity: 1,
            leadTimeDays: slug === 'atlas' ? 9 : 12,
            availabilityType: 'AVAILABLE_TO_ORDER',
            originCountry: 'US',
            warranty: '12 months',
            isAlternate: false,
            exceptions: null,
            evidence: [],
          }],
          evidence: [],
        }, commandHeaders(`commercial-v2-capture-${slug}`),
      ));
      supplierQuoteId = capture.supplierQuoteId;
    }
    const detail = await jsonOk<{ version: number }>(
      await api(page, token, 'get', `/api/supplier-quote-inbox/${supplierQuoteId}`),
    );
    const refreshed = await getWorkbench(page, token);
    if (!refreshed.offers.some((offer) => offer.supplierName === solicitation.supplierName)) {
      await jsonOk(await api(page, token, 'post', `/api/supplier-quote-inbox/${supplierQuoteId}/comparison-projections`, {
        expectedVersion: detail.version,
      }, commandHeaders(`commercial-v2-project-${slug}`)));
    }
    capturedIds.push(supplierQuoteId);
  }
  return capturedIds;
}

async function ensureClientPoAcceptance(
  page: Parameters<typeof api>[0], token: string, kind: 'exact' | 'partial',
): Promise<ClientPoMatch> {
  const exact = kind === 'exact';
  const quoteIdValue = requiredNumber(exact
    ? 'E2E_V2_CLIENT_PO_EXACT_QUOTE_ID'
    : 'E2E_V2_CLIENT_PO_PARTIAL_QUOTE_ID');
  const poNumber = exact ? 'V2-CLIENT-PO-EXACT-001' : 'V2-CLIENT-PO-PARTIAL-001';
  const existing = await jsonOk<Array<{ id: number; externalPoNumber: string }>>(
    await api(page, token, 'get', `/api/customer-awards/purchase-orders?search=${poNumber}&limit=20`),
  );
  if (existing.length > 0) {
    return jsonOk<ClientPoMatch>(await api(
      page, token, 'get', `/api/customer-awards/purchase-orders/${existing[0].id}`,
    ));
  }

  const quote = await jsonOk<QuoteDetail>(await api(page, token, 'get', `/api/Quote/${quoteIdValue}`));
  const projection = await jsonOk<{ quoteVersion: number }>(
    await api(page, token, 'get', `/api/customer-awards/quote/${quoteIdValue}`),
  );
  const quoteLine = quote.quoteItems[0];
  const orderedQuantity = exact ? quoteLine.quantity : quoteLine.quantity / 2;
  const poUnitPrice = exact ? quoteLine.unitPrice : quoteLine.unitPrice + 15;
  const purchaseOrder = await jsonOk<{ id: number; version: number; lines: Array<{ id: number }> }>(await api(
    page, token, 'post', '/api/customer-awards/purchase-orders', {
      quoteId: quote.id,
      commercialCaseId: quote.commercialCaseId,
      customerId: quote.customerId,
      currencyId: quote.currencyId,
      externalPoNumber: poNumber,
      poDate: new Date().toISOString(),
      receivedOn: new Date().toISOString(),
      expectedVersion: 0,
      lines: [{
        externalLineReference: '1',
        productId: quoteLine.productId,
        description: quoteLine.itemDescription,
        orderedQuantity,
        unitPrice: poUnitPrice,
        lineAmount: orderedQuantity * poUnitPrice,
      }],
    }, commandHeaders(`commercial-v2-${kind}-create-po`),
  ));
  const award = await jsonOk<{ id: number; version: number }>(await api(
    page, token, 'post', '/api/customer-awards', {
      customerPurchaseOrderId: purchaseOrder.id,
      quoteId: quote.id,
      expectedVersion: 0,
      customerPurchaseOrderExpectedVersion: purchaseOrder.version,
      quoteExpectedVersion: projection.quoteVersion,
      allocations: [{
        customerPurchaseOrderLineId: purchaseOrder.lines[0].id,
        quoteItemId: quoteLine.id,
        awardedQuantity: orderedQuantity,
      }],
    }, commandHeaders(`commercial-v2-${kind}-create-award`),
  ));
  const confirmed = await jsonOk<{ id: number; version: number }>(await api(
    page, token, 'post', `/api/customer-awards/${award.id}/confirm`, { expectedVersion: award.version },
    commandHeaders(`commercial-v2-${kind}-confirm-award`),
  ));
  if (exact) {
    await jsonOk(await api(
      page, token, 'post', `/api/customer-awards/${award.id}/convert-to-order`,
      { expectedVersion: confirmed.version }, commandHeaders('commercial-v2-exact-create-order'),
    ));
  }
  return jsonOk<ClientPoMatch>(await api(
    page, token, 'get', `/api/customer-awards/purchase-orders/${purchaseOrder.id}`,
  ));
}

test.describe.configure({ mode: 'serial' });

test('01 RFQ Command Workspace opens through the normal authenticated route', async ({ page }) => {
  await loginAs(page, 'manager');
  await page.goto(`/procurement/rfqs/view/${requiredNumber('E2E_CORE_RFQ_ID')}`);
  await expect(page.getByText(required('E2E_CORE_NEXORA_SERIAL'), { exact: false }).first()).toBeVisible();
  await expect(page.getByText(required('E2E_CORE_CUSTOMER_NAME'), { exact: false }).first()).toBeVisible();
  await expect(page.getByText(required('E2E_CORE_ACCOUNT_OWNER_NAME'), { exact: false }).first()).toBeVisible();
  await expect(page.getByText(required('E2E_CORE_OPPORTUNITY_OWNER_NAME'), { exact: false }).first()).toBeVisible();
});

test('02 RFQ summary cards filter persisted commercial lines', async ({ page }) => {
  await loginAs(page, 'manager');
  await page.goto(`/procurement/rfqs/view/${requiredNumber('E2E_CORE_RFQ_ID')}`);
  const total = page.getByRole('button', { name: /Total lines/i });
  const sourcing = page.getByRole('button', { name: /Sourcing required/i });
  await expect(total).toBeVisible();
  await expect(sourcing).toBeVisible();
  await sourcing.click();
  await expect(sourcing).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('row').filter({ hasText: /to source/i }).first()).toBeVisible();
  await total.click();
  await expect(page.getByRole('row')).toHaveCount(7);
});

test('03 RFQ line evidence opens without inventing unavailable provenance', async ({ page }) => {
  await loginAs(page, 'manager');
  await page.goto(`/procurement/rfqs/view/${requiredNumber('E2E_CORE_RFQ_ID')}`);
  await page.getByRole('button', { name: /Sourcing required/i }).click();
  await page.getByRole('button', { name: 'Evidence' }).first().click();
  await expect(page.getByText('Source evidence', { exact: true })).toBeVisible();
  await expect(page.getByText(/Open Canonical Lead to inspect document/i)).toBeVisible();
  await fs.mkdir(evidenceDir, { recursive: true });
  await page.screenshot({ path: path.join(evidenceDir, 'rfq-command-workspace.png'), fullPage: true });
});

test('04 RFQ Command Workspace remains usable on mobile', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 });
  await loginAs(page, 'manager');
  await page.goto(`/procurement/rfqs/view/${requiredNumber('E2E_CORE_RFQ_ID')}`);
  await expect(page.getByRole('button', { name: /Total lines/i })).toBeVisible();
  await expect(page.locator('body')).not.toHaveCSS('overflow-x', 'scroll');
});

test('05 out-of-stock RFQ line opens a real Sourcing Case with known Suppliers', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const sourcingCase = await ensureOutOfStockCase(page, token);
  await page.goto(`/procurement/sourcing-cases/${sourcingCase.id}`);
  await expect(page.getByRole('heading', { name: 'Known Supplier candidates' })).toBeVisible();
  await expect(page.getByText('Precision Controls Supply').first()).toBeVisible();
  await expect(page.getByText('Atlas Automation Partners').first()).toBeVisible();
  await expect(page.getByText('Meridian Process Equipment').first()).toBeVisible();
});

test('06 Supplier candidate limit supports 10, 20, and 50 without external search', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const sourcingCase = await ensureOutOfStockCase(page, token);
  await page.goto(`/procurement/sourcing-cases/${sourcingCase.id}`);
  for (const limit of [20, 50, 10]) {
    const control = page.getByRole('button', { name: `Show ${limit} Supplier candidates` });
    await control.click();
    await expect(control).toHaveAttribute('aria-pressed', 'true');
  }
  await expect(page.getByText('Tenant records only.')).toBeVisible();
});

test('07 selected known Suppliers become governed Supplier RFQs', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const sourcingCase = await ensureOutOfStockCase(page, token);
  let workbench = await getWorkbench(page, token);
  if (workbench.solicitations.length < 2) {
    await page.goto(`/procurement/sourcing-cases/${sourcingCase.id}`);
    await page.getByRole('checkbox', { name: 'Select Atlas Automation Partners' }).check();
    await page.getByRole('checkbox', { name: 'Select Meridian Process Equipment' }).check();
    await page.getByRole('button', { name: 'Prepare Supplier RFQ' }).click();
    await expect(page.getByRole('heading', { name: 'Prepare Supplier RFQs' })).toBeVisible();
    await page.getByRole('button', { name: 'Create Supplier RFQs' }).click();
    await expect(page).toHaveURL(new RegExp(`/procurement/rfqs/${rfqId()}/sourcing`));
    workbench = await getWorkbench(page, token);
  }
  expect(workbench.solicitations.filter((item) =>
    ['Atlas Automation Partners', 'Meridian Process Equipment'].includes(item.supplierName))).toHaveLength(2);
  await page.goto(`/procurement/rfqs/${rfqId()}/sourcing`);
  await page.getByRole('tab', { name: /Solicitations/i }).click();
  await expect(page.getByText('Atlas Automation Partners').first()).toBeVisible();
  await expect(page.getByText('Meridian Process Equipment').first()).toBeVisible();
});

test('08 two real Supplier Quotes enter the canonical inbox and projection path', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const supplierQuoteIds = await captureAndProjectOffers(page, token);
  expect(supplierQuoteIds).toHaveLength(2);
  await page.goto('/procurement/supplier-quotes');
  await expect(page.getByText('V2-ATLAS-001')).toBeVisible();
  await expect(page.getByText('V2-MERIDIAN-001')).toBeVisible();
  await expect(page.getByText('READY FOR COMPARISON').first()).toBeVisible();
});

test('09 Supplier Quote review preserves source, revision, and Nexora Serial', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const [supplierQuoteId] = await captureAndProjectOffers(page, token);
  const detail = await jsonOk<{ supplierQuoteReference: string }>(
    await api(page, token, 'get', `/api/supplier-quote-inbox/${supplierQuoteId}`),
  );
  await page.goto(`/procurement/supplier-quotes/${supplierQuoteId}`);
  await expect(page.getByText(required('E2E_CORE_NEXORA_SERIAL'), { exact: false }).first()).toBeVisible();
  await expect(page.getByText(detail.supplierQuoteReference)).toBeVisible();
  await expect(page.getByText(/Revision 1/i).first()).toBeVisible();
});

test('10 offer comparison shows persisted price, lead time, reliability, and landed cost', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  await captureAndProjectOffers(page, token);
  await page.goto(`/procurement/rfqs/${rfqId()}/sourcing`);
  await page.getByRole('tab', { name: /Supplier offers/i }).click();
  const atlas = page.getByRole('row').filter({ hasText: 'V2-ATLAS-001' });
  const meridian = page.getByRole('row').filter({ hasText: 'V2-MERIDIAN-001' });
  await expect(atlas).toContainText('$449.00');
  await expect(atlas).toContainText('9 days');
  await expect(atlas).toContainText('94%');
  await expect(meridian).toContainText('$451.00');
  await expect(meridian).toContainText('12 days');
  await expect(meridian).toContainText('94%');
});

test('11 approved Supplier offer prices the actual Customer Quote with cost evidence', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  await captureAndProjectOffers(page, token);
  let workbench = await getWorkbench(page, token);
  const line = workbench.lines.find((item) => item.partNumber === required('E2E_CORE_OUT_OF_STOCK_PART'))!;
  let award = workbench.awards.find((item) => item.rfqItemId === line.id);
  if (!award) {
    const offer = workbench.offers.find((item) => item.supplierName === 'Atlas Automation Partners')!;
    award = await jsonOk(await api(page, token, 'post', '/api/procurement/awards', {
      supplierQuotedItemId: offer.id,
      quantity: line.shortfallQuantity,
      expectedQuoteVersion: offer.version,
      rationale: 'Best eligible landed cost with shorter verified lead time.',
    }, commandHeaders('commercial-v2-award-atlas')));
    workbench = await getWorkbench(page, token);
  }
  const quoteLine = workbench.customerQuoteDraft?.lines.find((item) => item.rfqItemId === line.id);
  expect(quoteLine).toBeTruthy();
  await jsonOk(await api(page, token, 'post', '/api/supplier-quote-inbox/customer-quote-pricing', {
    quoteItemId: quoteLine!.quoteItemId,
    sourcingAwardId: award!.id,
    targetMarginPercent: 24,
    rationale: 'Target margin approved for Release 02 acceptance.',
  }, commandHeaders('commercial-v2-customer-pricing-atlas')));
  await page.goto(`/sales/quotes/view/${quoteId()}`);
  await expect(page.getByText('SELECTED SUPPLIER QUOTE')).toBeVisible();
  await expect(page.getByText('Atlas Automation Partners', { exact: false })).toBeVisible();
  await expect(page.getByText('Supplier validity does not support this Customer Quote')).toBeVisible();
  await fs.mkdir(evidenceDir, { recursive: true });
  await page.screenshot({ path: path.join(evidenceDir, 'supplier-offer-customer-pricing.png'), fullPage: true });
});

test('19 Client PO Inbox is visible in normal navigation with persisted lineage', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const match = await ensureClientPoAcceptance(page, token, 'exact');
  await page.goto('/sales/client-pos');
  await expect(page.getByRole('heading', { name: 'Client PO Inbox' })).toBeVisible();
  await expect(page.getByText(match.header.externalPoNumber)).toBeVisible();
  await expect(page.getByText(match.header.nexoraSerial, { exact: false }).first()).toBeVisible();
});

test('20 exact Client PO match reconciles to the selected Customer Quote revision', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const match = await ensureClientPoAcceptance(page, token, 'exact');
  expect(match.header.matchOutcome).toBe('EXACT_ACCEPTANCE');
  await page.goto(`/sales/client-pos/${match.header.id}`);
  await expect(page.getByText('EXACT ACCEPTANCE')).toBeVisible();
  await expect(page.getByText('EXACT MATCH')).toBeVisible();
  await expect(page.getByText(/Every accepted Client PO line reconciles/i)).toBeVisible();
});

test('21 partial Client PO award remains distinct from the full quoted quantity', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const match = await ensureClientPoAcceptance(page, token, 'partial');
  expect(match.lines[0].differences).toContain('PARTIAL_QUOTE_AWARD');
  await page.goto(`/sales/client-pos/${match.header.id}`);
  await expect(page.getByText('PARTIAL QUOTE AWARD')).toBeVisible();
});

test('22 Client PO price and quantity discrepancy matrix is evidence based', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const match = await ensureClientPoAcceptance(page, token, 'partial');
  expect(match.lines[0].differences).toContain('PRICE_DISCREPANCY');
  await page.goto(`/sales/client-pos/${match.header.id}`);
  await expect(page.getByText('PRICE DISCREPANCY')).toBeVisible();
  await expect(page.getByRole('columnheader', { name: 'PO price' })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: 'Quote price' })).toBeVisible();
  await fs.mkdir(evidenceDir, { recursive: true });
  await page.screenshot({ path: path.join(evidenceDir, 'client-po-discrepancy-review.png'), fullPage: true });
});

test('23 exact Client PO acceptance creates a governed Customer Order', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const match = await ensureClientPoAcceptance(page, token, 'exact');
  expect(match.header.customerOrderId).toBeTruthy();
  expect(match.header.customerOrderNumber).toBeTruthy();
  await page.goto(`/sales/client-pos/${match.header.id}`);
  await expect(page.getByRole('button', { name: 'Customer Order' })).toBeVisible();
});

test('30 Client PO review remains usable on mobile', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 });
  const token = await loginAs(page, 'manager');
  const match = await ensureClientPoAcceptance(page, token, 'exact');
  await page.goto(`/sales/client-pos/${match.header.id}`);
  await expect(page.getByRole('heading', { name: match.header.externalPoNumber })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: 'Decision' })).toBeVisible();
});

test.afterEach(({ page }, testInfo) => {
  void page;
  expect(testInfo.annotations.filter((annotation) => annotation.type === 'skip')).toHaveLength(0);
});
