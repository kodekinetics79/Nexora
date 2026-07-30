import { expect, test, type Page } from '@playwright/test';
import { fixture } from './support/environment';

async function setLeadPermissions(page: Page, canCreate: boolean, canEdit: boolean) {
  await page.addInitScript(({ create, edit }) => {
    const current = JSON.parse(localStorage.getItem('userData') ?? '{}');
    const permissions = Array.isArray(current.permissions) ? current.permissions : [];
    const next = permissions.map((permission: { moduleName?: string }) =>
      permission.moduleName === 'Leads'
        ? { ...permission, canCreate: create, canEdit: edit }
        : permission);
    localStorage.setItem('userData', JSON.stringify({ ...current, permissions: next }));
  }, { create: canCreate, edit: canEdit });
}

test('view-only ingestion hides commands and labels pending confidence honestly', async ({ page }) => {
  await setLeadPermissions(page, false, false);
  const batchId = 'a773ea48-0f43-4e76-a4cf-f5973e8a4248';
  await page.route(`**/api/LeadIngestion/batches/${batchId}`, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      batchId,
      filesReceived: 1,
      logicalInquiries: 0,
      newLeads: 0,
      exactDuplicates: 0,
      revisions: 0,
      possibleMatches: 1,
      rejected: 0,
      awaitingSecurityScan: 1,
      localFirstOccurrences: 0,
      externalOccurrences: 0,
      externalCost: 0,
      items: [{
        occurrenceId: 41,
        sourceDocumentOccurrenceId: 41,
        leadId: null,
        nexoraSerial: null,
        classification: 'Pending',
        revisionNumber: null,
        fileName: 'pending-rfq.pdf',
        ingestedAtUtc: '2026-07-30T12:00:00Z',
        processingPath: 'IntakeAwaitingSecurityScan',
        externalAiUsed: false,
        confidence: 0,
        reasons: ['Awaiting malware scanner availability.'],
        matchCandidates: [{
          candidateId: 7,
          candidateLeadId: Number(fixture.leadId),
          nexoraSerial: fixture.nexoraSerial,
          customerRfqReference: 'RFQ-RELEASE-01C',
          confidence: 0.72,
          matchEvidenceJson: '{}',
          differencesJson: '{}',
          downstreamImpactJson: '{}',
          reviewState: 'Pending',
          version: 1,
        }],
        customerResolutionStatus: 'Awaiting customer resolution',
        assignedOpportunityOwner: null,
        intakeStatus: 'AwaitingSecurityScan',
        errorCode: 'security_scanner_unavailable',
        securityStatus: 'Quarantined',
        securityScanUpdatedAtUtc: '2026-07-30T12:00:00Z',
        lastUpdatedAtUtc: '2026-07-30T12:00:00Z',
        extractionStatus: null,
        extractionUpdatedAtUtc: null,
      }],
    }),
  }));

  await page.goto(`/procurement/leads/ingestion/${batchId}`);
  await expect(page.getByText('Not yet scored')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Retry Blocked Files' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Treat as revision' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Create new lead' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'New upload' })).toHaveCount(0);
});

test('edit user can save but cannot approve extraction without evidence', async ({ page }) => {
  await setLeadPermissions(page, true, true);
  await page.route(`**/api/processing-evidence/leads/${fixture.leadId}`, route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      leadId: Number(fixture.leadId), nexoraSerial: fixture.nexoraSerial,
      rfqs: [], occurrences: [], jobs: [], runs: [], aiRequests: [],
      localRequestCount: 0, externalRequestCount: 0,
      localRequestRate: 0, externalRequestRate: 0,
      externalCostAmount: null, externalCostCurrency: null,
      externalCostStatus: 'LocalComputeUnpriced',
    }),
  }));
  await page.goto(`/procurement/extraction/review/${fixture.leadId}`);

  await expect(page.getByRole('button', { name: 'Save corrections' })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Approve' })).toBeDisabled();
  await expect(page.getByText(/Approval is blocked: No source attachment is available/)).toBeVisible();
});

