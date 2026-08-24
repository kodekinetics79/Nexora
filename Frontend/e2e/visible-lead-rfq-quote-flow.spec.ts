import fs from 'node:fs/promises';
import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { loginThroughUi } from './support/login';

const apiUrl = 'http://127.0.0.1:5192';
const evidenceDir = path.resolve('../docs/nexora/evidence/visible-lead-rfq-quote-flow');
const password = 'Nexora#Release01C1Local';
const manager = { email: 'manager@release01c1.local', password, businessUnitId: '80101' };
const denied = { email: 'denied@release01c1.local', password, businessUnitId: '80101' };
const other = { email: 'other@release01c1.local', password, businessUnitId: '80102' };
const initialUpload = path.resolve('e2e/fixtures/release-visible-new-inquiry.csv');
const postQuoteRevision = path.resolve('e2e/fixtures/release-visible-post-quote-revision.csv');
const postQuoteRevisionTwo = path.resolve('e2e/fixtures/release-visible-post-quote-revision-2.csv');
const possibleMatch = path.resolve('e2e/fixtures/release-visible-possible-match.csv');
let batchId = '';
let rfqId = 0;
let quoteId = 0;
let nexoraSerial = '';

async function login(page: Page, credentials = manager) {
  await loginThroughUi(page, credentials);
}

async function token(page: Page) {
  const value = await page.evaluate(() => localStorage.getItem('token'));
  if (!value) throw new Error('Missing authenticated token');
  return value;
}

async function waitForBatch(page: Page) {
  await expect(page).toHaveURL(/\/procurement\/leads\/ingestion\/[0-9a-f-]+$/i);
  batchId = page.url().split('/').at(-1)!;
  await expect(page.getByRole('heading', { name: 'Batch reconciliation' })).toBeVisible();
  await expect(page.getByText(/Processing complete/)).toBeVisible({ timeout: 90_000 });
}

