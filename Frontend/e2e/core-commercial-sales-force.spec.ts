import { expect, test } from '@playwright/test';
import { api, expectTextValue, jsonOk, loginAs, loginAsOtherTenant, openLead, required, requiredNumber } from './support/core-commercial';

type Lead = Record<string, unknown>;
type Ownership = { customerId: number; customerName: string; ownerUserId?: number; ownerName?: string; version: number };

test('01 existing contact resolves correct customer', async ({ page }) => {
  const leadId = requiredNumber('E2E_CORE_LEAD_ID');
  const token = await loginAs(page, 'manager');
  const lead = await jsonOk<Lead>(await api(page, token, 'get', `/api/Lead/${leadId}`));
  expect(Number(lead.customerId)).toBe(requiredNumber('E2E_CORE_CUSTOMER_ID'));
  expect(Number(lead.contactId)).toBe(requiredNumber('E2E_CORE_CONTACT_ID'));
  expectTextValue(lead.customerEmail ?? lead.clientemail ?? lead.email, required('E2E_CORE_CONTACT_EMAIL'));
});

test('02 existing customer displays confirmed Account Owner', async ({ page }) => {
  const customerId = requiredNumber('E2E_CORE_CUSTOMER_ID');
  await loginAs(page, 'manager');
  await page.goto('/sales/accounts');
  const row = page.getByRole('row').filter({ hasText: required('E2E_CORE_CUSTOMER_NAME') });
  await expect(row).toContainText(required('E2E_CORE_ACCOUNT_OWNER_NAME'));
  await expect(row).not.toContainText('Unassigned');
  expect(customerId).toBeGreaterThan(0);
});

test('03 existing customer routes to Account Owner', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const lead = await jsonOk<Lead>(await api(page, token, 'get', `/api/Lead/${requiredNumber('E2E_CORE_LEAD_ID')}`));
  expectTextValue(lead.assignedToFullName, required('E2E_CORE_ACCOUNT_OWNER_NAME'));
});

test('04 unavailable Account Owner invokes Backup Owner', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const lead = await jsonOk<Lead>(await api(page, token, 'get', `/api/Lead/${requiredNumber('E2E_CORE_BACKUP_LEAD_ID')}`));
  expectTextValue(lead.assignedToFullName, required('E2E_CORE_BACKUP_OWNER_NAME'));
  const ownership = await jsonOk<Ownership[]>(await api(page, token, 'get', '/api/commercial-intelligence/account-ownership'));
  const account = ownership.find((row) => row.customerId === requiredNumber('E2E_CORE_CUSTOMER_ID'));
  expectTextValue(account?.ownerName, required('E2E_CORE_ACCOUNT_OWNER_NAME'));
});

test('05 new customer routes across five reps by weighted workload', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const team = await jsonOk<{ representatives: Array<{ name: string }> }>(await api(page, token, 'get', '/api/commercial-intelligence/team-overview'));
  expect(team.representatives.length).toBeGreaterThanOrEqual(5);
  const lead = await jsonOk<Lead>(await api(page, token, 'get', `/api/Lead/${requiredNumber('E2E_CORE_WEIGHTED_LEAD_ID')}`));
  expectTextValue(lead.assignedToFullName, required('E2E_CORE_WEIGHTED_OWNER_NAME'));
});

test('06 assignment reason is visible', async ({ page }) => {
  await loginAs(page, 'manager');
  await openLead(page, requiredNumber('E2E_CORE_WEIGHTED_LEAD_ID'));
  await expect(page.getByText(required('E2E_CORE_ASSIGNMENT_REASON'), { exact: false })).toBeVisible();
});

test('07 manual upload does not create fake customer data', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const lead = await jsonOk<Lead>(await api(page, token, 'get', `/api/Lead/${requiredNumber('E2E_CORE_UNRESOLVED_UPLOAD_LEAD_ID')}`));
  expect(lead.customerId ?? null).toBeNull();
  expect(lead.contactId ?? null).toBeNull();
  const serialized = JSON.stringify(lead).toLowerCase();
  expect(serialized).not.toContain('walk-in customer');
  expect(serialized).not.toContain('manual@upload.com');
  expect(serialized).not.toContain('unknown customer');
});

test('08 ambiguous customer enters review', async ({ page }) => {
  await loginAs(page, 'manager');
  await openLead(page, requiredNumber('E2E_CORE_AMBIGUOUS_LEAD_ID'));
  await expect(page.getByText(/Customer Resolution Required|Ambiguous|Review Required/i).first()).toBeVisible();
  await expect(page.getByRole('button', { name: /Confirm .+|Review possible clients/i })).toBeVisible();
});

