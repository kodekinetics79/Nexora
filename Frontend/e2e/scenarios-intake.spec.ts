import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type APIResponse, type Page } from '@playwright/test';
import net from 'node:net';
import { api, apiUrl, jsonOk, loginAs, loginAsOtherTenant, required, resolveLead } from './support/core-commercial';

/**
 * INTAKE SCENARIO TESTING — document → extraction → assembly → Lead, with varied inputs.
 *
 * The intake entry used here is the door the Manual Upload screen itself posts to:
 * `POST /api/Extraction/upload` with an `Idempotency-Key` (leadService.uploadGoverned →
 * ExtractionController.Upload → IDocumentIngestion). `/api/LeadIngestion/upload` does not exist;
 * `/api/ManualUpload/upload` is an older API-only door and is covered by one test of its own. The
 * e-mail door is exercised for real: the runner starts a loopback GreenMail sink, repoints the
 * fixture's mailbox row at it, and the spec posts MIME messages over SMTP and then calls the
 * screen's own "Poll now" (`POST /api/Email/fetch`). Every door feeds the same
 * DocumentIngestionService, ExtractionWorker and LeadIdentityApplicationService, which is where
 * the assertions below land. Outcomes are asserted through persisted API state (batch
 * reconciliation, Lead, revisions, resolutions, workbench, routing queue) and through the
 * operator screen where the scenario asks for one.
 *
 * Run three times by scripts/e2e/run-intake-scenarios.sh; a test that passes once and fails once
 * is a finding of its own class ("non-deterministic").
 */

const fixtureDir = path.resolve('e2e/fixtures/intake-scenarios');
// One nonce per process so references never collide across the three runs against one database.
const nonce = `${Date.now().toString(36)}${process.env.E2E_INTAKE_RUN ?? ""}`.toUpperCase();
const customerName = () => required('E2E_CORE_CUSTOMER_NAME');
const knownParts = () => [
  required('E2E_CORE_FULL_ATP_PART'),
  required('E2E_CORE_PARTIAL_ATP_PART'),
  required('E2E_CORE_OUT_OF_STOCK_PART'),
];

type Line = { product: string; qty: string; part: string; uom?: string; currency?: string; rfq?: string; buyer?: string };
type BatchItem = Record<string, any>;
type Batch = { batchId: string; items: BatchItem[]; newLeads: number; exactDuplicates: number; revisions: number; rejected: number } & Record<string, any>;
type Job = { jobId: number; occurrenceId: number | null; fileName: string; outcome: string; errorCode: string | null; reason?: string };
type Uploaded = { batchId: string; jobs: Job[]; idempotencyKey: string; status: number };

/** Batches this run stopped (rejected / dead-lettered), for the queue-truth check at the end. */
const stoppedBatches: Array<{ label: string; batchId: string }> = [];

function csv(lines: Line[], opts: { rfq: string; buyer?: string; header?: string[] }): Buffer {
  const header = opts.header ?? ['rfqno', 'buyername', 'productname', 'quantity', 'manufacturerpartnumber', 'uom', 'currency'];
  const q = (v: string | undefined) => `"${(v ?? '').replace(/"/g, '""')}"`;
  const rows = lines.map((l) => {
    const cells: Record<string, string | undefined> = {
      rfqno: l.rfq ?? opts.rfq,
      buyername: l.buyer ?? opts.buyer ?? customerName(),
      productname: l.product,
      quantity: l.qty,
      manufacturerpartnumber: l.part,
      uom: l.uom ?? 'EA',
      currency: l.currency ?? 'SAR',
    };
    return header.map((h) => q(cells[h])).join(',');
  });
  return Buffer.from('﻿' + [header.join(','), ...rows].join('\r\n') + '\r\n', 'utf8');
}

function cleanLines(qtys: [number, number, number] = [10, 5, 2]): Line[] {
  const parts = knownParts();
  return [
    { product: 'Ball valve 2IN class 300', qty: String(qtys[0]), part: parts[0] },
    { product: 'Gasket spiral wound 4IN', qty: String(qtys[1]), part: parts[1] },
    { product: 'Hex bolt M12 x 60 A4-80', qty: String(qtys[2]), part: parts[2] },
  ];
}

const newKey = () => `manual-upload:${crypto.randomUUID()}`;

/** The screen's door. One file per call, exactly the multipart the Manual Upload page sends. */
async function uploadRaw(page: Page, token: string, name: string, bytes: Buffer, mimeType = 'text/csv',
  idempotencyKey: string = newKey()): Promise<APIResponse> {
  return page.request.post(`${apiUrl}/api/Extraction/upload`, {
    headers: { Authorization: `Bearer ${token}`, 'Idempotency-Key': idempotencyKey },
    multipart: { files: { name, mimeType, buffer: bytes } },
    timeout: 60_000,
  });
}

async function upload(page: Page, token: string, name: string, bytes: Buffer, mimeType = 'text/csv',
  idempotencyKey: string = newKey()): Promise<Uploaded> {
  const response = await uploadRaw(page, token, name, bytes, mimeType, idempotencyKey);
  const text = await response.text();
  expect(response.status(), `POST /api/Extraction/upload → ${response.status()} ${text}`).toBe(202);
  const body = JSON.parse(text) as { batchId: string; jobs: Job[] };
  expect(body.jobs, text).toHaveLength(1);
  return { batchId: body.batchId, jobs: body.jobs, idempotencyKey, status: response.status() };
}

function settled(item: BatchItem): boolean {
  if (item.classification && item.classification !== 'Pending') return true;
  if (['Rejected', 'DeadLetter'].includes(String(item.intakeStatus))) return true;
  if (['DeadLetter', 'Succeeded'].includes(String(item.extractionStatus))) return true;
  return false;
}

async function batch(page: Page, token: string, batchId: string): Promise<Batch> {
  return jsonOk<Batch>(await api(page, token, 'get', `/api/LeadIngestion/batches/${batchId}`));
}

async function waitForBatch(page: Page, token: string, batchId: string, timeoutMs = 90_000,
  isSettled: (b: Batch) => boolean = (b) => b.items.length > 0 && b.items.every(settled)): Promise<Batch> {
  const started = Date.now();
  let last: Batch | null = null;
  while (Date.now() - started < timeoutMs) {
    last = await batch(page, token, batchId);
    if (isSettled(last)) return last;
    await page.waitForTimeout(2_000);
  }
  throw new Error(`Batch ${batchId} did not settle within ${timeoutMs} ms. Last state: ${JSON.stringify(last)}`);
}

function leadIdOf(b: Batch, classification = 'New'): number {
  const item = b.items.find((x) => x.classification === classification);
  expect(item, `batch ${b.batchId} has a ${classification} occurrence: ${JSON.stringify(b.items)}`).toBeTruthy();
  const id = Number(item!.leadId);
  expect(id).toBeGreaterThan(0);
  return id;
}

/**
 * A brand-new Lead whose customer did not resolve has no owner, and an owner-less Lead is out of
 * ordinary record scope by design (CommercialAccessFilters.InCommercialScope): it sits in the
 * governed routing queue until a MANAGER assigns an owner (a rep may only claim; the claim is a
 * lease, not access). Walk that step the way the Routing queue screen does — find the item and
 * assign it to the customer's account owner — so the rest of the journey can be examined. Returns
 * how access was obtained, which the audit records per scenario.
 */
async function ensureLeadAccess(page: Page, token: string, leadId: number): Promise<'direct' | 'assigned-from-routing-queue'> {
  if ((await api(page, token, 'get', `/api/Lead/${leadId}`)).status() === 200) return 'direct';
  // Routing runs after the occurrence is reconciled, so the queue item can trail the batch by a
  // few seconds.
  let item: Record<string, any> | undefined;
  for (let attempt = 0; attempt < 10 && !item; attempt++) {
    const queue = await jsonOk<{ items: Array<Record<string, any>> }>(
      await api(page, token, 'get', '/api/commercial-routing/queue?pageNumber=1&pageSize=200'));
    item = queue.items.find((x) => Number(x.leadId) === leadId);
    if (!item) await page.waitForTimeout(1_500);
  }
  expect(item, `Lead ${leadId} is neither readable nor in the routing queue`).toBeTruthy();
  const ownerUserId = await eligibleOwnerUserId(page, token);
  const assign = await api(page, token, 'post', `/api/commercial-intelligence/routing-queue/${item!.id}/assign`,
    { ownerUserId, expectedVersion: item!.version, reason: 'Scenario test: manager assigns the uploaded inquiry to an available rep.' },
    { 'Idempotency-Key': `scn-assign-${leadId}-${crypto.randomUUID()}` });
  expect(assign.ok(), `assign → ${assign.status()} ${await assign.text()}`).toBeTruthy();
  const after = await api(page, token, 'get', `/api/Lead/${leadId}`);
  expect(after.status(), `after assigning an owner, GET /api/Lead/${leadId} → ${after.status()}`).toBe(200);
  return 'assigned-from-routing-queue';
}

