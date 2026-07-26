import { expect, test, type Page } from '@playwright/test';
import { fixture } from './support/environment';

const now = '2026-07-26T12:00:00Z';

const installProcurementApi = async (
  page: Page,
  deliveryFailed = false,
  multipleLines = false,
  existingSplitAwards = false,
  authoritativeBlocked = false,
) => {
  const workbench: any = {
    rfqId: Number(fixture.rfqId), rfqNumber: 'RFQ-RELEASE-01C',
    nexoraSerial: fixture.nexoraSerial, customerName: fixture.customerName, currencyCode: 'USD',
    lines: [{
      id: 1, rfqId: Number(fixture.rfqId), productId: 701, partNumber: 'NXR-TEST-PART-001',
      description: 'Flight control component', requestedQuantity: 10, availableQuantity: 4,
      reservedQuantity: 4, shortfallQuantity: 6, resolution: 'PARTIAL', resolutionCheckedOn: now,
    }, ...(multipleLines ? [{
      id: 2, rfqId: Number(fixture.rfqId), productId: 702, partNumber: 'NXR-TEST-PART-002',
      description: 'Navigation component', requestedQuantity: 3, availableQuantity: 0,
      reservedQuantity: 0, shortfallQuantity: 3, resolution: 'SHORTAGE', resolutionCheckedOn: now,
    }] : [])],
    solicitations: [{
      id: 801, rfqId: Number(fixture.rfqId), supplierId: 901, supplierName: 'Certified Components Inc.',
      supplierEmail: 'quotes@certified-components.test', status: deliveryFailed ? 'DELIVERY_FAILED' : 'SENT',
      channel: 'EMAIL', attemptCount: 1, providerReference: deliveryFailed ? null : 'provider-801',
      lastErrorCode: deliveryFailed ? 'DELIVERY_UNCERTAIN' : null, sentOn: deliveryFailed ? null : now,
      respondedOn: null, updatedOn: now, requestedRfqItemIds: multipleLines ? [1, 2] : [1],
    }],
    offers: [], awards: [], purchaseOrders: [],
  };

  if (existingSplitAwards) {
    workbench.offers = [{
      id: 1101, solicitationId: 801, rfqItemId: 1, supplierId: 901,
      supplierName: 'Certified Components Inc.', quoteReference: 'SUP-Q-SPLIT', quoteRevision: 1,
      currencyId: 1, currencyCode: 'USD', quantity: 10, availableQuantity: 10, unitPrice: 20,
      freightCost: 12, dutyCost: 0, otherCost: 0, landedUnitCost: 22,
      leadTimeDays: 12, reliabilitySnapshot: 94, validUntil: '2026-09-30T23:59:59Z',
      eligible: true, blockingReasons: [], awarded: true, version: 1,
    }];
    workbench.awards = [
      {
        id: 1201, rfqItemId: 1, supplierQuotedItemId: 1101, supplierName: 'Certified Components Inc.',
        supplierId: 901, quantity: 4, landedUnitCost: 22, currencyCode: 'USD', currencyId: 1,
        status: 'SPLIT_APPROVED', rationale: 'First split', purchaseOrderId: null, version: 1,
      },
      {
        id: 1202, rfqItemId: 1, supplierQuotedItemId: 1101, supplierName: 'Certified Components Inc.',
        supplierId: 901, quantity: 2, landedUnitCost: 20, currencyCode: 'USD', currencyId: 1,
        status: 'APPROVED', rationale: 'Final split', purchaseOrderId: null, version: 1,
      },
    ];
    if (authoritativeBlocked) {
      workbench.offers[0].eligible = false;
      workbench.offers[0].blockingReasons = ['minimum order quantity cannot be satisfied'];
      workbench.offers[0].reliabilitySnapshot = 61;
      workbench.offers[0].awarded = false;
      workbench.awards = [];
    }
  }

  await page.route('**/api/procurement/rfqs/*/workbench', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(workbench) }));
  await page.route('**/api/procurement/rfq-items/*/quote-comparison', route => {
    const segments = new URL(route.request().url()).pathname.split('/');
    const rfqItemId = Number(segments.at(-2));
    const lines = workbench.offers
      .filter((offer: any) => offer.rfqItemId === rfqItemId)
      .map((offer: any) => ({
        supplierQuotedItemId: offer.id, supplierId: offer.supplierId,
        quantity: offer.quantity, availableQuantity: offer.availableQuantity,
        unitPrice: offer.unitPrice, landedUnitCost: offer.landedUnitCost,
        currencyId: offer.currencyId, leadTimeDays: offer.leadTimeDays,
        reliability: offer.reliabilitySnapshot, validUntil: offer.validUntil,
        blockers: offer.blockingReasons, eligible: offer.eligible,
      }));
    const recommended = lines.find((line: any) => line.eligible)?.supplierQuotedItemId ?? null;
    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ rfqItemId, lines, recommendedSupplierQuotedItemId: recommended }),
    });
  });
  await page.route('**/api/Supplier?**', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
    items: [{ id: 901, name: 'Certified Components Inc.', contactEmail: 'quotes@certified-components.test', isActive: true }],
    totalCount: 1, pageNumber: 1, pageSize: 500,
  }) }));
  await page.route('**/api/Currency?**', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
    items: [{ id: 1, code: 'USD', currencyName: 'US Dollar', symbol: '$', exchangeRate: 1, isBaseCurrency: true, businessUnitId: 9001, isActive: true }],
    totalItems: 1, pageNumber: 1, pageSize: 500, totalPages: 1,
  }) }));
  await page.route('**/api/Warehouse?**', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
    items: [{ id: 1001, warehouseCode: 'MAIN', warehouseName: 'Main Receiving', businessUnitId: 9001, isActive: true }],
    totalItems: 1, pageNumber: 1, pageSize: 500, totalPages: 1,
  }) }));

  await page.route('**/api/procurement/solicitations/*/retry', async route => {
    workbench.solicitations[0] = { ...workbench.solicitations[0], status: 'PENDING_DISPATCH', lastErrorCode: null, updatedOn: now };
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: 801, replayed: false }) });
  });
  await page.route('**/api/procurement/supplier-quotes', async route => {
    const request = route.request().postDataJSON();
    const revision = Number(request.revision);
    workbench.solicitations[0] = { ...workbench.solicitations[0], status: 'RESPONDED', respondedOn: now };
    workbench.offers.push({
      id: 1100 + revision, solicitationId: 801, rfqItemId: 1, supplierId: 901,
      supplierName: 'Certified Components Inc.', quoteReference: request.supplierQuoteReference, quoteRevision: revision,
      currencyId: 1, currencyCode: 'USD', quantity: request.lines[0].quantity,
      availableQuantity: request.lines[0].availableQuantity, unitPrice: request.lines[0].unitPrice,
      freightCost: request.lines[0].freightCost, dutyCost: request.lines[0].dutyCost,
      otherCost: request.lines[0].otherCost, landedUnitCost: request.lines[0].unitPrice + 2,
      leadTimeDays: request.lines[0].leadTimeDays, reliabilitySnapshot: request.lines[0].reliabilitySnapshot,
      validUntil: request.validUntil,
      eligible: true, blockingReasons: [], awarded: false, version: 1,
    });
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ quoteIds: [1100 + revision], replayed: false }) });
  });
  await page.route('**/api/procurement/awards', async route => {
    workbench.offers[0].awarded = true;
    workbench.awards = [{
      id: 1201, rfqItemId: 1, supplierQuotedItemId: 1101, supplierName: 'Certified Components Inc.',
      supplierId: 901, quantity: 6, landedUnitCost: 22, currencyCode: 'USD', currencyId: 1,
      status: 'APPROVED', rationale: 'Best eligible commercial offer', purchaseOrderId: null, version: 1,
    }];
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: 1201, replayed: false }) });
  });
  await page.route('**/api/procurement/purchase-orders', async route => {
    if (route.request().method() === 'GET') {
      const url = new URL(route.request().url());
      const search = (url.searchParams.get('search') || '').toLowerCase();
      const summaries = [{
        id: 1301, purchaseOrderNumber: 'PO-SIT-001', rfqId: Number(fixture.rfqId),
        rfqNumber: 'RFQ-RELEASE-01C', nexoraSerial: fixture.nexoraSerial,
        supplierId: 901, supplierName: 'Certified Components Inc.', currencyCode: 'USD',
        status: 'DRAFT', totalValue: 132, expectedOn: '2026-08-15T00:00:00Z',
        createdOn: now, lineCount: 1, openQuantity: 6,
      }].filter(order => !search || JSON.stringify(order).toLowerCase().includes(search));
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(summaries) });
      return;
    }
    const request = route.request().postDataJSON();
    expect(request.purchaseOrderNumber).toBeUndefined();
    workbench.awards
      .filter((award: any) => request.awardIds.includes(award.id))
      .forEach((award: any) => {
        award.status = 'CONVERTED_TO_PO';
        award.purchaseOrderId = 1301;
      });
    workbench.purchaseOrders = [{
      id: 1301, rfqId: Number(fixture.rfqId), purchaseOrderNumber: 'PO-SIT-001', supplierId: 901,
      supplierName: 'Certified Components Inc.', currencyId: 1, currencyCode: 'USD', status: 'DRAFT',
      totalValue: 132, expectedOn: '2026-08-15T00:00:00Z', version: 1,
      lines: [{ id: 1401, rfqItemId: 1, productId: 701, description: 'Flight control component',
        orderedQuantity: 6, receivedQuantity: 0, openQuantity: 6, unitCost: 20, landedUnitCost: 22, warehouseId: 1001 }],
    }];
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      id: 1301, purchaseOrderNumber: 'PO-SIT-001', status: 'DRAFT', replayed: false,
    }) });
  });
  await page.route('**/api/procurement/purchase-orders/*/issue', async route => {
    const request = route.request().postDataJSON();
    expect(request.expectedVersion).toBe(1);
    expect(request.deliveryEvidenceReference).toBe('provider-receipt-1301');
    expect(request.deliveryEvidenceSha256).toBe('a'.repeat(64));
    expect(request.deliveredOn).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:00\.000Z$/);
    expect(new Date(request.deliveredOn).getTime()).toBeLessThanOrEqual(Date.now() + 5 * 60_000);
    workbench.purchaseOrders[0].status = 'ISSUED';
    workbench.purchaseOrders[0].version = 2;
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      id: 1301, purchaseOrderNumber: 'PO-SIT-001', status: 'ISSUED', replayed: false,
    }) });
  });
  await page.route('**/api/procurement/goods-receipts', async route => {
    const request = route.request().postDataJSON();
    expect(request.receivedOn).not.toMatch(/T00:00:00(?:\.000)?Z$/);
    const order = workbench.purchaseOrders[0];
    order.status = 'RECEIVED'; order.version = 2;
    order.lines[0].receivedQuantity = 6; order.lines[0].openQuantity = 0;
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: 1501, status: 'POSTED', replayed: false }) });
  });
};

