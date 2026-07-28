import fs from 'node:fs/promises';
import path from 'node:path';
import { createHmac } from 'node:crypto';
import { expect, test } from '@playwright/test';
import * as XLSX from 'xlsx';
import { api, apiUrl, jsonOk, loginAs, loginAsOtherTenant, required, requiredNumber } from './support/core-commercial';

const evidenceRoot = process.env.E2E_EVIDENCE_ROOT
  ? path.resolve(process.env.E2E_EVIDENCE_ROOT)
  : path.resolve('../docs/nexora/evidence');
const evidenceDir = path.join(evidenceRoot, 'commercial-journey-v2');
const v1EvidenceDir = path.join(evidenceRoot, 'v1');
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

type ProcurementHandoff = {
  id: number;
  customerOrderId: number;
  customerOrderNumber: string;
  customerOrderLineId: number;
  supplierId: number;
  supplierName: string;
  nexoraSerial: string;
  requiredQuantity: number;
  selectedUnitCost: number;
  status: string;
  externalSystemTarget: string;
  externalSupplierPoNumber?: string | null;
  sourceOfTruth?: string | null;
  isAuthoritative: boolean;
  version: number;
};

type SupplierQuoteDetail = {
  supplierQuoteId: number;
  version: number;
  inboxStatus: string;
  revisions: Array<{
    revisionId: number;
    captureChannel: string;
    lines: Array<{ partNumber?: string | null; quantity: number; unitPrice: number }>;
    evidence: Array<{
      id: number;
      confidence: number;
      reviewRequired: boolean;
      latestReviewStatus?: string | null;
    }>;
  }>;
};

type RfqCommercialIntelligence = {
  commercialDecision: string;
  nextBestAction: { label: string; explanation: string };
  lines: Array<{ rfqItemId: number; blockers: string[]; eligibleOfferCount: number }>;
  digitalTwin: {
    validity: string;
    scenarios: Array<{ code: string; label: string; eligible: boolean; explanation: string }>;
  };
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

async function ensureProcurementHandoff(
  page: Parameters<typeof api>[0], token: string,
): Promise<ProcurementHandoff> {
  const orderLineId = requiredNumber('E2E_V2_SOURCED_CUSTOMER_ORDER_LINE_ID');
  const current = await jsonOk<ProcurementHandoff[]>(await api(
    page, token, 'get', '/api/procurement-handoffs?limit=100',
  ));
  const existing = current.find((item) => item.customerOrderLineId === orderLineId);
  if (existing) return existing;
  await page.goto('/procurement/handoffs');
  await page.getByRole('button', { name: 'Create handoff' }).click();
  await page.getByLabel('Sourced Customer Order line').click();
  await page.getByRole('option').filter({ hasText: required('E2E_CORE_NEXORA_SERIAL') }).click();
  await page.getByLabel('Delivery location').fill('ABC Engineering authorized acceptance ship-to');
  await page.getByRole('button', { name: 'Create handoff', exact: true }).last().click();
  await expect(page.getByRole('dialog')).not.toBeVisible();
  const refreshed = await jsonOk<ProcurementHandoff[]>(await api(
    page, token, 'get', '/api/procurement-handoffs?limit=100',
  ));
  return refreshed.find((item) => item.customerOrderLineId === orderLineId)!;
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
  const line = page.getByRole('row').filter({ hasText: required('E2E_CORE_PARTIAL_ATP_PART') });
  await line.getByRole('button', { name: /Inspect persisted source and normalization evidence/i }).click();
  await expect(page.getByText('Source evidence', { exact: true })).toBeVisible();
  await expect(page.getByText(/Open Canonical Lead to inspect document/i)).toBeVisible();
  await fs.mkdir(evidenceDir, { recursive: true });
  await page.screenshot({ path: path.join(evidenceDir, 'rfq-command-workspace.png'), fullPage: true });
});

