import fs from 'node:fs/promises';
import path from 'node:path';
import { createHmac } from 'node:crypto';
import { expect, test } from '@playwright/test';
import * as XLSX from 'xlsx';
import { api, apiUrl, jsonOk, loginAs, loginAsOtherTenant, required, requiredNumber } from './support/core-commercial';
import { loginThroughUi } from './support/login';

const evidenceRoot = process.env.E2E_EVIDENCE_ROOT
  ? path.resolve(process.env.E2E_EVIDENCE_ROOT)
  : path.resolve('../docs/nexora/evidence');
const evidenceDir = path.join(evidenceRoot, 'commercial-journey-v2');
const v1EvidenceDir = path.join(evidenceRoot, 'v1');
const rfqId = () => requiredNumber('E2E_CORE_RFQ_ID');
const quoteId = () => requiredNumber('E2E_CORE_QUOTE_ID');

const decodeBase32 = (value: string): Buffer => {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  const bits = value.toUpperCase().replace(/[^A-Z2-7]/g, '')
    .split('').map((character) => alphabet.indexOf(character).toString(2).padStart(5, '0')).join('');
  return Buffer.from((bits.match(/.{8}/g) ?? []).map((byte) => Number.parseInt(byte, 2)));
};

const currentTotp = (secret: string, now: number): string => {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(now / 30_000)));
  const digest = createHmac('sha1', decodeBase32(secret)).update(counter).digest();
  const offset = digest[digest.length - 1] & 0x0f;
  return ((digest.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).toString().padStart(6, '0');
};

const nextTotpAfterFixtureEnrollment = async (page: import('@playwright/test').Page): Promise<string> => {
  const secret = required('E2E_PLATFORM_TOTP_SECRET');
  const now = Date.now();
  const nextStepAt = (Math.floor(now / 30_000) + 1) * 30_000;
  await page.waitForTimeout(nextStepAt - now + 500);
  return currentTotp(secret, Date.now());
};

type Workbench = {
  nexoraSerial: string;
  lines: Array<{
    id: number;
    demandLineId?: number | null;
    productId?: number | null;
    partNumber?: string | null;
    shortfallQuantity: number;
  }>;
  solicitations: Array<{ id: number; supplierId: number; supplierName: string }>;
  offers: Array<{ id: number; rfqItemId: number; supplierName: string; version: number }>;
  awards: Array<{
    id: number;
    rfqItemId: number;
    supplierId: number;
    supplierName: string;
    currencyId: number;
    status: string;
    purchaseOrderId?: number | null;
    version: number;
  }>;
  purchaseOrders: SupplierPurchaseOrder[];
  customerQuoteDraft: { lines: Array<{ quoteItemId: number; rfqItemId: number }> } | null;
};

type SupplierPurchaseOrder = {
  id: number;
  rfqId: number;
  purchaseOrderNumber: string;
  supplierId: number;
  supplierName: string;
  currencyId: number;
  status: string;
  version: number;
  lines: Array<{
    id: number;
    rfqItemId: number;
    productId: number;
    orderedQuantity: number;
    receivedQuantity: number;
    warehouseId: number;
  }>;
};

type InboundShipment = {
  id: number;
  purchaseOrderId: number;
  shipmentNumber: string;
  trackingReference?: string | null;
  milestone: string;
  receiptState: string;
  receiptedQuantity: number;
  outstandingReceiptQuantity: number;
  version: number;
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

type CustomerOrder = {
  id: number;
  orderNo: string;
  customerId: number;
  statusId: number;
  status: string;
  currencyId?: number | null;
  commercialCaseId?: number | null;
  nexoraSerial?: string | null;
  items: Array<{ id: number; productId: number; warehouseId?: number | null; quantity: number }>;
};

type InventoryAvailability = {
  inventoryId: number;
  productId: number;
  warehouseId?: number | null;
  onHand: number;
  reserved: number;
  available: number;
};

type StockReservation = {
  status: string;
  demandReference: string;
  quantity: number;
};

type Shipment = {
  id: number;
  shipmentNo: string;
  orderId: number;
  orderNo: string;
  deliveryStatus: string;
  items: Array<{ id: number; orderItemId: number; quantity: number }>;
};

type DeliveryProof = {
  id: number;
  shipmentId: number;
  outcome: string;
  lines: Array<{ shipmentItemId: number; acceptedQuantity: number; refusedQuantity: number }>;
};

type ReceivableDocument = {
  id: number;
  commercialCaseId?: number | null;
  customerId: number;
  orderId?: number | null;
  currencyId?: number | null;
  documentNumber?: string | null;
  status: string;
  documentDate: string;
  totalAmount: number;
  allocatedAmount: number;
  outstandingAmount: number;
  version: number;
};

type CustomerPayment = {
  id: number;
  receiptNumber: string;
  bankReference?: string | null;
  status: string;
  amount: number;
  allocatedAmount: number;
  unappliedAmount: number;
  journalEntryId?: number | null;
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
    mode: string;
    policyVersion: string;
    scenarios: Array<{ code: string; label: string; eligible: boolean; explanation: string; riskBand: string; riskExplanation: string; quantities: unknown[]; costSources: unknown[]; approvalRequirements: string[] }>;
    predictivePricing: Array<{ rfqItemId: number; status: string; mode: string }>;
    customerTargetBridges: unknown[];
    backtest: { status: string; holdoutCount: number };
  };
};

const commandHeaders = (key: string) => ({
  'Idempotency-Key': key,
  'X-Correlation-ID': key,
});

async function loginAsFinance(page: Parameters<typeof api>[0]): Promise<string> {
  await loginThroughUi(page, {
    email: required('E2E_FINANCE_EMAIL'),
    password: required('E2E_FINANCE_PASSWORD'),
    businessUnitId: required('E2E_FINANCE_BUSINESS_UNIT_ID'),
  });
  const token = await page.evaluate(() => localStorage.getItem('token'));
  if (!token) throw new Error('Authenticated finance session did not contain an access token.');
  return token;
}

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
  let workbench = await getWorkbench(page, token);
  const governedSupplierNames = ['Atlas Automation Partners', 'Meridian Process Equipment'];
  if (workbench.solicitations.filter((item) => governedSupplierNames.includes(item.supplierName)).length < 2) {
    await page.goto(`/procurement/sourcing-cases/${sourcingCase.id}`);
    for (const supplierName of governedSupplierNames) {
      const checkbox = page.getByRole('checkbox', { name: `Select ${supplierName}` });
      if (!(await checkbox.isChecked())) await checkbox.check();
    }
    await page.getByRole('button', { name: 'Prepare and Queue Supplier RFQ' }).click();
    await expect(page.getByRole('heading', { name: 'Approve Supplier RFQ Delivery' })).toBeVisible();
    await page.getByRole('button', { name: 'Approve and Queue' }).click();
    await expect(page).toHaveURL(new RegExp(`/procurement/rfqs/${rfqId()}/sourcing`));
    workbench = await getWorkbench(page, token);
  }
  const selected = workbench.solicitations
    .filter((item) => governedSupplierNames.includes(item.supplierName));
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

