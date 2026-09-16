import { expect, test, type Page } from '@playwright/test';
import { loginThroughUi } from './support/login';
import { requireEnv } from './support/environment';

const apiUrl = process.env.E2E_API_URL || 'http://127.0.0.1:5192';
const email = process.env.E2E_MANAGER_EMAIL || 'robert@example.com';
const evidenceDir = '../docs/nexora/evidence/wave-01-platform-parity';
const runId = Date.now().toString(36);

async function login(page: Page): Promise<string> {
  // Resolved here, not at module scope — see requireEnv. Same failure, without destroying
  // discovery for every other spec in the suite.
  const { E2E_MANAGER_PASSWORD: password } = requireEnv('Wave 1 browser acceptance', 'E2E_MANAGER_PASSWORD');
  await loginThroughUi(page, { email, password });
  const token = await page.evaluate(() => localStorage.getItem('token'));
  if (!token) throw new Error('The authenticated browser session has no access token.');
  return token;
}

async function createAndPublish(
  page: Page,
  route: string,
  heading: string,
  type: string,
  name: string,
): Promise<void> {
  const uniqueName = `${name} ${runId}`;
  await page.goto(route);
  await expect(page.getByRole('heading', { name: heading })).toBeVisible();
  await page.getByRole('button', { name: 'Create governed artifact' }).click();
  const dialog = page.getByRole('dialog', { name: 'Create governed artifact' });
  await dialog.getByRole('combobox').click();
  await page.getByRole('option', { name: type }).click();
  await dialog.getByRole('textbox', { name: /^Name/ }).fill(uniqueName);
  await dialog.getByRole('textbox', { name: /^Stable key/ }).fill(`${name.toLowerCase().replaceAll(' ', '-')}-${runId}`);
  await dialog.getByRole('textbox', { name: 'Description' }).fill('Wave 1 authenticated acceptance evidence.');
  await dialog.getByRole('button', { name: 'Create draft' }).click();
  await expect(page.getByText(uniqueName, { exact: true }).first()).toBeVisible();
  await expect(page.getByRole('button', { name: 'Send to test' })).toBeVisible();
  await page.getByRole('button', { name: 'Send to test' }).click();
  await expect(page.getByRole('button', { name: 'Publish' })).toBeVisible();
  await page.getByRole('button', { name: 'Publish' }).click();
  await expect(page.getByText('v1 · Production')).toBeVisible();
}

test.describe.serial('Wave 1 enterprise platform parity', () => {
  test('human action and exception center', async ({ page }) => {
    const token = await login(page);
    const actionTitle = `Review uncertain customer reference ${runId}`;
    const response = await page.request.post(`${apiUrl}/api/platform-governance/actions`, {
      headers: { Authorization: `Bearer ${token}`, 'Idempotency-Key': crypto.randomUUID() },
      data: {
        actionType: 'COMMERCIAL_REVIEW', sourceType: 'Lead', sourceReference: 'WAVE1-SIT-001',
        title: actionTitle, summary: 'A commercial identity decision requires a human.',
        recommendation: 'Confirm the customer reference before workflow resume.', evidenceJson: '{"source":"authenticated-sit"}',
        confidence: 0.72, commercialImpact: 'Prevents an incorrect RFQ association.',
        resumeActionCode: 'RESUME_LEAD_RECONCILIATION', priority: 'High',
        assignedToUserId: null, dueOn: new Date(Date.now() + 3_600_000).toISOString(),
      },
    });
    expect(response.ok(), await response.text()).toBeTruthy();
    await page.goto('/sales/actions');
    await expect(page.getByRole('heading', { name: 'Human Action Center' })).toBeVisible();
    await expect(page.getByText(actionTitle, { exact: true })).toBeVisible();
    await page.getByLabel(`Select ${actionTitle}`).check();
    await page.getByRole('button', { name: 'Decide selected' }).click();
    await page.getByLabel('Decision comment').fill('Customer reference verified in authenticated acceptance.');
    await page.getByRole('button', { name: 'Record decision' }).click();
    await expect(page.getByText('Completed', { exact: true }).first()).toBeVisible();
    await page.screenshot({ path: `${evidenceDir}/02-human-action-center.png`, fullPage: true });
  });

  test('integration hub and connector SDK', async ({ page }) => {
    await login(page);
    await createAndPublish(page, '/admin/platform/integrations',
      'Integration Hub & Connector SDK', 'Connector', 'Wave 1 Sandbox REST Connector');
    await expect(page.getByText('Connector SDK v1.0')).toBeVisible();
    await page.screenshot({ path: `${evidenceDir}/05-integration-hub.png`, fullPage: true });
  });

  test('commercial document archive and search', async ({ page }) => {
    await login(page);
    await page.goto('/admin/platform/archive');
    await expect(page.getByRole('heading', { name: 'Commercial Document Archive' })).toBeVisible();
    await expect(page.getByText(/Tenant metadata, filenames, immutable hashes/)).toBeVisible();
    await page.getByRole('tab', { name: 'Retention Policies' }).click();
    await expect(page.getByRole('heading', { name: 'Retention & Legal Hold Policies' })).toBeVisible();
    await page.getByRole('button', { name: 'Create governed artifact' }).click();
    const dialog = page.getByRole('dialog', { name: 'Create governed artifact' });
    await dialog.getByRole('textbox', { name: /^Name/ }).fill(`Wave 1 Evidence Retention Policy ${runId}`);
    await dialog.getByRole('textbox', { name: /^Stable key/ }).fill(`wave-1-evidence-retention-policy-${runId}`);
    await page.getByRole('button', { name: 'Create draft' }).click();
    await page.getByRole('button', { name: 'Send to test' }).click();
    await page.getByRole('button', { name: 'Publish' }).click();
    await expect(page.getByText('v1 · Production')).toBeVisible();
    await page.screenshot({ path: `${evidenceDir}/07-document-archive.png`, fullPage: true });
  });

  test('platform raw materials are not tenant screens', async ({ page }) => {
    // Owner decision 2026-09-16: model policy, taxonomy and document skills, model lifecycle,
    // quality thresholds and release control are configured at Platform Admin level. A tenant
    // manager who types the old address gets the not-found page, not the studio.
    await login(page);
    for (const route of ['/admin/platform/ai-trust', '/admin/platform/taxonomy', '/admin/platform/lifecycle', '/admin/platform/quality', '/admin/platform/releases']) {
      await page.goto(route);
      await expect(page.getByRole('heading', { level: 1 })).toHaveText('Page not found');
      await expect(page.getByText(/ollama|egress|model policy|token limit/i)).toHaveCount(0);
    }
    await page.screenshot({ path: `${evidenceDir}/06-platform-screens-not-for-tenants.png`, fullPage: true });
  });
});
