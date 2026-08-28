import { expect, test } from '@playwright/test';
import { api, apiUrl, jsonOk, loginAs, loginAsOtherTenant, openLeadIntelligence, required, requiredNumber, resolutionFor, resolveLead } from './support/core-commercial';

type Availability = { partNumber: string; warehouseId: number; warehouseName: string; onHand: number; reserved: number; available: number; incoming: number };

test('15 exact Product match displays ATP', async ({ page }) => {
  const leadId = requiredNumber('E2E_CORE_LEAD_ID');
  const part = required('E2E_CORE_FULL_ATP_PART');
  const token = await loginAs(page, 'manager');
  const row = resolutionFor(await resolveLead(page, token, leadId), part);
  expect(row.classification).toBe('KnownInStock');
  expect(Number(row.availableToPromise)).toBeGreaterThanOrEqual(Number(required('E2E_CORE_FULL_ATP_REQUESTED_QTY')));
  await openLeadIntelligence(page, leadId);
  await expect(page.getByText(part, { exact: true }).first()).toBeVisible();
  await expect(page.getByText(/Known, in stock/)).toBeVisible();
});

test('16 reserved stock is excluded from ATP', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const rows = await jsonOk<Availability[]>(await api(page, token, 'get', `/api/inventory-intelligence/availability?search=${encodeURIComponent(required('E2E_CORE_RESERVED_PART'))}`));
  const row = rows.find((value) => value.reserved > 0);
  expect(row).toBeTruthy();
  expect(row!.available).toBeLessThan(row!.onHand);
  expect(row!.onHand - row!.reserved).toBeGreaterThanOrEqual(row!.available);
});

test('17 partial availability displays correctly', async ({ page }) => {
  const part = required('E2E_CORE_PARTIAL_ATP_PART');
  const token = await loginAs(page, 'manager');
  const row = resolutionFor(await resolveLead(page, token, requiredNumber('E2E_CORE_LEAD_ID')), part);
  expect(row.classification).toBe('KnownShortage');
  expect(Number(row.availableToPromise)).toBeGreaterThan(0);
  expect(Number(row.availableToPromise)).toBeLessThan(Number(row.requestedQuantity));
});

test('18 out-of-stock item shows known resources', async ({ page }) => {
  const row = resolutionFor(await resolveLead(page, await loginAs(page, 'manager'), requiredNumber('E2E_CORE_LEAD_ID')), required('E2E_CORE_OUT_OF_STOCK_PART'));
  expect(row.classification).toBe('KnownShortage');
  expect(Number(row.availableToPromise)).toBe(0);
  expect(Array.isArray(row.relatedResources) && row.relatedResources.length > 0).toBeTruthy();
});

test('19 incoming stock shows quantity and expected date', async ({ page }) => {
  const part = required('E2E_CORE_INCOMING_PART');
  const token = await loginAs(page, 'manager');
  const rows = await jsonOk<Array<{ partNumber: string; orderedQuantity: number; receivedQuantity: number; expectedAt?: string }>>(
    await api(page, token, 'get', '/api/inventory-intelligence/incoming'));
  const row = rows.find((value) => value.partNumber === part && value.orderedQuantity > value.receivedQuantity);
  expect(row).toBeTruthy();
  expect(row!.expectedAt).toBeTruthy();
  await page.goto('/inventory/incoming');
  const expectedDate = await page.evaluate((value) => new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }), row!.expectedAt!);
  await expect(page.getByRole('row').filter({ hasText: part })).toContainText(expectedDate);
});

test('20 unknown item opens related-resource search', async ({ page }) => {
  const part = required('E2E_CORE_UNKNOWN_PART');
  const token = await loginAs(page, 'manager');
  const row = resolutionFor(await resolveLead(page, token, requiredNumber('E2E_CORE_LEAD_ID')), part);
  expect(row.classification).toBe('UnknownProduct');
  await openLeadIntelligence(page, requiredNumber('E2E_CORE_LEAD_ID'));
  await expect(page.getByText(/x.?unknown.?900/i).first()).toBeVisible();
  await expect(page.getByText(/Unknown product/)).toBeVisible();
  await page.goto('/inventory/resources');
  await expect(page.getByRole('heading', { name: 'Related resources' })).toBeVisible();
});

