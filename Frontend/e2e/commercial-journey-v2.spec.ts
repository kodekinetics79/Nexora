import fs from 'node:fs/promises';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { loginAs, required, requiredNumber } from './support/core-commercial';

const evidenceDir = path.resolve('../docs/nexora/evidence/commercial-journey-v2');

test('01 RFQ Command Workspace opens through the normal authenticated route', async ({ page }) => {
  await loginAs(page, 'manager');
  await page.goto(`/procurement/rfqs/view/${requiredNumber('E2E_CORE_RFQ_ID')}`);
  await expect(page.getByText(required('E2E_CORE_NEXORA_SERIAL'), { exact: false }).first()).toBeVisible();
  await expect(page.getByText(required('E2E_CORE_CUSTOMER_NAME'), { exact: false }).first()).toBeVisible();
  await expect(page.getByText(required('E2E_CORE_ACCOUNT_OWNER_NAME'), { exact: false }).first()).toBeVisible();
  await expect(page.getByText(required('E2E_CORE_OPPORTUNITY_OWNER_NAME'), { exact: false }).first()).toBeVisible();
});

test('02 RFQ summary cards filter persisted commercial lines', async ({ page }) => {
  await loginAs(page, 'manager');
  await page.goto(`/procurement/rfqs/view/${requiredNumber('E2E_CORE_RFQ_ID')}`);
  const total = page.getByRole('button', { name: /Total lines/i });
  const sourcing = page.getByRole('button', { name: /Sourcing required/i });
  await expect(total).toBeVisible();
  await expect(sourcing).toBeVisible();
  await sourcing.click();
  await expect(sourcing).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('row').filter({ hasText: /to source/i }).first()).toBeVisible();
  await total.click();
  await expect(page.getByRole('row')).toHaveCount(7);
});

test('03 RFQ line evidence opens without inventing unavailable provenance', async ({ page }) => {
  await loginAs(page, 'manager');
  await page.goto(`/procurement/rfqs/view/${requiredNumber('E2E_CORE_RFQ_ID')}`);
  await page.getByRole('button', { name: /Sourcing required/i }).click();
  await page.getByRole('button', { name: 'Evidence' }).first().click();
  await expect(page.getByText('Source evidence', { exact: true })).toBeVisible();
  await expect(page.getByText(/Open Canonical Lead to inspect document/i)).toBeVisible();
  await fs.mkdir(evidenceDir, { recursive: true });
  await page.screenshot({ path: path.join(evidenceDir, 'rfq-command-workspace.png'), fullPage: true });
});

test('04 RFQ Command Workspace remains usable on mobile', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 });
  await loginAs(page, 'manager');
  await page.goto(`/procurement/rfqs/view/${requiredNumber('E2E_CORE_RFQ_ID')}`);
  await expect(page.getByRole('button', { name: /Total lines/i })).toBeVisible();
  await expect(page.locator('body')).not.toHaveCSS('overflow-x', 'scroll');
});

test.afterEach(({}, testInfo) => {
  expect(testInfo.annotations.filter((annotation) => annotation.type === 'skip')).toHaveLength(0);
});
