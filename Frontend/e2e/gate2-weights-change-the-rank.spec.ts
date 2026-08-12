import { test, expect, type Page } from '@playwright/test';

/**
 * Gate 2, the one claim that matters: changing the supplier comparison weights changes which
 * supplier the product recommends, and a person can SEE the ranking change on screen.
 *
 * A weight a customer can edit that does not move the recommendation is decoration. So this spec
 * refuses to assert "the table rendered". It reads the supplier order out of the comparison table
 * before and after the weight change and fails unless BOTH the recommended supplier and the row
 * order actually moved. The weights are changed the way a customer changes them — in the browser,
 * on Setup > Commercial Policy, with a reason — not through the API.
 */

const EMAIL = process.env.GATE2_EMAIL!;
const PASSWORD = process.env.GATE2_PASSWORD!;
const BUSINESS_UNIT = process.env.GATE2_BUSINESS_UNIT!;
const SHOTS = process.env.GATE2_SHOTS ?? 'gate2-shots';
const RFQ_ID = process.env.RANK_RFQ_ID!;

/** Supplier names as created by the scenario builder, keyed by role in the trade-off. */
const CHEAP_SLOW = process.env.RANK_SUPPLIER_CHEAP!;
const DEAR_FAST = process.env.RANK_SUPPLIER_FAST!;
const NO_WARRANTY = process.env.RANK_SUPPLIER_NOWARRANTY!;

async function login(page: Page): Promise<void> {
  await page.goto('/login');
  await page.evaluate(() => {
    localStorage.removeItem('token');
    localStorage.removeItem('userData');
  });
  await page.getByLabel('Email Address').fill(EMAIL);
  await page.getByRole('textbox', { name: 'Password', exact: true }).fill(PASSWORD);
  await page.getByRole('button', { name: 'LOGIN' }).click();
  const cont = page.getByRole('button', { name: 'CONTINUE' });
  if (await cont.isVisible({ timeout: 3_000 }).catch(() => false)) {
    await page.getByRole('combobox').selectOption(BUSINESS_UNIT);
    await cont.click();
  }
  await expect(page).not.toHaveURL(/\/login(?:\?|$)/, { timeout: 25_000 });
  await expect
    .poll(() => page.evaluate(() => Boolean(localStorage.getItem('token'))), { timeout: 25_000 })
    .toBe(true);
}

/** The comparison table is the one whose header carries "Weighted score". */
function comparisonTable(page: Page) {
  return page.locator('table').filter({ has: page.getByRole('columnheader', { name: 'Weighted score' }) });
}

/** The workbench opens on Coverage; the comparison lives behind the Supplier offers tab. */
async function openComparisonTab(page: Page): Promise<void> {
  const tab = page.getByRole('tab', { name: /supplier offers/i });
  await expect(tab).toBeVisible({ timeout: 30_000 });
  await tab.click();
}

/**
 * The supplier names in the order the comparison table actually paints them, plus the scores
 * beside them. Read from the DOM, so a table that renders offers in raw API order and never
 * applies the ranking cannot pass this spec.
 */
async function readRanking(page: Page): Promise<{ order: string[]; scores: string[]; recommended: string }> {
  const table = comparisonTable(page);
  await expect(table).toBeVisible({ timeout: 30_000 });
  const rows = table.locator('tbody tr');
  await expect.poll(async () => rows.count(), { timeout: 30_000 }).toBeGreaterThan(1);

  const order: string[] = [];
  const scores: string[] = [];
  let recommended = '';
  for (let index = 0; index < (await rows.count()); index += 1) {
    const row = rows.nth(index);
    const text = await row.innerText();
    const name = [CHEAP_SLOW, DEAR_FAST, NO_WARRANTY].find((candidate) => text.includes(candidate));
    if (!name) continue;
    order.push(name);
    scores.push((await row.locator('td').nth(10).innerText()).replace(/\s+/g, ' ').trim());
    // The recommendation is the row carrying the "Best weighted score …" chip the product paints.
    if (/Best weighted score/i.test(text)) recommended = name;
  }
  return { order, scores, recommended };
}

