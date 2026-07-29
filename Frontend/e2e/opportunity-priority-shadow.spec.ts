import { expect, test, type Locator } from '@playwright/test';
import { api, jsonOk, loginAs } from './support/core-commercial';

type OpportunityPriority = {
  recommendationId: number;
  commercialCaseId: number;
  nexoraSerial: string;
  ownerUserId?: number | null;
  recommendedActionLabel: string;
  reasons: string[];
  policyVersion: string;
  mode: string;
  latestFeedback?: { decision: string; reason: string } | null;
};

type OpportunityPriorityPage = {
  items: OpportunityPriority[];
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

  const reconcileResponse = page.waitForResponse((response) =>
    response.url().endsWith('/api/opportunity-priorities/reconcile')
      && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Reconcile' }).click();
  expect((await reconcileResponse).status()).toBe(200);

  const priorities = await jsonOk<OpportunityPriorityPage>(
    await api(page, token, 'get', '/api/opportunity-priorities?pageSize=100'),
  );
  expect(priorities.mode).toBe('Shadow');
  expect(priorities.accessScope).toBe('tenant');
  expect(priorities.items.length, 'Reconciliation must persist at least one eligible shadow recommendation.').toBeGreaterThan(0);

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
  await openButton.click();

  await expect(page).toHaveURL(new RegExp(`/commercial-cases/${selected.commercialCaseId}$`));
  await expect(page.getByText(selected.nexoraSerial, { exact: true }).first()).toBeVisible();
  const prioritySection = page.getByRole('region', { name: 'Opportunity priority' });
  await expect(prioritySection).toContainText('Shadow');
  await expect(prioritySection).toContainText(selected.recommendedActionLabel);
  await expect(prioritySection).toContainText('Advisory guidance only.');

  await prioritySection.getByRole('button', { name: 'Record feedback' }).click();
  const feedbackDialog = page.getByRole('dialog', { name: 'Record recommendation feedback' });
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
  await expect(prioritySection).toContainText('Latest feedback: Deferred');

  const persisted = await jsonOk<OpportunityPriority>(
    await api(page, token, 'get', `/api/opportunity-priorities/commercial-cases/${selected.commercialCaseId}`),
  );
  expect(persisted.latestFeedback?.decision).toBe('Deferred');
  expect(persisted.latestFeedback?.reason).toBe(feedbackReason);

  const caseAfter = await jsonOk<CommercialCaseSnapshot>(
    await api(page, token, 'get', `/api/commercial-cases/${selected.commercialCaseId}`),
  );
  expect(caseAfter.currentStatus).toBe(caseBefore.currentStatus);
  expect(caseAfter.documents).toEqual(caseBefore.documents);
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
  await page.getByRole('option', { name: 'Agree with recommendation' }).click();
  const reason = `Assigned owner confirmed evidence ${Date.now()}.`;
  await feedbackDialog.getByLabel('Reason').fill(reason);
  await feedbackDialog.getByRole('button', { name: 'Record feedback' }).click();
  await expect(page.getByText('Recommendation feedback recorded. No commercial workflow state was changed.')).toBeVisible();
  await expect(prioritySection).toContainText('Latest feedback: Accepted');
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
