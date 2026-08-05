import { expect, test, type Page } from '@playwright/test';

const lead = {
  id: 401,
  rfqno: 'CUSTOMER-RFQ-401',
  buyersName: 'Pilot Buyer',
  recDate: '2026-07-30T12:00:00Z',
  bidClosingDate: '2026-08-05T12:00:00Z',
  leadSource: 'Upload',
  clientemail: 'buyer@example.invalid',
  createdDate: '2026-07-30T12:00:00Z',
  leadItems: [{
    id: 501,
    lineItemNo: '1',
    productShortName: 'CONTROL MODULE',
    productShortDescription: 'Industrial control module',
    manufacturerName: 'Acme Controls',
    manufacturerPartNumber: 'ACM-100',
    quantity: 5,
    unitPrice: 0,
    aiconfidence: 0.96,
  }],
  attachments: [],
};

async function setProductPermissions(page: Page, permissions: { create: boolean; edit: boolean }) {
  await page.addInitScript(({ create, edit }) => {
    const current = JSON.parse(localStorage.getItem('userData') ?? '{}');
    localStorage.setItem('userData', JSON.stringify({
      ...current,
      isSuperAdmin: false,
      permissions: [{
        id: 9401,
        moduleId: 9401,
        moduleName: 'Products',
        roleId: current.roleId ?? 1,
        canCreate: create,
        canEdit: edit,
        canDelete: false,
      }],
    }));
  }, permissions);
}

test('inventory dependency outage fails closed and retry restores ATP evidence', async ({ page }) => {
  await page.route('**/api/UnAssignedLead/401', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(lead) }));
  await page.route('**/api/Customer?*', route => route.fulfill({ status: 200, contentType: 'application/json', body: '{"items":[]}' }));
  await page.route('**/api/Customer/by-email*', route => route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));

  let unavailable = true;
  await page.route('**/api/Product/match-product', route => {
    expect(route.request().postDataJSON()).toMatchObject({ quantity: 5, description: 'Industrial control module' });
    return unavailable
      ? route.fulfill({ status: 503, contentType: 'application/json', body: '{"error":"Inventory dependency unavailable"}' })
      : route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        hasExactMatch: true,
        exactMatch: {
          id: 701,
          productId: 701,
          productName: 'Control Module',
          partNo: 'ACM-100',
          manufacturerName: 'Acme Controls',
          qtyOnHand: 99,
          reorderPoint: 5,
          availableToPromise: 3,
          incomingAvailable: 0,
          projectedShortage: 2,
          availabilityStatus: 'Partial',
          leadTimeDays: 12,
          unitCost: 45,
          costCurrencyCode: 'USD',
          decisionState: 'ApprovedExact',
          evidenceReference: 'inventory-snapshot:701',
          isActive: true,
          createdBy: 'fixture',
          createdOn: '2026-07-30T12:00:00Z',
          images: [],
          attachments: [],
        },
        fuzzyMatches: [],
      }),
      });
  });

  await page.goto('/procurement/rfqs/process/401');
  await expect(page.getByText('Inventory check unavailable').first()).toBeVisible();
  await expect(page.getByRole('button', { name: 'Create As Draft' })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Batch Quote' })).toBeDisabled();
  await expect(page.getByText('Sourcing required')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Search Internet' })).toHaveCount(0);

  unavailable = false;
  await page.getByRole('button', { name: 'Retry unavailable checks' }).click();
  await expect(page.getByText('ATP 3')).toBeVisible();
  await expect(page.getByText('Short 2')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Create As Draft' })).toBeEnabled();
});

test('view-only product access hides mutation controls and exposes retry', async ({ page }) => {
  await setProductPermissions(page, { create: false, edit: false });
  let fail = true;
  await page.route('**/api/Product?*', route => fail
    ? route.fulfill({ status: 503, contentType: 'application/json', body: '{"error":"temporary"}' })
    : route.fulfill({ status: 200, contentType: 'application/json', body: '{"items":[],"totalItems":0,"pageNumber":1,"pageSize":10,"totalPages":0}' }));

  await page.goto('/inventory/products');
  await expect(page.getByRole('alert')).toContainText('Products could not be loaded');
  fail = false;
  await page.getByRole('button', { name: 'Retry' }).click();
  await expect(page.getByRole('alert')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Add Product' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Import' })).toHaveCount(0);
  await expect(page.getByText('Low Stock on this page')).toBeVisible();
  await expect(page.getByText('Out of Stock on this page')).toBeVisible();
});

