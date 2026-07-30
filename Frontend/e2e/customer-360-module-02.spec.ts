import { expect, test, type Page } from '@playwright/test';

const generatedAt = '2026-07-30T12:00:00Z';
const customerToken = '11111111-1111-1111-1111-111111111111';
const contactToken = '22222222-2222-2222-2222-222222222222';
const customer = { id: 401, docId: 'CUST-401', name: 'Atlas Pilot Customer', contactEmail: 'buyer@atlas.test', isActive: true, concurrencyToken: customerToken };
const contact = { id: 501, customerId: 401, firstName: 'Robin', lastName: 'Buyer', email: 'robin@atlas.test', phoneNo: '+1 555 0100', mobileNo: '+1 555 0101', position: 'Procurement Director', isPrimary: true, isActive: true, concurrencyToken: contactToken };

async function setPermissions(
  page: Page,
  customers = { create: true, edit: true, delete: true },
  suppliers = { create: false, edit: false, delete: false },
) {
  await page.addInitScript(({ customerActions, supplierActions }) => {
    const current = JSON.parse(localStorage.getItem('userData') ?? '{}');
    const modules = [
      { moduleName: 'Customers', canCreate: customerActions.create, canEdit: customerActions.edit, canDelete: customerActions.delete },
      { moduleName: 'Quotations', canCreate: false, canEdit: false, canDelete: false },
      { moduleName: 'RFQ Management', canCreate: false, canEdit: false, canDelete: false },
      { moduleName: 'Orders', canCreate: false, canEdit: false, canDelete: false },
      { moduleName: 'Suppliers', canCreate: supplierActions.create, canEdit: supplierActions.edit, canDelete: supplierActions.delete },
    ].map((item, index) => ({ ...item, id: 9200 + index, moduleId: 9200 + index, roleId: current.roleId ?? 1 }));
    localStorage.setItem('userData', JSON.stringify({ ...current, isSuperAdmin: false, businessUnitId: 1, permissions: modules }));
  }, { customerActions: customers, supplierActions: suppliers });
}

const health = {
  customerId: 401, generatedAt, dataCompleteness: { status: 'complete', incompleteSources: [] },
  period: { from: '2026-05-01', to: '2026-07-30' },
  rfqTrend: { status: 'MEASURED', currentCount: 1, previousCount: 1, changePercent: 0 },
  quoteCoverage: { status: 'MEASURED', rfqCount: 1, quotedRfqCount: 1, coveragePercent: 100 },
  quoteDecisions: { status: 'MEASURED', decidedCount: 1, wonCount: 1, lostCount: 0 },
  conversion: { status: 'MEASURED', ratePercent: 100, sampleSize: 1 },
  acceptedPrices: [], margin: { status: 'INSUFFICIENT_EVIDENCE', grossMarginPercent: null },
  revisionBurden: { status: 'MEASURED', revisionCount: 0, inquiryCount: 1, changedFieldCount: 0, comparedFieldCount: 0, changedLineCount: 0, comparedLineCount: 0 },
  followUp: { status: 'MEASURED', openCount: 0, overdueCount: 0, effectivenessPercent: null },
  lastCommercialActivity: null, healthBand: 'HEALTHY', healthReasons: [], opportunities: [], nextBestAction: null,
};