async function ensureAtlasAward(
  page: Parameters<typeof api>[0], token: string,
): Promise<Workbench['awards'][number]> {
  await captureAndProjectOffers(page, token);
  let workbench = await getWorkbench(page, token);
  const line = workbench.lines.find((item) => item.partNumber === required('E2E_CORE_OUT_OF_STOCK_PART'))!;
  let award = workbench.awards.find((item) => item.rfqItemId === line.id
    && item.supplierName === 'Atlas Automation Partners');
  if (!award) {
    const offer = workbench.offers.find((item) => item.supplierName === 'Atlas Automation Partners')!;
    await jsonOk(await api(page, token, 'post', '/api/procurement/awards', {
      supplierQuotedItemId: offer.id,
      quantity: line.shortfallQuantity,
      expectedQuoteVersion: offer.version,
      rationale: 'Best eligible landed cost with shorter verified lead time.',
    }, commandHeaders('commercial-v2-award-atlas')));
    workbench = await getWorkbench(page, token);
    award = workbench.awards.find((item) => item.rfqItemId === line.id
      && item.supplierName === 'Atlas Automation Partners');
  }
  expect(award, 'The Atlas supplier offer must become the governed sourcing award.').toBeTruthy();
  return award!;
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
  await expect(page.getByText(/Open the Lead decision record's Evidence stage/i)).toBeVisible();
  const exactEvidence = page.getByRole('button', { name: 'Open exact source evidence' });
  await expect(exactEvidence).toBeVisible();
  await fs.mkdir(evidenceDir, { recursive: true });
  await page.screenshot({ path: path.join(evidenceDir, 'rfq-command-workspace.png'), fullPage: true });
  await exactEvidence.click();
  await expect(page).toHaveURL(new RegExp(
    `/procurement/leads/${requiredNumber('E2E_CORE_LEAD_ID')}/workbench\\?stage=evidence$`,
  ));
  await expect(page.getByRole('tab', { name: '1. Evidence' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('heading', { name: 'Source evidence' })).toBeVisible();
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
    await page.getByRole('button', { name: 'Prepare and Queue Supplier RFQ' }).click();
    await expect(page.getByRole('heading', { name: 'Approve Supplier RFQ Delivery' })).toBeVisible();
    await page.getByRole('button', { name: 'Approve and Queue' }).click();
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
  await expect(page.getByText(/Drop ship/i)).toBeVisible();
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
  await expect(page.getByText('Commercial work across your managed teams that needs attention now.')).toBeVisible();
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
  // /executive/today and /dashboard are the same screen, and it is no longer the "Executive view":
  // it is one dashboard a rep, a manager and a director all read, so the heading is plain and the
  // rail row is open to everyone holding the module. The verified Release 01 snapshot survives the
  // redesign but only its MEASURED rows are stated as figures — the fourteen that can never become
  // available are counted in a sentence instead of rendered as fourteen permanent "not available"
  // cards. The route into the full board is still here, under the words it actually offers.
  await expect(page.getByRole('heading', { name: 'Dashboard', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Verified performance' })).toBeVisible();
  await page.getByRole('button', { name: 'Every deadline in full' }).click();
  await expect(page).toHaveURL(/\/analytics\/deadlines$/);

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
  expect(intelligence.digitalTwin.mode).toBe('SHADOW');
  expect(intelligence.digitalTwin.policyVersion).toBe('digital-twin-v2.3');
  expect(intelligence.digitalTwin.scenarios).toHaveLength(9);
  expect(intelligence.digitalTwin.scenarios.map(scenario => scenario.code)).toEqual(expect.arrayContaining([
    'STOCK_ONLY', 'SUPPLIER_ONLY', 'SPLIT_STOCK_SOURCE', 'FASTEST_DELIVERY',
    'LOWEST_LANDED_COST', 'BEST_MARGIN', 'LOWEST_RISK', 'APPROVED_ALTERNATE', 'PARTIAL_IMMEDIATE',
  ]));
  expect(intelligence.digitalTwin.predictivePricing).toHaveLength(intelligence.lines.length);
  expect(intelligence.nextBestAction.explanation.length).toBeGreaterThan(10);
  const blockedApply = await api(page, token, 'post', `/api/intelligence/rfqs/${rfqId()}/apply-pricing`, {
    lines: [{ rfqItemId: intelligence.lines[0].rfqItemId, unitPrice: 1 }],
  });
  expect(blockedApply.status()).toBe(409);

  await page.goto(`/procurement/rfqs/view/${rfqId()}`);
  await expect(page.getByText('Opportunity Digital Twin', { exact: true })).toBeVisible();
  await expect(page.getByText(intelligence.nextBestAction.label, { exact: false }).first()).toBeVisible();
  await expect(page.getByText(intelligence.digitalTwin.validity, { exact: false }).first()).toBeVisible();
  for (const scenario of intelligence.digitalTwin.scenarios) {
    await expect(page.getByText(scenario.label, { exact: true })).toBeVisible();
  }
  await expect(page.getByText('Predictive pricing · shadow mode', { exact: true })).toBeVisible();
  await expect(page.getByText('Customer target bridge', { exact: true })).toBeVisible();
  const firstScenarioEvidence = page.locator('details').first();
  await firstScenarioEvidence.locator('summary').click();
  await expect(firstScenarioEvidence.getByText(intelligence.digitalTwin.scenarios[0].riskExplanation, { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Smart Pricing' }).click();
  await expect(page).toHaveURL(new RegExp(`/procurement/rfqs/${rfqId()}/pricing$`));
  await expect(page.getByRole('heading', { name: 'Shadow pricing workspace' })).toBeVisible();
  await expect(page.getByText(/governed Supplier award pricing bridge/i)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Apply pricing' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Return to RFQ' }).click();
  await expect(page).toHaveURL(new RegExp(`/procurement/rfqs/view/${rfqId()}$`));
  // The client gate now matches the server: a draft is refused ONLY on NO_QUOTE_REVIEW.
  // This assertion previously read `!== 'VIABLE_READY'`, which pinned a rule the server had
  // already abandoned — it required every line covered by stock or an approved offer, so the
  // button stayed disabled for any RFQ needing sourcing, i.e. the normal case for a
  // distributor. The suite stayed green while enforcing it, which is why nobody noticed.
  if (intelligence.commercialDecision === 'NO_QUOTE_REVIEW') {
    await expect(page.getByRole('button', { name: 'Prepare Quote Draft' })).toBeDisabled();
  } else {
    await expect(page.getByRole('button', { name: 'Prepare Quote Draft' })).toBeEnabled();
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
    aiExternalDependency: { local: number; external: number; authorizedExternal: number; externalSharePercent: number; ceilingPercent: number; ceilingBreached: boolean; unresolved: number };
  }>(await api(page, token, 'get', '/api/operations/readiness'));

  expect(readiness.healthChecks.length).toBeGreaterThanOrEqual(5);
  expect(readiness.queues).toHaveLength(3);
  expect(readiness.aiExternalDependency.ceilingBreached).toBe(false);

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
  await page.getByRole('textbox', { name: 'Password', exact: true }).fill(required('E2E_PLATFORM_PASSWORD'));
  await page.getByRole('button', { name: 'Enter Control Plane' }).click();
  await page.getByLabel('6-digit authenticator code').fill(await nextTotpAfterFixtureEnrollment(page));
  await page.getByRole('button', { name: 'Verify and enter' }).click();
  await expect(page.getByRole('heading', { name: 'Platform Overview' })).toBeVisible();
  await expect(page.getByText('System Health', { exact: true })).toBeVisible();

  await page.getByRole('link', { name: 'Tenants' }).click();
  await expect(page.getByRole('heading', { name: 'Tenants' })).toBeVisible();
  await expect(page.getByText('Release 01C1 Acceptance', { exact: true }).first()).toBeVisible();

  await page.getByRole('button', { name: 'Create Company' }).click();
  const provisionDialog = page.getByRole('dialog', { name: 'Create a company workspace' });
  await provisionDialog.getByLabel('Organization name').fill('V1 Platform Acceptance Tenant');
  await provisionDialog.getByLabel('Workspace slug').fill('v1-platform-acceptance');
  await provisionDialog.getByLabel('Company contact email').fill('contact@v1-platform-acceptance.local');
  await provisionDialog.getByLabel('Address line 1').fill('1 Acceptance Way');
  await provisionDialog.getByLabel('City').fill('Riyadh');
  await provisionDialog.getByLabel('Country of registration').click();
  await page.getByRole('option', { name: /Saudi Arabia/ }).click();
  await provisionDialog.getByRole('button', { name: 'Next' }).click();

  await provisionDialog.getByRole('combobox', { name: 'Plan' }).click();
  await page.getByRole('option', { name: 'Pro' }).click();
  await provisionDialog.getByLabel('Billing contact name').fill('V1 Billing Contact');
  await provisionDialog.getByLabel('Billing contact email').fill('billing@v1-platform-acceptance.local');
  await provisionDialog.getByLabel('Account owner email (internal)').fill('owner@acceptance.local');
  await provisionDialog.getByRole('button', { name: 'Next' }).click();

  await provisionDialog.getByLabel('First name').fill('V1');
  await provisionDialog.getByLabel('Last name').fill('Administrator');
  await provisionDialog.getByLabel('Work email').fill('admin@v1-platform-acceptance.local');
  await provisionDialog.getByRole('button', { name: 'Next' }).click();
  await provisionDialog.getByRole('button', { name: 'Create workspace' }).click();
  const provisioningProgress = page.getByRole('dialog', {
    name: 'Provisioning V1 Platform Acceptance Tenant',
  });
  await expect(provisioningProgress.getByText('Succeeded', { exact: true }).first()).toBeVisible();
  await provisioningProgress.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.getByText('V1 Platform Acceptance Tenant', { exact: true }).first()).toBeVisible();

  let tenantRow = page.getByRole('row').filter({ hasText: 'Release 01C1 Acceptance' });
  await tenantRow.getByRole('button', { name: 'Impersonate tenant', exact: true }).click();
  await page.getByLabel('Audit reason').fill('V1 read-only support-session verification');
  await page.getByRole('button', { name: 'Impersonate', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Impersonating Release 01C1 Acceptance — read-only' }))
    .toBeVisible();
  await page.getByRole('button', { name: 'Exit impersonation', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Tenants' })).toBeVisible();

  // Exercise destructive lifecycle controls against the seeded secondary tenant, whose own
  // cross-tenant scenario has already finished. The newly provisioned tenant is still in its
  // honest Provisioning state here, while suspending the primary acceptance tenant makes every
  // later commercial login fail.
  tenantRow = page.getByRole('row').filter({ hasText: 'Release 01C1 Other Tenant' });
  await tenantRow.getByRole('button', { name: 'Suspend tenant', exact: true }).click();
  await page.getByLabel('Audit reason').fill('V1 acceptance lifecycle verification');
  await page.getByRole('button', { name: 'Suspend', exact: true }).click();
  await expect(tenantRow.getByText('suspended', { exact: true })).toBeVisible();

  tenantRow = page.getByRole('row').filter({ hasText: 'Release 01C1 Other Tenant' });
  await tenantRow.getByRole('button', { name: 'Resume tenant', exact: true }).click();
  await page.getByLabel('Audit reason').fill('V1 acceptance lifecycle restoration');
  const refusedResume = page.waitForResponse(
    (response) => response.request().method() === 'POST'
      && /\/api\/platform\/tenants\/\d+\/resume$/.test(response.url()),
  );
  await page.getByRole('button', { name: 'Resume', exact: true }).click();
  expect((await refusedResume).status()).toBe(409);
  await expect(page.getByRole('dialog', { name: 'Resume tenant' })).toBeVisible();
  await page.getByRole('dialog', { name: 'Resume tenant' })
    .getByRole('button', { name: 'Cancel', exact: true })
    .click();
  await expect(tenantRow.getByText('suspended', { exact: true })).toBeVisible();

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
  expect(failures).toEqual([
    expect.stringMatching(/^409 .*\/api\/platform\/tenants\/\d+\/resume$/),
  ]);
});

test('40 accepted shipment closes order to cash with replay safety', async ({ page }) => {
  let token = await loginAs(page, 'manager');
  const match = await ensureClientPoAcceptance(page, token, 'exact');
  expect(match.header.customerOrderId).toBeTruthy();
  const orderId = match.header.customerOrderId!;
  const order = await jsonOk<CustomerOrder>(await api(page, token, 'get', `/api/Order/${orderId}`));
  expect(order.items).toHaveLength(1);
  expect(order.nexoraSerial).toBe(required('E2E_V2_CLIENT_PO_EXACT_NEXORA_SERIAL'));

  const allocation = await jsonOk<{ orderId: number; fullyAllocated: boolean }>(
    await api(page, token, 'post', `/api/Order/${orderId}/allocate`),
  );
  expect(allocation.orderId).toBe(orderId);
  expect(allocation.fullyAllocated).toBe(true);

  const shipments = await jsonOk<Shipment[]>(await api(page, token, 'get', '/api/Shipment'));
  let shipment = shipments.find((item) => item.orderId === orderId);
  const availabilityBeforeShipment = shipment ? null : await jsonOk<InventoryAvailability[]>(
    await api(page, token, 'get', '/api/inventory-intelligence/availability'),
  );
  if (!shipment) {
    shipment = await jsonOk<Shipment>(await api(page, token, 'post', '/api/Shipment', {
      orderId,
      businessUnitId: requiredNumber('E2E_MANAGER_BUSINESS_UNIT_ID'),
      statusId: requiredNumber('E2E_V2_SHIPMENT_STATUS_ID'),
      shipmentDate: new Date().toISOString(),
      estimatedDeliveryDate: new Date(Date.now() + 86_400_000).toISOString(),
      carrier: 'Nexora Acceptance Carrier',
      serviceLevel: 'Controlled acceptance',
      trackingNumber: 'V2-OTC-TRACK-001',
      externalId: 'commercial-v2-order-to-cash-shipment',
      shippingAddress: 'ABC Engineering acceptance dock',
      notes: 'Controlled order-to-cash acceptance shipment.',
      items: order.items.map((item) => ({ orderItemId: item.id, quantity: item.quantity })),
    }));
  }
  expect(shipment.items).toHaveLength(order.items.length);
  expect(shipment.deliveryStatus).toBe('DISPATCHED');

  const consumedReservations = await jsonOk<StockReservation[]>(
    await api(page, token, 'get', '/api/inventory-intelligence/reservations?status=Consumed'),
  );
  expect(consumedReservations.some((item) => item.demandReference === `Order ${orderId}`
    && item.quantity === order.items[0].quantity)).toBe(true);
  const reconciliation = await jsonOk<{ balanced: boolean; driftCount: number }>(
    await api(page, token, 'get', '/api/inventory-intelligence/stock/reconciliation?driftOnly=true'),
  );
  expect(reconciliation).toEqual(expect.objectContaining({ balanced: true, driftCount: 0 }));
  if (availabilityBeforeShipment) {
    const availabilityAfterShipment = await jsonOk<InventoryAvailability[]>(
      await api(page, token, 'get', '/api/inventory-intelligence/availability'),
    );
    for (const item of order.items) {
      const before = availabilityBeforeShipment.find((row) => row.productId === item.productId
        && (!item.warehouseId || row.warehouseId === item.warehouseId));
      const after = availabilityAfterShipment.find((row) => row.inventoryId === before?.inventoryId);
      expect(before, `Order line ${item.id} must resolve to authoritative stock.`).toBeTruthy();
      expect(after?.onHand).toBe(before!.onHand - item.quantity);
      expect(after?.reserved).toBe(before!.reserved - item.quantity);
    }
  }

  const duplicateShipment = await api(page, token, 'post', '/api/Shipment', {
    orderId,
    businessUnitId: requiredNumber('E2E_MANAGER_BUSINESS_UNIT_ID'),
    statusId: requiredNumber('E2E_V2_SHIPMENT_STATUS_ID'),
    shipmentDate: new Date().toISOString(),
    carrier: 'Nexora Acceptance Carrier',
    externalId: 'commercial-v2-order-to-cash-shipment-replay',
    shippingAddress: 'ABC Engineering acceptance dock',
    items: order.items.map((item) => ({ orderItemId: item.id, quantity: item.quantity })),
  });
  expect(duplicateShipment.status()).toBe(409);
  const shipmentReplayCheck = await jsonOk<Shipment[]>(await api(page, token, 'get', '/api/Shipment'));
  expect(shipmentReplayCheck.filter((item) => item.orderId === orderId)).toHaveLength(1);

  const confirmationPath = `/api/delivery/shipments/${shipment.id}/confirmation`;
  const currentConfirmation = await api(page, token, 'get', confirmationPath);
  let confirmation: DeliveryProof;
  if (currentConfirmation.status() === 404) {
    const confirmationCommand = {
      receivedByName: 'Amira Cole',
      receivedByContact: 'procurement@abc-engineering.local',
      receivedByPosition: 'Acceptance Receiving',
      receivedOn: new Date().toISOString(),
      signatureEvidenceId: null,
      stampEvidenceId: null,
      photoEvidenceId: null,
      gpsLatitude: null,
      gpsLongitude: null,
      gpsAccuracyMeters: null,
      gpsCapturedOn: null,
      notes: 'Controlled order-to-cash acceptance proof.',
      lines: shipment.items.map((item) => ({
        shipmentItemId: item.id,
        acceptedQuantity: item.quantity,
        exceptionReasonCode: null,
        exceptionNote: null,
        notes: 'Accepted in full.',
      })),
    };
    confirmation = await jsonOk<DeliveryProof>(await api(
      page, token, 'post', confirmationPath, confirmationCommand,
      commandHeaders('commercial-v2-delivery-confirmation'),
    ));
    const confirmationReplay = await jsonOk<DeliveryProof>(await api(
      page, token, 'post', confirmationPath, confirmationCommand,
      commandHeaders('commercial-v2-delivery-confirmation'),
    ));
    expect(confirmationReplay.id).toBe(confirmation.id);
  } else {
    confirmation = await jsonOk<DeliveryProof>(currentConfirmation);
  }
  expect(confirmation.shipmentId).toBe(shipment.id);
  expect(confirmation.outcome).toBe('DELIVERED');
  expect(confirmation.lines.every((line) => line.refusedQuantity === 0)).toBe(true);
  shipment = await jsonOk<Shipment>(await api(page, token, 'get', `/api/Shipment/${shipment.id}`));
  expect(shipment.deliveryStatus).toBe('DELIVERED');
  const deliveredOrder = await jsonOk<CustomerOrder>(await api(page, token, 'get', `/api/Order/${orderId}`));
  expect(deliveredOrder.status.toUpperCase()).toBe('DELIVERED');

  await page.goto(`/sales/shipments/${shipment.id}`);
  await expect(page.getByRole('heading', { name: `Shipment ${shipment.shipmentNo}` })).toBeVisible();
  await expect(page.getByText('Delivered', { exact: true }).first()).toBeVisible();
  await expect(page.getByText(order.orderNo, { exact: true })).toBeVisible();

  const documentsPath = `/api/commercial-finance/documents?customerId=${order.customerId}`;
  expect((await api(page, token, 'get', documentsPath)).status()).toBe(200);
  expect((await api(page, token, 'get', `/api/commercial-finance/payments?customerId=${order.customerId}`)).status()).toBe(403);
  expect((await api(page, token, 'post', '/api/commercial-finance/payments', {
    customerId: order.customerId,
    commercialCaseId: order.commercialCaseId ?? null,
    currencyId: order.currencyId ?? null,
    paymentDate: '2026-08-29T00:00:00.000Z',
    amount: 1,
    method: 'BankTransfer',
    bankAccountId: requiredNumber('E2E_V2_BANK_ACCOUNT_ID'),
    bankReference: 'MANAGER-MUST-NOT-POST-CASH',
    allocations: [],
  }, commandHeaders('commercial-v2-sales-manager-payment-denied'))).status()).toBe(403);
  expect((await api(
    page, token, 'post', `/api/commercial-finance/orders/${orderId}/invoices`, {
      documentDate: null,
      dueDate: null,
      lines: order.items.map((item) => ({ orderItemId: item.id, quantity: item.quantity })),
    }, commandHeaders('commercial-v2-sales-manager-invoice-denied'),
  )).status()).toBe(403);

  token = await loginAsFinance(page);
  let documents = await jsonOk<ReceivableDocument[]>(await api(page, token, 'get', documentsPath));
  let invoice = documents.find((item) => item.orderId === orderId);
  if (!invoice) {
    const invoiceCommand = {
      documentDate: null,
      dueDate: null,
      lines: order.items.map((item) => ({ orderItemId: item.id, quantity: item.quantity })),
    };
    invoice = await jsonOk<ReceivableDocument>(await api(
      page, token, 'post', `/api/commercial-finance/orders/${orderId}/invoices`, invoiceCommand,
      commandHeaders('commercial-v2-order-invoice'),
    ));
    const invoiceReplay = await jsonOk<ReceivableDocument>(await api(
      page, token, 'post', `/api/commercial-finance/orders/${orderId}/invoices`, invoiceCommand,
      commandHeaders('commercial-v2-order-invoice'),
    ));
    expect(invoiceReplay.id).toBe(invoice.id);
  }
  if (invoice.status === 'Draft') {
    invoice = await jsonOk<ReceivableDocument>(await api(
      page, token, 'post', `/api/commercial-finance/documents/${invoice.id}/issue`,
      { expectedVersion: invoice.version },
    ));
  }
  expect(invoice.status).toBe('Issued');
  expect(invoice.totalAmount).toBeGreaterThan(0);

  const paymentCommand = {
    customerId: order.customerId,
    commercialCaseId: order.commercialCaseId ?? null,
    currencyId: order.currencyId ?? null,
    // Stable across a Playwright retry or a retained-database rerun: an idempotency key must
    // describe one immutable command, not the wall clock of whichever retry happened to run.
    paymentDate: invoice.documentDate,
    amount: invoice.totalAmount,
    method: 'BankTransfer',
    bankAccountId: requiredNumber('E2E_V2_BANK_ACCOUNT_ID'),
    bankReference: 'COMMERCIAL-V2-CASH-001',
    allocations: [{ receivableDocumentId: invoice.id, amount: invoice.totalAmount }],
  };
  const payment = await jsonOk<CustomerPayment>(await api(
    page, token, 'post', '/api/commercial-finance/payments', paymentCommand,
    commandHeaders('commercial-v2-customer-payment'),
  ));
  const paymentReplay = await jsonOk<CustomerPayment>(await api(
    page, token, 'post', '/api/commercial-finance/payments', paymentCommand,
    commandHeaders('commercial-v2-customer-payment'),
  ));
  expect(paymentReplay.id).toBe(payment.id);
  expect(payment.status).toBe('Posted');
  expect(payment.allocatedAmount).toBe(payment.amount);
  expect(payment.unappliedAmount).toBe(0);
  expect(payment.journalEntryId).toBeGreaterThan(0);

  invoice = await jsonOk<ReceivableDocument>(await api(
    page, token, 'get', `/api/commercial-finance/documents/${invoice.id}`,
  ));
  expect(invoice.outstandingAmount).toBe(0);
  documents = await jsonOk<ReceivableDocument[]>(await api(page, token, 'get', documentsPath));
  expect(documents.filter((item) => item.orderId === orderId)).toHaveLength(1);
  const openItems = await jsonOk<Array<{ documentId: number }>>(
    await api(page, token, 'get', '/api/commercial-finance/ar/open-items'),
  );
  expect(openItems.some((item) => item.documentId === invoice.id)).toBe(false);
  const payments = await jsonOk<CustomerPayment[]>(await api(
    page, token, 'get', `/api/commercial-finance/payments?customerId=${order.customerId}`,
  ));
  expect(payments.filter((item) => item.id === payment.id)).toHaveLength(1);

  await page.goto(`/sales/finance?documentId=${invoice.id}`);
  await expect(page.getByRole('heading', { name: 'Accounts Receivable' })).toBeVisible();
  await expect(page.getByText(invoice.documentNumber!, { exact: true })).toBeVisible();
  const invoiceRow = page.getByRole('row').filter({ hasText: invoice.documentNumber! });
  await expect(invoiceRow).toContainText('0.00');
  await page.getByRole('tab', { name: /Payments/ }).click();
  const paymentRow = page.getByRole('row').filter({ hasText: payment.receiptNumber });
  await expect(paymentRow).toContainText('Posted');
  await expect(paymentRow).toContainText('0.00');
  await fs.mkdir(evidenceDir, { recursive: true });
  await page.screenshot({ path: path.join(evidenceDir, 'order-to-cash-settled.png'), fullPage: true });
});

test('41 sourced demand crosses supplier PO, inbound receipt, inventory, delivery, and cash', async ({ page }) => {
  let token = await loginAs(page, 'manager');
  const award = await ensureAtlasAward(page, token);
  let workbench = await getWorkbench(page, token);
  const sourcedLine = workbench.lines.find((item) => item.partNumber === required('E2E_CORE_OUT_OF_STOCK_PART'))!;
  expect(sourcedLine.productId, 'The sourced demand line must retain its authoritative product identity.').toBeTruthy();
  expect(award?.status).toMatch(/APPROVED/);

  let supplierPo = workbench.purchaseOrders.find((item) => item.id === award.purchaseOrderId);
  if (!supplierPo) {
    const created = await jsonOk<{ id: number }>(await api(page, token, 'post', '/api/procurement/purchase-orders', {
      rfqId: rfqId(),
      supplierId: award.supplierId,
      currencyId: award.currencyId,
      warehouseId: requiredNumber('E2E_CORE_PRIMARY_WAREHOUSE_ID'),
      expectedOn: '2099-12-31',
      awardIds: [award.id],
      incoterm: 'DAP',
      portOfLoading: 'Supplier dock',
      portOfDischarge: 'Acceptance warehouse',
    }, commandHeaders('commercial-v2-sourced-supplier-po')));
    workbench = await getWorkbench(page, token);
    supplierPo = workbench.purchaseOrders.find((item) => item.id === created.id);
  }
  expect(supplierPo, 'The approved sourcing award must produce one governed Supplier PO.').toBeTruthy();

  if (supplierPo!.status === 'DRAFT') {
    // The sourcing award was approved by the manager. A distinct authorized editor approves the
    // Supplier PO so the acceptance journey proves segregation of duties rather than bypassing it.
    token = await loginAs(page, 'editor');
    await jsonOk(await api(page, token, 'post', `/api/procurement/purchase-orders/${supplierPo!.id}/approve`, {
      expectedVersion: supplierPo!.version,
    }, commandHeaders('commercial-v2-sourced-supplier-po-approval')));
    token = await loginAs(page, 'manager');
    supplierPo = (await getWorkbench(page, token)).purchaseOrders.find((item) => item.id === supplierPo!.id)!;
  }

  const poSummary = (await jsonOk<Array<{ id: number; nexoraSerial?: string | null }>>(await api(
    page, token, 'get', `/api/procurement/purchase-orders?search=${supplierPo!.purchaseOrderNumber}&limit=10`,
  ))).find((item) => item.id === supplierPo!.id)!;
  expect(poSummary.nexoraSerial).toBe(required('E2E_CORE_NEXORA_SERIAL'));
  expect(supplierPo!.lines).toContainEqual(expect.objectContaining({
    rfqItemId: sourcedLine.id,
    productId: sourcedLine.productId,
  }));
  // Captured when the event actually occurs. Adding an artificial second to CreatedOn can put the
  // evidence in the future on a fast run, which the domain correctly rejects.
  const supplierDispatchTime = new Date().toISOString();
  if (supplierPo!.status === 'APPROVED') {
    await jsonOk(await api(page, token, 'post', `/api/procurement/purchase-orders/${supplierPo!.id}/issue`, {
      expectedVersion: supplierPo!.version,
      deliveryEvidenceReference: 'provider-receipt:commercial-v2-supplier-po-controlled-release',
      deliveryEvidenceSha256: '8'.repeat(64),
      deliveredOn: supplierDispatchTime,
    }, commandHeaders('commercial-v2-sourced-supplier-po-issue')));
    supplierPo = (await getWorkbench(page, token)).purchaseOrders.find((item) => item.id === supplierPo!.id)!;
  }
  expect(supplierPo!.status).toMatch(/SENT|ISSUED|ACKNOWLEDGED|IN_PRODUCTION|SHIPPED|PARTIALLY_RECEIVED|RECEIVED/);

  let inbound = (await jsonOk<InboundShipment[]>(await api(
    page, token, 'get', `/api/inbound-shipments/purchase-orders/${supplierPo!.id}`,
  ))).find((item) => item.trackingReference === 'V2-INBOUND-SOURCED-001');
  if (!inbound) {
    const inboundCommand = {
      purchaseOrderId: supplierPo!.id,
      lines: supplierPo!.lines.map((line) => ({
        purchaseOrderLineId: line.id,
        quantity: line.orderedQuantity - line.receivedQuantity,
      })),
      carrierName: 'Nexora Acceptance Inbound',
      trackingReference: 'V2-INBOUND-SOURCED-001',
      etaDate: '2099-12-30',
      readyAtFactoryOn: new Date().toISOString().slice(0, 10),
    };
    inbound = await jsonOk<InboundShipment>(await api(page, token, 'post', '/api/inbound-shipments', inboundCommand,
      commandHeaders('commercial-v2-sourced-inbound-shipment')));
    const inboundReplay = await jsonOk<InboundShipment>(await api(
      page, token, 'post', '/api/inbound-shipments', inboundCommand,
      commandHeaders('commercial-v2-sourced-inbound-shipment'),
    ));
    expect(inboundReplay.id).toBe(inbound.id);
  }

  const availabilityBeforeReceipt = supplierPo!.status === 'RECEIVED' ? null : await jsonOk<InventoryAvailability[]>(
    await api(page, token, 'get', '/api/inventory-intelligence/availability'),
  );
  if (supplierPo!.status !== 'RECEIVED') {
    supplierPo = (await getWorkbench(page, token)).purchaseOrders.find((item) => item.id === supplierPo!.id)!;
    const receiptCommand = {
      purchaseOrderId: supplierPo!.id,
      warehouseId: supplierPo!.lines[0].warehouseId,
      receiptNumber: 'V2-GR-SOURCED-001',
      receivedOn: new Date().toISOString(),
      expectedPurchaseOrderVersion: supplierPo!.version,
      lines: supplierPo!.lines.map((line) => ({
        purchaseOrderLineId: line.id,
        quantity: line.orderedQuantity - line.receivedQuantity,
      })),
    };
    const receipt = await jsonOk<{ id: number; purchaseOrderStatus: string; replayed: boolean }>(await api(
      page, token, 'post', '/api/procurement/goods-receipts', receiptCommand,
      commandHeaders('commercial-v2-sourced-goods-receipt'),
    ));
    expect(receipt.purchaseOrderStatus).toBe('RECEIVED');
    const receiptReplay = await jsonOk<{ id: number; replayed: boolean }>(await api(
      page, token, 'post', '/api/procurement/goods-receipts', receiptCommand,
      commandHeaders('commercial-v2-sourced-goods-receipt'),
    ));
    expect(receiptReplay.id).toBe(receipt.id);
    expect(receiptReplay.replayed).toBe(true);
  }
  inbound = (await jsonOk<InboundShipment[]>(await api(
    page, token, 'get', `/api/inbound-shipments/purchase-orders/${supplierPo!.id}`,
  ))).find((item) => item.id === inbound.id)!;
  expect(inbound.milestone).toBe('RECEIVED_AT_WAREHOUSE');
  expect(inbound.receiptState).toBe('RECEIPTED');
  expect(inbound.outstandingReceiptQuantity).toBe(0);
  const availabilityAfterReceipt = await jsonOk<InventoryAvailability[]>(
    await api(page, token, 'get', '/api/inventory-intelligence/availability'),
  );
  const receivedInventory = availabilityAfterReceipt.find((row) => row.productId === supplierPo!.lines[0].productId
    && row.warehouseId === supplierPo!.lines[0].warehouseId);
  expect(receivedInventory, 'Goods receipt must materialize in authoritative inventory.').toBeTruthy();
  if (availabilityBeforeReceipt) {
    const before = availabilityBeforeReceipt.find((row) => row.inventoryId === receivedInventory!.inventoryId);
    expect(receivedInventory!.onHand).toBe((before?.onHand ?? 0) + supplierPo!.lines[0].orderedQuantity);
  }

  const orders = await jsonOk<CustomerOrder[]>(await api(page, token, 'get', '/api/Order'));
  const sourcedOrderLineId = requiredNumber('E2E_V2_SOURCED_CUSTOMER_ORDER_LINE_ID');
  const sourcedOrder = orders.find((item) => item.items.some((line) => line.id === sourcedOrderLineId))!;
  expect(sourcedOrder?.commercialCaseId).toBeTruthy();
  expect(sourcedOrder.nexoraSerial).toBe(required('E2E_CORE_NEXORA_SERIAL'));
  expect(sourcedOrder.items.find((item) => item.id === sourcedOrderLineId)?.productId)
    .toBe(supplierPo!.lines[0].productId);
  const allocation = await jsonOk<{ fullyAllocated: boolean }>(await api(
    page, token, 'post', `/api/Order/${sourcedOrder.id}/allocate`,
  ));
  expect(allocation.fullyAllocated).toBe(true);

  let outbound = (await jsonOk<Shipment[]>(await api(page, token, 'get', '/api/Shipment')))
    .find((item) => item.orderId === sourcedOrder.id);
  const outboundAlreadyExisted = Boolean(outbound);
  if (!outbound) {
    const outboundCommand = {
      orderId: sourcedOrder.id,
      businessUnitId: requiredNumber('E2E_MANAGER_BUSINESS_UNIT_ID'),
      statusId: requiredNumber('E2E_V2_SHIPMENT_STATUS_ID'),
      shipmentDate: new Date().toISOString(),
      carrier: 'Nexora Acceptance Outbound',
      trackingNumber: 'V2-OUTBOUND-SOURCED-001',
      externalId: 'commercial-v2-sourced-outbound-shipment',
      shippingAddress: 'ABC Engineering acceptance dock',
      items: sourcedOrder.items.map((item) => ({ orderItemId: item.id, quantity: item.quantity })),
    };
    outbound = await jsonOk<Shipment>(await api(page, token, 'post', '/api/Shipment', outboundCommand,
      commandHeaders('commercial-v2-sourced-outbound-shipment')));
    const outboundReplay = await jsonOk<Shipment>(await api(
      page, token, 'post', '/api/Shipment', outboundCommand,
      commandHeaders('commercial-v2-sourced-outbound-shipment'),
    ));
    expect(outboundReplay.id).toBe(outbound.id);
  }
  const availabilityAfterDespatch = await jsonOk<InventoryAvailability[]>(
    await api(page, token, 'get', '/api/inventory-intelligence/availability'),
  );
  const despatchedInventory = availabilityAfterDespatch.find((row) => row.inventoryId === receivedInventory!.inventoryId);
  if (!outboundAlreadyExisted) {
    expect(despatchedInventory?.onHand).toBe(receivedInventory!.onHand - sourcedOrder.items[0].quantity);
  }
  const consumed = await jsonOk<StockReservation[]>(await api(
    page, token, 'get', '/api/inventory-intelligence/reservations?status=Consumed',
  ));
  expect(consumed.some((item) => item.demandReference === `Order ${sourcedOrder.id}`
    && item.quantity === sourcedOrder.items[0].quantity)).toBe(true);
  expect(await jsonOk(await api(
    page, token, 'get', '/api/inventory-intelligence/stock/reconciliation?driftOnly=true',
  ))).toEqual(expect.objectContaining({ balanced: true, driftCount: 0 }));
  const podCommand = {
    receivedByName: 'Amira Cole',
    receivedByContact: 'procurement@abc-engineering.local',
    receivedByPosition: 'Acceptance Receiving',
    receivedOn: new Date().toISOString(),
    notes: 'Sourced material accepted in full.',
    lines: outbound.items.map((item) => ({ shipmentItemId: item.id, acceptedQuantity: item.quantity,
      exceptionReasonCode: null, exceptionNote: null, notes: 'Accepted in full.' })),
  };
  const existingPod = await api(page, token, 'get', `/api/delivery/shipments/${outbound.id}/confirmation`);
  if (existingPod.status() === 404) {
    const pod = await jsonOk<DeliveryProof>(await api(
      page, token, 'post', `/api/delivery/shipments/${outbound.id}/confirmation`, podCommand,
      commandHeaders('commercial-v2-sourced-pod')));
    const podReplay = await jsonOk<DeliveryProof>(await api(
      page, token, 'post', `/api/delivery/shipments/${outbound.id}/confirmation`, podCommand,
      commandHeaders('commercial-v2-sourced-pod')));
    expect(podReplay.id).toBe(pod.id);
  }
  expect((await jsonOk<CustomerOrder>(await api(page, token, 'get', `/api/Order/${sourcedOrder.id}`)))
    .status.toUpperCase()).toBe('DELIVERED');

  token = await loginAsFinance(page);
  const documentPath = `/api/commercial-finance/documents?customerId=${sourcedOrder.customerId}`;
  let invoice = (await jsonOk<ReceivableDocument[]>(await api(page, token, 'get', documentPath)))
    .find((item) => item.orderId === sourcedOrder.id);
  if (!invoice) {
    const invoiceCommand = {
        documentDate: supplierDispatchTime,
        dueDate: '2099-12-31T00:00:00.000Z',
        lines: sourcedOrder.items.map((item) => ({ orderItemId: item.id, quantity: item.quantity })),
    };
    invoice = await jsonOk<ReceivableDocument>(await api(
      page, token, 'post', `/api/commercial-finance/orders/${sourcedOrder.id}/invoices`, invoiceCommand,
      commandHeaders('commercial-v2-sourced-invoice'),
    ));
    const invoiceReplay = await jsonOk<ReceivableDocument>(await api(
      page, token, 'post', `/api/commercial-finance/orders/${sourcedOrder.id}/invoices`, invoiceCommand,
      commandHeaders('commercial-v2-sourced-invoice'),
    ));
    expect(invoiceReplay.id).toBe(invoice.id);
  }
  if (invoice.status === 'Draft') {
    invoice = await jsonOk<ReceivableDocument>(await api(
      page, token, 'post', `/api/commercial-finance/documents/${invoice.id}/issue`, { expectedVersion: invoice.version },
    ));
  }
  let payment: CustomerPayment;
  if (invoice.outstandingAmount > 0) {
    // Exercise the same governed cash-capture workbench a finance user sees in a pilot. Test 40
    // proves the underlying keyed replay; this verifies bank-account selection and allocation are
    // actually wired through the client journey rather than only callable by an API test.
    await page.goto(`/sales/finance?documentId=${invoice.id}`);
    await expect(page.getByRole('heading', { name: 'Accounts Receivable' })).toBeVisible();
    await page.getByRole('button', { name: `Record payment for ${invoice.documentNumber}` }).click();
    const paymentDialog = page.getByRole('dialog', { name: 'Record payment' });
    await expect(paymentDialog.getByRole('combobox', { name: 'Deposit bank account' })).toHaveText(/Acceptance operating bank/);
    await paymentDialog.getByLabel('Bank reference').fill('COMMERCIAL-V2-SOURCED-CASH-001');
    const posted = page.waitForResponse((response) => response.request().method() === 'POST'
      && response.url().endsWith('/api/commercial-finance/payments'));
    await paymentDialog.getByRole('button', { name: 'Post payment' }).click();
    const paymentResponse = await posted;
    const paymentRequest = paymentResponse.request();
    expect(paymentRequest.postDataJSON()).toEqual(expect.objectContaining({
      bankAccountId: requiredNumber('E2E_V2_BANK_ACCOUNT_ID'),
      bankReference: 'COMMERCIAL-V2-SOURCED-CASH-001',
      allocations: [{ receivableDocumentId: invoice.id, amount: invoice.outstandingAmount }],
    }));
    expect(paymentRequest.headers()['idempotency-key']).toBeTruthy();
    payment = await jsonOk<CustomerPayment>(paymentResponse);
    await expect(page.getByText('Payment posted and allocated')).toBeVisible();
  } else {
    const priorPayments = await jsonOk<CustomerPayment[]>(await api(
      page, token, 'get', `/api/commercial-finance/payments?customerId=${sourcedOrder.customerId}`,
    ));
    payment = priorPayments.find((item) => item.bankReference === 'COMMERCIAL-V2-SOURCED-CASH-001')!;
    expect(payment, 'A settled retained run must retain its governed cash receipt.').toBeTruthy();
  }
  expect(payment.journalEntryId).toBeGreaterThan(0);
  invoice = await jsonOk<ReceivableDocument>(await api(
    page, token, 'get', `/api/commercial-finance/documents/${invoice.id}`,
  ));
  expect(invoice.outstandingAmount).toBe(0);

  // Respect persona boundaries in the visible evidence: finance owns cash, while the manager
  // has the shipment/supplier visibility required to inspect physical fulfilment.
  token = await loginAs(page, 'manager');
  await page.goto('/suppliers/purchase-orders');
  const supplierPoRow = page.getByRole('row').filter({ hasText: supplierPo!.purchaseOrderNumber });
  await expect(supplierPoRow).toBeVisible();
  await expect(supplierPoRow.getByText('RECEIVED', { exact: true })).toBeVisible();
  await page.goto(`/sales/shipments/${outbound.id}`);
  await expect(page.getByText('Delivered', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('PROOF OF DELIVERY', { exact: true })).toBeVisible();
  await expect(page.getByText(/Received by/)).toContainText('Amira Cole');
  token = await loginAsFinance(page);
  await page.goto(`/sales/finance?documentId=${invoice.id}`);
  const invoiceRow = page.getByRole('row').filter({ hasText: invoice.documentNumber! });
  await expect(invoiceRow).toBeVisible();
  await expect(invoiceRow).toContainText('0.00');
  await page.getByRole('tab', { name: /Payments/ }).click();
  const paymentRow = page.getByRole('row').filter({ hasText: payment.receiptNumber });
  await expect(paymentRow).toContainText('Posted');
  await expect(paymentRow).toContainText('0.00');
  await fs.mkdir(evidenceDir, { recursive: true });
  await page.screenshot({ path: path.join(evidenceDir, 'sourced-order-to-cash-settled.png'), fullPage: true });
});

test.afterEach(({ page }, testInfo) => {
  void page;
  expect(testInfo.annotations.filter((annotation) => annotation.type === 'skip')).toHaveLength(0);
});
