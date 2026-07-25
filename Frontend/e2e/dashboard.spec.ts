import { expect, test } from '@playwright/test';
import { credentialsFor, fixture } from './support/environment';

test.beforeEach(async ({ page }) => {
  expect(credentialsFor('manager')).not.toBeNull();
  await page.goto('/dashboard');
});

test('dashboard reconciles the configured KPI with its drill-down records', async ({ page }) => {
  expect(fixture.dashboardKpiValue).toBeTruthy();
  expect(fixture.dashboardDrillDownCount).toBeTruthy();
  const card = page.getByRole('article').filter({ hasText: fixture.dashboardKpiLabel }).first();
  await expect(card).toBeVisible();
  await expect(card).toContainText(fixture.dashboardKpiValue!);

  const drillDown = card.getByRole('button', { name: /View \d+ records?/ });
  await drillDown.click();
  const expectedCount = Number(fixture.dashboardDrillDownCount);
  expect(Number.isInteger(expectedCount) && expectedCount >= 0).toBeTruthy();
  await expect(page.getByRole('dialog').locator('ul > *')).toHaveCount(expectedCount);
});

test('dashboard explains every insufficient-data KPI', async ({ page }) => {
  await expect(page.getByText('Insufficient data', { exact: true }).first()).toBeVisible();
  const insufficientCards = page.getByRole('article').filter({ hasText: 'Insufficient data' });
  const count = await insufficientCards.count();
  expect(count).toBeGreaterThan(0);
  for (let index = 0; index < count; index += 1) {
    const text = (await insufficientCards.nth(index).innerText()).trim();
    expect(text.split('\n').filter(Boolean).length).toBeGreaterThan(3);
  }
});
