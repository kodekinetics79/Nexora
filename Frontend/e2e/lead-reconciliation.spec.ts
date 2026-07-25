import { expect, test } from '@playwright/test';
import { credentialsFor, fixture, missingFixtureValues } from './support/environment';

test.beforeEach(() => {
  expect(credentialsFor('manager')).not.toBeNull();
});

test('authorized bulk upload reaches governed reconciliation', async ({ page }) => {
  expect(fixture.uploadFile).toBeTruthy();
  await page.route('**/api/Extraction/upload', async (route) => {
    await route.continue({ headers: { ...route.request().headers(), 'idempotency-key': 'release-01c-governed-upload' } });
  });
  await page.goto('/procurement/leads/manual-upload');
  await page.locator('input[type=file]').setInputFiles(fixture.uploadFile!);
  await page.getByRole('button', { name: 'Queue for reconciliation' }).click();
  await expect(page).toHaveURL(new RegExp(`/procurement/leads/ingestion/${fixture.batchId}$`), { timeout: 30_000 });
  await expect(page.getByRole('heading', { name: 'Batch reconciliation' })).toBeVisible({ timeout: 60_000 });
  await expect(page.getByText(/New lead|Exact duplicate|Revision|Possible match review/).first()).toBeVisible({ timeout: 60_000 });
});

test('known batch exposes new, duplicate, revision and possible-match outcomes', async ({ page }) => {
  expect(fixture.batchId).toBeTruthy();
  expect(Object.values(fixture.batchCounts).every((value) => value !== undefined)).toBeTruthy();
  await page.goto(`/procurement/leads/ingestion/${fixture.batchId}`);
  await expect(page.getByRole('heading', { name: 'Batch reconciliation' })).toBeVisible();
  for (const [label, value] of Object.entries(fixture.batchCounts)) {
    const metric = page.getByText(label, { exact: true }).locator('..');
    await expect(metric).toContainText(value!);
  }
});

test('revision UI preserves immutable differences and the Nexora Serial', async ({ page }) => {
  const missing = missingFixtureValues(
    ['E2E_REVISION_LEAD_ID', fixture.revisionLeadId],
    ['E2E_NEXORA_SERIAL', fixture.nexoraSerial],
  );
  expect(missing, `Missing ${missing.join(', ')}.`).toEqual([]);
  await page.goto(`/procurement/leads/view/${fixture.revisionLeadId}`);
  await expect(page.getByText(`Nexora Serial: ${fixture.nexoraSerial}`)).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Revision history' })).toBeVisible();
  await expect(page.getByText(/Revision \d+/).first()).toBeVisible();
  await expect(page.getByText(/Added|Removed|Modified|Unchanged/).first()).toBeVisible();
});
