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
  test('commercial taxonomy and document skill studio', async ({ page }) => {
    await login(page);
    await createAndPublish(page, '/admin/platform/taxonomy',
      'Commercial Taxonomy & Document Skills', 'CommercialTaxonomy', 'Wave 1 Customer RFQ Taxonomy');
    await page.screenshot({ path: `${evidenceDir}/01-taxonomy-studio.png`, fullPage: true });
  });

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

  test('AI trust and governance center', async ({ page }) => {
    await login(page);
    await page.goto('/admin/platform/ai-trust');
    await expect(page.getByRole('heading', { name: 'AI Trust & Governance' })).toBeVisible();
    await expect(page.getByText('Disabled', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Edit policy' }).click();
    await page.getByLabel('Change reason').fill('Authenticated Wave 1 policy verification.');
    await page.getByRole('button', { name: 'Save governed policy' }).click();
    await page.getByRole('tab', { name: 'Audit & rollback' }).click();
    await expect(page.getByText('POLICY_UPDATED', { exact: true }).first()).toBeVisible();
    await page.screenshot({ path: `${evidenceDir}/03-ai-trust-center.png`, fullPage: true });
  });

  test('model rule and dataset lifecycle studio', async ({ page }) => {
    await login(page);
    await createAndPublish(page, '/admin/platform/lifecycle',
      'Model, Rule & Dataset Lifecycle', 'Rule', 'Wave 1 Confidence Review Rule');
    await page.screenshot({ path: `${evidenceDir}/04-lifecycle-studio.png`, fullPage: true });
  });

  test('integration hub and connector SDK', async ({ page }) => {
    await login(page);
    await createAndPublish(page, '/admin/platform/integrations',
      'Integration Hub & Connector SDK', 'Connector', 'Wave 1 Sandbox REST Connector');
    await expect(page.getByText('Connector SDK v1.0')).toBeVisible();
    await page.screenshot({ path: `${evidenceDir}/05-integration-hub.png`, fullPage: true });
  });

  test('test simulation and release center', async ({ page }) => {
    await login(page);
    await page.goto('/admin/platform/releases');
    await expect(page.getByRole('heading', { name: 'Test, Simulation & Release Center' })).toBeVisible();
    await page.getByRole('button', { name: 'Create governed artifact' }).click();
    const dialog = page.getByRole('dialog', { name: 'Create governed artifact' });
    await dialog.getByRole('combobox').click();
    await page.getByRole('option', { name: 'TestSuite' }).click();
    await dialog.getByRole('textbox', { name: /^Name/ }).fill(`Wave 1 Contract Suite ${runId}`);
    await dialog.getByRole('textbox', { name: /^Stable key/ }).fill(`wave-1-contract-suite-${runId}`);
    await dialog.getByRole('button', { name: 'Create draft' }).click();
    await page.getByRole('button', { name: 'Run simulation' }).click();
    await expect(page.getByText(/Suite v1: 1\/1 tests passed/)).toBeVisible();
    await page.getByRole('button', { name: 'Send to test' }).click();
    await page.getByRole('button', { name: 'Publish' }).click();
    await expect(page.getByText('v1 · Production')).toBeVisible();
    await page.screenshot({ path: `${evidenceDir}/06-test-release-center.png`, fullPage: true });
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

  test('quality analytics center', async ({ page }) => {
    await login(page);
    await page.goto('/admin/platform/quality');
    await expect(page.getByRole('heading', { name: 'Quality Analytics Center' })).toBeVisible();
    await expect(page.getByText(/require an independently labeled evaluation corpus/)).toBeVisible();
    await expect(page.getByText(/Insufficient evidence/).first()).toBeVisible();
    await page.getByRole('tab', { name: 'Metric Definitions' }).click();
    await expect(page.getByRole('heading', { name: 'Quality Metric Definitions' })).toBeVisible();
    await page.getByRole('button', { name: 'Create governed artifact' }).click();
    const dialog = page.getByRole('dialog', { name: 'Create governed artifact' });
    await dialog.getByRole('textbox', { name: /^Name/ }).fill(`Wave 1 Quality Definition ${runId}`);
    await dialog.getByRole('textbox', { name: /^Stable key/ }).fill(`wave-1-quality-definition-${runId}`);
    await dialog.getByRole('button', { name: 'Create draft' }).click();
    await page.getByRole('button', { name: 'Send to test' }).click();
    await page.getByRole('button', { name: 'Publish' }).click();
    await expect(page.getByText('v1 · Production')).toBeVisible();
    await page.screenshot({ path: `${evidenceDir}/08-quality-analytics.png`, fullPage: true });
  });
});
