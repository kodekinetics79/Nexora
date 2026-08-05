import { expect, test, type Page } from '@playwright/test';
import { loginThroughUi } from './support/login';

const recoverableFile = process.env.E2E_RECOVERABLE_FILE ?? 'pilot-recoverable-source.csv';
const unavailableFile = process.env.E2E_UNAVAILABLE_FILE ?? 'pilot-unavailable-source-v2.csv';

async function openOperations(page: Page) {
  const email = process.env.E2E_MANAGER_EMAIL;
  const password = process.env.E2E_MANAGER_PASSWORD;
  if (!email || !password) throw new Error('Pilot browser credentials are required.');

  await loginThroughUi(page, {
    email,
    password,
    businessUnitId: process.env.E2E_MANAGER_BUSINESS_UNIT_ID,
  });
  await page.goto('/admin/operations');
  await expect(page.getByRole('heading', { name: 'Production readiness' })).toBeVisible();
}

async function recoverRow(page: Page, fileName: string) {
  const row = page.getByRole('row').filter({ hasText: fileName });
  await expect(row).toHaveCount(1);
  await row.getByRole('button', { name: 'Verify and retry' }).click();
  await expect(page.getByRole('dialog', { name: 'Verify source and retry extraction' })).toBeVisible();
  await page.getByLabel('Recovery reason').fill('Pilot acceptance: verify immutable source availability.');
  const response = page.waitForResponse(request =>
    request.url().includes('/api/operations/readiness/extraction-dead-letters/')
      && request.url().endsWith('/recover')
      && request.request().method() === 'POST');
  await page.getByRole('button', { name: 'Verify and retry', exact: true }).last().click();
  expect((await response).status()).toBe(200);
  return row;
}

test('tenant operator verifies a stored source and queues extraction without re-upload', async ({ page }) => {
  await openOperations(page);
  const row = await recoverRow(page, recoverableFile);

  await expect(page.getByText('Dead-letter verification completed: RetryQueued.')).toBeVisible();
  await expect(row).toHaveCount(0);
});

test('tenant operator records an unavailable source without leaving a false retry blocker', async ({ page }) => {
  await openOperations(page);
  const row = page.getByRole('row').filter({ hasText: unavailableFile });
  await expect(row).toContainText('EVIDENCE INTEGRITY');
  await expect(row).toContainText('Open');

  await recoverRow(page, unavailableFile);

  await expect(page.getByText('Dead-letter verification completed: SourceObjectUnavailable.')).toBeVisible();
  await expect(row).toHaveCount(0);
  await expect(page.getByText(/Runtime readiness is Healthy/)).toBeVisible();
});
