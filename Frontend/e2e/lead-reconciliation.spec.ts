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
  await expect(page.getByRole('button', { name: /\d+ New leads/ })).toBeVisible({ timeout: 60_000 });
});

test('quarantined upload remains visible with its inspection reason', async ({ page }) => {
  const batchId = 'c4ee65d2-6e99-47b1-b894-f0e1723037a8';
  await page.route('**/api/Extraction/upload', async (route) => {
    await route.fulfill({ status: 202, contentType: 'application/json', body: JSON.stringify({
      batchId,
      jobs: [{ jobId: 0, occurrenceId: 901, fileName: 'customer-rfq.csv', outcome: 'Quarantined', errorCode: 'document_quarantined', reason: 'Malware scanner unavailable; the file remains quarantined.' }],
    }) });
  });
  await page.route(`**/api/LeadIngestion/batches/${batchId}`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      batchId, filesReceived: 1, logicalInquiries: 0, newLeads: 0, exactDuplicates: 0,
      revisions: 0, possibleMatches: 0, rejected: 1, externalOccurrences: 0, externalCost: 0,
      items: [{
        occurrenceId: 0, sourceDocumentOccurrenceId: 901, leadId: null, nexoraSerial: null,
        classification: 'RejectedOrUnprocessable', revisionNumber: null,
        fileName: 'customer-rfq.csv', ingestedAtUtc: '2026-07-28T18:45:37Z',
        processingPath: 'IntakeRejected', externalAiUsed: false, confidence: 1,
        reasons: ['Malware scanner unavailable; the file remains quarantined.'], matchCandidates: [],
        customerResolutionStatus: 'Awaiting customer resolution', assignedOpportunityOwner: null,
        intakeStatus: 'Rejected', errorCode: 'document_quarantined',
      }],
    }) });
  });

  await page.goto('/procurement/leads/manual-upload');
  await page.locator('input[type=file]').setInputFiles(fixture.uploadFile!);
  await page.getByRole('button', { name: 'Queue for reconciliation' }).click();

  await expect(page).toHaveURL(new RegExp(`/procurement/leads/ingestion/${batchId}$`));
  await expect(page.getByText('Rejected or unsupported', { exact: true })).toBeVisible();
  await expect(page.getByText('Malware scanner unavailable; the file remains quarantined.')).toBeVisible();
  await expect(page.getByText(/Intake: Rejected/)).toBeVisible();
});

test('known batch exposes new, duplicate, revision and possible-match outcomes', async ({ page }) => {
  expect(fixture.batchId).toBeTruthy();
  expect(Object.values(fixture.batchCounts).every((value) => value !== undefined)).toBeTruthy();
  await page.goto(`/procurement/leads/ingestion/${fixture.batchId}`);
  await expect(page.getByRole('heading', { name: 'Batch reconciliation' })).toBeVisible();
  for (const [label, value] of Object.entries(fixture.batchCounts)) {
    await expect(page.getByRole('button', { name: `${value} ${label}` })).toBeVisible();
  }
  await expect(page.getByRole('button', { name: 'Prepare RFQ' }).first()).toBeVisible();
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