/** The fixture's account owner (Sarah) has exhausted capacity and is refused by governed routing
 *  ("Assignee is not currently eligible for governed routing"); ask the product who IS eligible. */
async function eligibleOwnerUserId(page: Page, token: string): Promise<number> {
  const options = await jsonOk<Array<Record<string, any>>>(await api(page, token, 'get', '/api/commercial-intelligence/routing-owner-options'));
  const eligible = options.find((o) => o.isAvailable === true);
  expect(eligible, `no routing-eligible owner in the fixture: ${JSON.stringify(options.map((o) => [o.userId, o.eligibilityReason]))}`).toBeTruthy();
  return Number(eligible!.userId);
}

async function lead(page: Page, token: string, id: number): Promise<Record<string, any>> {
  return jsonOk<Record<string, any>>(await api(page, token, 'get', `/api/Lead/${id}`));
}

async function workbench(page: Page, token: string, id: number): Promise<Record<string, any>> {
  return jsonOk<Record<string, any>>(await api(page, token, 'get', `/api/leads/${id}/decision-workbench`));
}

async function uploadAndWaitForLead(page: Page, token: string, name: string, bytes: Buffer, timeoutMs = 90_000): Promise<{ leadId: number; batch: Batch; uploaded: Uploaded; access: string }> {
  const uploaded = await upload(page, token, name, bytes);
  expect(uploaded.jobs[0].outcome, JSON.stringify(uploaded.jobs)).toBe('Enqueued');
  const started = Date.now();
  const b = await waitForBatch(page, token, uploaded.batchId, timeoutMs);
  test.info().annotations.push({ type: 'intake-seconds', description: `${name}: ${Math.round((Date.now() - started) / 1000)} s to settle` });
  const leadId = leadIdOf(b);
  const access = await ensureLeadAccess(page, token, leadId);
  test.info().annotations.push({ type: 'lead-access', description: `${name} → Lead ${leadId}: ${access}` });
  return { leadId, batch: b, uploaded, access };
}

// ---------------------------------------------------------------------------------------------
// 1. Clean CSV → one Lead, lines resolved; the same bytes again → Duplicate, no second Lead.
// ---------------------------------------------------------------------------------------------

test('S1 clean CSV becomes one Lead with resolved lines and the same bytes are a Duplicate', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const rfq = `SCN-${nonce}-CLEAN`;
  const bytes = csv(cleanLines(), { rfq });

  const first = await uploadAndWaitForLead(page, token, `scn-${nonce}-clean.csv`, bytes);
  expect(first.batch.newLeads).toBe(1);

  const persisted = await lead(page, token, first.leadId);
  expect(persisted.rfqno).toBe(rfq);
  expect(persisted.leadItems).toHaveLength(3);
  for (const line of persisted.leadItems) expect(Number(line.quantity)).toBeGreaterThan(0);

  const resolved = await resolveLead(page, token, first.leadId);
  expect(resolved).toHaveLength(3);
  const classes = resolved.map((r) => String(r.classification));
  expect(classes.every((c) => ['KnownInStock', 'KnownShortage', 'KnownIncoming'].includes(c)), classes.join(',')).toBe(true);

  // The screen's own retry: same bytes, same Idempotency-Key → the same batch, the same job.
  const retry = await upload(page, token, `scn-${nonce}-clean.csv`, bytes, 'text/csv', first.uploaded.idempotencyKey);
  expect(retry.jobs[0].outcome).toBe('Duplicate');
  expect(retry.batchId).toBe(first.uploaded.batchId);
  expect((await batch(page, token, first.uploaded.batchId)).newLeads).toBe(1);

  // A person uploading the same file again later: a new batch, no new Lead.
  const again = await upload(page, token, `scn-${nonce}-clean.csv`, bytes);
  expect(again.jobs[0].outcome).toBe('Duplicate');
  expect(again.batchId).not.toBe(first.uploaded.batchId);
  const againBatch = await waitForBatch(page, token, again.batchId, 60_000);
  expect(againBatch.newLeads).toBe(0);
  const dup = againBatch.items.find((x) => x.classification === 'ExactDuplicate');
  expect(dup, JSON.stringify(againBatch.items)).toBeTruthy();
  expect(dup!.leadId == null || Number(dup!.leadId) === first.leadId, 'a duplicate never mints a new Lead').toBe(true);

  const dupes = await jsonOk<Array<Record<string, any>>>(await api(page, token, 'get', '/api/LeadIngestion/duplicates'));
  expect(dupes.some((d) => d.uploadBatch === again.batchId), 'the duplicate is discoverable on the Duplicate uploads screen').toBe(true);
});

test('S1b the uploader can open the new Lead from the batch page without a detour', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const rfq = `SCN-${nonce}-OPEN`;
  const uploaded = await upload(page, token, `scn-${nonce}-open.csv`, csv(cleanLines(), { rfq }));
  const b = await waitForBatch(page, token, uploaded.batchId);
  const leadId = leadIdOf(b);

  await page.goto(`/procurement/leads/ingestion/${uploaded.batchId}`);
  await expect(page.getByRole('heading', { name: 'Batch reconciliation' })).toBeVisible();
  const open = page.getByRole('button', { name: 'Review inquiry' }).first();
  await expect(open).toBeVisible();
  await open.click();
  await expect(page).toHaveURL(new RegExp(`/procurement/leads/view/${leadId}$`));
  // Either the Lead opens, or the page says where it went and how to get it — never a dead end.
  await expect(page.getByText(/Lead Details Analysis Engine|routing queue/i).first()).toBeVisible({ timeout: 20_000 });
  const opened = await page.getByText('Lead Details Analysis Engine').isVisible().catch(() => false);
  if (!opened) {
    // The explanation and the way out are HARD requirements; that the uploader is not let in at
    // all is the product finding (owner-less Leads are out of a manager's record scope by
    // design; the person who uploaded the file is sent to the Routing queue to assign it).
    await expect(page.getByText(/not in your view yet/i)).toBeVisible();
    await expect(page.getByRole('button', { name: /open routing queue/i })).toBeVisible();
  }
  expect.soft(opened, `F4: the manager who uploaded Lead ${leadId} cannot open it until an owner is assigned (${page.url()})`).toBe(true);
});

test('S1c the documented sample shape (customer_rfq_reference,customer_name,part_number,quantity) keeps the reference', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const rfq = `SCN-${nonce}-SAMPLE`;
  const part = knownParts()[0];
  const bytes = Buffer.from(`customer_rfq_reference,customer_name,part_number,quantity\r\n${rfq},${customerName()},${part},4\r\n`, 'utf8');
  const uploaded = await upload(page, token, `scn-${nonce}-sample.csv`, bytes);
  const b = await waitForBatch(page, token, uploaded.batchId);
  const item = b.items[0];
  // Product finding, soft: `customer_rfq_reference` is not a recognised header synonym, so the
  // reference is dropped and the inquiry matches the earlier same-buyer upload instead of standing
  // on its own. The hard requirement is that it is never silently minted as a second Lead.
  expect.soft(item.classification, `F7: with its reference dropped the inquiry cannot be told apart from earlier ones: ${JSON.stringify(item.reasons)}`).toBe('New');
  expect(['New', 'PossibleMatchReviewRequired', 'Revision']).toContain(item.classification);
  if (item.classification === 'New') {
    const leadId = leadIdOf(b);
    await ensureLeadAccess(page, token, leadId);
    const persisted = await lead(page, token, leadId);
    expect(persisted.leadItems).toHaveLength(1);
    expect(persisted.leadItems[0].manufacturerPartNumber).toBe(part);
    expect.soft(persisted.rfqno, 'F7: the customer RFQ reference column is mapped, not dropped').toBe(rfq);
  }
});

test('S1d a CSV naming an existing customer by name only is UNRESOLVED with a reason, and linking resolves it', async ({ page }) => {
  // Customer identity is decided by customer-set identifiers (e-mail address, domain) — a buyer
  // NAME in a spreadsheet cell is evidence, not identity, and the fixture customer carries only
  // e-mail/domain identifiers. The requirement is that the unresolved state says so and that the
  // person can resolve it in one call.
  const token = await loginAs(page, 'manager');
  const rfq = `SCN-${nonce}-KNOWNCUST`;
  const { leadId } = await uploadAndWaitForLead(page, token, `scn-${nonce}-knowncust.csv`, csv(cleanLines(), { rfq }));
  const persisted = await lead(page, token, leadId);
  expect(persisted.buyersName).toBe(customerName());
  expect(String(persisted.customerMatchStatus)).toBe('UNRESOLVED');
  expect(String(persisted.customerMatchExplanation ?? ''), 'an unresolved customer is explained').not.toBe('');
  const link = await api(page, token, 'put', `/api/Lead/${leadId}/client`, { customerId: Number(required('E2E_CORE_CUSTOMER_ID')) });
  expect(link.ok(), `PUT /api/Lead/${leadId}/client → ${link.status()} ${await link.text()}`).toBeTruthy();
  const linked = await lead(page, token, leadId);
  expect(Number(linked.customerId)).toBe(Number(required('E2E_CORE_CUSTOMER_ID')));
  expect(String(linked.customerMatchStatus)).not.toBe('UNRESOLVED');
});