test('04 RFQ line outcomes use progressive disclosure for the next commercial action', async ({ page }) => {
  await loginAs(page, 'manager');
  await page.goto(`/procurement/rfqs/view/${requiredNumber('E2E_CORE_RFQ_ID')}`);
  await page.getByRole('button', { name: /Sourcing required/i }).click();
  const sourcingRow = page.getByRole('row').filter({ hasText: required('E2E_CORE_PARTIAL_ATP_PART') });
  await expect(sourcingRow).toContainText(/to source/i);
  await expect(sourcingRow.getByRole('button', { name: 'Create / Open Sourcing Case' })).toBeVisible();
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

test('12 XLSX Supplier Quote extracts locally while PDF enters governed review', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const sourcingCase = await ensureOutOfStockCase(page, token);
  const workbench = await getWorkbench(page, token);
  const solicitation = workbench.solicitations.find((item) => item.supplierName === 'Atlas Automation Partners')!;
  expect(solicitation).toBeTruthy();
  const existing = await jsonOk<Array<{ supplierQuoteId: number; supplierQuoteReference: string }>>(
    await api(page, token, 'get', '/api/supplier-quote-inbox?limit=200'),
  );
  let spreadsheetQuoteId = existing.find((item) => item.supplierQuoteReference === 'V2-XLSX-LOCAL-002')?.supplierQuoteId;
  if (!spreadsheetQuoteId) {
    const sheet = XLSX.utils.json_to_sheet([{
      'Part number': required('E2E_CORE_OUT_OF_STOCK_PART'),
      Description: 'Native spreadsheet supplier response', Quantity: 12,
      'Unit price': 443, 'Lead time': 8, Revision: 'inspection-fix-2',
    }]);
    const workbook = XLSX.utils.book_new();
    XLSX.utils.book_append_sheet(workbook, sheet, 'Supplier Quote');
    const upload = await page.request.post(`${apiUrl}/api/supplier-quote-inbox/documents`, {
      headers: { Authorization: `Bearer ${token}`, ...commandHeaders('commercial-v2-xlsx-intake-2') },
      multipart: {
        File: { name: 'v2-supplier-quote.xlsx', mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', buffer: Buffer.from(XLSX.write(workbook, { type: 'buffer', bookType: 'xlsx' })) },
        SupplierId: String(solicitation.supplierId), SupplierSolicitationId: String(solicitation.id),
        SourcingCaseId: String(sourcingCase.id), NexoraSerial: sourcingCase.nexoraSerial,
        SupplierQuoteReference: 'V2-XLSX-LOCAL-002', RevisionNumber: '1',
        CurrencyId: String(requiredNumber('E2E_CORE_CURRENCY_ID')),
        ValidUntil: new Date(Date.now() + 30 * 86_400_000).toISOString(),
        Incoterms: 'DAP', PaymentTerms: 'Net 30', Notes: 'Authorized local XLSX acceptance.',
      },
    });
    const result = await jsonOk<{ supplierQuoteId: number; projectionStatus: string }>(upload);
    spreadsheetQuoteId = result.supplierQuoteId;
    expect(result.projectionStatus).toBe('REVIEW_REQUIRED');
  }
  const detail = await jsonOk<SupplierQuoteDetail>(await api(
    page, token, 'get', `/api/supplier-quote-inbox/${spreadsheetQuoteId}`,
  ));
  expect(detail.revisions[0].captureChannel).toBe('UPLOAD');
  expect(detail.revisions[0].lines).toContainEqual(expect.objectContaining({
    partNumber: required('E2E_CORE_OUT_OF_STOCK_PART'), quantity: 12, unitPrice: 443,
  }));

  const pdf = await page.request.post(`${apiUrl}/api/supplier-quote-inbox/documents`, {
    headers: { Authorization: `Bearer ${token}`, ...commandHeaders('commercial-v2-pdf-intake') },
    multipart: {
      File: { name: 'v2-supplier-quote.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\n%%EOF') },
      SupplierId: String(solicitation.supplierId), SupplierSolicitationId: String(solicitation.id),
      SourcingCaseId: String(sourcingCase.id), NexoraSerial: sourcingCase.nexoraSerial,
      SupplierQuoteReference: 'V2-PDF-REVIEW-001', RevisionNumber: '1',
      CurrencyId: String(requiredNumber('E2E_CORE_CURRENCY_ID')),
    },
  });
  const pdfResult = await jsonOk<{ supplierQuoteId?: number | null; projectionStatus: string }>(pdf);
  expect(pdfResult.supplierQuoteId).toBeNull();
  expect(pdfResult.projectionStatus).toBe('REVIEW_REQUIRED');
});

test('13 low-confidence critical Supplier Quote evidence requires and records review', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const sourcingCase = await ensureOutOfStockCase(page, token);
  const workbench = await getWorkbench(page, token);
  const solicitation = workbench.solicitations.find((item) => item.supplierName === 'Meridian Process Equipment')!;
  const inbox = await jsonOk<Array<{ supplierQuoteId: number; supplierQuoteReference: string }>>(
    await api(page, token, 'get', '/api/supplier-quote-inbox?limit=200'),
  );
  let supplierQuoteId = inbox.find((item) => item.supplierQuoteReference === 'V2-LOW-CONFIDENCE-001')?.supplierQuoteId;
  if (!supplierQuoteId) {
    const captured = await jsonOk<{ supplierQuoteId: number }>(await api(page, token, 'post', '/api/supplier-quote-inbox', {
      supplierId: solicitation.supplierId, supplierSolicitationId: solicitation.id,
      sourcingCaseId: sourcingCase.id, nexoraSerial: sourcingCase.nexoraSerial,
      supplierQuoteReference: 'V2-LOW-CONFIDENCE-001', revisionNumber: 1,
      captureChannel: 'UPLOAD', sourceDocumentId: null, sourceIdentity: 'authorized-low-confidence-field',
      sourceSha256: 'c'.repeat(64), currencyId: requiredNumber('E2E_CORE_CURRENCY_ID'),
      validUntil: new Date(Date.now() + 20 * 86_400_000).toISOString(), incoterms: 'DAP',
      freightAmount: 20, taxAmount: 0, paymentTerms: 'Net 30', notes: 'Review acceptance evidence.',
      lines: [{ lineNumber: 1, rfqItemId: sourcingCase.rfqItemId,
        commercialDemandLineId: sourcingCase.commercialDemandLineId,
        partNumber: required('E2E_CORE_OUT_OF_STOCK_PART'), manufacturer: 'Nexora Acceptance Controls',
        supplierPartNumber: 'MER-LOW-001', description: 'Low confidence supplier response',
        quantity: 12, availableQuantity: 12, unitOfMeasure: 'EA', unitPrice: 447,
        minimumOrderQuantity: 1, leadTimeDays: 10, availabilityType: 'AVAILABLE_TO_ORDER',
        originCountry: 'US', warranty: '12 months', isAlternate: false, exceptions: null,
        evidence: [{ fieldName: 'UnitPrice', originalValue: '$447?', normalizedValue: '447',
          confidence: 0.61, method: 'LOCAL_OCR', modelOrRuleVersion: 'local-ocr/v1',
          sourcePage: 1, sourceRegion: 'page:1:price', critical: true }] }], evidence: [],
    }, commandHeaders('commercial-v2-low-confidence-capture')));
    supplierQuoteId = captured.supplierQuoteId;
  }
  let detail = await jsonOk<SupplierQuoteDetail>(await api(page, token, 'get', `/api/supplier-quote-inbox/${supplierQuoteId}`));
  const revision = detail.revisions[0];
  const evidence = revision.evidence.find((item) => item.confidence === 0.61)!;
  expect(evidence).toBeTruthy();
  await page.goto(`/procurement/supplier-quotes/${supplierQuoteId}`);
  await expect(page.getByText('61%')).toBeVisible();
  if (!evidence.latestReviewStatus) {
    const response = await api(page, token, 'post',
      `/api/supplier-quote-inbox/${supplierQuoteId}/revisions/${revision.revisionId}/evidence/${evidence.id}/reviews`,
      { status: 'ACCEPTED', correctedValue: null, reason: 'Verified against authorized source evidence.' },
      { 'X-Correlation-ID': 'commercial-v2-low-confidence-review' });
    expect(response.status()).toBe(204);
    detail = await jsonOk<SupplierQuoteDetail>(await api(page, token, 'get', `/api/supplier-quote-inbox/${supplierQuoteId}`));
  }
  expect(detail.revisions[0].evidence.find((item) => item.id === evidence.id)?.latestReviewStatus).toBe('ACCEPTED');
});

