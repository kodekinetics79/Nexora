import { defineConfig, devices } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import contract from './e2e/support/fixture-contract.json' with { type: 'json' };

const fixtureMode = process.env.E2E_FIXTURE_MODE !== 'false';
// Worker processes reload this config without the original --project argument. The dedicated
// credentials therefore remain the durable lane marker after discovery hands work to a worker.
const visibleGoogleChromeRequested = Boolean(
  process.env.E2E_PLATFORM_ADMIN_EMAIL && process.env.E2E_PLATFORM_ADMIN_PASSWORD,
) || process.argv.some((argument) =>
  argument === 'visible-google-chrome' || argument.endsWith('=visible-google-chrome'));
const baseURL = process.env.E2E_BASE_URL || contract.baseUrl;
const apiURL = process.env.E2E_API_URL || contract.apiUrl;
const webPort = new URL(baseURL).port || (new URL(baseURL).protocol === 'https:' ? '443' : '80');
// Visible certification is deliberately attached to an already-running integration
// environment. Starting the fixture API here would make a watched run look real while
// silently replacing PostgreSQL and backend calls with canned responses.
const startWebServer = process.env.E2E_SKIP_WEB_SERVER !== 'true' && !visibleGoogleChromeRequested;
const authDir = 'node_modules/.cache/nexora-e2e';

if (!fixtureMode || process.env.E2E_ACCEPTANCE_MODE === 'true') {
  const required = (visibleGoogleChromeRequested
    ? [
        'E2E_BASE_URL', 'E2E_API_URL', 'E2E_PLATFORM_ADMIN_EMAIL', 'E2E_PLATFORM_ADMIN_PASSWORD',
        'E2E_PLATFORM_ADMIN_TOTP_SECRET',
      ]
    : [
        'E2E_BASE_URL',
        'E2E_MANAGER_EMAIL', 'E2E_MANAGER_PASSWORD', 'E2E_MANAGER_BUSINESS_UNIT_ID',
        'E2E_EDITOR_EMAIL', 'E2E_EDITOR_PASSWORD', 'E2E_EDITOR_BUSINESS_UNIT_ID',
        'E2E_DENIED_EMAIL', 'E2E_DENIED_PASSWORD', 'E2E_DENIED_BUSINESS_UNIT_ID',
        'E2E_UPLOAD_FILE', 'E2E_BATCH_ID', 'E2E_LEAD_ID', 'E2E_REVISION_LEAD_ID',
        'E2E_RFQ_ID', 'E2E_QUOTE_ID', 'E2E_NEXORA_SERIAL', 'E2E_CUSTOMER_NAME',
        'E2E_DASHBOARD_KPI_LABEL', 'E2E_DASHBOARD_KPI_VALUE',
        'E2E_DASHBOARD_DRILLDOWN_COUNT', 'E2E_EXPECTED_NEW_LEADS',
        'E2E_EXPECTED_EXACT_DUPLICATES', 'E2E_EXPECTED_REVISIONS',
        'E2E_EXPECTED_POSSIBLE_MATCHES',
      ]).filter((name) => !process.env[name]);
  if (required.length > 0) {
    throw new Error(`Release 01B browser acceptance configuration is incomplete: ${required.join(', ')}`);
  }
}

const uploadFile = process.env.E2E_UPLOAD_FILE || path.resolve('e2e/fixtures/release-01c-inquiry.csv');
if (!fs.existsSync(uploadFile) || !fs.statSync(uploadFile).isFile()) {
  throw new Error(`Browser acceptance upload fixture does not exist: ${uploadFile}`);
}

const resolvedValues = {
  E2E_MANAGER_EMAIL: process.env.E2E_MANAGER_EMAIL || contract.manager.email,
  E2E_MANAGER_PASSWORD: process.env.E2E_MANAGER_PASSWORD || contract.manager.password,
  E2E_EDITOR_EMAIL: process.env.E2E_EDITOR_EMAIL || contract.editor.email,
  E2E_EDITOR_PASSWORD: process.env.E2E_EDITOR_PASSWORD || contract.editor.password,
  E2E_DENIED_EMAIL: process.env.E2E_DENIED_EMAIL || contract.denied.email,
  E2E_DENIED_PASSWORD: process.env.E2E_DENIED_PASSWORD || contract.denied.password,
  E2E_BATCH_ID: process.env.E2E_BATCH_ID || contract.batchId,
  E2E_LEAD_ID: process.env.E2E_LEAD_ID || contract.leadId,
  E2E_REVISION_LEAD_ID: process.env.E2E_REVISION_LEAD_ID || contract.revisionLeadId,
  E2E_RFQ_ID: process.env.E2E_RFQ_ID || contract.rfqId,
  E2E_QUOTE_ID: process.env.E2E_QUOTE_ID || contract.quoteId,
  E2E_NEXORA_SERIAL: process.env.E2E_NEXORA_SERIAL || contract.nexoraSerial,
  E2E_CUSTOMER_NAME: process.env.E2E_CUSTOMER_NAME || contract.customerName,
  E2E_DASHBOARD_KPI_LABEL: process.env.E2E_DASHBOARD_KPI_LABEL || contract.dashboardKpiLabel,
  E2E_DASHBOARD_KPI_VALUE: process.env.E2E_DASHBOARD_KPI_VALUE || contract.dashboardKpiValue,
  E2E_DASHBOARD_DRILLDOWN_COUNT: process.env.E2E_DASHBOARD_DRILLDOWN_COUNT || contract.dashboardDrillDownCount,
  E2E_EXPECTED_NEW_LEADS: process.env.E2E_EXPECTED_NEW_LEADS || contract.expectedNewLeads,
  E2E_EXPECTED_EXACT_DUPLICATES: process.env.E2E_EXPECTED_EXACT_DUPLICATES || contract.expectedExactDuplicates,
  E2E_EXPECTED_REVISIONS: process.env.E2E_EXPECTED_REVISIONS || contract.expectedRevisions,
  E2E_EXPECTED_POSSIBLE_MATCHES: process.env.E2E_EXPECTED_POSSIBLE_MATCHES || contract.expectedPossibleMatches,
};