// ---------------------------------------------------------------------------------------------
// 2. Same reference, different bytes (quantities changed) → a REVISION on the same Lead.
// ---------------------------------------------------------------------------------------------

test('S2 an amendment with changed quantities becomes revision 2 of the same Lead with a visible diff', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const rfq = `SCN-${nonce}-AMEND`;
  const original = await uploadAndWaitForLead(page, token, `scn-${nonce}-amend-v1.csv`, csv(cleanLines([10, 5, 2]), { rfq }));

  const amended = await upload(page, token, `scn-${nonce}-amend-v2.csv`, csv(cleanLines([12, 5, 2]), { rfq }));
  expect(amended.jobs[0].outcome).toBe('Enqueued');
  const b = await waitForBatch(page, token, amended.batchId);
  const revision = b.items.find((x) => x.classification === 'Revision');
  expect(revision, `expected a Revision occurrence, got ${JSON.stringify(b.items.map((x) => [x.classification, x.leadId, x.reasons]))}`).toBeTruthy();
  expect(Number(revision!.leadId)).toBe(original.leadId);
  expect(Number(revision!.revisionNumber)).toBe(2);
  expect(b.newLeads).toBe(0);

  const revisions = await jsonOk<Array<Record<string, any>>>(await api(page, token, 'get', `/api/LeadIngestion/leads/${original.leadId}/revisions`));
  expect(revisions.length).toBeGreaterThanOrEqual(2);
  const latest = revisions[0];
  expect(Number(latest.revisionNumber)).toBe(2);
  expect(Number(latest.modifiedLineCount)).toBeGreaterThanOrEqual(1);
  // The diff is recorded per LINE (scope "Line", path $.items["1"]) with the whole line's JSON on
  // both sides; the changed quantity is inside it.
  const quantityChange = (latest.differences as Array<Record<string, any>>).find((d) => d.changeType === 'Modified'
    && (/quantity/i.test(String(d.path)) || (d.scope === 'Line' && /"quantity":\s*10\b/i.test(String(d.previousValueJson)) && /"quantity":\s*12\b/i.test(String(d.currentValueJson)))));
  expect(quantityChange, JSON.stringify(latest.differences.filter((d: any) => d.changeType !== 'Unchanged'))).toBeTruthy();

  const persisted = await lead(page, token, original.leadId);
  expect(persisted.leadItems.map((l: any) => Number(l.quantity)).sort((a: number, b2: number) => a - b2)).toEqual([2, 5, 12]);

  await page.goto(`/procurement/leads/view/${original.leadId}`);
  await expect(page.getByRole('heading', { name: 'Revision history' })).toBeVisible();
  await expect(page.getByText('Revision 2', { exact: true })).toBeVisible();
  await expect(page.getByText('Changes', { exact: true }).first()).toBeVisible();
});

// ---------------------------------------------------------------------------------------------
// 3. Unknown part numbers → Lead with UnknownProduct lines that need attention, explained.
// ---------------------------------------------------------------------------------------------

test('S3 unknown part numbers produce a Lead whose lines are UnknownProduct and the workbench says what to do', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const rfq = `SCN-${nonce}-UNKNOWN`;
  const lines: Line[] = [
    { product: 'Cryogenic globe valve DN80', qty: '3', part: `SCN-UNK-${nonce}-1` },
    { product: 'Thermowell 316L 12IN', qty: '7', part: `SCN-UNK-${nonce}-2` },
  ];
  const { leadId } = await uploadAndWaitForLead(page, token, `scn-${nonce}-unknown.csv`, csv(lines, { rfq }));

  const resolved = await resolveLead(page, token, leadId);
  expect(resolved).toHaveLength(2);
  expect(resolved.map((r) => r.classification)).toEqual(['UnknownProduct', 'UnknownProduct']);

  const wb = await workbench(page, token, leadId);
  expect(wb.lines).toHaveLength(2);
  for (const line of wb.lines) {
    expect(line.needsAttention, JSON.stringify(line)).toBe(true);
    expect(String(line.attentionReason)).toMatch(/no catalog match/i);
  }

  await page.goto(`/procurement/leads/view/${leadId}`);
  await expect(page.getByText('Unknown product').first()).toBeVisible();
  // The workbench opens on its Evidence stage; the line grid (with the part numbers) is the
  // Validate stage.
  await page.goto(`/procurement/leads/${leadId}/workbench?stage=validate`);
  const partOnScreen = page.getByText(`SCN-UNK-${nonce}-1`).first();
  if (!(await partOnScreen.isVisible().catch(() => false))) {
    const validateTab = page.getByRole('tab', { name: /validat/i }).first();
    if (await validateTab.isVisible().catch(() => false)) await validateTab.click();
  }
  await expect(partOnScreen).toBeVisible({ timeout: 20_000 });
});

// ---------------------------------------------------------------------------------------------
// 4. Hostile and edge inputs, each its own test.
// ---------------------------------------------------------------------------------------------

async function expectQuantityHeld(page: Page, token: string, label: string, qty: string) {
  const rfq = `SCN-${nonce}-${label}`;
  const { leadId, batch: b } = await uploadAndWaitForLead(page, token, `scn-${nonce}-${label.toLowerCase()}.csv`,
    csv([{ product: 'Ball valve 2IN class 300', qty, part: knownParts()[0] }], { rfq }));
  const persisted = await lead(page, token, leadId);
  expect(persisted.leadItems).toHaveLength(1);
  // Never a substituted number: a fabricated 1 or a demand for 0 units would be quoted as-is.
  expect(persisted.leadItems[0].quantity, `quantity "${qty}" must persist as null, not ${persisted.leadItems[0].quantity}`).toBeNull();
  expect(persisted.requiresCommercialReview, 'an unusable quantity keeps the Lead in review').toBe(true);
  const wb = await workbench(page, token, leadId);
  expect(wb.lines[0].needsAttention).toBe(true);
  expect(String(wb.lines[0].attentionReason)).toMatch(/quantity/i);
  return { leadId, batch: b, persisted };
}

test('S4a zero quantity is held for a person, never stored as 0 or 1', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  await expectQuantityHeld(page, token, 'QTY0', '0');
});

test('S4b negative quantity is held for a person', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  await expectQuantityHeld(page, token, 'QTYNEG', '-5');
});

test('S4c absurd quantity beyond the persisted contract is held for a person', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  await expectQuantityHeld(page, token, 'QTYHUGE', '100000000000000000000');
});

test('S4d missing quantity is held for a person', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  await expectQuantityHeld(page, token, 'QTYNONE', '');
});

test('S4e missing unit of measure is flagged before conversion', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const rfq = `SCN-${nonce}-NOUOM`;
  const bytes = csv([{ product: 'Ball valve 2IN class 300', qty: '6', part: knownParts()[0] }], {
    rfq, header: ['rfqno', 'buyername', 'productname', 'quantity', 'manufacturerpartnumber', 'currency'],
  });
  const { leadId } = await uploadAndWaitForLead(page, token, `scn-${nonce}-nouom.csv`, bytes);
  const persisted = await lead(page, token, leadId);
  expect(persisted.leadItems[0].unitOfMeasure ?? null).toBeNull();
  const wb = await workbench(page, token, leadId);
  expect(wb.lines[0].needsAttention).toBe(true);
  expect(String(wb.lines[0].attentionReason)).toMatch(/unit of measure/i);
});

test('S4f blank customer name yields an unresolved Lead, never a fabricated customer', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const rfq = `SCN-${nonce}-NOCUST`;
  const { leadId } = await uploadAndWaitForLead(page, token, `scn-${nonce}-nocust.csv`,
    csv([{ product: 'Ball valve 2IN class 300', qty: '6', part: knownParts()[0] }], { rfq, buyer: '' }));
  const persisted = await lead(page, token, leadId);
  expect(persisted.customerId ?? null).toBeNull();
  expect(String(persisted.customerMatchStatus)).not.toBe('MATCHED');
  expect(persisted.requiresCommercialReview).toBe(true);
});

test('S4g a 0-byte file is refused with a plain reason', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const response = await uploadRaw(page, token, `scn-${nonce}-empty.csv`, Buffer.alloc(0));
  expect(response.status()).toBe(400);
  const body = await response.json();
  expect(String(body.message)).toMatch(/empty/i);
});

