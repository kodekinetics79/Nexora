import fs from 'node:fs/promises';
import path from 'node:path';
import { expect, test, type Browser, type Page } from '@playwright/test';
import { loginThroughUi } from './support/login';

const apiURL = process.env.E2E_API_URL || 'http://127.0.0.1:5192';
const password = process.env.E2E_PASSWORD || 'Nexora#Release01C1Local';
const evidenceDir = path.resolve('../docs/nexora/evidence/release-01c1');
const files = ['01-new.csv', '02-duplicate.csv', '03-revision.csv', '04-possible-match.csv']
  .map((name) => path.resolve('e2e/fixtures/release-01c1', name));

type BatchItem = {
  occurrenceId: number;
  leadId?: number;
  nexoraSerial?: string;
  classification: string;
  revisionNumber?: number;
  reasons: string[];
  matchCandidates: Array<{
    candidateLeadId: number;
    nexoraSerial: string;
    customerRfqReference?: string;
    confidence: number;
    version: number;
  }>;
};

type Batch = {
  batchId: string;
  filesReceived: number;
  logicalInquiries: number;
  newLeads: number;
  exactDuplicates: number;
  revisions: number;
  possibleMatches: number;
  rejected: number;
  items: BatchItem[];
};

const credentials = (role: 'manager' | 'denied' | 'other') => ({
  email: `${role}@release01c1.local`,
  password,
  businessUnitId: role === 'other' ? '80102' : '80101',
});

async function tokenFor(page: Page): Promise<string> {
  const token = await page.evaluate(() => localStorage.getItem('token'));
  if (!token) throw new Error('Authenticated browser session did not contain an access token.');
  return token;
}

async function authenticatedPage(browser: Browser, role: 'denied' | 'other'): Promise<Page> {
  const context = await browser.newContext({ baseURL: process.env.E2E_BASE_URL || 'http://127.0.0.1:4173' });
  const page = await context.newPage();
  await loginThroughUi(page, credentials(role));
  return page;
}

async function metric(page: Page, label: string, value: number) {
  const button = page.getByRole('button', { name: new RegExp(`${value}\\s+${label}`, 'i') });
  await expect(button).toBeVisible();
  return button;
}

