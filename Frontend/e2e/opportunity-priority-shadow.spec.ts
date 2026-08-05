import { expect, test, type Locator } from '@playwright/test';
import { api, jsonOk, loginAs } from './support/core-commercial';

type OpportunityPriority = {
  recommendationId: number;
  commercialCaseId: number;
  leadId: number;
  nexoraSerial: string;
  ownerUserId?: number | null;
  recommendedActionLabel: string;
  reasons: string[];
  policyVersion: string;
  mode: string;
  currentBlocker: string;
  expectedCommercialValueStatus: string;
  components: Array<{ code: string; label: string; status: string }>;
  availableActions: Array<{ code: string; label: string }>;
  latestFeedback?: { decision: string; reason: string; replacementActionCode?: string | null } | null;
};

type OpportunityPriorityPage = {
  items: OpportunityPriority[];
  total: number;
  pageNumber: number;
  pageSize: number;
  accessScope: string;
  mode: string;
};

type CommercialCaseSnapshot = {
  currentStatus?: string | null;
  documents: Array<{ documentType: string; documentId: number }>;
};

const visibleRecommendationContainer = (
  pageWidth: number,
  desktopRow: Locator,
  openButton: Locator,
): Locator => pageWidth >= 900
  ? desktopRow
  : openButton.locator('xpath=ancestor::*[contains(@class, "MuiPaper-root")][1]');