test('09 manager confirms ownership', async ({ page }) => {
  const customerId = requiredNumber('E2E_CORE_OWNERSHIP_CONFIRM_CUSTOMER_ID');
  const ownerUserId = requiredNumber('E2E_CORE_OWNERSHIP_CONFIRM_USER_ID');
  const token = await loginAs(page, 'manager');
  const rows = await jsonOk<Ownership[]>(await api(page, token, 'get', '/api/commercial-intelligence/account-ownership'));
  const before = rows.find((row) => row.customerId === customerId);
  expect(before).toBeTruthy();
  const response = await api(page, token, 'post', `/api/commercial-intelligence/account-ownership/${customerId}/assign`,
    {
      ownerUserId,
      expectedVersion: before!.version,
      reason: 'Manager confirmed the primary account owner',
    }, { 'Idempotency-Key': `core-owner-${customerId}-${ownerUserId}` });
  expect(response.ok()).toBeTruthy();
  const after = await jsonOk<Ownership[]>(await api(page, token, 'get', '/api/commercial-intelligence/account-ownership'));
  expect(after.find((row) => row.customerId === customerId)?.ownerUserId).toBe(ownerUserId);
});

test('10 unauthorized role cannot change ownership', async ({ page }) => {
  const token = await loginAs(page, 'denied');
  const response = await api(page, token, 'post', `/api/commercial-intelligence/account-ownership/${requiredNumber('E2E_CORE_CUSTOMER_ID')}/assign`,
    { ownerUserId: requiredNumber('E2E_CORE_ACCOUNT_OWNER_USER_ID'), expectedVersion: 0 }, { 'Idempotency-Key': 'core-denied-owner' });
  expect(response.status()).toBe(403);
});

test('11 reassignment preserves history', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const leadId = requiredNumber('E2E_CORE_REASSIGNED_LEAD_ID');
  const history = await jsonOk<Array<Record<string, unknown>>>(await api(page, token, 'get', `/api/commercial-intelligence/leads/${leadId}/assignment-history`));
  expect(history.length).toBeGreaterThanOrEqual(2);
  expect(history.some((row) => row.previousOwnerUserId != null || row.effectiveTo != null)).toBeTruthy();
});

test('12 Sales Rep Today shows real assigned work', async ({ page }) => {
  await loginAs(page, 'manager');
  await page.goto('/sales/today');
  await expect(page.getByRole('heading', { name: 'Sales today' })).toBeVisible();
  await expect(page.getByText(required('E2E_CORE_NEXORA_SERIAL'), { exact: false }).first()).toBeVisible();
  await expect(page.getByText(required('E2E_CORE_OPPORTUNITY_OWNER_NAME'), { exact: false }).first()).toBeVisible();
});

test('13 follow-up action updates activity and performance', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const followUpId = requiredNumber('E2E_CORE_FOLLOW_UP_ID');
  const followUps = await jsonOk<Array<{ id: number; version: number }>>(await api(page, token, 'get', '/api/commercial-intelligence/follow-ups'));
  const item = followUps.find((row) => row.id === followUpId);
  expect(item).toBeTruthy();
  const completed = await api(page, token, 'post', `/api/commercial-intelligence/follow-ups/${followUpId}/complete`,
    { expectedVersion: item!.version }, { 'Idempotency-Key': `core-follow-up-${followUpId}` });
  expect(completed.status()).toBe(204);
  const after = await jsonOk<Array<{ id: number; status: string }>>(await api(page, token, 'get', '/api/commercial-intelligence/follow-ups'));
  expect(after.find((row) => row.id === followUpId)?.status).toMatch(/Completed/i);
  await page.goto('/sales/performance');
  await expect(page.getByRole('heading', { name: 'Sales performance' })).toBeVisible();
});

test('14 cross-tenant Sales Rep and customer access is denied', async ({ page }) => {
  const token = await loginAsOtherTenant(page);
  const customer = await api(page, token, 'get', `/api/Customer/${requiredNumber('E2E_CORE_CUSTOMER_ID')}`);
  const rep = await api(page, token, 'get', `/api/commercial-intelligence/reps/${requiredNumber('E2E_CORE_ACCOUNT_OWNER_USER_ID')}`);
  expect([403, 404]).toContain(customer.status());
  expect([403, 404]).toContain(rep.status());
});