test('14 offer comparison remains grounded in persisted landed cost and lead time', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  await captureAndProjectOffers(page, token);
  await page.goto(`/procurement/rfqs/${rfqId()}/sourcing`);
  await page.getByRole('tab', { name: /Supplier offers/i }).click();
  await expect(page.getByRole('row').filter({ hasText: 'V2-ATLAS-001' })).toContainText(/\$449\.00.*9 days/s);
});

test('15 Supplier selection is retained as the governed sourcing award', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const workbench = await getWorkbench(page, token);
  const line = workbench.lines.find((item) => item.partNumber === required('E2E_CORE_OUT_OF_STOCK_PART'))!;
  const award = workbench.awards.find((item) => item.rfqItemId === line.id);
  expect(award?.supplierName).toBe('Atlas Automation Partners');
});

test('16 Customer Quote shows the selected Supplier cost source', async ({ page }) => {
  await loginAs(page, 'manager');
  await page.goto(`/sales/quotes/view/${quoteId()}`);
  await expect(page.getByText('SELECTED SUPPLIER QUOTE')).toBeVisible();
  await expect(page.getByText('Atlas Automation Partners', { exact: false })).toBeVisible();
});

test('17 Customer Quote blocks silent use of insufficient Supplier validity', async ({ page }) => {
  await loginAs(page, 'manager');
  await page.goto(`/sales/quotes/view/${quoteId()}`);
  await expect(page.getByText('Supplier validity does not support this Customer Quote')).toBeVisible();
});

