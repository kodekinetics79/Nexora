import { expect, test } from '@playwright/test';
import { authStatePath, credentialsFor, fixture, missingFixtureValues } from './support/environment';

test('authenticated denied role sees a 403 surface and is not logged out', async ({ browser, baseURL }) => {
  expect(credentialsFor('denied')).not.toBeNull();
  const context = await browser.newContext({ baseURL, storageState: authStatePath('denied') });
  const page = await context.newPage();
  await page.goto('/procurement/leads/all');
  await expect(page.getByRole('heading', { name: 'Access Denied' })).toBeVisible();
  await expect(page).not.toHaveURL(/\/login/);
  await context.close();

  const editorContext = await browser.newContext({ baseURL, storageState: authStatePath('editor') });
  const editorPage = await editorContext.newPage();
  await editorPage.goto('/procurement/leads/all');
  await expect(editorPage.getByRole('heading', { name: 'Access Denied' })).toHaveCount(0);
  await expect(editorPage).not.toHaveURL(/\/login/);
  await editorContext.close();
});

test('Lead, RFQ and Quote retain one Nexora Serial', async ({ page }) => {
  expect(credentialsFor('manager')).not.toBeNull();
  const missing = missingFixtureValues(
    ['E2E_LEAD_ID', fixture.leadId], ['E2E_RFQ_ID', fixture.rfqId],
    ['E2E_QUOTE_ID', fixture.quoteId], ['E2E_NEXORA_SERIAL', fixture.nexoraSerial],
    ['E2E_CUSTOMER_NAME', fixture.customerName],
  );
  expect(missing, `Missing ${missing.join(', ')}.`).toEqual([]);

  for (const route of [
    `/procurement/leads/view/${fixture.leadId}`,
    `/procurement/rfqs/view/${fixture.rfqId}`,
    `/sales/quotes/view/${fixture.quoteId}`,
  ]) {
    await page.goto(route);
    await expect(page.getByText(`Nexora Serial: ${fixture.nexoraSerial}`)).toBeVisible();
    await expect(page.getByText(fixture.customerName!, { exact: true }).first()).toBeVisible();
  }
});
