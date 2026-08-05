import { defineConfig, devices } from '@playwright/test';

/**
 * Production smoke configuration — runs e2e/production-smoke.spec.ts against the
 * LIVE deployment (frontend on Vercel, backend on Render).
 *
 * Unlike the *-live configs, this file never throws when env is missing: the spec
 * itself skips with a clear message when E2E_PROD_EMAIL / E2E_PROD_PASSWORD are
 * unset, so `--list` and CI dry-runs work without secrets.
 *
 *   npx playwright test --config playwright.production.config.ts
 */

const baseURL = process.env.E2E_PROD_FRONTEND_URL ?? 'https://nexora1-ai.vercel.app';

export default defineConfig({
  testDir: './e2e',
  testMatch: /production-smoke\.spec\.ts/,
  // One real customer journey against one shared tenant: strictly serial.
  fullyParallel: false,
  forbidOnly: true,
  retries: 1,
  workers: 1,
  // Render cold starts + real extraction latency: generous but bounded. The
  // batch-reconciliation test raises its own timeout via test.setTimeout.
  timeout: 120_000,
  expect: { timeout: 15_000 },
  reporter: [
    ['list'],
    ['html', { outputFolder: 'playwright-report-production-smoke', open: 'never' }],
  ],
  outputDir: 'test-results/production-smoke',
  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        baseURL,
        headless: true,
        trace: 'retain-on-failure',
        screenshot: 'only-on-failure',
        video: 'retain-on-failure',
      },
    },
  ],
});