test('18 quote lifecycle preserves completed follow-up history', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const followUps = await jsonOk<Array<{ quoteId: number; nexoraSerial: string; status: string }>>(
    await api(page, token, 'get', '/api/commercial-intelligence/follow-ups'),
  );
  expect(followUps.some((item) => item.quoteId === quoteId()
    && item.nexoraSerial === required('E2E_CORE_NEXORA_SERIAL') && item.status === 'Completed')).toBe(true);
  await page.goto('/sales/follow-ups');
  await expect(page.getByRole('heading', { name: 'Follow-ups' })).toBeVisible();
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
  await expect(page.getByRole('button', { name: 'Customer Order', exact: true })).toBeVisible();
});

test('24 sourced Customer Order line creates a lineage-complete procurement handoff', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const handoff = await ensureProcurementHandoff(page, token);
  expect(handoff.customerOrderLineId).toBe(requiredNumber('E2E_V2_SOURCED_CUSTOMER_ORDER_LINE_ID'));
  expect(handoff.nexoraSerial).toBe(required('E2E_CORE_NEXORA_SERIAL'));
  await page.goto('/procurement/handoffs');
  await expect(page.getByRole('heading', { name: 'Procurement Handoffs' })).toBeVisible();
  await expect(page.getByText(handoff.customerOrderNumber)).toBeVisible();
  await expect(page.getByText(handoff.nexoraSerial, { exact: true })).toBeVisible();
  await expect(page.getByText(handoff.supplierName)).toBeVisible();
  await fs.mkdir(evidenceDir, { recursive: true });
  await page.screenshot({ path: path.join(evidenceDir, 'procurement-handoff-created.png'), fullPage: true });
});

test('25 external Supplier PO reference is linked through controlled UI and remains non-authoritative', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  let handoff = await ensureProcurementHandoff(page, token);
  await page.goto('/procurement/handoffs');
  const row = page.getByRole('row').filter({ hasText: handoff.customerOrderNumber });
  await row.getByRole('button', { name: /external reference|Update status/ }).click();
  await page.getByLabel('External Supplier PO number').fill('EXT-V2-PO-9001');
  await page.getByLabel('External Supplier PO line').fill('10');
  await page.getByRole('button', { name: 'Save reference' }).click();
  await expect(page.getByRole('dialog')).not.toBeVisible();
  handoff = await jsonOk<ProcurementHandoff>(await api(page, token, 'get', `/api/procurement-handoffs/${handoff.id}`));
  expect(handoff.externalSupplierPoNumber).toBe('EXT-V2-PO-9001');
  expect(handoff.isAuthoritative).toBe(false);
  expect(handoff.status).toBe(handoff.externalStatus);
  expect(handoff.externalStatus).toBe('EXTERNAL_PO_CREATED');
  await page.goto('/procurement/handoffs');
  await expect(page.getByText('EXT-V2-PO-9001')).toBeVisible();
  await expect(page.getByText('Not authoritative')).toBeVisible();
  await expect(page.getByText(/Authorized manual entry/)).toBeVisible();
});

test('26 Customer Order and procurement evidence update commercial memory', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const handoff = await ensureProcurementHandoff(page, token);
  const suppliers = await jsonOk<Array<{ supplierId: number; evidence: Array<{ recordType: string; recordId: number }> }>>(
    await api(page, token, 'get', '/api/commercial-learning/suppliers?limit=100'),
  );
  const supplier = suppliers.find((item) => item.supplierId === handoff.supplierId)!;
  expect(supplier.evidence.some((item) => item.recordType === 'ProcurementHandoff' && item.recordId === handoff.id)).toBe(true);
  const customers = await jsonOk<Array<{ wonCount: number; evidence: Array<{ role: string }> }>>(
    await api(page, token, 'get', '/api/commercial-learning/customers?limit=100'),
  );
  expect(customers.some((item) => item.wonCount > 0
    && item.evidence.some((evidence) => evidence.role === 'CUSTOMER_ORDER_WIN'))).toBe(true);
  await page.goto('/intelligence/commercial-memory');
  await page.getByRole('tab', { name: 'Supplier evaluation' }).click();
  await expect(page.getByText(handoff.supplierName)).toBeVisible();
});

test('27 role without Orders access cannot read or mutate procurement handoffs', async ({ page }) => {
  const token = await loginAs(page, 'denied');
  const read = await api(page, token, 'get', '/api/procurement-handoffs?limit=10');
  expect(read.status()).toBe(403);
  const create = await api(page, token, 'post', '/api/procurement-handoffs', {
    customerOrderLineId: requiredNumber('E2E_V2_SOURCED_CUSTOMER_ORDER_LINE_ID'),
    destinationType: 'DROP_SHIP', deliveryLocation: 'Denied',
  }, commandHeaders('commercial-v2-denied-handoff'));
  expect(create.status()).toBe(403);
  await page.goto('/procurement/handoffs');
  await expect(page.getByRole('alert')).toContainText('Access Denied');
  await expect(page.getByRole('button', { name: 'Create handoff' })).toHaveCount(0);
});