test('view-only extraction is read-only and exposes no mutation controls', async ({ page }) => {
  await setLeadPermissions(page, false, false);
  await page.goto(`/procurement/extraction/review/${fixture.leadId}`);

  await expect(page.getByRole('button', { name: 'Save corrections' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Approve' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Add row' })).toHaveCount(0);
  await expect(page.getByLabel('RFQ #')).toBeDisabled();
  await expect(page.getByText(/read-only for your role/i)).toBeVisible();
});

test('upload selection and filename-specific removal are disabled in flight', async ({ page }) => {
  await setLeadPermissions(page, true, true);
  await page.route('**/api/Extraction/upload', async route => {
    await new Promise(resolve => setTimeout(resolve, 1_000));
    await route.fulfill({
      status: 202,
      contentType: 'application/json',
      body: JSON.stringify({ batchId: fixture.batchId, jobs: [] }),
    });
  });

  await page.goto('/procurement/leads/manual-upload');
  await page.locator('input[type=file]').setInputFiles(fixture.uploadFile!);
  const remove = page.getByRole('button', { name: `Remove ${fixture.uploadFile!.split('/').pop()}` });
  await expect(remove).toBeEnabled();
  await page.getByRole('button', { name: 'Queue for reconciliation' }).click();
  await expect(remove).toBeDisabled();
  await expect(page.locator('input[type=file]')).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Select RFQ documents' })).toHaveAttribute('aria-disabled', 'true');
});

test('dead-letter recovery requires Users edit and Leads create together', async ({ page }) => {
  await page.addInitScript(() => {
    const current = JSON.parse(localStorage.getItem('userData') ?? '{}');
    const leadsCreateOverride = sessionStorage.getItem('testLeadsCreate');
    const usersEditOverride = sessionStorage.getItem('testUsersEdit');
    const leadsCreate = leadsCreateOverride === null || leadsCreateOverride === 'true';
    const usersEdit = usersEditOverride === 'true';
    const permissions = (Array.isArray(current.permissions) ? current.permissions : [])
      .map((permission: { moduleName?: string }) => permission.moduleName === 'Leads'
        ? { ...permission, canCreate: leadsCreate }
        : permission)
      .filter((permission: { moduleName?: string }) => permission.moduleName !== 'Users');
    permissions.push({ id: 9901, roleId: current.roleId ?? 1, moduleId: 9901, moduleName: 'Users', canCreate: false, canEdit: usersEdit, canDelete: false });
    localStorage.setItem('userData', JSON.stringify({ ...current, permissions }));
  });
  await page.route('**/api/User?*', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [], totalCount: 0, pageNumber: 1, pageSize: 500 }) }));
  await page.route('**/api/operations/readiness', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      checkedAt: '2026-07-30T12:00:00Z', deploymentReadiness: 'Degraded', blockingReasons: [],
      healthChecks: [], queues: [],
      aiLast30Days: { total: 0, local: 0, external: 0, unresolved: 0, externalSharePercent: 0 },
    }),
  }));
  await page.route('**/api/operations/readiness/extraction-dead-letters', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([{
      jobId: 81, batchId: fixture.batchId, sourceDocumentOccurrenceId: 91,
      fileName: 'blocked-rfq.pdf', sourceType: 'ManualUpload', attempts: 5, maxAttempts: 5,
      failureCategory: 'scanner_unavailable', createdOn: '2026-07-30T11:00:00Z',
      updatedOn: '2026-07-30T12:00:00Z', resolution: 'Unresolved', blocksReadiness: true,
    }]),
  }));

  await page.goto('/admin/operations');
  await expect(page.getByText('blocked-rfq.pdf')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Verify and retry' })).toHaveCount(0);

  await page.evaluate(() => {
    sessionStorage.setItem('testUsersEdit', 'true');
  });
  await page.reload();
  await expect(page.getByRole('button', { name: 'Verify and retry' })).toBeVisible();

  await page.evaluate(() => {
    sessionStorage.setItem('testLeadsCreate', 'false');
  });
  await page.reload();
  await expect(page.getByRole('button', { name: 'Verify and retry' })).toHaveCount(0);
});
