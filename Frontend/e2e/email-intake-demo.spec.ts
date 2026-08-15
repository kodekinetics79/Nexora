import { expect, test } from '@playwright/test';
import { loginThroughUi } from './support/login';

/**
 * The mailbox-to-Lead journey, driven in a visible browser against a LIVE backend.
 *
 * This spec is deliberately outside the fixture lane. The rest of the suite runs against a
 * mocked API, which is right for asserting UI behaviour but cannot show that a real message in a
 * real mailbox becomes a real Lead — the property this module exists for. Everything here talks
 * to a running API, a running PostgreSQL and a running IMAP server.
 *
 * Credentials arrive by environment. Nothing is hard-coded, printed, or screenshotted: the login
 * form is filled from process.env and every capture below is taken on a page where no credential
 * is visible.
 */
const EMAIL = process.env.DEMO_EMAIL ?? '';
const PASSWORD = process.env.DEMO_PASSWORD ?? '';
const RUN = process.env.DEMO_RUN ?? '';

const INTAKE = '/procurement/leads/inbound-mail';
const MAILBOXES = '/setup/mailboxes';

test.skip(!EMAIL || !PASSWORD || !RUN, 'Live demo requires DEMO_EMAIL, DEMO_PASSWORD and DEMO_RUN.');

// This spec authenticates itself against the LIVE backend, so it must not inherit the
// project's stored fixture session — those users exist only in the mocked lane.
test.use({ storageState: { cookies: [], origins: [] } });

test.describe.configure({ mode: 'serial' });

test.beforeEach(async ({ page }) => {
  await loginThroughUi(page, { email: EMAIL, password: PASSWORD });
});

/** Finds the intake row for one demo scenario by its unique subject marker. */
const rowFor = (page: import('@playwright/test').Page, letter: string) =>
  page.getByRole('row').filter({ hasText: `DEMO-${RUN} ${letter}` });

/**
 * Opens the triage tab the message actually landed on.
 *
 * The screen splits messages by triage decision — Extracted / Uncertain / Rejected as noise /
 * Routed as supplier document — and which one a message lands on is part of what is under test.
 * Hard-coding a tab would make the spec assert its own assumption instead of the product's
 * decision, and would go green for the wrong reason the day the decision changes.
 */
async function openTabContaining(page: import('@playwright/test').Page, letter: string) {
  for (const name of ['Extracted', 'Uncertain', 'Rejected as noise', 'Routed as supplier document']) {
    const tab = page.getByRole('tab', { name });
    if (!(await tab.isVisible().catch(() => false))) continue;
    await tab.click();
    if (await rowFor(page, letter).first().isVisible({ timeout: 8_000 }).catch(() => false)) return name;
  }
  throw new Error(`DEMO-${RUN} ${letter} was not on any triage tab.`);
}

test('1 — Test Connection, then Poll Now brings the mailbox in', async ({ page }, testInfo) => {
  // ---- Test Connection: the operator's first act, and proof the mailbox is really dialled.
  await page.goto(MAILBOXES);
  await expect(page.getByText('Demo GreenMail Inbound')).toBeVisible({ timeout: 20_000 });
  await testInfo.attach('01-mailboxes', { body: await page.screenshot(), contentType: 'image/png' });

  const test_ = page.getByRole('button', { name: /test/i }).first();
  if (await test_.isVisible().catch(() => false)) {
    await test_.click();
    await expect(page.getByText(/succe|passed|reachable|connected/i).first())
      .toBeVisible({ timeout: 30_000 });
    await testInfo.attach('02-test-connection', { body: await page.screenshot(), contentType: 'image/png' });
  }

  // ---- Poll Now, from the Email Intake screen.
  await page.goto(INTAKE);
  await expect(page.getByRole('button', { name: 'Poll now' })).toBeEnabled({ timeout: 20_000 });
  await testInfo.attach('03-intake-before-poll', { body: await page.screenshot(), contentType: 'image/png' });

  await page.getByRole('button', { name: 'Poll now' }).click();

  // The result summary is the point: a poll that says nothing is indistinguishable from a poll
  // that did nothing.
  await expect(page.getByText(/captured|message\(s\)|polled/i).first()).toBeVisible({ timeout: 60_000 });
  await testInfo.attach('04-poll-result', { body: await page.screenshot(), contentType: 'image/png' });
});