async function mockCustomerApis(page: Page, options: { contactsFailUntil?: number } = {}) {
  let contactAttempts = 0;
  await page.route('**/api/Customer/401', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(customer) }));
  await page.route('**/api/Customer?*', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [customer], totalCount: 1, pageNumber: 1, pageSize: 10 }) }));
  await page.route('**/api/Contact?*', route => {
    contactAttempts += 1;
    if (contactAttempts <= (options.contactsFailUntil ?? 0)) return route.fulfill({ status: 503, contentType: 'application/json', body: '{"message":"temporary"}' });
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [contact], totalCount: 1, pageNumber: 1, pageSize: 100 }) });
  });
  await page.route('**/api/Country?*', route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/State?*', route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/City?*', route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/intelligence/customers/401/context', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
    customerId: 401, customerName: customer.name, totalQuotes: 1, wonQuotes: 1, lostQuotes: 0, winRatePct: 100,
    ordersLast24Months: 1, orderValueLast24Months: 1250, orderValueStatus: 'single_currency',
    orderValueByCurrency: [{ currencyId: 1, currencyCode: 'USD', recordCount: 1, totalAmount: 1250, averageAmount: 1250 }],
    avgQuoteTotal: 1250, avgQuoteTotalStatus: 'single_currency',
    quoteValueByCurrency: [{ currencyId: 1, currencyCode: 'USD', recordCount: 1, totalAmount: 1250, averageAmount: 1250 }],
    avgMarginPct: null, lastQuoteDate: generatedAt,
    recentQuotes: [{ quoteId: 701, quoteNo: 'Q-701', quoteDate: generatedAt, totalAmount: 1250, statusValue: 'Won', outcome: 'won', outcomeReasonName: null, contactId: 501, contactName: 'Robin Buyer', keyLines: [] }],
    recentItemPrices: [], recentRfqs: [{ rfqId: 601, rfqNo: 'RFQ-601', receivedOn: generatedAt, bidClosingOn: null, status: 'Open', lineCount: 1, contactId: 501, contactName: 'Robin Buyer' }],
    recentOrders: [{ orderId: 801, orderNo: 'SO-801', orderDate: generatedAt, status: 'Open', totalAmount: 1250, quoteId: 701, contactId: 501, contactName: 'Robin Buyer', currencyCode: 'USD' }],
    demandProfile: [], completeness: { quoteAggregateScope: 'all_history', recentQuoteLimit: 10, recentQuotesTruncated: false, soldOrderEvidenceLimit: 250, soldOrdersEvaluated: 1, soldOrderEvidenceTruncated: false, quoteItemEvidenceLimit: 500, quoteItemsEvaluated: 0, quoteItemEvidenceTruncated: false, demandLookbackMonths: 24, demandCohortFrom: '2024-07-30T12:00:00Z', demandCohortTo: generatedAt, demandLineLimit: 500, demandLinesEvaluated: 0, demandLinesTruncated: false }, generatedAt,
  }) }));
  await page.route('**/api/commercial-learning/customers/401', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ customerId: 401, customerName: customer.name, inquiryCount: 1, quoteCount: 1, decidedCount: 1, wonCount: 1, lostCount: 0, pendingCount: 0, conversionRatePercent: 100, wonValues: [], lossReasons: [], evidence: [] }) }));
  await page.route('**/api/commercial-intelligence/account-ownership?*', route => {
    expect(new URL(route.request().url()).searchParams.get('customerId')).toBe('401');
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{ customerId: 401, customerName: customer.name, ownerUserId: 1, ownerName: 'Case Owner', openLeads: 1, openQuotes: 1, pipelineGroups: [], version: 1 }]) });
  });
  await page.route('**/api/intelligence/customers/401/health?*', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(health) }));
}

test('Customer 360 uses exact ownership, shows complete contact lineage, and opens the requested edit record', async ({ page }) => {
  await setPermissions(page);
  await mockCustomerApis(page);
  await page.goto('/customers/401');
  await expect(page.getByRole('heading', { name: customer.name })).toBeVisible();
  await expect(page.getByText('Procurement Director')).toBeVisible();
  await expect(page.getByText('robin@atlas.test')).toBeVisible();
  await expect(page.getByText('Mobile: +1 555 0101')).toBeVisible();
  await expect(page.getByRole('table', { name: 'Recent customer RFQs' })).toContainText('Robin Buyer');
  await expect(page.getByRole('table', { name: 'Recent customer quote outcomes' })).toContainText('Robin Buyer');
  await expect(page.getByRole('table', { name: 'Recent customer orders' })).toContainText('USD 1,250');
  await page.getByRole('button', { name: 'Edit Customer' }).click();
  await expect(page.getByRole('dialog', { name: `Edit: ${customer.name}` })).toBeVisible();
  await expect(page.getByLabel('Customer Name')).toHaveValue(customer.name);
  const dimensions = await page.evaluate(() => ({ documentWidth: document.documentElement.scrollWidth, viewportWidth: window.innerWidth }));
  expect(dimensions.documentWidth).toBeLessThanOrEqual(dimensions.viewportWidth);
});

test('Customer 360 reports a contact failure and retries without claiming an empty result', async ({ page }) => {
  await setPermissions(page);
  await mockCustomerApis(page, { contactsFailUntil: 100 });
  await page.goto('/customers/401');
  const error = page.getByRole('alert').filter({ hasText: 'Contacts could not be loaded. No empty result has been assumed.' });
  await expect(error).toBeVisible();
  await page.route('**/api/Contact?*', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ items: [contact], totalCount: 1, pageNumber: 1, pageSize: 100 }),
  }));
  await error.getByRole('button', { name: 'Retry' }).click();
  await expect(page.getByText('robin@atlas.test')).toBeVisible();
});

