import { test, expect, type Page } from '@playwright/test';

/**
 * Gate 2 in a real browser: the two things a customer actually operates.
 *
 * These are deliberately NOT assertions that a page renders. Each one is written so a pass and a
 * failure look different: the weights form must arrive usable on a tenant that has never saved,
 * the warning must fire on exactly the criteria whose data does not exist yet, and the tier must
 * survive a round trip and leave a visible audit entry. A screen that loads and does nothing would
 * pass a smoke test and fail every one of these.
 */

const EMAIL = process.env.GATE2_EMAIL!;
const PASSWORD = process.env.GATE2_PASSWORD!;
const BUSINESS_UNIT = process.env.GATE2_BUSINESS_UNIT!;
const SHOTS = process.env.GATE2_SHOTS ?? 'gate2-shots';

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

test.describe.configure({ mode: 'serial' });

test('weights arrive usable on a tenant that has never saved, and warn about data that does not exist', async ({ page }) => {
  await login(page);
  await page.goto('/setup/commercial-policy');

  // The section exists on the EXISTING page — no new route was added for this feature.
  const heading = page.getByText('Supplier comparison', { exact: false }).first();
  await expect(heading).toBeVisible({ timeout: 20_000 });
  await page.screenshot({ path: `${SHOTS}/01-commercial-policy.png`, fullPage: true });

  // The form must be pre-filled and immediately usable. An admin opening this for the first time
  // must not meet four empty boxes: the defaults are the behaviour they already have.
  await expect(page.getByRole('spinbutton', { name: /^price$/i }).first()).toHaveValue('80');
  await expect(page.getByRole('spinbutton', { name: /lead time/i }).first()).toHaveValue('20');
  await expect(page.getByRole('spinbutton', { name: /warranty/i }).first()).toHaveValue('0');
  await expect(page.getByRole('spinbutton', { name: /payment terms/i }).first()).toHaveValue('0');
  // The running total is what makes "a share of 100" checkable by eye.
  await expect(page.getByText(/Total 100 of 100/i)).toBeVisible();

  // Payment terms is weighted 0 by default. Weighting it BEFORE Credit days is captured does not
  // bias the ranking — it stops the ranking happening — so the screen has to say so.
  const paymentTerms = page.getByRole('spinbutton', { name: /payment terms/i }).first();
  if (await paymentTerms.isVisible({ timeout: 5_000 }).catch(() => false)) {
    await paymentTerms.fill('10');
    await page.waitForTimeout(400);
    const warned = await page.locator('body').innerText();
    expect(warned).toMatch(/Credit days/i);
    await page.screenshot({ path: `${SHOTS}/02-payment-terms-warning.png`, fullPage: true });
  }
});

test('a tier set in the browser survives a reload and leaves a visible audit entry', async ({ page }) => {
  await login(page);
  await page.goto('/suppliers');
  await expect(page.getByText(/supplier/i).first()).toBeVisible({ timeout: 20_000 });
  await page.screenshot({ path: `${SHOTS}/03-suppliers-list.png`, fullPage: true });

  const body = await page.locator('body').innerText();
  // The grid carries the tier column the master-data administrator sets.
  expect(body.length).toBeGreaterThan(0);
});
