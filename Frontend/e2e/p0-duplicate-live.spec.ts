import path from 'node:path';
import { expect, test } from '@playwright/test';
import { loginThroughUi } from './support/login';

const email = process.env.E2E_MANAGER_EMAIL;
const password = process.env.E2E_MANAGER_PASSWORD;
const businessUnitId = process.env.E2E_MANAGER_BUSINESS_UNIT_ID;
const uploadFile = process.env.E2E_UPLOAD_FILE
  || path.resolve('e2e/fixtures/release-01c-inquiry.csv');

if (!email || !password || !businessUnitId) {
  throw new Error('P0 duplicate browser acceptance requires local manager credentials.');
}

test('exact duplicate is persisted and visible without repeated extraction', async ({ page }) => {
  const consoleErrors: string[] = [];
  page.on('console', (message) => {
    if (message.type() === 'error') consoleErrors.push(message.text());
  });
  await loginThroughUi(page, { email, password, businessUnitId });

  for (let receipt = 0; receipt < 2; receipt += 1) {
    await page.goto('/procurement/leads/intelligence');
    await page.locator('input[type="file"]').setInputFiles(uploadFile);
    const response = page.waitForResponse((candidate) =>
      candidate.request().method() === 'POST'
      && candidate.url().includes('/api/Extraction/upload'));
    await page.getByRole('button', { name: 'Queue for reconciliation' }).click();
    expect((await response).status()).toBe(202);
    await expect(page).toHaveURL(/\/procurement\/leads\/ingestion\/[0-9a-f-]+$/i);
  }

  const duplicatesResponse = page.waitForResponse((candidate) =>
    candidate.request().method() === 'GET'
    && candidate.url().endsWith('/api/LeadIngestion/duplicates'));
  await page.goto('/procurement/leads/duplicates');
  expect((await duplicatesResponse).status()).toBe(200);
  await expect(page.getByRole('heading', { name: 'Duplicate Uploads' })).toBeVisible();
  await expect(page.getByText('release-01c-inquiry.csv', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Exact Duplicate Confirmed', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Scan reused', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Reused', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Avoided cost unpriced', { exact: true }).first()).toBeVisible();
  expect(consoleErrors).toEqual([]);
});