test('authenticated buyer completes shortage to receipt with immutable commercial context', async ({ page }) => {
  await installProcurementApi(page);
  await page.goto(`/procurement/rfqs/${fixture.rfqId}/sourcing`);
  await expect(page.getByRole('heading', { name: 'Sourcing Workbench' })).toBeVisible();
  await expect(page.getByText(fixture.nexoraSerial!)).toBeVisible();
  await expect(page.getByText(fixture.customerName!)).toBeVisible();
  await expect(page.getByText('NXR-TEST-PART-001')).toBeVisible();
  await expect(page.getByText('PARTIAL', { exact: true })).toBeVisible();

  await page.getByRole('tab', { name: /Solicitations/ }).click();
  await page.getByRole('button', { name: 'Capture response' }).click();
  await page.getByLabel('Supplier quote reference').fill('SUP-Q-9001');
  await page.getByRole('dialog').getByRole('combobox').first().click();
  await page.getByRole('option', { name: /USD/ }).click();
  await page.getByLabel('Quoted quantity').fill('6');
  await page.getByLabel('Available quantity').fill('6');
  await page.getByLabel('Unit price').fill('20');
  await page.getByLabel('Lead time (days)').fill('12');
  await page.getByLabel('Supplier reliability (%)').fill('94');
  await page.getByLabel('Freight').fill('12');
  await page.getByLabel('Tax amount').fill('4');
  await page.getByLabel('Discount amount').fill('2');
  await page.getByLabel('Minimum order quantity').fill('3');
  await page.getByLabel('Valid until').fill('2026-09-30');
  await page.getByRole('button', { name: 'Save response' }).click();

  await expect(page.getByText('SUP-Q-9001')).toBeVisible();
  await expect(page.getByText('94%')).toBeVisible();
  await expect(page.getByText('Recommended')).toBeVisible();
  await expect(page.getByText('Complete', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Approve' }).click();
  await page.getByRole('button', { name: 'Approve award' }).click();
  await page.getByRole('button', { name: 'Create supplier PO' }).click();
  await page.getByRole('dialog').getByRole('combobox').nth(1).click();
  await page.getByRole('option', { name: /MAIN/ }).click();
  await page.getByLabel('Expected delivery').fill('2026-08-15');
  await page.getByRole('button', { name: 'Create PO' }).click();

  await expect(page.getByText('PO-SIT-001', { exact: true })).toBeVisible();
  await expect(page.getByText('DRAFT', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Record receipt' })).toBeDisabled();
  await page.getByRole('button', { name: 'Issue PO' }).click();
  await page.getByLabel('Delivery evidence reference').fill('provider-receipt-1301');
  await page.getByLabel('Delivery evidence SHA-256').fill('not-a-valid-hash');
  await expect(page.getByRole('dialog').getByRole('button', { name: 'Issue PO' })).toBeDisabled();
  await page.getByLabel('Delivery evidence SHA-256').fill('a'.repeat(64));
  await page.getByRole('dialog').getByRole('button', { name: 'Issue PO' }).click();
  await expect(page.getByText('ISSUED', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Record receipt' }).click();
  await page.getByLabel('Receipt reference').fill('GR-SIT-001');
  await page.getByRole('button', { name: 'Post receipt' }).click();
  await expect(page.getByText('RECEIVED', { exact: true })).toBeVisible();
  await expect(page.getByRole('cell', { name: '0', exact: true })).toBeVisible();
});

test('delivery uncertainty is visible and requires an explicit retry', async ({ page }) => {
  await installProcurementApi(page, true);
  await page.goto(`/procurement/rfqs/${fixture.rfqId}/sourcing`);
  await page.getByRole('tab', { name: /Solicitations/ }).click();
  await expect(page.getByText('DELIVERY_UNCERTAIN')).toBeVisible();
  await page.getByRole('button', { name: 'Retry' }).click();
  await expect(page.getByText('PENDING DISPATCH')).toBeVisible();
});

test('partial supplier response submits only explicitly included lines', async ({ page }) => {
  await installProcurementApi(page, false, true);
  await page.goto(`/procurement/rfqs/${fixture.rfqId}/sourcing`);
  await page.getByRole('tab', { name: /Solicitations/ }).click();
  await page.getByRole('button', { name: 'Capture response' }).click();
  await page.getByLabel('Include NXR-TEST-PART-002').uncheck();
  await page.getByLabel('Supplier quote reference').fill('SUP-Q-PARTIAL');
  await page.getByRole('dialog').getByRole('combobox').first().click();
  await page.getByRole('option', { name: /USD/ }).click();
  await page.getByLabel('Quoted quantity').fill('6');
  await page.getByLabel('Available quantity').fill('6');
  await page.getByLabel('Unit price').fill('20');
  await page.getByLabel('Lead time (days)').fill('12');
  await page.getByLabel('Supplier reliability (%)').fill('94');
  await page.getByLabel('Tax amount').fill('4');
  await page.getByLabel('Discount amount').fill('2');
  await page.getByLabel('Minimum order quantity').fill('3');
  await page.getByLabel('Valid until').fill('2026-09-30');

  const requestPromise = page.waitForRequest('**/api/procurement/supplier-quotes');
  await page.getByRole('button', { name: 'Save response' }).click();
  const request = await requestPromise;
  const payload = request.postDataJSON();
  expect(payload.lines).toHaveLength(1);
  expect(payload.lines[0].rfqItemId).toBe(1);
  expect(payload.lines[0].quantity).toBe(6);
  expect(payload.lines[0].taxAmount).toBe(4);
  expect(payload.lines[0].discountAmount).toBe(2);
  expect(payload.lines[0].minimumOrderQuantity).toBe(3);
});

test('net shortfall remains authoritative and all split awards can create a PO', async ({ page }) => {
  await installProcurementApi(page, false, false, true);
  await page.goto(`/procurement/rfqs/${fixture.rfqId}/sourcing`);
  await page.getByRole('tab', { name: /Supplier offers/ }).click();

  await expect(page.getByRole('cell', { name: '6', exact: true }).last()).toBeVisible();
  await page.getByRole('button', { name: 'Create supplier PO' }).click();
  await page.getByRole('dialog').getByRole('combobox').nth(1).click();
  await page.getByRole('option', { name: /MAIN/ }).click();
  await page.getByLabel('Expected delivery').fill('2026-08-15');
  const requestPromise = page.waitForRequest('**/api/procurement/purchase-orders');
  await page.getByRole('button', { name: 'Create PO' }).click();
  const request = await requestPromise;
  expect(request.postDataJSON().awardIds).toEqual([1201, 1202]);
});

test('authoritative comparison exposes blockers and prevents an invalid award', async ({ page }) => {
  await installProcurementApi(page, false, false, true, true);
  await page.goto(`/procurement/rfqs/${fixture.rfqId}/sourcing`);
  await page.getByRole('tab', { name: /Supplier offers/ }).click();

  await expect(page.getByText('minimum order quantity cannot be satisfied')).toBeVisible();
  await expect(page.getByText('61%')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Approve' })).toBeDisabled();
  await expect(page.getByText('Recommended')).toHaveCount(0);
});

test('responded solicitation accepts an immutable supplier quote revision', async ({ page }) => {
  await installProcurementApi(page);
  await page.goto(`/procurement/rfqs/${fixture.rfqId}/sourcing`);
  await page.getByRole('tab', { name: /Solicitations/ }).click();

  const captureRevision = async (reference: string, revision: string, unitPrice: string) => {
    await page.getByRole('button', { name: 'Capture response' }).click();
    await page.getByLabel('Supplier quote reference').fill(reference);
    await page.getByLabel('Revision').fill(revision);
    await page.getByRole('dialog').getByRole('combobox').first().click();
    await page.getByRole('option', { name: /USD/ }).click();
    await page.getByLabel('Quoted quantity').fill('6');
    await page.getByLabel('Available quantity').fill('6');
    await page.getByLabel('Unit price').fill(unitPrice);
    await page.getByLabel('Lead time (days)').fill('12');
    await page.getByLabel('Supplier reliability (%)').fill('94');
    await page.getByLabel('Valid until').fill('2026-09-30');
    const requestPromise = page.waitForRequest('**/api/procurement/supplier-quotes');
    await page.getByRole('button', { name: 'Save response' }).click();
    return (await requestPromise).postDataJSON();
  };

  await captureRevision('SUP-Q-REV', '1', '20');
  await page.getByRole('tab', { name: /Solicitations/ }).click();
  await expect(page.getByText('RESPONDED', { exact: true })).toBeVisible();
  const revisionRequest = await captureRevision('SUP-Q-REV', '2', '19');
  expect(revisionRequest.revision).toBe(2);

  await page.getByRole('tab', { name: /Supplier offers/ }).click();
  await expect(page.getByText('Rev 1')).toBeVisible();
  await expect(page.getByText('Rev 2')).toBeVisible();
});

test('workbench keeps the mobile viewport free of document overflow', async ({ page }) => {
  await installProcurementApi(page);
  await page.goto(`/procurement/rfqs/${fixture.rfqId}/sourcing`);
  await expect(page.getByRole('heading', { name: 'Sourcing Workbench' })).toBeVisible();
  const dimensions = await page.evaluate(() => ({
    documentWidth: document.documentElement.scrollWidth,
    viewportWidth: window.innerWidth,
  }));
  expect(dimensions.documentWidth).toBeLessThanOrEqual(dimensions.viewportWidth);
});

test('supplier purchase-order search locates an order and opens its RFQ workbench', async ({ page }) => {
  await installProcurementApi(page);
  await page.goto('/suppliers/purchase-orders');
  await expect(page.getByRole('heading', { name: 'Supplier purchase orders' })).toBeVisible();
  await expect(page.getByText('PO-SIT-001', { exact: true })).toBeVisible();
  await expect(page.getByText(fixture.nexoraSerial!, { exact: true })).toBeVisible();
  await page.getByLabel('Search purchase orders').fill('Certified Components');
  const searchRequest = page.waitForRequest(request =>
    request.url().includes('/api/procurement/purchase-orders') &&
    request.url().includes('search=Certified+Components'),
  );
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  await searchRequest;
  await page.getByRole('button', { name: 'Open workbench' }).click();
  await expect(page).toHaveURL(new RegExp(`/procurement/rfqs/${fixture.rfqId}/sourcing$`));
  await expect(page.getByRole('heading', { name: 'Sourcing Workbench' })).toBeVisible();
});
