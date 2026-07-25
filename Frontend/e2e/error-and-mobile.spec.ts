import { expect, test } from '@playwright/test';
import { credentialsFor, fixture } from './support/environment';

test.beforeEach(() => {
  expect(credentialsFor('manager')).not.toBeNull();
});

for (const scenario of [
  { status: 403, message: 'You do not have permission' },
  { status: 404, message: 'was not found for your organization' },
  { status: 409, message: 'changed while it was being reviewed' },
  { status: 500, message: 'reconciliation service is unavailable' },
]) {
  test(`batch reports ${scenario.status} without calling it queue latency`, async ({ page }) => {
    await page.route('**/api/LeadIngestion/batches/error-state', (route) =>
      route.fulfill({ status: scenario.status, contentType: 'application/json', body: '{}' }));
    await page.goto('/procurement/leads/ingestion/error-state');
    await expect(page.getByText(scenario.message, { exact: false })).toBeVisible();
    await expect(page.getByText(/not available yet/i)).toHaveCount(0);
  });
}

test('dashboard, lead list and detail do not overflow the configured viewport', async ({ page }) => {
  expect(fixture.leadId).toBeTruthy();
  for (const route of ['/dashboard', '/procurement/leads/all', `/procurement/leads/view/${fixture.leadId}`]) {
    await page.goto(route);
    await page.waitForLoadState('networkidle');
    const dimensions = await page.evaluate(() => ({
      documentWidth: document.documentElement.scrollWidth,
      viewportWidth: window.innerWidth,
    }));
    expect(dimensions.documentWidth, `${route} has horizontal document overflow`).toBeLessThanOrEqual(dimensions.viewportWidth);
  }
});