test('21 result-count selection supports 10/20/50', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  await openLeadIntelligence(page, requiredNumber('E2E_CORE_LEAD_ID'));
  const select = page.getByRole('combobox', { name: 'Supplier options' });
  await expect(select).toBeVisible();
  for (const value of ['10', '20', '50']) {
    await select.click();
    await expect(page.getByRole('option', { name: value, exact: true })).toBeVisible();
    await page.getByRole('option', { name: value, exact: true }).click();
    await expect(select).toHaveText(value);
    const rows = await resolveLead(
      page,
      token,
      requiredNumber('E2E_CORE_LEAD_ID'),
      Number(value) as 10 | 20 | 50,
    );
    expect(rows).toHaveLength(6);
    expect(rows.every((row) => Number(row.resourceLimit) >= Number(value))).toBe(true);
  }
});

test('22 service line bypasses inventory lookup', async ({ page }) => {
  const row = resolutionFor(await resolveLead(page, await loginAs(page, 'manager'), requiredNumber('E2E_CORE_LEAD_ID')), required('E2E_CORE_SERVICE_LINE_REFERENCE'));
  expect(String(row.classification)).toMatch(/Service|NonInventory/i);
  expect(row.productId ?? null).toBeNull();
  expect(Number(row.availableToPromise)).toBe(0);
});

test('23 inventory failure shows Check Unavailable, not Out of Stock', async ({ page }) => {
  const leadId = requiredNumber('E2E_CORE_INVENTORY_FAILURE_LEAD_ID');
  await page.route(`${apiUrl}/api/inventory-intelligence/leads/${leadId}/resolve?*`, async (route) => {
    if (route.request().method() !== 'POST') return route.continue();
    await route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'Acceptance-simulated inventory dependency outage.' }) });
  });
  await loginAs(page, 'manager');
  await openLeadIntelligence(page, leadId);
  await page.getByRole('button', { name: 'Check', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: /Inventory Check Unavailable/i })).toBeVisible();
  await expect(page.getByText(/Out of Stock/i)).toHaveCount(0);
});

test('24 multi-warehouse fulfilment is shown', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const part = required('E2E_CORE_MULTI_WAREHOUSE_PART');
  const availability = await jsonOk<Availability[]>(await api(page, token, 'get', `/api/inventory-intelligence/availability?search=${encodeURIComponent(part)}`));
  expect(new Set(availability.filter((row) => row.partNumber === part && row.available > 0).map((row) => row.warehouseId)).size).toBeGreaterThanOrEqual(2);
  const resolution = resolutionFor(await resolveLead(page, token, requiredNumber('E2E_CORE_LEAD_ID')), part);
  expect(JSON.stringify(resolution.fulfilment).toLowerCase()).toMatch(/warehouse|allocation/);
});

test('25 inventory reservation prevents double allocation', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const orderId = requiredNumber('E2E_CORE_DOUBLE_ALLOCATION_ORDER_ID');
  const headers = { 'Idempotency-Key': `core-double-allocation-${orderId}` };
  const before = await jsonOk<Array<{ demandReference: string }>>(
    await api(page, token, 'get', '/api/inventory-intelligence/reservations'),
  );
  const [first, second] = await Promise.all([
    api(page, token, 'post', `/api/Order/${orderId}/allocate`, undefined, headers),
    api(page, token, 'post', `/api/Order/${orderId}/allocate`, undefined, headers),
  ]);
  expect(first.ok()).toBeTruthy();
  expect(second.ok()).toBeTruthy();
  const after = await jsonOk<Array<{ demandReference: string }>>(
    await api(page, token, 'get', '/api/inventory-intelligence/reservations'),
  );
  const orderReference = `Order ${orderId}`;
  const beforeCount = before.filter((row) => row.demandReference === orderReference).length;
  const afterCount = after.filter((row) => row.demandReference === orderReference).length;
  expect(afterCount).toBe(1);
  expect(afterCount - beforeCount).toBeGreaterThanOrEqual(0);
  expect(afterCount - beforeCount).toBeLessThanOrEqual(1);
});

test('26 inventory change marks downstream state stale', async ({ page }) => {
  await loginAs(page, 'manager');
  await page.goto(`/sales/quotes/view/${requiredNumber('E2E_CORE_STALE_QUOTE_ID')}`);
  await expect(page.getByText(/Inventory.*stale|Stock.*changed|Revalidation required/i).first()).toBeVisible();
  await expect(page.getByRole('button', { name: /Ready to Send/i })).toBeDisabled();
});

test('27 cross-tenant inventory access is denied', async ({ page }) => {
  const token = await loginAsOtherTenant(page);
  const part = required('E2E_CORE_FULL_ATP_PART');
  const response = await api(page, token, 'get', `/api/inventory-intelligence/availability?search=${encodeURIComponent(part)}`);
  if (response.status() === 403) return;
  const rows = await jsonOk<Availability[]>(response);
  expect(rows).toHaveLength(0);
});