test('28 procurement handoff is non-disclosing across authenticated tenants', async ({ page }) => {
  const managerToken = await loginAs(page, 'manager');
  const handoff = await ensureProcurementHandoff(page, managerToken);
  const otherToken = await loginAsOtherTenant(page);
  const direct = await api(page, otherToken, 'get', `/api/procurement-handoffs/${handoff.id}`);
  expect(direct.status()).toBe(404);
  const search = await jsonOk<ProcurementHandoff[]>(await api(
    page, otherToken, 'get', `/api/procurement-handoffs?search=${encodeURIComponent(handoff.nexoraSerial)}&limit=10`,
  ));
  expect(search).toHaveLength(0);
});

test('29 RFQ Command Workspace remains usable on mobile', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 });
  await loginAs(page, 'manager');
  await page.goto(`/procurement/rfqs/view/${requiredNumber('E2E_CORE_RFQ_ID')}`);
  await expect(page.getByRole('button', { name: /Total lines/i })).toBeVisible();
  await expect(page.locator('body')).not.toHaveCSS('overflow-x', 'scroll');
  await page.goto('/procurement/handoffs');
  await expect(page.getByRole('heading', { name: 'Procurement Handoffs' })).toBeVisible();
  await expect(page.getByText(required('E2E_CORE_NEXORA_SERIAL'), { exact: true })).toBeVisible();
  await expect(page.getByText('EXT-V2-PO-9001')).toBeVisible();
  await expect(page.getByText(/DROP SHIP/)).toBeVisible();
  await expect(page.getByText('Not authoritative')).toBeVisible();
  await expect(page.getByText(/Authorized manual entry/)).toBeVisible();
  await expect(page.locator('body')).not.toHaveCSS('overflow-x', 'scroll');
});

test('30 Client PO review remains usable on mobile', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 });
  const token = await loginAs(page, 'manager');
  const match = await ensureClientPoAcceptance(page, token, 'exact');
  await page.goto(`/sales/client-pos/${match.header.id}`);
  await expect(page.getByRole('heading', { name: match.header.externalPoNumber })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: 'Decision' })).toBeVisible();
});