test('Customer actions are hidden independently for a view-only role', async ({ page }) => {
  await setPermissions(page, { create: false, edit: false, delete: false });
  await mockCustomerApis(page);
  await page.goto('/customers/401');
  await expect(page.getByRole('button', { name: 'Edit Customer' })).toHaveCount(0);
  await page.goto('/customers');
  await expect(page.getByRole('button', { name: 'Add Customer' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Edit' })).toHaveCount(0);
});

test('customer and contact mutations validate input, omit actor fields, and confirm deactivation', async ({ page }) => {
  await setPermissions(page);
  await mockCustomerApis(page);
  let customerRequest = '';
  await page.route('**/api/Customer', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    customerRequest = route.request().postData() ?? '';
    return route.fulfill({ status: 201, contentType: 'application/json', body: JSON.stringify({ ...customer, id: 402 }) });
  });
  await page.goto('/customers');
  await page.getByRole('button', { name: 'Add Customer' }).click();
  await page.getByRole('button', { name: 'Save Customer' }).click();
  await expect(page.getByText('Customer name is required.')).toBeVisible();
  await page.getByLabel('Customer Name').fill('New Pilot Account');
  await page.getByLabel('Contact Email').fill('not-an-email');
  await page.getByRole('button', { name: 'Save Customer' }).click();
  await expect(page.getByText('Enter a valid email address.')).toBeVisible();
  await page.getByLabel('Contact Email').fill('new@pilot.test');
  await page.getByRole('button', { name: 'Save Customer' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(customerRequest.toLowerCase()).not.toContain('createdby');
  expect(customerRequest.toLowerCase()).not.toContain('businessunitid');

  let updateRequest = '';
  await page.route('**/api/Customer/401', route => {
    if (route.request().method() === 'PUT') {
      updateRequest = route.request().postData() ?? '';
      return route.fulfill({ status: 204 });
    }
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(customer) });
  });
  await page.goto('/customers?edit=401');
  await page.getByLabel('Customer Name').fill('Atlas Pilot Customer Updated');
  await page.getByRole('button', { name: 'Save Customer' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(updateRequest).toContain(customerToken);
  expect(updateRequest.toLowerCase()).not.toContain('name="isactive"');

  let deactivationToken: string | null = null;
  await page.route('**/api/Contact/501?*', route => {
    deactivationToken = new URL(route.request().url()).searchParams.get('concurrencyToken');
    return route.fulfill({ status: 204 });
  });
  await page.goto('/customers?edit=401');
  const contactsTable = page.getByRole('table', { name: 'Customer contacts' });
  await contactsTable.getByRole('button', { name: 'Deactivate contact' }).click();
  const confirmation = page.getByRole('dialog', { name: 'Deactivate contact?' });
  await expect(confirmation).toContainText('Robin Buyer');
  await confirmation.getByRole('button', { name: 'Cancel' }).click();
  await expect(confirmation).toHaveCount(0);
  await contactsTable.getByRole('button', { name: 'Deactivate contact' }).click();
  await page.getByRole('dialog', { name: 'Deactivate contact?' }).getByRole('button', { name: 'Deactivate' }).click();
  await expect.poll(() => deactivationToken).toBe(contactToken);

  let customerDeactivationToken: string | null = null;
  await page.route('**/api/Customer/401?*', route => {
    customerDeactivationToken = new URL(route.request().url()).searchParams.get('concurrencyToken');
    return route.fulfill({ status: 204 });
  });
  await page.goto('/customers?edit=401');
  await page.getByRole('dialog', { name: `Edit: ${customer.name}` }).getByRole('button', { name: 'Deactivate', exact: true }).click();
  const customerConfirmation = page.getByRole('dialog', { name: 'Deactivate customer?' });
  await expect(customerConfirmation).toContainText('All active contacts');
  await customerConfirmation.getByRole('button', { name: 'Deactivate customer' }).click();
  await expect.poll(() => customerDeactivationToken).toBe(customerToken);
});

test('supplier-only users can manage supplier contacts without customer permission', async ({ page }) => {
  await setPermissions(
    page,
    { create: false, edit: false, delete: false },
    { create: true, edit: true, delete: true },
  );
  const supplier = { id: 601, docId: 'SUP-601', name: 'Pilot Supply', isActive: true, concurrencyToken: '33333333-3333-3333-3333-333333333333' };
  await page.route('**/api/Supplier?*', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [supplier], totalCount: 1, pageNumber: 1, pageSize: 10 }) }));
  await page.route('**/api/Contact?*', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [{ ...contact, id: 602, customerId: null, supplierId: 601 }], totalCount: 1, pageNumber: 1, pageSize: 100 }) }));
  await page.route('**/api/Country?*', route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/Currency?*', route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.goto('/suppliers');
  await page.locator('.MuiDataGrid-virtualScroller').evaluate(element => element.scrollTo({ left: element.scrollWidth }));
  await page.getByRole('button', { name: 'Edit' }).click();
  await expect(page.getByRole('button', { name: 'Add Contact' })).toBeVisible();
  const contactsTable = page.getByRole('table');
  await expect(contactsTable.getByRole('button', { name: 'Edit' })).toBeVisible();
  await expect(contactsTable.getByRole('button', { name: 'Remove' })).toBeVisible();
});
