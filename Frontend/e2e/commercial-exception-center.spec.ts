import { expect, test } from '@playwright/test';
import { loginThroughUi } from './support/login';

const email = process.env.E2E_MANAGER_EMAIL;
const password = process.env.E2E_MANAGER_PASSWORD;
const userEmail = process.env.E2E_USER_EMAIL;
const userPassword = process.env.E2E_USER_PASSWORD;
const expectedSerial = process.env.E2E_NEXORA_SERIAL;
const apiBaseUrl = process.env.E2E_API_BASE_URL ?? 'http://127.0.0.1:5193';

test('manager reconciles and governs a persisted commercial exception', async ({ page }) => {
  if (!email || !password || !expectedSerial) {
    throw new Error('E2E_MANAGER_EMAIL, E2E_MANAGER_PASSWORD, and E2E_NEXORA_SERIAL are required.');
  }

  await loginThroughUi(page, { email, password });
  const normalizationStatus = await page.evaluate(async ({ baseUrl, serial }) => {
    const token = localStorage.getItem('token');
    const headers = { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };
    const list = await fetch(`${baseUrl}/api/commercial-exceptions?type=OverdueFollowUp`, { headers });
    const payload = await list.json();
    const item = payload.items.find((candidate: { nexoraSerial: string; ownerUserId?: number; status: string }) =>
      candidate.nexoraSerial === serial && candidate.ownerUserId === 1 && candidate.status === 'Acknowledged');
    if (!item) return 204;

    const idempotencyKey = crypto.randomUUID();
    const correlationId = crypto.randomUUID();
    const response = await fetch(`${baseUrl}/api/commercial-exceptions/${item.id}/transition`, {
      method: 'POST',
      headers: {
        ...headers,
        'Idempotency-Key': idempotencyKey,
        'X-Correlation-ID': correlationId,
      },
      body: JSON.stringify({
        expectedVersion: item.version,
        targetStatus: 'Resolved',
        actionCode: 'RESOLVE',
        reason: 'Browser SIT repeatability normalization.',
        idempotencyKey,
        correlationId,
      }),
    });
    return response.status;
  }, { baseUrl: apiBaseUrl, serial: expectedSerial });
  expect([200, 204]).toContain(normalizationStatus);

  await page.getByRole('button', { name: 'Commercial exceptions' }).click();
  await expect(page).toHaveURL(/\/sales\/exceptions$/);
  await expect(page.getByRole('heading', { name: 'Commercial Exception Center' })).toBeVisible();

  const metricDefinitions = [
    ['Matching current filters', 'Records matching the current filters.'],
    ['Active in scope', 'Open or acknowledged records in the current access scope, independent of filters.'],
    ['Critical active', 'Active critical-severity records in the current access scope, independent of filters.'],
    ['SLA overdue active', 'Active records past their SLA due time in the current access scope, independent of filters.'],
  ] as const;
  for (const [label, definitionText] of metricDefinitions) {
    await expect(page.getByLabel(`Definition: ${label}. ${definitionText}`)).toBeVisible();
  }

  const refreshIdentities: Array<{ idempotencyKey?: string; correlationId?: string }> = [];
  let refreshAttempt = 0;
  await page.route('**/api/commercial-exceptions/refresh', async (route) => {
    if (route.request().method() !== 'POST') return route.continue();
    const headers = route.request().headers();
    refreshIdentities.push({
      idempotencyKey: headers['idempotency-key'],
      correlationId: headers['x-correlation-id'],
    });
    refreshAttempt += 1;
    if (refreshAttempt === 1) return route.abort('failed');
    return route.continue();
  });
  const refreshResponse = page.waitForResponse((response) =>
    response.url().endsWith('/api/commercial-exceptions/refresh') && response.request().method() === 'POST');
  await page.getByRole('button', { name: /Reconcile sources|Retry reconciliation/ }).click();
  expect((await refreshResponse).status()).toBe(200);
  await expect.poll(() => refreshIdentities.length).toBe(2);
  expect(refreshIdentities[1]).toEqual(refreshIdentities[0]);
  await page.unroute('**/api/commercial-exceptions/refresh');

  await page.getByLabel('Exception type').click();
  await page.getByRole('option', { name: 'Overdue Follow Up' }).click();
  await page.getByRole('button', { name: 'SLA overdue only' }).click();
  await expect(page.getByRole('button', { name: 'SLA overdue only' })).toHaveAttribute('aria-pressed', 'true');

  let row = page.getByRole('row')
    .filter({ hasText: expectedSerial })
    .filter({ hasText: 'Follow-up is overdue' })
    .filter({ hasText: 'Robert Pilot' });
  await expect(row).toBeVisible();
  await expect(row).toContainText('Overdue Follow Up');
  await expect(row).toContainText(/Open|Acknowledged/);

  await row.getByRole('button', { name: 'Evidence' }).click();
  const evidenceDialog = page.getByRole('dialog', { name: 'Source evidence' });
  await expect(evidenceDialog).toContainText(`${expectedSerial}`);
  await expect(evidenceDialog).toContainText('Source version 1');
  await expect(evidenceDialog).toContainText('FollowUpTask');
  await evidenceDialog.getByRole('button', { name: 'Close' }).click();

  const acknowledge = row.getByRole('button', { name: 'Acknowledge' });
  await expect(acknowledge).toBeVisible();
  await acknowledge.click();
  const decisionDialog = page.getByRole('dialog', { name: 'Acknowledged exception' });
  await decisionDialog.getByLabel('Decision reason').fill('Browser SIT verified the source evidence and ownership.');
  const transitionResponse = page.waitForResponse((response) =>
    /\/api\/commercial-exceptions\/\d+\/transition$/.test(response.url()) && response.request().method() === 'POST');
  await decisionDialog.getByRole('button', { name: 'Record decision' }).click();
  expect((await transitionResponse).status()).toBe(200);
  await expect(page.getByRole('dialog', { name: 'Acknowledged exception' })).toHaveCount(0);
  await page.reload();
  row = page.getByRole('row')
    .filter({ hasText: expectedSerial })
    .filter({ hasText: 'Follow-up is overdue' })
    .filter({ hasText: 'Robert Pilot' });
  await expect(row).toContainText('Acknowledged');
  await expect(page.getByText('Source coverage:')).toHaveCount(0);

  await row.getByRole('button', { name: 'Open source' }).click();
  await expect(page).toHaveURL(/\/sales\/follow-ups\?sourceId=\d+$/);
  await expect(page.getByRole('heading', { name: 'Follow-ups' })).toBeVisible();
  const sourceFollowUp = page.getByRole('row').filter({ hasText: 'CUSTOMER_FOLLOW_UP' }).filter({ hasText: 'Robert Pilot' });
  await expect(sourceFollowUp).toHaveCount(1);
  await page.goBack();
  await expect(page.getByRole('heading', { name: 'Commercial Exception Center' })).toBeVisible();

  const unassignedRow = page.getByRole('row')
    .filter({ hasText: expectedSerial })
    .filter({ hasText: 'Lead requires assignment' });
  await expect(unassignedRow).toHaveCount(1);
  await unassignedRow.getByRole('button', { name: 'Open source' }).click();
  await expect(page).toHaveURL(/\/sales\/routing\?sourceId=\d+$/);
  await expect(page.getByRole('heading', { name: 'Routing queue' })).toBeVisible();
  await expect(page.getByRole('row').filter({ hasText: expectedSerial })).toHaveCount(1);
  await page.goBack();
  await expect(page.getByRole('heading', { name: 'Commercial Exception Center' })).toBeVisible();

  row = page.getByRole('row')
    .filter({ hasText: expectedSerial })
    .filter({ hasText: 'Follow-up is overdue' })
    .filter({ hasText: 'Robert Pilot' });
  await row.getByRole('button', { name: 'Resolve' }).click();
  const staleDialog = page.getByRole('dialog', { name: 'Resolved exception' });
  await staleDialog.getByLabel('Decision reason').fill('Browser SIT stale-version recovery check.');
  const directTransition = await page.evaluate(async ({ baseUrl, serial }) => {
    const token = localStorage.getItem('token');
    const headers = { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };
    const list = await fetch(`${baseUrl}/api/commercial-exceptions?type=OverdueFollowUp`, { headers });
    const payload = await list.json();
    const item = payload.items.find((candidate: { nexoraSerial: string; ownerUserId?: number }) =>
      candidate.nexoraSerial === serial && candidate.ownerUserId === 1);
    const idempotencyKey = crypto.randomUUID();
    const correlationId = crypto.randomUUID();
    const response = await fetch(`${baseUrl}/api/commercial-exceptions/${item.id}/transition`, {
      method: 'POST',
      headers: {
        ...headers,
        'Idempotency-Key': idempotencyKey,
        'X-Correlation-ID': correlationId,
      },
      body: JSON.stringify({
        expectedVersion: item.version,
        targetStatus: 'Resolved',
        actionCode: 'RESOLVE',
        reason: 'Concurrent authorized browser transition.',
        idempotencyKey,
        correlationId,
      }),
    });
    return response.status;
  }, { baseUrl: apiBaseUrl, serial: expectedSerial });
  expect(directTransition).toBe(200);
  const conflictResponse = page.waitForResponse((response) =>
    /\/api\/commercial-exceptions\/\d+\/transition$/.test(response.url()) && response.status() === 409);
  await staleDialog.getByRole('button', { name: 'Record decision' }).click();
  await conflictResponse;
  await expect(page.getByText(/Commercial exception changed/)).toBeVisible();
  await expect(row).toContainText('Resolved');
  const reopenResponse = page.waitForResponse((response) =>
    response.url().endsWith('/api/commercial-exceptions/refresh') && response.request().method() === 'POST');
  await page.getByRole('button', { name: /Reconcile sources|Retry reconciliation/ }).click();
  expect((await reopenResponse).status()).toBe(200);
  await expect(row).toContainText('Open');

  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.getByRole('article')
    .filter({ hasText: expectedSerial })
    .filter({ hasText: 'Follow-up is overdue' })
    .filter({ hasText: 'Robert Pilot' })).toBeVisible();
  const horizontalOverflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
  expect(horizontalOverflow).toBe(false);
});