test('authenticated four-inquiry bulk reconciliation is visible, governed, and tenant-safe', async ({ page, browser, request }) => {
  await fs.mkdir(evidenceDir, { recursive: true });
  const from = encodeURIComponent(new Date(Date.now() - 60 * 60 * 1000).toISOString());
  const to = encodeURIComponent(new Date(Date.now() + 60 * 60 * 1000).toISOString());
  let baselineIngestionVolume = 0;
  let baselineLeadsReceived = 0;

  await test.step('manager uploads the controlled four-file batch', async () => {
    await loginThroughUi(page, credentials('manager'));
    const token = await tokenFor(page);
    const baselineResponse = await request.get(`${apiURL}/api/LeadIngestion/analytics?from=${from}&to=${to}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
    expect(baselineResponse.ok()).toBeTruthy();
    const baseline = await baselineResponse.json() as { metrics: Array<{ key: string; numerator: number }> };
    baselineIngestionVolume = baseline.metrics.find((entry) => entry.key === 'ingestion-volume')?.numerator ?? 0;
    baselineLeadsReceived = baseline.metrics.find((entry) => entry.key === 'leads-received')?.numerator ?? 0;
    await page.goto('/procurement/leads/manual-upload');
    await page.locator('input[type="file"]').setInputFiles(files);
    await expect(page.getByText('01-new.csv')).toBeVisible();
    await expect(page.getByText('04-possible-match.csv')).toBeVisible();
    await page.getByRole('button', { name: 'Queue for reconciliation' }).click();
    await expect(page).toHaveURL(/\/procurement\/leads\/ingestion\/[0-9a-f-]+$/i);
    await expect(page.getByRole('heading', { name: 'Batch reconciliation' })).toBeVisible();
  });

  const batchId = page.url().split('/').at(-1)!;
  const managerToken = await tokenFor(page);

  await test.step('batch summary reaches the exact 1 new, 1 duplicate, 1 revision, 1 possible-match split', async () => {
    await expect.poll(async () => {
      const response = await request.get(`${apiURL}/api/LeadIngestion/batches/${batchId}`, {
        headers: { Authorization: `Bearer ${managerToken}` },
      });
      if (!response.ok()) return `${response.status()}`;
      const batch = await response.json() as Batch;
      return [batch.filesReceived, batch.logicalInquiries, batch.newLeads, batch.exactDuplicates,
        batch.revisions, batch.possibleMatches, batch.rejected].join('/');
    }, { timeout: 90_000 }).toBe('4/4/1/1/1/1/0');

    await page.getByRole('button', { name: 'Refresh' }).click();
    await metric(page, 'Files received', 4);
    await metric(page, 'Logical inquiries', 4);
    await metric(page, 'New leads', 1);
    await metric(page, 'Exact duplicates', 1);
    await metric(page, 'Revisions', 1);
    await metric(page, 'Possible matches', 1);
    await metric(page, 'Rejected', 0);
    await expect(page.getByText(/Processing complete: 4 inquiries classified/)).toBeVisible();
    await page.screenshot({ path: path.join(evidenceDir, 'batch-summary.png'), fullPage: true });
  });

  const batchResponse = await request.get(`${apiURL}/api/LeadIngestion/batches/${batchId}`, {
    headers: { Authorization: `Bearer ${managerToken}` },
  });
  expect(batchResponse.ok()).toBeTruthy();
  const batch = await batchResponse.json() as Batch;
  const newItem = batch.items.find((item) => item.classification === 'New')!;
  const duplicate = batch.items.find((item) => item.classification === 'ExactDuplicate')!;
  const revision = batch.items.find((item) => item.classification === 'Revision')!;
  const possible = batch.items.find((item) => item.classification === 'PossibleMatchReviewRequired')!;

  await test.step('canonical identity, revision, and analytics reconcile without duplicate inflation', async () => {
    expect(newItem.leadId).toBeGreaterThan(1);
    expect(duplicate.leadId).toBe(1);
    expect(revision.leadId).toBe(1);
    expect(duplicate.nexoraSerial).toBe('NXR-2026-000001');
    expect(revision.nexoraSerial).toBe(duplicate.nexoraSerial);
    expect(revision.revisionNumber).toBe(2);
    expect(duplicate.reasons.join(' ')).toMatch(/unchanged|exact|source identity|content/i);
    expect(possible.leadId ?? 0).toBe(0);
    expect(possible.matchCandidates).toHaveLength(1);
    expect(possible.matchCandidates[0].candidateLeadId).toBe(1);

    const analyticsResponse = await request.get(`${apiURL}/api/LeadIngestion/analytics?from=${from}&to=${to}`, {
      headers: { Authorization: `Bearer ${managerToken}` },
    });
    expect(analyticsResponse.ok()).toBeTruthy();
    const analytics = await analyticsResponse.json() as { metrics: Array<{ key: string; numerator: number }> };
    expect(analytics.metrics.find((entry) => entry.key === 'ingestion-volume')?.numerator)
      .toBe(baselineIngestionVolume + 4);
    expect(analytics.metrics.find((entry) => entry.key === 'leads-received')?.numerator)
      .toBe(baselineLeadsReceived + 1);
  });

  await test.step('duplicate detail links to the original canonical Lead', async () => {
    await (await metric(page, 'Exact duplicates', 1)).click();
    const row = page.locator('article').filter({ hasText: 'Exact duplicate' });
    await expect(row.getByText('NXR-2026-000001')).toBeVisible();
    await expect(row.getByRole('button', { name: 'Open lead' })).toBeVisible();
    await page.screenshot({ path: path.join(evidenceDir, 'duplicate-detail.png'), fullPage: true });
  });

  await test.step('revision timeline visibly preserves Revision 1 and Revision 2', async () => {
    await (await metric(page, 'Revisions', 1)).click();
    const row = page.locator('article').filter({ hasText: 'Revision' });
    await row.getByRole('button', { name: 'Open lead' }).click();
    await expect(page.getByRole('heading', { name: 'Revision history' })).toBeVisible();
    await expect(page.getByText('Revision 2', { exact: true })).toBeVisible();
    await expect(page.getByText('Revision 1', { exact: true })).toBeVisible();
    await expect(page.getByText(/Added|Modified|Unchanged/).first()).toBeVisible();
    await page.screenshot({ path: path.join(evidenceDir, 'revision-comparison.png'), fullPage: true });
    await page.goto(`/procurement/leads/ingestion/${batchId}`);
  });

  await test.step('possible match exposes evidence and all governed actions', async () => {
    await (await metric(page, 'Possible matches', 1)).click();
    await expect(page.getByText('Candidate NXR-2026-000001')).toBeVisible();
    await expect(page.getByText('Match reasons and line-item overlap')).toBeVisible();
    await expect(page.getByText('Material differences')).toBeVisible();
    await expect(page.getByText('Downstream commercial impact')).toBeVisible();
    for (const label of ['Treat as revision', 'Create new lead', 'Reject', 'Return for review'])
      await expect(page.getByRole('button', { name: label, exact: true })).toBeVisible();
    await page.screenshot({ path: path.join(evidenceDir, 'possible-match-decision.png'), fullPage: true });
  });

  await test.step('denied role cannot decide and another tenant cannot read the batch', async () => {
    const deniedPage = await authenticatedPage(browser, 'denied');
    const deniedResponse = await request.post(`${apiURL}/api/LeadIngestion/match-reviews/${possible.occurrenceId}/decision`, {
      headers: { Authorization: `Bearer ${await tokenFor(deniedPage)}` },
      data: {
        action: 'defer',
        candidateLeadId: possible.matchCandidates[0].candidateLeadId,
        expectedVersion: possible.matchCandidates[0].version,
        reason: 'Authorization acceptance probe',
        idempotencyKey: 'release-01c1-denied-probe',
      },
    });
    expect(deniedResponse.status()).toBe(403);
    await deniedPage.context().close();

    const otherPage = await authenticatedPage(browser, 'other');
    const otherResponse = await request.get(`${apiURL}/api/LeadIngestion/batches/${batchId}`, {
      headers: { Authorization: `Bearer ${await tokenFor(otherPage)}` },
    });
    expect(otherResponse.status()).toBe(404);
    await otherPage.context().close();
  });

  await test.step('manager records the governed Return for Review action', async () => {
    await page.getByRole('button', { name: 'Return for review' }).click();
    await page.getByLabel('Decision reason').fill('Commercial identity remains uncertain; retain for review.');
    await page.getByRole('button', { name: 'Record decision' }).click();
    await expect(page.getByRole('button', { name: 'Return for review' })).toHaveCount(0);
    await metric(page, 'Possible matches', 1);
  });

  await test.step('mobile viewport remains usable', async () => {
    await page.setViewportSize({ width: 375, height: 812 });
    await page.goto(`/procurement/leads/ingestion/${batchId}`);
    await expect(page.getByRole('heading', { name: 'Batch reconciliation' })).toBeVisible();
    await expect(page.getByRole('button', { name: /4\s+Files received/i })).toBeVisible();
    await page.screenshot({ path: path.join(evidenceDir, 'mobile.png'), fullPage: true });
  });
});