/** Changes the weights the way a customer does: preset radio, mandatory reason, Save policy. */
async function setWeightsInBrowser(page: Page, presetLabel: string, reason: string): Promise<void> {
  await page.goto('/setup/commercial-policy');
  await expect(page.getByText('Supplier comparison', { exact: false }).first()).toBeVisible({ timeout: 25_000 });
  await page.getByRole('radio', { name: new RegExp(presetLabel, 'i') }).check();
  await page.getByLabel('Reason for this change').fill(reason);
  const save = page.getByRole('button', { name: /save policy/i });
  // Save stays disabled when nothing changed — the page refuses a no-op write rather than
  // manufacturing an audit entry. That is correct, and it is not a failure of this spec.
  if (!(await save.isEnabled())) return;
  await save.click();
  await expect(page.getByText(/saved|updated/i).first()).toBeVisible({ timeout: 20_000 });
}

test.describe.configure({ mode: 'serial' });

test('changing the comparison weights re-ranks the suppliers on screen', async ({ page }) => {
  await login(page);

  // ── Weight set 1: price-led (80 price / 20 lead time) ───────────────────────────────────────
  await setWeightsInBrowser(page, 'Balanced', 'Price-led ranking for the Gate 2 demonstration');
  await page.goto(`/procurement/rfqs/${RFQ_ID}/sourcing`);
  await openComparisonTab(page);
  const before = await readRanking(page);
  await comparisonTable(page).scrollIntoViewIfNeeded();
  await page.screenshot({ path: `${SHOTS}/10-rank-before.png`, fullPage: true });
  console.log('BEFORE order:', before.order, 'scores:', before.scores, 'recommended:', before.recommended);

  expect(before.order.length).toBe(3);
  expect(before.recommended).toBe(CHEAP_SLOW);
  expect(before.order[0]).toBe(CHEAP_SLOW);

  // ── Weight set 2: lead-time-led (40 price / 60 lead time) ───────────────────────────────────
  await setWeightsInBrowser(
    page,
    'Speed matters',
    'Our customer awards on delivery date this quarter, so lead time now outweighs price',
  );
  await page.goto(`/procurement/rfqs/${RFQ_ID}/sourcing`);
  await openComparisonTab(page);
  const after = await readRanking(page);
  await comparisonTable(page).scrollIntoViewIfNeeded();
  await page.screenshot({ path: `${SHOTS}/11-rank-after.png`, fullPage: true });
  console.log('AFTER  order:', after.order, 'scores:', after.scores, 'recommended:', after.recommended);

  // The claim, stated as the two things a person can see.
  expect(after.recommended).toBe(DEAR_FAST);
  expect(after.recommended).not.toBe(before.recommended);
  expect(after.order[0]).toBe(DEAR_FAST);
  expect(after.order).not.toEqual(before.order);
  expect(after.scores).not.toEqual(before.scores);
});

test('an offer that cannot be scored says why and is still awardable', async ({ page }) => {
  await login(page);

  // Weighting warranty makes the offer that never captured a warranty period unscorable — and the
  // product must say so rather than score it zero, while leaving it fully awardable.
  await page.goto('/setup/commercial-policy');
  await expect(page.getByText('Supplier comparison', { exact: false }).first()).toBeVisible({ timeout: 25_000 });
  await page.getByRole('spinbutton', { name: /^price$/i }).first().fill('40');
  await page.getByRole('spinbutton', { name: /lead time/i }).first().fill('40');
  await page.getByRole('spinbutton', { name: /warranty/i }).first().fill('20');
  await page.getByRole('spinbutton', { name: /payment terms/i }).first().fill('0');
  await expect(page.getByText(/Total 100 of 100/i)).toBeVisible();
  await page.getByLabel('Reason for this change').fill('Weight warranty so offers with no captured warranty are visible');
  const save = page.getByRole('button', { name: /save policy/i });
  await expect(save).toBeEnabled({ timeout: 10_000 });
  await save.click();
  await expect(page.getByText(/saved|updated/i).first()).toBeVisible({ timeout: 20_000 });

  await page.goto(`/procurement/rfqs/${RFQ_ID}/sourcing`);
  await openComparisonTab(page);
  const table = comparisonTable(page);
  await expect(table).toBeVisible({ timeout: 30_000 });
  const unscorable = table.locator('tbody tr').filter({ hasText: NO_WARRANTY });
  await expect(unscorable).toContainText(/cannot score/i, { timeout: 20_000 });
  await expect(unscorable).toContainText(/warranty/i);
  // Still awardable: the score annotates, the human awards.
  await expect(unscorable.getByRole('button', { name: /^(approve|award more)$/i }).first()).toBeEnabled();
  await table.scrollIntoViewIfNeeded();
  await page.screenshot({ path: `${SHOTS}/12-cannot-score.png`, fullPage: true });
});