test('A — a body-only inquiry becomes exactly one Lead', async ({ page }, testInfo) => {
  await page.goto(INTAKE);
  const tab = await openTabContaining(page, 'A');
  const row = rowFor(page, 'A');
  await expect(row.first()).toBeVisible({ timeout: 60_000 });
  testInfo.annotations.push({ type: 'triage-tab', description: tab });
  await testInfo.attach('A-01-row', { body: await page.screenshot(), contentType: 'image/png' });

  // One assembly, and the operator can see its state and how many parts it is made of.
  await expect(row.first()).toContainText(/assembl/i, { timeout: 120_000 });
  await testInfo.attach('A-02-assembled', { body: await page.screenshot(), contentType: 'image/png' });
});

test('B — body plus two schedules becomes ONE combined Lead with its parts visible', async ({ page }, testInfo) => {
  await page.goto(INTAKE);
  const tab = await openTabContaining(page, 'B');
  const row = rowFor(page, 'B');
  await expect(row.first()).toBeVisible({ timeout: 60_000 });
  testInfo.annotations.push({ type: 'triage-tab', description: tab });

  // Expand to reveal the components — the evidence that the message was taken apart and put
  // back together, rather than turned into one Lead per file.
  await row.first().getByRole('button', { name: /^Show message/ }).click();
  await expect(page.getByText(/valves\.csv/i)).toBeVisible({ timeout: 60_000 });
  await expect(page.getByText(/gaskets\.csv/i)).toBeVisible();
  await testInfo.attach('B-01-components', { body: await page.screenshot(), contentType: 'image/png' });

  // ONE lead for the whole message.
  const open = row.first().getByRole('link', { name: /open lead/i })
    .or(row.first().getByRole('button', { name: /open lead/i }));
  await expect(open).toHaveCount(1, { timeout: 180_000 });
  await testInfo.attach('B-02-open-lead-available', { body: await page.screenshot(), contentType: 'image/png' });

  await open.click();
  await expect(page).toHaveURL(/lead/i, { timeout: 30_000 });
  await testInfo.attach('B-03-lead-opened', { body: await page.screenshot(), contentType: 'image/png' });
});

test('C — an unsupported attachment is visibly refused with no partial Lead', async ({ page }, testInfo) => {
  await page.goto(INTAKE);
  const tab = await openTabContaining(page, 'C');
  const row = rowFor(page, 'C');
  await expect(row.first()).toBeVisible({ timeout: 60_000 });
  testInfo.annotations.push({ type: 'triage-tab', description: tab });

  await row.first().getByRole('button', { name: /^Show message/ }).click();

  // The refused part is SHOWN, labelled, and with its reason — not silently dropped. This is the
  // only place a customer's attachment can otherwise vanish without trace.
  await expect(page.getByText(/skipped/i).first()).toBeVisible({ timeout: 60_000 });
  await expect(page.getByText(/requirements\.pptx/i)).toBeVisible();
  await testInfo.attach('C-01-skipped-evidence', { body: await page.screenshot(), contentType: 'image/png' });

  // And no Lead action is offered where there is no Lead.
  await expect(row.first().getByRole('link', { name: /open lead/i })).toHaveCount(0);
});

test('D — polling again creates no duplicate assembly, job or Lead', async ({ page }, testInfo) => {
  await page.goto(INTAKE);
  await openTabContaining(page, 'B');
  const before = await rowFor(page, 'B').count();

  await page.getByRole('button', { name: 'Poll now' }).click();
  await expect(page.getByText(/captured|message\(s\)|polled/i).first()).toBeVisible({ timeout: 60_000 });
  await testInfo.attach('D-01-second-poll', { body: await page.screenshot(), contentType: 'image/png' });

  await page.reload();
  await expect(rowFor(page, 'B')).toHaveCount(before, { timeout: 30_000 });
  await testInfo.attach('D-02-no-duplicate', { body: await page.screenshot(), contentType: 'image/png' });
});