test('manager opens a persisted shadow priority from Sales Today and records advisory feedback', async ({ page }) => {
  const token = await loginAs(page, 'manager');

  await page.goto('/sales/today');
  await expect(page).toHaveURL(/\/sales\/today$/);
  await expect(page.getByRole('heading', { name: 'Sales today' })).toBeVisible();
  await expect(page.getByText('Opportunity priority', { exact: true }).first()).toBeVisible();
  await expect(page.getByText(/Server-ranked guidance in shadow mode/)).toBeVisible();
  await page.getByRole('button', { name: 'Reconcile' }).click();
  await expect(page.getByText(/Reconciliation completed for \d+ opportunities across all available batches\./)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Reconcile' })).toBeEnabled();

  const priorities = await jsonOk<OpportunityPriorityPage>(
    await api(page, token, 'get', '/api/opportunity-priorities?pageSize=100'),
  );
  expect(priorities.mode).toBe('Shadow');
  expect(priorities.accessScope).toBe('tenant');
  expect(priorities.items.length, 'Reconciliation must persist at least one eligible shadow recommendation.').toBeGreaterThan(0);
  const firstUiPage = await jsonOk<OpportunityPriorityPage>(
    await api(page, token, 'get', '/api/opportunity-priorities?pageNumber=1&pageSize=10'),
  );
  expect(firstUiPage.total, 'The representative queue must exercise normal UI pagination.').toBeGreaterThan(firstUiPage.pageSize);

  const pageTwoResponse = page.waitForResponse((response) =>
    response.url().includes('/api/opportunity-priorities')
      && response.url().includes('pageNumber=2')
      && response.request().method() === 'GET');
  await page.getByRole('button', { name: 'Go to page 2' }).click();
  expect((await pageTwoResponse).status()).toBe(200);
  await expect(page.getByRole('button', { name: /Open opportunity/ }).first()).toBeVisible();
  const pageOneResponse = page.waitForResponse((response) =>
    response.url().includes('/api/opportunity-priorities')
      && response.url().includes('pageNumber=1')
      && response.request().method() === 'GET');
  await page.getByRole('button', { name: 'Go to page 1' }).click();
  expect((await pageOneResponse).status()).toBe(200);

  const expectedSerial = process.env.E2E_OPPORTUNITY_NEXORA_SERIAL?.trim();
  const recommendation = expectedSerial
    ? priorities.items.find((item) => item.nexoraSerial === expectedSerial)
    : priorities.items[0];
  expect(
    recommendation,
    expectedSerial
      ? `No persisted opportunity recommendation matched E2E_OPPORTUNITY_NEXORA_SERIAL=${expectedSerial}.`
      : 'The persisted priority queue did not return a recommendation.',
  ).toBeTruthy();
  const selected = recommendation!;

  const openButton = page
    .getByRole('button', { name: `Open opportunity ${selected.nexoraSerial}` })
    .filter({ visible: true });
  await expect(openButton).toHaveCount(1);
  const duplicateIds = await page.locator('[id*="priority-evidence-"]').evaluateAll((elements) => {
    const ids = elements.map(element => element.id).filter(Boolean);
    return ids.filter((id, index) => ids.indexOf(id) !== index);
  });
  expect(duplicateIds).toEqual([]);
  const desktopRow = page.getByRole('row')
    .filter({ hasText: selected.nexoraSerial })
    .filter({ hasText: selected.recommendedActionLabel });
  const container = visibleRecommendationContainer(page.viewportSize()?.width ?? 1280, desktopRow, openButton);
  await expect(container).toContainText('Shadow');
  await expect(container).toContainText(selected.recommendedActionLabel);
  await expect(container).toContainText(`Policy ${selected.policyVersion}`);

  await container.getByRole('button', { name: 'Show rationale' }).click();
  if (selected.reasons.length > 0) {
    await expect(container).toContainText(selected.reasons[0]);
  } else {
    await expect(container).toContainText('No rationale was supplied.');
  }

  const caseBefore = await jsonOk<CommercialCaseSnapshot>(
    await api(page, token, 'get', `/api/commercial-cases/${selected.commercialCaseId}`),
  );
  expect(caseBefore.documents.some(document => document.documentType === 'RFQ')).toBe(true);
  expect(caseBefore.documents.some(document => document.documentType === 'Quote')).toBe(true);
  const leadBefore = await jsonOk<unknown>(await api(page, token, 'get', `/api/Lead/${selected.leadId}`));
  const inventoryBefore = await jsonOk<unknown[]>(
    await api(page, token, 'get', `/api/inventory-intelligence/leads/${selected.leadId}/resolutions`),
  );
  const rfqsBefore = await jsonOk<unknown>(
    await api(page, token, 'get', '/api/Rfq?pageNumber=1&pageSize=1000'),
  );
  const quotesAndPricingBefore = await jsonOk<unknown>(
    await api(page, token, 'get', '/api/Quote?pageNumber=1&pageSize=1000'),
  );
  const ordersBefore = await jsonOk<unknown>(await api(page, token, 'get', '/api/Order'));
  const ownershipBefore = await jsonOk<unknown>(
    await api(page, token, 'get', '/api/commercial-intelligence/account-ownership'),
  );
  await openButton.click();

  await expect(page).toHaveURL(new RegExp(`/commercial-cases/${selected.commercialCaseId}$`));
  await expect(page.getByText(selected.nexoraSerial, { exact: true }).first()).toBeVisible();
  const prioritySection = page.getByRole('region', { name: 'Opportunity priority' });
  await expect(prioritySection).toContainText('Shadow');
  await expect(prioritySection).toContainText(selected.recommendedActionLabel);
  await expect(prioritySection).toContainText('Advisory guidance only.');
  await expect(prioritySection).toContainText('Expected Commercial Value components');
  await expect(prioritySection).toContainText('Current blocker:');
  await expect(prioritySection).toContainText('Response deadline:');
  expect(selected.components).toHaveLength(7);
  for (const label of [
    'Opportunity value',
    'Evidenced win likelihood',
    'Expected margin',
    'Urgency',
    'Customer quality',
    'Fulfilment confidence',
    'Estimated sourcing effort',
  ]) {
    await expect(prioritySection).toContainText(label);
  }
  await expect(prioritySection).toContainText(
    selected.expectedCommercialValueStatus === 'insufficient_evidence'
      ? selected.currentBlocker
      : 'in shadow mode',
  );

  const feedbackTrigger = prioritySection.getByRole('button', { name: 'Record feedback' });
  await feedbackTrigger.click();
  const feedbackDialog = page.getByRole('dialog', { name: 'Record recommendation feedback' });
  await expect(feedbackDialog).toBeVisible();
  await expect(feedbackDialog.getByLabel('Reason')).toBeFocused();
  await expect(feedbackDialog).toContainText('does not execute the recommendation or change');
  await feedbackDialog.getByLabel('Decision').click();
  await page.getByRole('option', { name: 'Defer assessment' }).click();
  const feedbackReason = `Gate 2 browser acceptance deferred assessment ${Date.now()}.`;
  await feedbackDialog.getByLabel('Reason').fill(feedbackReason);

  const feedbackResponse = page.waitForResponse((response) =>
    response.url().endsWith(`/api/opportunity-priorities/${selected.recommendationId}/feedback`)
      && response.request().method() === 'POST');
  await feedbackDialog.getByRole('button', { name: 'Record feedback' }).click();
  expect((await feedbackResponse).status()).toBe(200);
  await expect(page.getByText('Recommendation feedback recorded. No commercial workflow state was changed.')).toBeVisible();
  await expect(feedbackTrigger).toBeFocused();
  await expect(prioritySection).toContainText('Latest feedback: Deferred');

  const persisted = await jsonOk<OpportunityPriority>(
    await api(page, token, 'get', `/api/opportunity-priorities/commercial-cases/${selected.commercialCaseId}`),
  );
  expect(persisted.latestFeedback?.decision).toBe('Deferred');
  expect(persisted.latestFeedback?.reason).toBe(feedbackReason);

  const caseAfter = await jsonOk<CommercialCaseSnapshot>(
    await api(page, token, 'get', `/api/commercial-cases/${selected.commercialCaseId}`),
  );
  const leadAfter = await jsonOk<unknown>(await api(page, token, 'get', `/api/Lead/${selected.leadId}`));
  const inventoryAfter = await jsonOk<unknown[]>(
    await api(page, token, 'get', `/api/inventory-intelligence/leads/${selected.leadId}/resolutions`),
  );
  const rfqsAfter = await jsonOk<unknown>(
    await api(page, token, 'get', '/api/Rfq?pageNumber=1&pageSize=1000'),
  );
  const quotesAndPricingAfter = await jsonOk<unknown>(
    await api(page, token, 'get', '/api/Quote?pageNumber=1&pageSize=1000'),
  );
  const ordersAfter = await jsonOk<unknown>(await api(page, token, 'get', '/api/Order'));
  const ownershipAfter = await jsonOk<unknown>(
    await api(page, token, 'get', '/api/commercial-intelligence/account-ownership'),
  );
  expect(caseAfter).toEqual(caseBefore);
  expect(leadAfter).toEqual(leadBefore);
  expect(inventoryAfter).toEqual(inventoryBefore);
  expect(rfqsAfter).toEqual(rfqsBefore);
  expect(quotesAndPricingAfter).toEqual(quotesAndPricingBefore);
  expect(ordersAfter).toEqual(ordersBefore);
  expect(ownershipAfter).toEqual(ownershipBefore);
});

test('assigned sales owner sees only owned priorities, cannot reconcile, and can record feedback', async ({ page }) => {
  const token = await loginAs(page, 'editor');

  await page.goto('/sales/today');
  await expect(page).toHaveURL(/\/sales\/today$/);
  await expect(page.getByRole('heading', { name: 'Sales today' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Reconcile' })).toHaveCount(0);

  const priorities = await jsonOk<OpportunityPriorityPage>(
    await api(page, token, 'get', '/api/opportunity-priorities?pageSize=100'),
  );
  expect(priorities.accessScope).toBe('assigned_to_me');
  expect(priorities.items.length, 'The assigned owner fixture must have a persisted recommendation.').toBeGreaterThan(0);

  const reconcileIdentity = crypto.randomUUID();
  const forbiddenReconcile = await api(
    page,
    token,
    'post',
    '/api/opportunity-priorities/reconcile',
    { idempotencyKey: reconcileIdentity, correlationId: reconcileIdentity, batchSize: 1 },
    { 'Idempotency-Key': reconcileIdentity, 'X-Correlation-ID': reconcileIdentity },
  );
  expect(forbiddenReconcile.status()).toBe(403);

  const selected = priorities.items[0];
  await page.goto(`/commercial-cases/${selected.commercialCaseId}`);
  await expect(page.getByText(selected.nexoraSerial, { exact: true }).first()).toBeVisible();
  const prioritySection = page.getByRole('region', { name: 'Opportunity priority' });
  await expect(prioritySection).toContainText(selected.recommendedActionLabel);
  await prioritySection.getByRole('button', { name: 'Record feedback' }).click();
  const feedbackDialog = page.getByRole('dialog', { name: 'Record recommendation feedback' });
  await feedbackDialog.getByLabel('Decision').click();
  await page.getByRole('option', { name: 'Suggest another action' }).click();
  expect(selected.availableActions.length).toBeGreaterThan(0);
  const replacement = selected.availableActions[0];
  await feedbackDialog.getByLabel('Suggested action').click();
  await page.getByRole('option', { name: replacement.label }).click();
  const reason = `Assigned owner confirmed evidence ${Date.now()}.`;
  await feedbackDialog.getByLabel('Reason').fill(reason);
  await feedbackDialog.getByRole('button', { name: 'Record feedback' }).click();
  await expect(page.getByText('Recommendation feedback recorded. No commercial workflow state was changed.')).toBeVisible();
  await expect(prioritySection).toContainText('Latest feedback: Replaced');
  const persisted = await jsonOk<OpportunityPriority>(
    await api(page, token, 'get', `/api/opportunity-priorities/commercial-cases/${selected.commercialCaseId}`),
  );
  expect(persisted.latestFeedback?.decision).toBe('Replaced');
  expect(persisted.latestFeedback?.replacementActionCode).toBe(replacement.code);
});

test('user without Lead access cannot read or mutate opportunity priorities', async ({ page }) => {
  const token = await loginAs(page, 'denied');
  const query = await api(page, token, 'get', '/api/opportunity-priorities?pageSize=100');
  expect(query.status()).toBe(403);

  const identity = crypto.randomUUID();
  const reconcile = await api(
    page,
    token,
    'post',
    '/api/opportunity-priorities/reconcile',
    { idempotencyKey: identity, correlationId: identity, batchSize: 1 },
    { 'Idempotency-Key': identity, 'X-Correlation-ID': identity },
  );
  expect(reconcile.status()).toBe(403);
});