/** A file the door refuses: a per-file Rejected row with a reason, recorded on its batch. */
async function expectDoorRefusal(page: Page, token: string, label: string, name: string, bytes: Buffer, mimeType: string, reason: RegExp) {
  const uploaded = await upload(page, token, name, bytes, mimeType);
  const job = uploaded.jobs[0];
  expect(job.outcome, JSON.stringify(job)).toBe('Rejected');
  expect(String(job.errorCode ?? '')).not.toBe('');
  expect(String(job.reason ?? ''), 'the refusal says why in plain words').toMatch(reason);
  const b = await batch(page, token, uploaded.batchId);
  const item = b.items.find((x) => x.classification === 'RejectedOrUnprocessable');
  expect(item, `the refusal is recorded on batch ${uploaded.batchId}: ${JSON.stringify(b.items)}`).toBeTruthy();
  expect((item!.reasons as string[]).join(' ')).toMatch(reason);
  stoppedBatches.push({ label, batchId: uploaded.batchId });

  await page.goto(`/procurement/leads/ingestion/${uploaded.batchId}`);
  await expect(page.getByRole('heading', { name: 'Batch reconciliation' })).toBeVisible();
  await expect(page.getByText(/did not pass inspection|rejected/i).first()).toBeVisible({ timeout: 30_000 });
  return { uploaded, batch: b, item: item! };
}

test('S4h CSV bytes under a spreadsheet name are refused with a reason that names the fix', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  await expectDoorRefusal(page, token, 'wrong-extension', `scn-${nonce}-wrongext.xlsx`, csv(cleanLines(), { rfq: `SCN-${nonce}-WRONGEXT` }),
    'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', /\.xlsx/);
});

test('S4h-legacy the older /api/ManualUpload/upload door answers an inspection refusal without a 500', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const response = await page.request.post(`${apiUrl}/api/ManualUpload/upload`, {
    headers: { Authorization: `Bearer ${token}` },
    multipart: { files: { name: `scn-${nonce}-legacy-wrongext.xlsx`, mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', buffer: csv(cleanLines(), { rfq: `SCN-${nonce}-LEGACY` }) } },
  });
  const text = await response.text();
  expect(response.status(), `a document inspection refusal is the caller's outcome, not a server error: ${text}`).not.toBe(500);
  expect(response.status()).toBeGreaterThanOrEqual(400);
  expect(response.status()).toBeLessThan(500);
  const body = JSON.parse(text);
  expect(String(body.message ?? body.detail ?? '')).toMatch(/\.xlsx/);
});

test('S4i a 500-line CSV becomes one Lead with 500 lines', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const rfq = `SCN-${nonce}-BIG`;
  const parts = knownParts();
  const lines: Line[] = Array.from({ length: 500 }, (_, i) => ({
    product: `Line ${i + 1} fitting`, qty: String((i % 40) + 1), part: parts[i % parts.length],
  }));
  // F13 (P2): a 500-line bid list takes minutes to persist (one SaveChanges per evidence line);
  // the wait is widened so the count below is what is asserted, and the elapsed time is annotated.
  const { leadId } = await uploadAndWaitForLead(page, token, `scn-${nonce}-big.csv`, csv(lines, { rfq }), 600_000);
  const persisted = await lead(page, token, leadId);
  expect(persisted.leadItems).toHaveLength(500);
  expect(persisted.leadItems.every((l: any) => Number(l.quantity) > 0)).toBe(true);
});

test('S4j UTF-8 Arabic descriptions survive intake verbatim', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const rfq = `SCN-${nonce}-ARABIC`;
  const arabic = 'صمام كروي ٢ بوصة فئة ٣٠٠ مع حشية';
  const { leadId } = await uploadAndWaitForLead(page, token, `scn-${nonce}-arabic.csv`,
    csv([{ product: arabic, qty: '4', part: knownParts()[0] }], { rfq }));
  const persisted = await lead(page, token, leadId);
  expect(persisted.leadItems).toHaveLength(1);
  const line = persisted.leadItems[0];
  expect([line.productShortName, line.productShortDescription, line.itemText].map(String).join('\n')).toContain(arabic);
  expect(Number(line.quantity)).toBe(4);
});

test('S4k a 5,000-character description does not lose the document', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const rfq = `SCN-${nonce}-LONG`;
  const long = `Long spec ${'x'.repeat(4_980)} end`;
  const { leadId, batch: b } = await uploadAndWaitForLead(page, token, `scn-${nonce}-long.csv`,
    csv([{ product: long, qty: '2', part: knownParts()[0] }], { rfq }));
  expect(b.rejected).toBe(0);
  const persisted = await lead(page, token, leadId);
  expect(persisted.leadItems).toHaveLength(1);
  expect(String(persisted.leadItems[0].productShortName ?? persisted.leadItems[0].productShortDescription)).toContain('Long spec');
});

// ---------------------------------------------------------------------------------------------
// 5. Password-protected / corrupt XLSX and a PDF stub → an explicit, human-readable terminal
//    outcome, visible where an operator looks, never a silent nothing.
// ---------------------------------------------------------------------------------------------

async function expectTerminalOutcome(page: Page, token: string, label: string, file: string, mimeType: string,
  expectedCode: RegExp, timeoutMs = 90_000) {
  const bytes = fs.readFileSync(path.join(fixtureDir, file));
  const uploaded = await upload(page, token, `scn-${nonce}-${file}`, bytes, mimeType);
  expect(uploaded.jobs[0].outcome, JSON.stringify(uploaded.jobs)).toBe('Enqueued');
  const b = await waitForBatch(page, token, uploaded.batchId, timeoutMs, (x) => x.items.length > 0 && x.items.every((i) =>
    ['Rejected', 'DeadLetter'].includes(String(i.intakeStatus)) || String(i.extractionStatus) === 'DeadLetter'
    || i.classification === 'RejectedOrUnprocessable'));
  const item = b.items[0];
  expect(item.classification, JSON.stringify(item)).toBe('RejectedOrUnprocessable');
  // The code is the generic dead-letter code; the REASON is where the person reads what happened.
  expect(`${item.errorCode ?? ''} ${(item.reasons as string[]).join(' ')}`, JSON.stringify(item)).toMatch(expectedCode);
  expect((item.reasons as string[]).filter((r) => r.trim().length > 0).length, 'a terminal outcome carries a reason').toBeGreaterThan(0);
  expect(b.rejected).toBeGreaterThanOrEqual(1);
  stoppedBatches.push({ label, batchId: uploaded.batchId });

  await page.goto(`/procurement/leads/ingestion/${uploaded.batchId}`);
  await expect(page.getByRole('heading', { name: 'Batch reconciliation' })).toBeVisible();
  await expect(page.getByText(/rejected|could not read|password|needs attention|did not pass|switched off/i).first()).toBeVisible({ timeout: 30_000 });
  return { uploaded, batch: b, item };
}

test('S5a a password-protected PDF ends as a visible password_protected outcome', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  await expectTerminalOutcome(page, token, 'password-protected', 'password-protected.pdf', 'application/pdf', /password.protected/i);
});

test('S5b a corrupt XLSX (valid package, broken sheet) ends as a visible parse failure', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  await expectTerminalOutcome(page, token, 'corrupt-xlsx', 'corrupt-sheet.xlsx',
    'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', /document_parse_failed|unsupported_format|could not be parsed/i);
});

test('S5c random bytes named .xlsx are refused at the door with a reason, and the refusal is recorded', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const bytes = fs.readFileSync(path.join(fixtureDir, 'garbage.xlsx'));
  await expectDoorRefusal(page, token, 'garbage-xlsx', `scn-${nonce}-garbage.xlsx`, bytes,
    'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', /not in that format|does not match|could not confirm/i);
});

test('S5d a PDF with no readable content ends as a visible terminal outcome within the retry budget', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  await expectTerminalOutcome(page, token, 'stub-pdf', 'stub.pdf', 'application/pdf', /extraction_failed|ai_not_authorized|not authorized|ocr|entitlement|external processing/i, 150_000);
});

// ---------------------------------------------------------------------------------------------
// 6. Two uploads racing with the same bytes → exactly one Lead.
// ---------------------------------------------------------------------------------------------

test('S6 two concurrent uploads of the same bytes mint exactly one Lead', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const rfq = `SCN-${nonce}-RACE`;
  const bytes = csv(cleanLines([3, 3, 3]), { rfq });

  // The same click twice (same Idempotency-Key) racing: one batch, one job.
  const key = newKey();
  const [a, b] = await Promise.all([
    uploadRaw(page, token, `scn-${nonce}-race.csv`, bytes, 'text/csv', key),
    uploadRaw(page, token, `scn-${nonce}-race.csv`, bytes, 'text/csv', key),
  ]);
  for (const r of [a, b]) expect(r.status(), await r.text()).toBe(202);
  const first = [await a.json(), await b.json()] as Array<{ batchId: string; jobs: Job[] }>;
  expect(new Set(first.map((x) => x.batchId)).size).toBe(1);
  expect(first.map((x) => x.jobs[0].outcome).sort()).toEqual(['Duplicate', 'Enqueued']);
  const settledBatch = await waitForBatch(page, token, first[0].batchId);
  expect(settledBatch.newLeads).toBe(1);
  expect(settledBatch.items.filter((x) => x.classification === 'New')).toHaveLength(1);

  // Two people uploading the same file at once (different keys): two batches, still one Lead.
  const [c, d] = await Promise.all([
    uploadRaw(page, token, `scn-${nonce}-race-c.csv`, bytes),
    uploadRaw(page, token, `scn-${nonce}-race-d.csv`, bytes),
  ]);
  for (const r of [c, d]) expect(r.status(), await r.text()).toBe(202);
  const second = [await c.json(), await d.json()] as Array<{ batchId: string; jobs: Job[] }>;
  expect(second.every((x) => x.jobs[0].outcome === 'Duplicate'), JSON.stringify(second)).toBe(true);
  for (const x of second) expect((await waitForBatch(page, token, x.batchId, 60_000)).newLeads).toBe(0);
});