test('individual sees owned exceptions and cannot perform manager or decision actions', async ({ page }) => {
  if (!userEmail || !userPassword || !expectedSerial) {
    throw new Error('E2E_USER_EMAIL, E2E_USER_PASSWORD, and E2E_NEXORA_SERIAL are required.');
  }

  await loginThroughUi(page, { email: userEmail, password: userPassword });
  await page.getByRole('button', { name: 'Sales Management' }).click();
  await page.getByText('Commercial Exceptions', { exact: true }).click();
  await expect(page).toHaveURL(/\/sales\/exceptions$/);
  await expect(page.getByRole('heading', { name: 'Commercial Exception Center' })).toBeVisible();
  await expect(page.getByRole('button', { name: /Reconcile sources|Retry reconciliation/ })).toHaveCount(0);

  const rows = page.getByRole('row')
    .filter({ hasText: expectedSerial })
    .filter({ hasText: 'Follow-up is overdue' });
  await expect(rows).toHaveCount(1);
  await expect(rows.first()).toContainText('Browser Representative');
  await expect(rows.first().getByRole('button', { name: 'Acknowledge' })).toHaveCount(0);
  await expect(rows.first().getByRole('button', { name: 'Resolve' })).toHaveCount(0);
  await expect(rows.first().getByRole('button', { name: 'Dismiss' })).toHaveCount(0);

  const forbiddenStatus = await page.evaluate(async (baseUrl) => {
    const token = localStorage.getItem('token');
    const response = await fetch(`${baseUrl}/api/commercial-exceptions/refresh`, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${token}`,
        'Content-Type': 'application/json',
        'Idempotency-Key': crypto.randomUUID(),
        'X-Correlation-ID': crypto.randomUUID(),
      },
      body: JSON.stringify({}),
    });
    return response.status;
  }, apiBaseUrl);
  expect(forbiddenStatus).toBe(403);

  await page.getByLabel('Status').click();
  await page.getByRole('option', { name: 'Dismissed' }).click();
  await expect(page.getByText('No commercial exceptions match this scope and filter.')).toBeVisible();
  await page.getByRole('button', { name: 'Clear filters' }).click();
  await expect(rows).toHaveCount(1);

  await page.route('**/api/commercial-exceptions?*', (route) => route.abort('failed'));
  await page.getByRole('button', { name: 'SLA overdue only' }).click();
  await expect(page.getByText('Commercial exceptions could not be loaded. No inferred results are shown.')).toBeVisible();
  await page.unroute('**/api/commercial-exceptions?*');
  await page.getByRole('button', { name: 'Retry' }).click();
  await expect(rows).toHaveCount(1);

  await rows.first().getByRole('button', { name: 'Open source' }).click();
  await expect(page).toHaveURL(/\/sales\/follow-ups\?sourceId=\d+$/);
  await expect(page.getByRole('heading', { name: 'Follow-ups' })).toBeVisible();
  await expect(page.getByRole('row').filter({ hasText: 'CUSTOMER_RESPONSE' }).filter({ hasText: 'Browser Representative' })).toHaveCount(1);
});
