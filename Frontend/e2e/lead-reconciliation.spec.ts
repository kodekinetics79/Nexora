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

test('scanner outage remains recoverable and retries the stored occurrence', async ({ page }) => {
  const batchId = 'c4ee65d2-6e99-47b1-b894-f0e1723037a8';
  let retried = false;
  await page.route('**/api/Extraction/upload', async (route) => {
    await route.fulfill({ status: 202, contentType: 'application/json', body: JSON.stringify({
      batchId,
      jobs: [{ jobId: 0, occurrenceId: 901, fileName: 'customer-rfq.csv', outcome: 'AwaitingSecurityScan', errorCode: 'security_scanner_unavailable', reason: 'Malware scanner unavailable; the file remains quarantined.' }],
    }) });
  });
  await page.route(`**/api/LeadIngestion/batches/${batchId}/retry-blocked-files`, async (route) => {
    retried = true;
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      batchId, eligible: 1, queued: 1, stillAwaiting: 0, rejected: 0,
      items: [{ sourceDocumentOccurrenceId: 901, fileName: 'customer-rfq.csv', status: 'Queued', extractionJobId: 902 }],
    }) });
  });
  await page.route(`**/api/LeadIngestion/batches/${batchId}`, async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      batchId, filesReceived: 1, logicalInquiries: 0, newLeads: 0, exactDuplicates: 0,
      revisions: 0, possibleMatches: 0, rejected: 0, awaitingSecurityScan: retried ? 0 : 1,
      localFirstOccurrences: 0, externalOccurrences: 0, externalCost: 0,
      items: [{
        occurrenceId: 0, sourceDocumentOccurrenceId: 901, leadId: null, nexoraSerial: null,
        classification: 'Pending', revisionNumber: null,
        fileName: 'customer-rfq.csv', ingestedAtUtc: '2026-07-28T18:45:37Z',
        processingPath: retried ? 'IntakeQueued' : 'IntakeAwaitingSecurityScan', externalAiUsed: false, confidence: 0,
        reasons: ['Malware scanner unavailable; the file remains quarantined.'], matchCandidates: [],
        customerResolutionStatus: 'Awaiting customer resolution', assignedOpportunityOwner: null,
        intakeStatus: retried ? 'Queued' : 'AwaitingSecurityScan',
        errorCode: retried ? null : 'security_scanner_unavailable', securityStatus: retried ? 'Cleared' : 'Quarantined',
        securityScanUpdatedAtUtc: '2026-07-28T18:46:00Z', lastUpdatedAtUtc: '2026-07-28T18:46:00Z',
        extractionStatus: retried ? 'Pending' : null, extractionUpdatedAtUtc: retried ? '2026-07-28T18:47:00Z' : null,
      }],
    }) });
  });

  await page.goto('/procurement/leads/manual-upload');
  await page.locator('input[type=file]').setInputFiles(fixture.uploadFile!);
  await page.getByRole('button', { name: 'Queue for reconciliation' }).click();

  await expect(page).toHaveURL(new RegExp(`/procurement/leads/ingestion/${batchId}$`));
  await expect(page.getByText('Awaiting Security Scan', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Malware scanner unavailable; the file remains quarantined.')).toBeVisible();
  await expect(page.getByText(/Intake: Awaiting Security Scan/)).toBeVisible();
  await expect(page.getByText(/Security Quarantined updated/)).toBeVisible();

  await page.getByRole('button', { name: 'Retry Blocked Files' }).click();
  await expect(page.getByText('Retry complete: 1 queued, 0 still awaiting security scan, 0 rejected.')).toBeVisible();
  await expect(page.getByText(/Intake: Queued/)).toBeVisible();
  await expect(page.getByText(/Extraction Pending updated/)).toBeVisible();
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