for (const [name, value] of Object.entries(resolvedValues)) {
  if (!value.trim()) throw new Error(`${name} must not be empty.`);
}

for (const [name, value] of Object.entries({
  E2E_LEAD_ID: resolvedValues.E2E_LEAD_ID,
  E2E_REVISION_LEAD_ID: resolvedValues.E2E_REVISION_LEAD_ID,
  E2E_RFQ_ID: resolvedValues.E2E_RFQ_ID,
  E2E_QUOTE_ID: resolvedValues.E2E_QUOTE_ID,
  E2E_DASHBOARD_KPI_VALUE: resolvedValues.E2E_DASHBOARD_KPI_VALUE,
  E2E_DASHBOARD_DRILLDOWN_COUNT: resolvedValues.E2E_DASHBOARD_DRILLDOWN_COUNT,
  E2E_EXPECTED_NEW_LEADS: resolvedValues.E2E_EXPECTED_NEW_LEADS,
  E2E_EXPECTED_EXACT_DUPLICATES: resolvedValues.E2E_EXPECTED_EXACT_DUPLICATES,
  E2E_EXPECTED_REVISIONS: resolvedValues.E2E_EXPECTED_REVISIONS,
  E2E_EXPECTED_POSSIBLE_MATCHES: resolvedValues.E2E_EXPECTED_POSSIBLE_MATCHES,
})) {
  if (!/^\d+$/.test(value)) throw new Error(`${name} must be a non-negative integer.`);
}

for (const [name, value] of Object.entries({ E2E_BASE_URL: baseURL, E2E_API_URL: apiURL })) {
  const parsed = new URL(value);
  if (!['http:', 'https:'].includes(parsed.protocol)) throw new Error(`${name} must use HTTP or HTTPS.`);
  if (fixtureMode && !['127.0.0.1', 'localhost'].includes(parsed.hostname)) {
    throw new Error(`${name} must be loopback-only in fixture mode.`);
  }
}

export default defineConfig({
  testDir: './e2e',
  outputDir: 'test-results/playwright',
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 1 : 0,
  workers: visibleGoogleChromeRequested || process.env.CI ? 1 : undefined,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [['list'], ['./e2e/support/zero-skips-reporter.ts'], ['html', { outputFolder: 'playwright-report', open: 'never' }]],
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
  projects: [
    { name: 'auth-setup', testMatch: /auth\.setup\.ts/ },
    {
      name: 'desktop-chromium',
      use: { ...devices['Desktop Chrome'], storageState: `${authDir}/manager.json` },
      dependencies: ['auth-setup'],
      testIgnore: [/auth\.setup\.ts/, /platform-admin-visible-certification\.spec\.ts/],
    },
    {
      name: 'mobile-chromium',
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 375, height: 812 },
        storageState: `${authDir}/manager.json`,
      },
      dependencies: ['auth-setup'],
      testIgnore: [/auth\.setup\.ts/, /platform-admin-visible-certification\.spec\.ts/],
    },
    ...(visibleGoogleChromeRequested ? [{
        name: 'visible-google-chrome',
        testMatch: /platform-admin-visible-certification\.spec\.ts/,
        timeout: 120_000,
        use: {
          ...devices['Desktop Chrome'],
          channel: 'chrome',
          headless: false,
          launchOptions: { slowMo: 150 },
          viewport: { width: 1440, height: 900 },
        },
      }] : []),
  ],
  webServer: startWebServer ? [
    ...(fixtureMode ? [{
      command: 'node e2e/support/fixture-server.mjs',
      url: `${apiURL}/health`,
      reuseExistingServer: !process.env.CI,
      timeout: 30_000,
    }] : []),
    {
      command: `npm run dev -- --host 127.0.0.1 --port ${webPort}`,
      url: baseURL,
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
      env: { ...process.env, VITE_API_BASE_URL: apiURL },
    },
  ] : undefined,
});