test.describe.serial('Visible Lead Intelligence to Quote Draft journey', () => {
  test.beforeAll(async () => fs.mkdir(evidenceDir, { recursive: true }));

  test('01 Dashboard exposes Lead Intelligence and RFQ entry points', async ({ page }) => {
    await login(page);
    await expect(page.getByText('Commercial Attention')).toBeVisible();
    await expect(page.getByRole('button', { name: /Leads ready for RFQ/ })).toBeVisible();
    await expect(page.getByRole('button', { name: /RFQs ready for Quote/ })).toBeVisible();
    // The rail is five flat rows now — "Lead Management" was a collapsible group that had to be
    // expanded before its children existed in the DOM. "Leads" is a link, so there is nothing to
    // open; what this step is really asserting is that the lead surface is one click from the
    // landing screen, and it now is.
    const leadsRow = page.getByRole('button', { name: 'Leads' }).first();
    await expect(leadsRow).toBeVisible();
    await leadsRow.click();
    await expect(page).toHaveURL(/\/procurement\/leads\/all/);
    await page.screenshot({ path: path.join(evidenceDir, '01-dashboard-navigation.png'), fullPage: true });
  });

  test('02 Bulk upload navigates to persisted reconciliation summary', async ({ page }) => {
    await login(page);
    await page.goto('/procurement/leads/intelligence');
    await page.screenshot({ path: path.join(evidenceDir, '02-bulk-upload.png'), fullPage: true });
    await page.locator('input[type="file"]').setInputFiles(initialUpload);
    await page.getByRole('button', { name: 'Queue for reconciliation' }).click();
    await waitForBatch(page);
    await expect(page.getByRole('button', { name: /Files received/i })).toBeVisible();
    await page.screenshot({ path: path.join(evidenceDir, '03-reconciliation-summary.png'), fullPage: true });
  });

  test('03 New canonical Lead visibly creates one RFQ', async ({ page }) => {
    await login(page);
    await page.goto('/procurement/leads/view/1');
    const serialChip = page.getByText(/Nexora Serial:/).first();
    await expect(serialChip).toBeVisible();
    nexoraSerial = (await serialChip.textContent())!.replace(/^Nexora Serial:\s*/, '');
    await page.screenshot({ path: path.join(evidenceDir, '04-canonical-lead.png'), fullPage: true });
    const review = page.getByRole('button', { name: 'Review & Create RFQ' });
    await expect(review).toBeVisible();
    await page.screenshot({ path: path.join(evidenceDir, '05-review-create-rfq.png'), fullPage: true });
    await review.click();
    await expect(page.getByRole('heading', { name: 'Review inquiry and create RFQ' })).toBeVisible();
    await page.getByRole('button', { name: 'Create RFQ' }).click();
    await expect(page).toHaveURL(/\/procurement\/rfqs\/view\/\d+$/);
    rfqId = Number(page.url().split('/').at(-1));
    expect(rfqId).toBeGreaterThan(0);
  });

  test('04 Created RFQ appears once in All RFQs', async ({ page }) => {
    await login(page);
    await page.goto('/procurement/rfqs/all');
    await expect(page.getByText(nexoraSerial).first()).toBeVisible();
    await expect(page.getByText(nexoraSerial)).toHaveCount(1);
    await page.screenshot({ path: path.join(evidenceDir, '06-rfq-list.png'), fullPage: true });
  });

  test('05 RFQ links to canonical Lead with the same Nexora Serial', async ({ page }) => {
    await login(page);
    await page.goto(`/procurement/rfqs/view/${rfqId}`);
    await expect(page.getByText(`Nexora Serial: ${nexoraSerial}`)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Open Canonical Lead' })).toBeVisible();
    await expect(page.getByText(/Active Lead Revision/i)).toBeVisible();
    await page.screenshot({ path: path.join(evidenceDir, '07-rfq-detail-lineage.png'), fullPage: true });
  });

  test('06 Exact duplicate does not create another RFQ or Quote', async ({ page, request }) => {
    await login(page);
    const auth = { Authorization: `Bearer ${await token(page)}` };
    const beforeRfqs = await (await request.get(`${apiUrl}/api/Rfq?pageNumber=1&pageSize=100`, { headers: auth })).json();
    const beforeQuotes = await (await request.get(`${apiUrl}/api/Quote?pageNumber=1&pageSize=100`, { headers: auth })).json();
    await page.goto('/procurement/leads/intelligence');
    await page.locator('input[type="file"]').setInputFiles(initialUpload);
    await page.getByRole('button', { name: 'Queue for reconciliation' }).click();
    await waitForBatch(page);
    await expect(page.getByRole('button', { name: /1 Exact duplicates/i })).toBeVisible();
    const afterRfqs = await (await request.get(`${apiUrl}/api/Rfq?pageNumber=1&pageSize=100`, { headers: auth })).json();
    const afterQuotes = await (await request.get(`${apiUrl}/api/Quote?pageNumber=1&pageSize=100`, { headers: auth })).json();
    expect(afterRfqs.totalItems).toBe(beforeRfqs.totalItems);
    expect(afterQuotes.totalItems).toBe(beforeQuotes.totalItems);
  });

  test('07 Active Lead revision is visible with RFQ impact', async ({ page }) => {
    await login(page);
    await page.goto('/procurement/leads/intelligence');
    await page.locator('input[type="file"]').setInputFiles(postQuoteRevision);
    await page.getByRole('button', { name: 'Queue for reconciliation' }).click();
    await waitForBatch(page);
    await expect(page.getByRole('button', { name: /1 Revisions/i })).toBeVisible();
    await page.goto('/procurement/leads/view/1');
    await expect(page.getByRole('heading', { name: 'Revision history' })).toBeVisible();
    await expect(page.getByText(/Revision 2|Revision 3/).first()).toBeVisible();
    await page.goto('/procurement/leads/all?view=revisions');
    await expect(page.getByText(nexoraSerial).first()).toBeVisible();
    await page.screenshot({ path: path.join(evidenceDir, '08-revision-impact.png'), fullPage: true });
  });

  test('08 Revised inquiry marks an existing Quote Draft stale', async ({ page }) => {
    await login(page);
    await page.goto(`/procurement/rfqs/view/${rfqId}`);
    await page.getByRole('button', { name: 'Prepare Quote Draft' }).click();
    await expect(page).toHaveURL(/\/sales\/quotes\/view\/\d+$/);
    quoteId = Number(page.url().split('/').at(-1));
    await page.goto('/procurement/leads/intelligence');
    await page.locator('input[type="file"]').setInputFiles(postQuoteRevisionTwo);
    await page.getByRole('button', { name: 'Queue for reconciliation' }).click();
    await waitForBatch(page);
    await page.goto(`/sales/quotes/view/${quoteId}`);
    await expect(page.getByText('Customer Revision Received')).toBeVisible();
  });

  test('09 Possible Match decision offers Treat as Revision', async ({ page }) => {
    await login(page);
    await page.goto('/procurement/leads/intelligence');
    await page.locator('input[type="file"]').setInputFiles(possibleMatch);
    await page.getByRole('button', { name: 'Queue for reconciliation' }).click();
    await waitForBatch(page);
    await page.goto('/procurement/leads/possible-matches');
    const queueItem = page.locator('.MuiPaper-root').filter({ hasText: 'release-visible-possible-match.csv' }).first();
    await expect(queueItem).toBeVisible();
    await queueItem.getByRole('button', { name: 'Review evidence' }).click();
    await expect(page).toHaveURL(new RegExp(`/procurement/leads/ingestion/${batchId}$`));
    const button = page.getByRole('button', { name: 'Treat as revision' });
    await expect(button).toBeVisible();
    await page.screenshot({ path: path.join(evidenceDir, '09-possible-match-decision.png'), fullPage: true });
    await button.click();
    await expect(page.getByRole('heading', { name: 'Revision' })).toBeVisible();
    await page.getByRole('button', { name: 'Cancel' }).click();
  });

  test('10 Possible Match Create New Lead persists a canonical Lead with RFQ review', async ({ page }) => {
    await login(page);
    await page.goto(`/procurement/leads/ingestion/${batchId}`);
    const create = page.getByRole('button', { name: 'Create new lead' });
    await expect(create).toBeVisible();
    await create.click();
    await page.getByLabel('Decision reason').fill('Commercial review confirmed this is a separate customer inquiry.');
    await page.getByRole('button', { name: 'Record decision' }).click();
    const openLead = page.getByRole('button', { name: 'Open lead' });
    await expect(openLead).toBeVisible();
    await openLead.click();
    await expect(page).toHaveURL(/\/procurement\/leads\/view\/\d+$/);
    await expect(page.getByRole('button', { name: 'Review & Create RFQ' })).toBeVisible();
  });

  test('11 RFQ Prepare Quote Draft is idempotent', async ({ page }) => {
    await login(page);
    await page.goto(`/procurement/rfqs/view/${rfqId}`);
    await page.getByRole('button', { name: 'Prepare Quote Draft' }).click();
    await expect(page).toHaveURL(new RegExp(`/sales/quotes/view/${quoteId}$`));
    await page.screenshot({ path: path.join(evidenceDir, '10-prepare-quote-draft.png'), fullPage: true });
  });

  test('12 Quote Draft preserves full commercial lineage', async ({ page, request }) => {
    await login(page);
    await page.goto(`/sales/quotes/view/${quoteId}`);
    await expect(page.getByText(`Nexora Serial: ${nexoraSerial}`)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Open Source RFQ' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Open Canonical Lead' })).toBeVisible();
    await expect(page.getByText(/Pricing Pending/).first()).toBeVisible();
    const pdf = await request.get(`${apiUrl}/api/Quote/${quoteId}/pdf`, {
      headers: { Authorization: `Bearer ${await token(page)}` },
    });
    expect(pdf.status()).toBe(409);
    await page.getByRole('button', { name: 'Mark review complete' }).click();
    await expect(page.getByText('Customer Revision Received')).not.toBeVisible();
    await page.screenshot({ path: path.join(evidenceDir, '11-quote-detail-lineage.png'), fullPage: true });
  });

  test('13 Restricted role cannot prepare a Quote Draft', async ({ page, request }) => {
    await login(page, denied);
    const response = await request.post(`${apiUrl}/api/Rfq/${rfqId}/prepare-quote-draft`, {
      headers: { Authorization: `Bearer ${await token(page)}` },
    });
    expect(response.status()).toBe(403);
  });

  test('14 Cross-tenant RFQ and Quote requests are non-disclosing', async ({ page, request }) => {
    await login(page, other);
    const headers = { Authorization: `Bearer ${await token(page)}` };
    expect((await request.get(`${apiUrl}/api/Rfq/${rfqId}`, { headers })).status()).toBe(404);
    expect((await request.get(`${apiUrl}/api/Quote/${quoteId}`, { headers })).status()).toBe(404);
  });

  test('15 Dashboard retains reconciled drill-downs after the journey', async ({ page }) => {
    await login(page);
    await expect(page.getByText('Verified Performance')).toBeVisible();
    await expect(page.getByRole('button', { name: /Quote Drafts awaiting action/ })).toBeVisible();
    await page.screenshot({ path: path.join(evidenceDir, '12-dashboard-reconciliation.png'), fullPage: true });
  });

  test('16 Mobile navigation and key pages remain usable', async ({ page }) => {
    await page.setViewportSize({ width: 375, height: 812 });
    await login(page);
    await page.goto(`/sales/quotes/view/${quoteId}`);
    await expect(page.getByText(`Nexora Serial: ${nexoraSerial}`)).toBeVisible();
    await expect(page.getByText(/Commercial Review Required/).first()).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
    await page.screenshot({ path: path.join(evidenceDir, '13-mobile-view.png'), fullPage: true });
  });
});