// ---------------------------------------------------------------------------------------------
// 7. Authorization: denied role → 403 with a reason; other tenant's Lead → 404 to tenant 80101.
// ---------------------------------------------------------------------------------------------

test('S7a the denied role gets a 403 with a plain-English reason', async ({ page }) => {
  const token = await loginAs(page, 'denied');
  const response = await uploadRaw(page, token, `scn-${nonce}-denied.csv`, csv(cleanLines(), { rfq: `SCN-${nonce}-DENIED` }));
  expect(response.status()).toBe(403);
  const body = await response.json();
  const message = String(body.error ?? body.message ?? body.detail ?? body.title ?? '');
  expect(message.length, `403 body: ${JSON.stringify(body)}`).toBeGreaterThan(10);
  expect(message).toMatch(/permission|not allowed|not authori[sz]ed|access/i);
});

test('S7b another tenant\'s Lead is invisible to tenant 80101 as a 404, not a 403', async ({ page }) => {
  const other = await loginAsOtherTenant(page);
  const rfq = `SCN-${nonce}-OTHER`;
  const uploaded = await upload(page, other, `scn-${nonce}-other.csv`,
    csv([{ product: 'Other tenant valve', qty: '2', part: `SCN-OTHER-${nonce}` }], { rfq, buyer: 'Other Tenant Buyer' }));
  const leadId = leadIdOf(await waitForBatch(page, other, uploaded.batchId));

  const manager = await loginAs(page, 'manager');
  for (const route of [`/api/Lead/${leadId}`, `/api/leads/${leadId}/decision-workbench`, `/api/LeadIngestion/batches/${uploaded.batchId}`]) {
    const response = await api(page, manager, 'get', route);
    expect(response.status(), `${route} → ${response.status()} ${await response.text()}`).toBe(404);
  }
  // The revisions list is tenant-scoped in the query (it leaks nothing) but answers 200 [] for a
  // foreign id where every sibling verb answers 404 (F14, P2).
  const revisions = await api(page, manager, 'get', `/api/LeadIngestion/leads/${leadId}/revisions`);
  expect([200, 404]).toContain(revisions.status());
  if (revisions.status() === 200) expect(await revisions.json()).toEqual([]);
  expect.soft(revisions.status(), 'F14: a foreign lead id should be a 404 on every verb').toBe(404);
});

// ---------------------------------------------------------------------------------------------
// 8. Intake queue truth: what an operator can find after all of the above.
// ---------------------------------------------------------------------------------------------

test('S8 the stopped-mail queue counts honestly and every stopped upload is findable somewhere an operator looks', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const stopped = await api(page, token, 'get', '/api/email-triage?state=stopped');
  expect([200, 403], `email-triage stopped → ${stopped.status()} ${await stopped.text()}`).toContain(stopped.status());
  if (stopped.status() === 200) {
    const body = await stopped.json();
    expect(typeof body.totalCount).toBe('number');
    const rows: Array<Record<string, any>> = body.items ?? body.rows ?? [];
    expect(rows.length).toBeLessThanOrEqual(body.totalCount);
    for (const row of rows) {
      expect(row.id).toBeTruthy();
      const explanation = [row.assemblyReason, row.parseStatus, row.triageReasons, row.reasons].flat().filter(Boolean);
      expect(explanation.length, `stopped row ${row.id} explains itself: ${JSON.stringify(row)}`).toBeGreaterThan(0);
    }
    test.info().annotations.push({ type: 'email-triage-stopped', description: `totalCount=${body.totalCount}` });
    await page.goto('/procurement/leads/inbound-mail');
    await expect(page.getByRole('button', { name: 'Poll now' })).toBeVisible({ timeout: 20_000 });
    await expect(page.getByRole('tab', { name: /needs a person/i })).toBeVisible();
  }

  // Stopped uploads never reach the mail queue (by design: it is the inbound-mail audit). The
  // question is whether they are findable ANYWHERE without the batch URL.
  const blocked = await jsonOk<{ blockedFiles: number; batches: Array<Record<string, any>> }>(await api(page, token, 'get', '/api/LeadIngestion/blocked-files'));
  const dupes = await jsonOk<Array<Record<string, any>>>(await api(page, token, 'get', '/api/LeadIngestion/duplicates'));
  const matches = await jsonOk<Array<Record<string, any>>>(await api(page, token, 'get', '/api/LeadIngestion/match-reviews'));
  const findable = new Set<string>([
    ...blocked.batches.map((b) => String(b.batchId)),
    ...dupes.map((d) => String(d.uploadBatch)),
    ...matches.map((m) => String(m.batchId)),
  ]);
  const orphaned = stoppedBatches.filter((s) => !findable.has(s.batchId));
  test.info().annotations.push({ type: 'intake-queue-truth', description: `stopped uploads this run: ${stoppedBatches.length}; on an operator list: ${stoppedBatches.length - orphaned.length}; only via batch URL: ${orphaned.map((o) => o.label).join(', ') || 'none'}` });
  expect(orphaned, `every stopped upload needs a home an operator can find without the batch URL; orphaned: ${JSON.stringify(orphaned)}`).toHaveLength(0);
});

// =============================================================================================
// Scenario set two (2026-09-05): lead verbs, the bid list with no unit/currency, the mail door,
// cross-channel duplicates, role boundaries, and the tenant's fallback owner.
// =============================================================================================

const smtpPort = () => Number(required('E2E_SMTP_PORT'));
const mailboxAddress = () => required('E2E_MAILBOX_ADDRESS');

type Attachment = { name: string; bytes: Buffer; mime: string };

/** A MIME message with optional attachments, delivered to the loopback sink over plain SMTP. */
async function sendMail(opts: { subject: string; from: string; body: string; attachments?: Attachment[]; headers?: Record<string, string> }): Promise<string> {
  const boundary = `scn-${crypto.randomUUID()}`;
  const messageId = `<${crypto.randomUUID()}@scn.local>`;
  const head = [
    `From: ${opts.from}`, `To: ${mailboxAddress()}`, `Subject: ${opts.subject}`, `Date: ${new Date().toUTCString()}`,
    `Message-ID: ${messageId}`, 'MIME-Version: 1.0',
    ...Object.entries(opts.headers ?? {}).map(([k, v]) => `${k}: ${v}`),
    `Content-Type: multipart/mixed; boundary="${boundary}"`, '', `--${boundary}`,
    'Content-Type: text/plain; charset=utf-8', 'Content-Transfer-Encoding: 8bit', '', opts.body, '',
  ];
  const parts = (opts.attachments ?? []).flatMap((a) => [
    `--${boundary}`, `Content-Type: ${a.mime}; name="${a.name}"`, `Content-Disposition: attachment; filename="${a.name}"`,
    'Content-Transfer-Encoding: base64', '', ...(a.bytes.toString('base64').match(/.{1,76}/g) ?? []), '',
  ]);
  const raw = [...head, ...parts, `--${boundary}--`, ''].join('\r\n').replace(/\r\n\./g, '\r\n..');
  const sender = opts.from.match(/<([^>]+)>/)?.[1] ?? opts.from;
  await new Promise<void>((resolve, reject) => {
    const socket = net.createConnection({ host: '127.0.0.1', port: smtpPort() });
    const steps = [`EHLO scn.local\r\n`, `MAIL FROM:<${sender}>\r\n`, `RCPT TO:<${mailboxAddress()}>\r\n`, `DATA\r\n`, `${raw}\r\n.\r\n`, `QUIT\r\n`];
    let buffer = '';
    let step = -1;
    socket.setEncoding('utf8');
    socket.on('data', (chunk: string) => {
      buffer += chunk;
      // A reply is complete when its last line is "NNN " (multi-line EHLO replies use "NNN-").
      if (!/(^|\r\n)\d{3} [^\r\n]*\r\n$/.test(buffer)) return;
      const code = Number(buffer.slice(buffer.lastIndexOf('\r\n', buffer.length - 3) + 2, buffer.lastIndexOf('\r\n', buffer.length - 3) + 5) || buffer.slice(0, 3));
      if (code >= 400) { socket.destroy(); reject(new Error(`SMTP step ${step} refused: ${buffer.trim()}`)); return; }
      buffer = '';
      step += 1;
      if (step < steps.length) socket.write(steps[step]);
      else { socket.end(); resolve(); }
    });
    socket.on('error', reject);
    socket.setTimeout(20_000, () => { socket.destroy(); reject(new Error('SMTP timed out')); });
  });
  return messageId;
}