test('product update submits only persisted product fields', async ({ page }) => {
  await setProductPermissions(page, { create: false, edit: true });
  const product = {
    id: 701,
    productName: 'Control Module',
    partNo: 'ACM-100',
    modelNo: 'CM-1',
    qtyOnHand: 8,
    reorderPoint: 2,
    unitCost: 45,
    isActive: true,
    createdBy: 'Pilot User',
    createdOn: '2026-07-30T12:00:00Z',
    images: [],
    attachments: [],
  };
  await page.route('**/api/Product/701', async route => {
    if (route.request().method() === 'GET') {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(product) });
      return;
    }
    const body = route.request().postData() ?? '';
    expect(body).toContain('name="modelNo"');
    expect(body).toContain('CM-2');
    expect(body).not.toContain('name="manufacturerName"');
    expect(body).not.toContain('name="costCurrencyCode"');
    expect(body).not.toContain('name="concurrencyToken"');
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(product) });
  });
  await page.route(/\/api\/Product\/lookups\//, route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));

  await page.goto('/inventory/products/701');
  await page.getByRole('button', { name: 'Edit Product' }).click();
  await page.getByLabel('Model No').fill('CM-2');
  await page.getByRole('button', { name: 'Update Product' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
});

test('RFQ line wording is based on persisted resolution and history has no fabricated approval', async ({ page }) => {
  await page.route('**/api/Rfq/402?*', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      id: 402,
      rfqno: 'RFQ-402',
      nexoraSerial: 'NXR-2026-000402',
      recDate: '2026-07-30T12:00:00Z',
      activeLeadRevision: 1,
      createdBy: 'Pilot User',
      createdDate: '2026-07-30T12:00:00Z',
      businessUnitId: 9001,
      rfqstatusValue: 'Approved',
      readiness: 'Review',
      rfqitems: [{ id: 801, rfqid: 402, productId: 701, productName: 'Control Module', manufacturerName: 'Acme Controls', manufacturerPartNumber: 'ACM-100', quantity: 5, bidClosingDateLine: '2026-08-05T12:00:00Z', createdBy: 'Pilot User', createdDate: '2026-07-30T12:00:00Z' }],
    }),
  }));
  await page.route('**/api/commercial-cases/rfqs/402/lifecycle', route => route.fulfill({ status: 200, contentType: 'application/json', body: '{"aggregateId":402,"currentStatusCode":"APPROVED","version":2,"isTerminal":false,"allowedTransitions":[]}' }));
  await page.route('**/api/inventory-intelligence/rfqs/402/resolutions', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{
    id: 901, leadId: 401, leadRevisionId: 1, leadLineId: 501, rfqId: 402, rfqItemId: 801, productId: 701,
    requestedPartNumber: 'ACM-100', requestedQuantity: 5, classification: 'KnownShortage', availableToPromise: 3,
    incomingAvailable: 0, projectedShortage: 2, leadTimeDays: 12, unitCost: 45, costCurrencyCode: 'USD',
    fulfilment: {}, relatedResources: [], productResolution: { decisionState: 'ApprovedExact' },
    evidenceReference: 'inventory-snapshot:701', inventoryAsOfUtc: '2026-07-30T12:00:00Z', resolvedOn: '2026-07-30T12:00:00Z', externalDiscoveryUsed: false,
  }]) }));
  await page.route('**/api/procurement/rfqs/402/workbench', route => route.fulfill({ status: 503, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/commercial-learning/rfqs/402/intelligence', route => route.fulfill({ status: 503, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/processing-evidence/rfqs/402', route => route.fulfill({ status: 404, contentType: 'application/json', body: '{}' }));

  await page.goto('/procurement/rfqs/view/402');
  await expect(page.getByText('Persisted resolution')).toBeVisible();
  await expect(page.getByText('Request data verified')).toHaveCount(0);
  await expect(page.getByText('RFQ Created')).toBeVisible();
  await expect(page.getByText('Approved & Sent')).toHaveCount(0);
});
