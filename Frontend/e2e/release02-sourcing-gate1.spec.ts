import { expect, test, type Page } from '@playwright/test';

const rfqId = 77;
const rfqItemId = 402;
const sourcingCaseId = 8801;
const now = '2026-07-26T12:00:00Z';

const rfq = {
  id: rfqId,
  rfqno: 'CRFQ-000077',
  nexoraSerial: 'NXR-2026-000077',
  buyersName: 'Authorized Test Customer',
  recDate: now,
  leadId: 31,
  activeLeadRevision: 1,
  createdBy: 'manager@example.test',
  createdDate: now,
  businessUnitId: 9001,
  rfqstatusValue: 'Draft',
  customerId: 61,
  customerName: 'Authorized Test Customer',
  readiness: 'Commercial review',
  rfqitems: [{
    id: rfqItemId,
    rfqid: rfqId,
    productId: 701,
    productName: 'Control Module',
    productShortDescription: 'Qualified flight control module',
    manufacturerName: 'Test Manufacturer',
    manufacturerPartNumber: 'NXR-R02-OOS-001',
    quantity: 12,
    unitOfMeasure: 'EA',
    bidClosingDateLine: now,
    createdBy: 'manager@example.test',
    createdDate: now,
  }],
};

const workbench = {
  rfqId,
  rfqNumber: rfq.rfqno,
  nexoraSerial: rfq.nexoraSerial,
  customerName: rfq.customerName,
  currencyCode: 'USD',
  lines: [{
    id: rfqItemId,
    rfqId,
    productId: 701,
    partNumber: 'NXR-R02-OOS-001',
    description: 'Qualified flight control module',
    requestedQuantity: 12,
    availableQuantity: 0,
    reservedQuantity: 0,
    shortfallQuantity: 12,
    requiredOn: '2026-08-15T00:00:00Z',
    resolution: 'SHORTAGE',
    resolutionCheckedOn: now,
  }],
  solicitations: [],
  offers: [],
  awards: [],
  purchaseOrders: [],
};

const candidate = (supplierId: number, rank: number) => ({
  id: 9000 + supplierId,
  supplierId,
  supplierName: `Approved Supplier ${rank}`,
  contactEmail: `quotes${rank}@supplier.test`,
  rank,
  evidenceType: rank === 1 ? 'PRIOR_SUPPLIER_QUOTE' : 'PURCHASE_HISTORY',
  recommendationReason: rank === 1 ? 'Quoted this Product for this tenant.' : 'Prior tenant purchase history for this Product.',
  evidenceScore: rank === 1 ? 0.91 : 0.82,
  evidenceFreshOn: '2026-07-20T12:00:00Z',
  selected: false,
  approvalStatus: 'APPROVED',
  governanceStatus: 'APPROVED',
  verificationStatus: 'VERIFIED',
  complianceStatus: 'CLEARED',
  riskStatus: 'LOW',
  readinessStatus: 'READY',
  eligibleForSupplierRfq: true,
  blockingReasons: [],
});

const sourcingCase = {
  id: sourcingCaseId,
  commercialDemandLineId: 7001,
  rfqId,
  rfqItemId,
  nexoraSerial: rfq.nexoraSerial,
  productId: 701,
  requestedPartNumber: 'NXR-R02-OOS-001',
  description: 'Qualified flight control module',
  requestedQuantity: 12,
  stockQuantity: 0,
  unfulfilledQuantity: 12,
  requiredOn: '2026-08-15T00:00:00Z',
  searchLimit: 10,
  status: 'INTERNAL_SEARCH',
  nextAction: 'Select suppliers for outreach',
  version: 1,
  candidates: [candidate(901, 1), candidate(902, 2)],
};