/** The screen's own "Poll now". */
async function pollMail(page: Page, token: string): Promise<Record<string, any>> {
  const response = await api(page, token, 'post', '/api/Email/fetch');
  const text = await response.text();
  expect(response.status(), `POST /api/Email/fetch → ${response.status()} ${text}`).toBe(200);
  return JSON.parse(text);
}

type TriageRow = Record<string, any>;

function mailSettled(row: TriageRow): boolean {
  const state = String(row.assemblyState ?? '');
  if (['Assembled', 'NeedsReview', 'FailedRecoverable', 'RejectedSecurity', 'NoInquiry'].includes(state)) return true;
  const parse = String(row.parseStatus ?? '');
  return parse === 'Rejected' || parse === 'Success' || parse.startsWith('Failed');
}

async function triageRow(page: Page, token: string, subject: string, timeoutMs = 120_000): Promise<TriageRow> {
  const started = Date.now();
  let last: TriageRow | undefined;
  while (Date.now() - started < timeoutMs) {
    const pageOne = await jsonOk<{ items: TriageRow[] }>(await api(page, token, 'get', '/api/email-triage?page=1&pageSize=100'));
    last = pageOne.items.find((r) => String(r.subject) === subject);
    if (last && mailSettled(last)) return last;
    await page.waitForTimeout(2_000);
  }
  throw new Error(`mail "${subject}" did not settle within ${timeoutMs} ms; last: ${JSON.stringify(last)}`);
}

async function stoppedMail(page: Page, token: string): Promise<{ totalCount: number; items: TriageRow[] }> {
  return jsonOk(await api(page, token, 'get', '/api/email-triage?state=stopped&page=1&pageSize=100'));
}

async function lifecycle(page: Page, token: string, leadId: number): Promise<Record<string, any>> {
  return jsonOk(await api(page, token, 'get', `/api/commercial-cases/leads/${leadId}/lifecycle`));
}

async function transition(page: Page, token: string, leadId: number, target: string, expectedVersion?: number): Promise<APIResponse> {
  return api(page, token, 'post', `/api/commercial-cases/leads/${leadId}/transition`,
    { targetStatusCode: target, ...(expectedVersion === undefined ? {} : { expectedVersion }), idempotencyKey: `scn-${crypto.randomUUID()}` });
}

/** RECEIVED → … → QUALIFIED, re-reading the version between hops the way the screen does. */
async function qualify(page: Page, token: string, leadId: number): Promise<void> {
  for (const target of ['PENDING_IDENTIFICATION', 'ASSIGNED', 'UNDER_REVIEW', 'QUALIFIED']) {
    const current = await lifecycle(page, token, leadId);
    if (current.currentStatusCode === 'QUALIFIED') return;
    const hop = await transition(page, token, leadId, target, Number(current.version));
    expect(hop.ok(), `${target} ← v${current.version}: ${hop.status()} ${await hop.text()}`).toBeTruthy();
  }
  expect((await lifecycle(page, token, leadId)).currentStatusCode).toBe('QUALIFIED');
}

function reviewItems(persisted: Record<string, any>, corrections: Record<string, unknown> = {}): Array<Record<string, unknown>> {
  return (persisted.leadItems as Array<Record<string, any>>).map((i) => ({
    id: i.id, quantity: i.quantity, unitOfMeasure: i.unitOfMeasure, currency: i.currency,
    manufacturerPartNumber: i.manufacturerPartNumber, productShortName: i.productShortName, ...corrections,
  }));
}

async function approveReview(page: Page, token: string, leadId: number, reason: string | undefined, corrections: Record<string, unknown> = {}): Promise<APIResponse> {
  const persisted = await lead(page, token, leadId);
  return api(page, token, 'put', `/api/Lead/${leadId}/review`, {
    action: 'approve', ...(reason === undefined ? {} : { reason }), expectedVersion: persisted.reviewVersion, items: reviewItems(persisted, corrections),
  });
}

const governedFit = ['ELIGIBILITY', 'CAPABILITY', 'DELIVERY', 'COMPLIANCE', 'COMMERCIAL'].map((code) => ({ code, decision: 'PASS' }));

async function fitAssessment(page: Page, token: string, leadId: number): Promise<void> {
  const wb = await workbench(page, token, leadId);
  const fit = await api(page, token, 'post', `/api/leads/${leadId}/participation/fit-assessments`,
    { expectedLeadRevisionId: wb.leadRevisionId, expectedDecisionVersion: wb.decisionVersion, overallDecision: 'FIT', rationale: 'Scenario test: standard valve line we stock.', criteria: governedFit },
    { 'Idempotency-Key': `scn-fit-${crypto.randomUUID()}` });
  expect(fit.ok(), `fit assessment → ${fit.status()} ${await fit.text()}`).toBeTruthy();
}

async function decideBid(page: Page, token: string, leadId: number, line: Record<string, unknown>): Promise<APIResponse> {
  const wb = await workbench(page, token, leadId);
  return api(page, token, 'post', `/api/leads/${leadId}/participation/decisions`, {
    expectedLeadRevisionId: wb.leadRevisionId, expectedDecisionVersion: wb.decisionVersion, expectedParticipationVersion: wb.participationVersion ?? null,
    commit: true, lines: [{ leadItemRevisionId: wb.lines[0].revisionLineId, choice: 'Bid', quantity: wb.lines[0].quantity ?? 1, ...line }],
  }, { 'Idempotency-Key': `scn-decide-${crypto.randomUUID()}` });
}

async function promote(page: Page, token: string, leadId: number): Promise<APIResponse> {
  const wb = await workbench(page, token, leadId);
  return api(page, token, 'post', `/api/leads/${leadId}/participation/promote`,
    { expectedLeadRevisionId: wb.leadRevisionId, expectedDecisionVersion: wb.decisionVersion, expectedParticipationVersion: wb.participationVersion ?? 0 },
    { 'Idempotency-Key': `scn-promote-${crypto.randomUUID()}` });
}

async function linkCustomer(page: Page, token: string, leadId: number): Promise<void> {
  const link = await api(page, token, 'put', `/api/Lead/${leadId}/client`, { customerId: Number(required('E2E_CORE_CUSTOMER_ID')) });
  expect(link.ok(), `link customer → ${link.status()} ${await link.text()}`).toBeTruthy();
}

async function problemDetail(response: APIResponse): Promise<string> {
  const body = await response.json().catch(() => ({}));
  return String(body.detail ?? body.error ?? body.message ?? body.title ?? '');
}

// ---------------------------------------------------------------------------------------------
// 9. Lead review needs a reason; lifecycle hops need expectedVersion; stale → 409 with a sentence.
// ---------------------------------------------------------------------------------------------

test('S9 approve needs a reason, hops need expectedVersion, and a stale version is a 409 that says so', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const { leadId } = await uploadAndWaitForLead(page, token, `scn-${nonce}-verbs.csv`, csv(cleanLines(), { rfq: `SCN-${nonce}-VERBS` }));

  const noReason = await approveReview(page, token, leadId, undefined);
  expect(noReason.status()).toBe(400);
  expect(await problemDetail(noReason)).toMatch(/approval reason is required/i);

  const before = (await lead(page, token, leadId)).reviewVersion;
  const approved = await approveReview(page, token, leadId, 'Scenario test: figures checked against the document.');
  expect(approved.ok(), await approved.text()).toBeTruthy();
  expect((await lead(page, token, leadId)).commercialFactsVerified).toBe(true);

  // The same approval replayed with the version the screen loaded before: refused with the two
  // numbers, not silently accepted twice.
  const stale = await api(page, token, 'put', `/api/Lead/${leadId}/review`,
    { action: 'approve', reason: 'stale replay', expectedVersion: before, items: reviewItems(await lead(page, token, leadId)) });
  expect(stale.status()).toBe(409);
  expect(await problemDetail(stale)).toMatch(/version .* is stale; current version is/i);

  const current = await lifecycle(page, token, leadId);
  expect(current.currentStatusCode).toBe('RECEIVED');
  const noVersion = await transition(page, token, leadId, 'PENDING_IDENTIFICATION');
  expect(noVersion.status()).toBe(400);
  expect(await problemDetail(noVersion)).toMatch(/expected version/i);
  const wrongVersion = await transition(page, token, leadId, 'PENDING_IDENTIFICATION', Number(current.version) + 5);
  expect(wrongVersion.status()).toBe(409);
  expect(await problemDetail(wrongVersion)).toMatch(/lifecycle state changed.*reload/i);
  expect((await lifecycle(page, token, leadId)).currentStatusCode, 'a refused hop moves nothing').toBe('RECEIVED');

  await qualify(page, token, leadId);
});

// ---------------------------------------------------------------------------------------------
// 10. A bid list with no unit and no currency: flagged, refused by name, corrected, promoted.
// ---------------------------------------------------------------------------------------------