test('31 Customer 360 preserves ownership and commercial continuity', async ({ page }) => {
  await loginAs(page, 'manager');
  await page.goto(`/customers/${requiredNumber('E2E_CORE_CUSTOMER_ID')}`);
  await expect(page.getByRole('heading', { name: required('E2E_CORE_CUSTOMER_NAME') })).toBeVisible();
  await expect(page.getByText('Account ownership and active work', { exact: true }).first()).toBeVisible();
  await expect(page.getByText(required('E2E_CORE_ACCOUNT_OWNER_NAME'), { exact: true })).toBeVisible();
  await expect(page.getByText('Commercial performance', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Follow-up and next action', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Account health', { exact: true })).toBeVisible();
  await expect(page.getByText('Recent Customer RFQs', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Recent quote outcomes', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Recent Customer Orders', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Demand profile', { exact: true }).first()).toBeVisible();
  await expect(page.getByRole('button', { name: 'Open active commercial work' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Open' }).first()).toBeVisible();
});

test('32 Sales Today is an actionable role-scoped work queue', async ({ page }) => {
  await loginAs(page, 'manager');
  await page.goto('/sales/today');
  await expect(page.getByRole('heading', { name: 'Sales today' })).toBeVisible();
  await expect(page.getByText('Team-wide commercial work that needs attention now.')).toBeVisible();
  const firstAction = page.getByRole('button', { name: 'Open' }).first();
  await expect(firstAction).toBeVisible();
  await firstAction.click();
  await expect(page).not.toHaveURL(/\/sales\/today$/);
});

test('33 commercial workspace accepts relationship-search deep links', async ({ page }) => {
  await loginAs(page, 'manager');
  await page.goto(`/commercial-cases?search=${encodeURIComponent(required('E2E_CORE_CUSTOMER_NAME'))}`);
  await expect(page.getByRole('heading', { name: 'Commercial Workspace' })).toBeVisible();
  await expect(page.getByPlaceholder(/Search by master reference/i)).toHaveValue(required('E2E_CORE_CUSTOMER_NAME'));
  const targetCase = page.getByRole('button').filter({ hasText: required('E2E_CORE_NEXORA_SERIAL') }).first();
  await expect(targetCase).toBeVisible();
  await targetCase.click();
  await expect(page.getByText(required('E2E_CORE_NEXORA_SERIAL'), { exact: true }).first()).toBeVisible();
  await expect(page.getByText(/Matched on Customer/i).first()).toBeVisible();
  await expect(page.getByText('Opportunity command view', { exact: true })).toBeVisible();
  await expect(page.getByText('Requested lines, Product match and ATP', { exact: true })).toBeVisible();
});

test('34 role Today surfaces expose persisted operational work', async ({ page }) => {
  const token = await loginAs(page, 'manager');

  const sourcing = await jsonOk<Array<{ supplierName: string }>>(await api(page, token, 'get', '/api/supplier-quote-inbox'));
  await page.goto('/sourcing/today');
  await expect(page.getByRole('heading', { name: 'Sourcing today' })).toBeVisible();
  if (sourcing.length) await expect(page.getByText(sourcing[0].supplierName, { exact: true }).first()).toBeVisible();
  await page.getByRole('button', { name: 'Open sourcing queue' }).click();
  await expect(page).toHaveURL(/\/procurement\/rfqs\/all\?state=requires-sourcing$/);

  const inventory = await jsonOk<{ metrics: Array<{ label: string; value: number }>; exceptions: Array<{ partNumber: string }> }>(
    await api(page, token, 'get', '/api/inventory-intelligence/overview'),
  );
  await page.goto('/inventory/today');
  await expect(page.getByRole('heading', { name: 'Inventory today' })).toBeVisible();
  if (inventory.metrics.length) await expect(page.getByText(inventory.metrics[0].label, { exact: true })).toBeVisible();
  if (inventory.exceptions.length) await expect(page.getByText(inventory.exceptions[0].partNumber, { exact: true }).first()).toBeVisible();
  await page.getByRole('button', { name: 'View demand intelligence' }).click();
  await expect(page).toHaveURL(/\/inventory\/demand$/);

  await page.goto('/executive/today');
  await expect(page.getByRole('heading', { name: 'Executive RFQ-to-Revenue' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Commercial Attention' })).toBeVisible();
  await page.getByRole('button', { name: /New inquiries/ }).click();
  await expect(page).toHaveURL(/\/procurement\/leads\/all$/);

  const users = await jsonOk<{ totalCount: number }>(await api(page, token, 'get', '/api/User?pageSize=500'));
  await page.goto('/admin/operations');
  await expect(page.getByRole('heading', { name: 'Tenant admin operations' })).toBeVisible();
  await expect(page.getByText('Tenant users', { exact: true })).toBeVisible();
  await expect(page.getByText(users.totalCount.toLocaleString(), { exact: true }).first()).toBeVisible();
  await page.getByRole('button', { name: 'Manage users' }).click();
  await expect(page).toHaveURL(/\/security\/users$/);
});

test('35 RFQ intelligence reconciles current coverage and explainable Digital Twin', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const intelligence = await jsonOk<RfqCommercialIntelligence>(await api(
    page, token, 'get', `/api/commercial-learning/rfqs/${rfqId()}/intelligence`,
  ));
  expect(intelligence.lines.length).toBeGreaterThan(0);
  expect(intelligence.digitalTwin.scenarios.length).toBeGreaterThanOrEqual(3);
  expect(intelligence.nextBestAction.explanation.length).toBeGreaterThan(10);

  await page.goto(`/procurement/rfqs/view/${rfqId()}`);
  await expect(page.getByText('Opportunity Digital Twin', { exact: true })).toBeVisible();
  await expect(page.getByText(intelligence.nextBestAction.label, { exact: false }).first()).toBeVisible();
  await expect(page.getByText(intelligence.digitalTwin.validity, { exact: false }).first()).toBeVisible();
  for (const scenario of intelligence.digitalTwin.scenarios.slice(0, 3)) {
    await expect(page.getByText(scenario.label, { exact: true })).toBeVisible();
  }
  if (intelligence.commercialDecision !== 'VIABLE_READY') {
    await expect(page.getByRole('button', { name: 'Prepare Quote Draft' })).toBeDisabled();
  }
  await fs.mkdir(v1EvidenceDir, { recursive: true });
  await page.screenshot({
    path: path.join(v1EvidenceDir, 'gate-02-opportunity-digital-twin.png'),
    fullPage: true,
  });
});

test('36 authenticated procurement callback becomes authoritative operational evidence', async ({ page }, testInfo) => {
  const token = await loginAs(page, 'manager');
  const handoff = await ensureProcurementHandoff(page, token);
  const callback = {
    handoffId: handoff.id,
    externalEventId: `gate-3-${testInfo.project.name}-${Date.now()}`,
    externalSupplierPoNumber: handoff.externalSupplierPoNumber || 'EXT-V2-PO-9001',
    externalSupplierPoLineNumber: '10',
    externalSalesOrderNumber: 'EXT-V2-SO-4001',
    orderedQuantity: handoff.requiredQuantity,
    approvedUnitCost: handoff.selectedUnitCost,
    expectedOn: new Date(Date.now() + 10 * 86_400_000).toISOString().slice(0, 10),
    status: handoff.status === 'CREATED' ? 'EXTERNAL_PO_CREATED' : handoff.status,
    observedOn: new Date().toISOString(),
  };
  const timestamp = Math.floor(Date.now() / 1000).toString();
  const raw = JSON.stringify(callback);
  const secret = required('E2E_PROCUREMENT_INTEGRATION_SECRET');
  const signature = createHmac('sha256', secret).update(`${timestamp}\n${raw}`).digest('hex');
  const response = await api(page, token, 'post', '/api/procurement-integrations/callbacks', callback, {
    'X-Nexora-Timestamp': timestamp,
    'X-Nexora-Signature': signature,
    'X-Correlation-ID': `gate-3-${testInfo.project.name}`,
  });
  expect(response.status()).toBe(200);

  await page.goto('/procurement/handoffs');
  await expect(page.getByText('Operational synchronization', { exact: true })).toBeVisible();
  await expect(page.getByText('Disposable ERP', { exact: false }).first()).toBeVisible();
  await expect(page.getByText('Authoritative', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Sales Order EXT-V2-SO-4001', { exact: true }).first()).toBeVisible();
  const handoffRow = page.getByRole('row').filter({ hasText: handoff.customerOrderNumber });
  await expect(handoffRow.getByRole('button', { name: 'Update status' })).toHaveCount(0);
  await fs.mkdir(v1EvidenceDir, { recursive: true });
  await page.screenshot({
    path: path.join(v1EvidenceDir, 'gate-03-procurement-operational-sync.png'),
    fullPage: true,
  });
});

test('37 local-first processing evidence and governed learning remain visible across the journey', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const processing = await jsonOk<{
    leadId: number;
    nexoraSerial: string;
    localRequestCount: number;
    externalRequestCount: number;
    externalCostAmount?: number | null;
    externalCostStatus: string;
  }>(await api(page, token, 'get', `/api/processing-evidence/rfqs/${rfqId()}`));
  expect(processing.leadId).toBe(requiredNumber('E2E_CORE_LEAD_ID'));
  expect(processing.nexoraSerial).toBe(required('E2E_CORE_NEXORA_SERIAL'));
  expect(processing.externalCostAmount).toBeNull();
  expect(processing.externalCostStatus).toBe('LocalComputeUnpriced');

  await page.goto(`/procurement/rfqs/view/${rfqId()}`);
  await expect(page.getByText('Processing evidence', { exact: true })).toBeVisible();
  await expect(page.getByText('Local-first', { exact: true })).toBeVisible();

  const [supplierQuoteId] = await captureAndProjectOffers(page, token);
  await page.goto(`/procurement/supplier-quotes/${supplierQuoteId}`);
  await expect(page.getByText('Processing evidence', { exact: true })).toBeVisible();

  const clientPo = await ensureClientPoAcceptance(page, token, 'exact');
  await page.goto(`/sales/client-pos/${clientPo.header.id}`);
  await expect(page.getByText('Processing evidence', { exact: true })).toBeVisible();

  await page.goto('/intelligence/commercial-memory');
  await page.getByRole('tab', { name: 'Learning Studio' }).click();
  await expect(page.getByRole('columnheader', { name: 'Learned signal' })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: 'Evidence status' })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: 'Governance' })).toBeVisible();
  await fs.mkdir(v1EvidenceDir, { recursive: true });
  await page.screenshot({
    path: path.join(v1EvidenceDir, 'gate-04-processing-learning.png'),
    fullPage: true,
  });
});

test('38 production readiness reconciles runtime health and tenant queues', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const readiness = await jsonOk<{
    deploymentReadiness: string;
    blockingReasons: string[];
    healthChecks: Array<{ name: string; status: string }>;
    queues: Array<{ label: string; pending: number; inFlight: number; deadLetter: number }>;
    aiLast30Days: { local: number; external: number; externalSharePercent: number; unresolved: number };
  }>(await api(page, token, 'get', '/api/operations/readiness'));

  expect(readiness.healthChecks.length).toBeGreaterThanOrEqual(5);
  expect(readiness.queues).toHaveLength(3);
  expect(readiness.aiLast30Days.externalSharePercent).toBeLessThanOrEqual(10);

  await page.goto('/admin/operations');
  await expect(page.getByRole('heading', { name: 'Production readiness' })).toBeVisible();
  await expect(page.getByText(`Runtime readiness is ${readiness.deploymentReadiness}.`, { exact: false })).toBeVisible();
  for (const reason of readiness.blockingReasons) {
    await expect(page.getByText(reason, { exact: true })).toBeVisible();
  }
  for (const check of readiness.healthChecks) {
    await expect(page.getByRole('row').filter({ hasText: check.name }).getByText(check.status, { exact: true })).toBeVisible();
  }
  for (const queue of readiness.queues) {
    const row = page.getByRole('row').filter({ hasText: queue.label });
    await expect(row).toContainText(queue.pending.toLocaleString());
    await expect(row).toContainText(queue.inFlight.toLocaleString());
    await expect(row).toContainText(queue.deadLetter.toLocaleString());
  }
  await fs.mkdir(v1EvidenceDir, { recursive: true });
  await page.screenshot({
    path: path.join(v1EvidenceDir, 'gate-05-production-readiness.png'),
    fullPage: true,
  });
});

test('39 platform owner console uses authenticated persisted operational data', async ({ page }) => {
  const failures: string[] = [];
  page.on('response', (response) => {
    if (response.url().includes('/api/platform/') && response.status() >= 400) {
      failures.push(`${response.status()} ${response.url()}`);
    }
  });

  await page.goto('/platform/overview');
  await page.getByLabel('Email').fill(required('E2E_PLATFORM_EMAIL'));
  await page.getByLabel('Password').fill(required('E2E_PLATFORM_PASSWORD'));
  await page.getByRole('button', { name: 'Enter Control Plane' }).click();
  await expect(page.getByRole('heading', { name: 'Platform Overview' })).toBeVisible();
  await expect(page.getByText('System Health', { exact: true })).toBeVisible();

  await page.getByRole('link', { name: 'Tenants' }).click();
  await expect(page.getByRole('heading', { name: 'Tenants' })).toBeVisible();
  await expect(page.getByText('Release 01C1 Acceptance', { exact: true }).first()).toBeVisible();

  await page.getByRole('button', { name: 'Provision Tenant' }).click();
  const provisionDialog = page.getByRole('dialog', { name: 'Provision New Tenant' });
  await provisionDialog.getByLabel('Organization name').fill('V1 Platform Acceptance Tenant');
  await provisionDialog.getByLabel('Slug').fill('v1-platform-acceptance');
  await provisionDialog.getByRole('combobox', { name: /Plan/ }).click();
  await page.getByRole('option', { name: 'Pro' }).click();
  await page.getByRole('button', { name: 'Provision', exact: true }).click();
  await expect(page.getByText('V1 Platform Acceptance Tenant', { exact: true })).toBeVisible();

  let tenantRow = page.getByRole('row').filter({ hasText: 'Release 01C1 Acceptance' });
  await tenantRow.getByRole('button', { name: 'Suspend' }).click();
  await page.getByLabel('Audit reason').fill('V1 acceptance lifecycle verification');
  await page.getByRole('button', { name: 'suspend', exact: true }).click();
  await expect(tenantRow.getByText('suspended', { exact: true })).toBeVisible();

  tenantRow = page.getByRole('row').filter({ hasText: 'Release 01C1 Acceptance' });
  await tenantRow.getByRole('button', { name: 'Resume' }).click();
  await page.getByLabel('Audit reason').fill('V1 acceptance lifecycle restoration');
  await page.getByRole('button', { name: 'resume', exact: true }).click();
  await expect(tenantRow.getByText('active', { exact: true })).toBeVisible();

  await tenantRow.getByRole('button', { name: 'Impersonate' }).click();
  await page.getByLabel('Audit reason').fill('V1 read-only support-session verification');
  await page.getByRole('button', { name: 'impersonate', exact: true }).click();
  await page.getByText('Release 01C1 Acceptance', { exact: true }).first().click();
  await expect(page.getByText('Tenant Registry', { exact: true })).toBeVisible();
  await expect(page.getByText(required('E2E_PLATFORM_TENANT_ID'), { exact: true })).toBeVisible();

  await page.getByRole('link', { name: 'Pipeline' }).click();
  await expect(page.getByRole('heading', { name: 'Extraction Pipeline' })).toBeVisible();
  await expect(page.getByText('Queue Depth', { exact: true })).toBeVisible();

  await page.getByRole('link', { name: 'Plans', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Plans', exact: true })).toBeVisible();
  await expect(page.getByText('Enterprise', { exact: true }).first()).toBeVisible();

  await page.getByRole('link', { name: 'Audit Log' }).click();
  await expect(page.getByRole('heading', { name: 'Audit Log' })).toBeVisible();
  await expect(page.getByText('tenant.provision', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('impersonate.issue', { exact: true }).first()).toBeVisible();
  expect(failures).toEqual([]);
});

test.afterEach(({ page }, testInfo) => {
  void page;
  expect(testInfo.annotations.filter((annotation) => annotation.type === 'skip')).toHaveLength(0);
});