async function installGateOneApi(page: Page) {
  let currentCase = structuredClone(sourcingCase);
  await page.route(`**/api/Rfq/${rfqId}*`, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(rfq) }));
  await page.route(`**/api/procurement/rfqs/${rfqId}/workbench`, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workbench) }));
  await page.route('**/api/procurement/sourcing-cases', async (route) => {
    expect(route.request().method()).toBe('POST');
    expect(route.request().postDataJSON()).toEqual({ rfqId, rfqItemId, searchLimit: 10, sourceEntireQuantity: false });
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(currentCase) });
  });
  await page.route(`**/api/procurement/sourcing-cases/${sourcingCaseId}`, (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(currentCase) }));
  await page.route(`**/api/procurement/sourcing-cases/${sourcingCaseId}/supplier-candidates/search`, async (route) => {
    const request = route.request().postDataJSON();
    expect([10, 20, 50]).toContain(request.limit);
    expect(request.expectedVersion).toBe(currentCase.version);
    currentCase = {
      ...currentCase,
      searchLimit: request.limit,
      version: currentCase.version + 1,
      candidates: request.limit === 50 ? [] : [candidate(901, 1), candidate(902, 2), candidate(903, 3)],
    };
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        sourcingCaseId,
        requestedLimit: request.limit,
        resultCount: currentCase.candidates.length,
        version: currentCase.version,
        replayed: false,
        candidates: currentCase.candidates,
      }),
    });
  });
  await page.route(`**/api/procurement/sourcing-cases/${sourcingCaseId}/supplier-rfqs`, async (route) => {
    const request = route.request().postDataJSON();
    expect(request.expectedVersion).toBe(currentCase.version);
    currentCase.version += 1;
    await route.fulfill({
      status: 201,
      contentType: 'application/json',
      body: JSON.stringify({
        sourcingCaseId,
        supplierSolicitationId: 12000 + request.supplierId,
        status: 'PENDING_DISPATCH',
        sourcingCaseVersion: currentCase.version,
        solicitationVersion: 1,
        replayed: false,
      }),
    });
  });
  await page.route(`**/api/procurement/sourcing-cases/${sourcingCaseId}/supplier-rfqs/*/queue`, async (route) => {
    const request = route.request().postDataJSON();
    expect(request.expectedSourcingCaseVersion).toBe(currentCase.version);
    expect(request.expectedSolicitationVersion).toBe(1);
    currentCase.version += 1;
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        sourcingCaseId,
        supplierSolicitationId: Number(route.request().url().split('/').at(-2)),
        status: 'QUEUED',
        sourcingCaseVersion: currentCase.version,
        solicitationVersion: 2,
        replayed: false,
      }),
    });
  });
}

test('out-of-stock Customer RFQ line creates or opens a persisted Sourcing Case', async ({ page }) => {
  await installGateOneApi(page);
  await page.goto(`/procurement/rfqs/view/${rfqId}`);

  await expect(page.getByText('12 to source · SHORTAGE')).toBeVisible();
  await page.getByRole('button', { name: 'Create / Open Sourcing Case' }).click();
  await expect(page).toHaveURL(new RegExp(`/procurement/sourcing-cases/${sourcingCaseId}$`));
  await expect(page.getByRole('heading', { name: 'Sourcing Case' })).toBeVisible();
  await expect(page.getByText(rfq.nexoraSerial, { exact: true })).toBeVisible();
  await expect(page.getByText('NXR-R02-OOS-001', { exact: true })).toBeVisible();
});

test('10/20/50 candidate control uses deterministic API evidence and approves Supplier RFQ delivery', async ({ page }) => {
  await installGateOneApi(page);
  await page.goto(`/procurement/sourcing-cases/${sourcingCaseId}`);

  await expect(page.getByText('Quoted this Product for this tenant.')).toBeVisible();
  const searchRequest = page.waitForRequest(`**/api/procurement/sourcing-cases/${sourcingCaseId}/supplier-candidates/search`);
  await page.getByRole('button', { name: 'Show 20 Supplier candidates' }).click();
  expect((await searchRequest).postDataJSON().limit).toBe(20);
  await expect(page.getByText('Approved Supplier 3')).toBeVisible();

  await page.getByLabel('Select Approved Supplier 1').check();
  await page.getByRole('button', { name: 'Prepare and Queue Supplier RFQ' }).click();
  await expect(page.getByRole('dialog', { name: 'Approve Supplier RFQ Delivery' })).toBeVisible();
  const prepareRequest = page.waitForRequest(`**/api/procurement/sourcing-cases/${sourcingCaseId}/supplier-rfqs`);
  const queueRequest = page.waitForRequest(`**/api/procurement/sourcing-cases/${sourcingCaseId}/supplier-rfqs/*/queue`);
  await page.getByRole('button', { name: 'Approve and Queue' }).click();
  expect((await prepareRequest).postDataJSON().supplierId).toBe(901);
  expect((await queueRequest).postDataJSON().expectedSolicitationVersion).toBe(1);
  await expect(page).toHaveURL(new RegExp(`/procurement/rfqs/${rfqId}/sourcing$`));
});

test('candidate search has a truthful empty state and does not start external discovery', async ({ page }) => {
  await installGateOneApi(page);
  await page.goto(`/procurement/sourcing-cases/${sourcingCaseId}`);

  await page.getByRole('button', { name: 'Show 50 Supplier candidates' }).click();
  await expect(page.getByText('No known Supplier candidates found')).toBeVisible();
  await expect(page.getByText('No tenant Supplier history matched this demand line. No external search was started.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Prepare and Queue Supplier RFQ' })).toBeDisabled();
});