function bareBidList(rfq: string, qty = 6): Buffer {
  return csv([{ product: 'Ball valve 2IN class 300', qty: String(qty), part: knownParts()[0] }], {
    rfq, header: ['rfqno', 'buyername', 'productname', 'quantity', 'manufacturerpartnumber'],
  });
}

test('S10 a bid list with no unit or currency is held for review, refused by field name, and becomes an RFQ once a person supplies both', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const { leadId } = await uploadAndWaitForLead(page, token, `scn-${nonce}-bare.csv`, bareBidList(`SCN-${nonce}-BARE`));

  const wb = await workbench(page, token, leadId);
  expect(wb.lines).toHaveLength(1);
  expect(wb.lines[0].unitOfMeasure ?? null).toBeNull();
  expect(wb.lines[0].currency ?? null).toBeNull();
  expect(wb.lines[0].needsAttention).toBe(true);
  expect(String(wb.lines[0].attentionReason)).toMatch(/unit of measure/i);
  const currency = String((wb.currencyOptions as Array<{ code: string }>)[0]?.code ?? 'USD');
  const unit = String((wb.unitOptions as Array<{ code: string }>)[0]?.code ?? 'EA');

  // The person supplies both in the governed extraction approval, with a reason.
  await linkCustomer(page, token, leadId);
  const approved = await approveReview(page, token, leadId, 'Scenario test: unit and currency taken from the customer frame agreement.', { unitOfMeasure: unit, currency });
  expect(approved.ok(), await approved.text()).toBeTruthy();
  const corrected = await workbench(page, token, leadId);
  expect(corrected.lines[0].unitOfMeasure).toBe(unit);
  expect(corrected.lines[0].currency).toBe(currency);
  expect(corrected.lines[0].needsAttention).toBe(false);

  await qualify(page, token, leadId);
  await fitAssessment(page, token, leadId);
  const decided = await decideBid(page, token, leadId, { unitOfMeasure: unit, currency, reasonNotes: 'Scenario test: bid on the corrected line.' });
  expect(decided.ok(), `decide → ${decided.status()} ${await decided.text()}`).toBeTruthy();
  const promoted = await promote(page, token, leadId);
  expect(promoted.ok(), `promote → ${promoted.status()} ${await promoted.text()}`).toBeTruthy();
  const receipt = await promoted.json();
  expect(Number(receipt.rfqId)).toBeGreaterThan(0);
  expect(String(receipt.rfqNumber)).not.toBe('');
  expect(Number(receipt.promotedLineCount)).toBe(1);
});

test('S10b without the correction the Bid is refused naming unit then currency, and the approval cannot be redone', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const { leadId } = await uploadAndWaitForLead(page, token, `scn-${nonce}-bare2.csv`, bareBidList(`SCN-${nonce}-BARE2`, 7));
  await linkCustomer(page, token, leadId);
  // Approved as extracted — the person did not notice the blank unit and currency.
  const approved = await approveReview(page, token, leadId, 'Scenario test: approved as extracted.');
  expect(approved.ok(), await approved.text()).toBeTruthy();
  await qualify(page, token, leadId);
  await fitAssessment(page, token, leadId);

  const unacknowledged = await decideBid(page, token, leadId, {});
  expect(unacknowledged.status()).toBe(400);
  expect(await problemDetail(unacknowledged)).toMatch(/acknowledgement note/i);
  const noUnit = await decideBid(page, token, leadId, { reasonNotes: 'Scenario test: acknowledged the warning.' });
  expect(noUnit.status()).toBe(400);
  expect(await problemDetail(noUnit), 'the refusal names the line and the field').toMatch(/line \d+ requires an active tenant unit of measure/i);
  const noCurrency = await decideBid(page, token, leadId, { reasonNotes: 'Scenario test: acknowledged the warning.', unitOfMeasure: 'EA' });
  expect(noCurrency.status()).toBe(400);
  expect(await problemDetail(noCurrency), 'the refusal names the line and the field').toMatch(/line \d+ requires an active tenant currency/i);

  // Supplying both at decision time is not enough: the source must prove the unit, and the only
  // door that records that proof is the extraction approval — which is now closed.
  const wb = await workbench(page, token, leadId);
  const currency = String((wb.currencyOptions as Array<{ code: string }>)[0]?.code ?? 'USD');
  const evidence = await decideBid(page, token, leadId, { reasonNotes: 'Scenario test: acknowledged the warning.', unitOfMeasure: 'EA', currency });
  expect(evidence.status()).toBe(409);
  expect(await problemDetail(evidence)).toMatch(/lacks exact evidence for unit of measure/i);
  const redo = await approveReview(page, token, leadId, 'Scenario test: second look, unit and currency supplied.', { unitOfMeasure: 'EA', currency });
  expect.soft(redo.ok(), `F5: one-way gate — the line can never be bid and the approval cannot be redone: ${redo.status()} ${await redo.text()}`).toBe(true);
  // What the person gets instead, verbatim, so the report can quote it.
  test.info().annotations.push({ type: 'F5-redo-approval', description: `${redo.status()} ${await problemDetail(redo)}` });
});

// ---------------------------------------------------------------------------------------------
// 11. Inbound mail that is NOT an RFQ: rejected with a stated reason, findable, never stranded.
// ---------------------------------------------------------------------------------------------

test('S11 a newsletter is rejected as noise with its reason, and an invoice with a PDF is stopped where an operator looks', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const newsSubject = `SCN-${nonce} NEWS September fittings newsletter`;
  const invoiceSubject = `SCN-${nonce} INVOICE INV-${nonce} attached`;
  await sendMail({ subject: newsSubject, from: 'Vendor News <newsletter@vendor-news.local>',
    body: 'Our September newsletter: 20% off all fittings this month. Unsubscribe here.',
    headers: { 'List-Unsubscribe': '<mailto:unsub@vendor-news.local>', Precedence: 'bulk' } });
  await sendMail({ subject: invoiceSubject, from: 'Accounts <accounts@some-supplier.local>',
    body: `Please find attached invoice INV-${nonce} for your order. Payment is due in 30 days.`,
    attachments: [{ name: 'invoice.pdf', bytes: Buffer.concat([fs.readFileSync(path.join(fixtureDir, 'stub.pdf')), Buffer.from(`\n%SCN-${nonce}-invoice\n`)]), mime: 'application/pdf' }] });

  const report = await pollMail(page, token);
  expect(Number(report.totals?.mailboxesFailed ?? 0), JSON.stringify(report)).toBe(0);
  expect(Number(report.totals?.messagesCaptured ?? 0)).toBeGreaterThanOrEqual(2);

  const news = await triageRow(page, token, newsSubject);
  expect(news.outcome).toBe('Noise');
  expect(news.parseStatus).toBe('Rejected');
  expect((news.reasonCodes as string[]).length, 'the rejection states its reason').toBeGreaterThan(0);
  expect(news.assemblyState).toBe('NoInquiry');
  expect(news.leadId ?? null).toBeNull();

  const invoice = await triageRow(page, token, invoiceSubject);
  expect(invoice.leadId ?? null, 'an invoice never becomes a Lead').toBeNull();
  expect(invoice.assembledLeadId ?? null).toBeNull();
  // Not a Lead and not a decided verdict → it must be on the one list that answers "what stopped?"
  const stopped = await stoppedMail(page, token);
  const listed = stopped.items.find((r) => Number(r.id) === Number(invoice.id));
  expect(listed, `F1: mail ${invoice.id} (${invoice.assemblyState} / ${invoice.parseStatus}) is not on the stopped list: ${JSON.stringify(stopped.items.map((r) => r.id))}`).toBeTruthy();
  expect(String(listed!.assemblyReason ?? listed!.parseStatus ?? ''), 'the stopped row explains itself').not.toBe('');

  // Every part of it carries a state, and every part that stopped says why.
  const detail = await jsonOk<TriageRow>(await api(page, token, 'get', `/api/email-triage/${invoice.id}`));
  const components = (detail.components ?? []) as TriageRow[];
  expect(components.length).toBeGreaterThanOrEqual(2);
  for (const component of components) {
    expect(String(component.state ?? '')).not.toBe('');
    if (['FailedRecoverable', 'Skipped'].includes(String(component.state)))
      expect(String(component.reasonCode ?? ''), `F2: part ${component.fileName} stopped without a reason code: ${JSON.stringify(component)}`).not.toBe('');
  }

  // And the screen: the "Needs a person" tab (the stopped state) is where the operator lands on it.
  await page.goto('/procurement/leads/inbound-mail');
  await expect(page.getByRole('button', { name: 'Poll now' })).toBeVisible({ timeout: 20_000 });
  await page.getByRole('tab', { name: /needs a person/i }).click();
  await expect(page.getByText(invoiceSubject).first()).toBeVisible({ timeout: 30_000 });
});

// ---------------------------------------------------------------------------------------------
// 12. The same RFQ by e-mail and by upload: one Lead, whichever door it came through first.
// ---------------------------------------------------------------------------------------------

test('S12 the same bid list arriving by upload and by e-mail attachment is one Lead, in either order', async ({ page }) => {
  const token = await loginAs(page, 'manager');

  // Upload first, e-mail second.
  const bytesA = csv(cleanLines([4, 4, 4]), { rfq: `SCN-${nonce}-XCHAN-A` });
  const first = await uploadAndWaitForLead(page, token, `scn-${nonce}-xchan-a.csv`, bytesA);
  const subjectA = `SCN-${nonce} XCHAN-A RFQ by mail after upload`;
  await sendMail({ subject: subjectA, from: `Procurement <${required('E2E_CORE_CONTACT_EMAIL')}>`, body: 'Please quote the attached bid list.',
    attachments: [{ name: `scn-${nonce}-xchan-a.csv`, bytes: bytesA, mime: 'text/csv' }] });
  await pollMail(page, token);
  const mailA = await triageRow(page, token, subjectA);
  expect(mailA.linkedBatchId, JSON.stringify(mailA)).toBeTruthy();
  const batchA = await waitForBatch(page, token, String(mailA.linkedBatchId), 90_000, (b) => b.items.some((i) => /\.csv$/i.test(String(i.fileName)) && settled(i)));
  const csvA = batchA.items.find((i) => /\.csv$/i.test(String(i.fileName)))!;
  expect(csvA.classification, JSON.stringify(csvA)).toBe('ExactDuplicate');
  expect(csvA.leadId == null || Number(csvA.leadId) === first.leadId).toBe(true);
  expect(batchA.newLeads).toBe(0);

  // E-mail first, upload second.
  const bytesB = csv(cleanLines([9, 1, 1]), { rfq: `SCN-${nonce}-XCHAN-B` });
  const subjectB = `SCN-${nonce} XCHAN-B RFQ by mail before upload`;
  await sendMail({ subject: subjectB, from: `Procurement <${required('E2E_CORE_CONTACT_EMAIL')}>`, body: 'Please quote the attached bid list.',
    attachments: [{ name: `scn-${nonce}-xchan-b.csv`, bytes: bytesB, mime: 'text/csv' }] });
  await pollMail(page, token);
  const mailB = await triageRow(page, token, subjectB);
  await waitForBatch(page, token, String(mailB.linkedBatchId), 90_000, (b) => b.items.some((i) => /\.csv$/i.test(String(i.fileName)) && settled(i)));
  const again = await upload(page, token, `scn-${nonce}-xchan-b.csv`, bytesB);
  expect(again.jobs[0].outcome, 'the door already knows these bytes').toBe('Duplicate');
  expect((await waitForBatch(page, token, again.batchId, 60_000)).newLeads).toBe(0);
});

// ---------------------------------------------------------------------------------------------
// 13. Role boundaries on every lead verb: editor / denied / other tenant.
// ---------------------------------------------------------------------------------------------

test('S13 an editor outside the lead scope gets 404s on the lead, 403 with a sentence on manager verbs, and the screen says where the lead is', async ({ page }) => {
  const manager = await loginAs(page, 'manager');
  const { leadId } = await uploadAndWaitForLead(page, manager, `scn-${nonce}-editor.csv`, csv(cleanLines(), { rfq: `SCN-${nonce}-EDITOR` }));

  const editor = await loginAs(page, 'editor');
  for (const [method, route, body] of [
    ['get', `/api/Lead/${leadId}`, undefined],
    ['get', `/api/leads/${leadId}/decision-workbench`, undefined],
    ['put', `/api/Lead/${leadId}/review`, { action: 'approve', reason: 'x', expectedVersion: 1, items: [] }],
    ['post', `/api/commercial-cases/leads/${leadId}/transition`, { targetStatusCode: 'PENDING_IDENTIFICATION', expectedVersion: 1, idempotencyKey: 'scn-x' }],
  ] as const) {
    const response = await api(page, editor, method, route, body, { 'Idempotency-Key': `scn-${crypto.randomUUID()}` });
    expect(response.status(), `${method.toUpperCase()} ${route} → ${response.status()} ${await response.text()}`).toBe(404);
  }
  for (const [route, body] of [
    ['/api/commercial-intelligence/routing-queue/1/assign', { ownerUserId: 1, expectedVersion: 1, reason: 'scenario test' }],
    ['/api/commercial-routing/default-owner', { defaultOwnerUserId: 1 }],
  ] as const) {
    const response = await api(page, editor, route.endsWith('default-owner') ? 'put' : 'post', route, body, { 'Idempotency-Key': `scn-${crypto.randomUUID()}` });
    expect(response.status(), `${route} → ${response.status()}`).toBe(403);
    expect(await problemDetail(response)).toMatch(/permission/i);
  }
  const canUpload = await uploadRaw(page, editor, `scn-${nonce}-editor-own.csv`, csv(cleanLines([2, 2, 2]), { rfq: `SCN-${nonce}-EDITOROWN` }));
  expect(canUpload.status(), 'an editor may still bring documents in').toBe(202);

  await page.goto(`/procurement/leads/view/${leadId}`);
  await expect(page.getByText(/not in your view yet/i)).toBeVisible({ timeout: 20_000 });
  await expect(page.getByRole('button', { name: /open routing queue/i })).toBeVisible();
});

// ---------------------------------------------------------------------------------------------
// 14. The tenant's fallback owner: a lead nobody matches is still routed to a person.
// ---------------------------------------------------------------------------------------------

test('S14 with a fallback owner set, an unmatched upload is routed to them and the uploader can open it; cleared, it waits on the queue', async ({ page }) => {
  const token = await loginAs(page, 'manager');
  const before = await jsonOk<Record<string, any>>(await api(page, token, 'get', '/api/commercial-routing/default-owner'));
  expect(before.defaultOwnerUserId ?? null).toBeNull();
  expect(String(before.eligibilityReason)).toMatch(/no fallback owner is set/i);

  const ownerUserId = await eligibleOwnerUserId(page, token);
  const set = await api(page, token, 'put', '/api/commercial-routing/default-owner', { defaultOwnerUserId: ownerUserId });
  expect(set.ok(), await set.text()).toBeTruthy();
  const setBody = await set.json();
  expect(Number(setBody.defaultOwnerUserId)).toBe(ownerUserId);
  expect(setBody.isEligible, `the fallback owner is usable: ${setBody.eligibilityReason}`).toBe(true);

  try {
    const uploaded = await upload(page, token, `scn-${nonce}-fallback.csv`, csv([{ product: 'Fallback owner valve', qty: '3', part: knownParts()[0] }], { rfq: `SCN-${nonce}-FALLBACK`, buyer: 'Nobody We Know Ltd' }));
    let b = await waitForBatch(page, token, uploaded.batchId);
    const leadId = leadIdOf(b);
    // Routing runs after reconciliation and can trail it by a few seconds.
    for (let attempt = 0; attempt < 15 && !b.items.find((x) => Number(x.leadId) === leadId)?.assignedOpportunityOwner; attempt++) {
      await page.waitForTimeout(2_000);
      b = await batch(page, token, uploaded.batchId);
    }
    const item = b.items.find((x) => Number(x.leadId) === leadId)!;
    expect(String(item.assignedOpportunityOwner ?? ''), 'the batch names the owner the fallback gave it').toBe(String(setBody.name));
    // The uploader opens it directly: it has an owner, so it is in ordinary record scope.
    const direct = await api(page, token, 'get', `/api/Lead/${leadId}`);
    expect(direct.status(), `GET /api/Lead/${leadId} → ${direct.status()}`).toBe(200);
    const persisted = await direct.json();
    expect(String(persisted.assignmentReason)).toBe('DEFAULT_OWNER_ASSIGNED');
    const queue = await jsonOk<{ items: Array<Record<string, any>> }>(await api(page, token, 'get', '/api/commercial-routing/queue?pageNumber=1&pageSize=200'));
    expect(queue.items.some((x) => Number(x.leadId) === leadId), 'a routed lead is not also parked on the queue').toBe(false);
  } finally {
    const cleared = await api(page, token, 'put', '/api/commercial-routing/default-owner', { defaultOwnerUserId: null });
    expect(cleared.ok(), await cleared.text()).toBeTruthy();
  }

  const after = await upload(page, token, `scn-${nonce}-nofallback.csv`, csv([{ product: 'No fallback valve', qty: '3', part: knownParts()[0] }], { rfq: `SCN-${nonce}-NOFALLBACK`, buyer: 'Nobody We Know Ltd' }));
  const parked = await waitForBatch(page, token, after.batchId);
  const parkedLead = leadIdOf(parked);
  expect(parked.items.find((x) => Number(x.leadId) === parkedLead)!.assignedOpportunityOwner ?? null).toBeNull();
  const queue = await jsonOk<{ items: Array<Record<string, any>> }>(await api(page, token, 'get', '/api/commercial-routing/queue?pageNumber=1&pageSize=200'));
  expect(queue.items.some((x) => Number(x.leadId) === parkedLead), 'with no fallback the lead waits on the routing queue').toBe(true);
});
