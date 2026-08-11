import { defineConfig, devices } from '@playwright/test';

/**
 * PART C — one-shot seed for the SYNTHETIC failing tenant.
 *
 * TEST-ONLY, and deliberately a separate config from the journey: this WRITES to the control
 * plane, and it must never be possible to run it by accident as part of a certification. The
 * journey config's `testMatch` excludes it; this one runs nothing else.
 *
 * Artifacts go to `.local-run/`, outside the Vite root — see the note in
 * `playwright.partc-governed-recovery.config.ts` for why that is load-bearing rather than tidy.
 */
const ARTIFACTS = process.env.PARTC_ARTIFACT_ROOT || '../.local-run/partc';

export default defineConfig({
  testDir: './e2e',
  testMatch: /partc-synthetic-tenant\.seed\.ts/,
  outputDir: `${ARTIFACTS}/seed-test-results`,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 300_000,
  expect: { timeout: 15_000 },
  reporter: [['list'], ['html', { outputFolder: `${ARTIFACTS}/seed-report`, open: 'never' }]],
  use: {
    baseURL: process.env.E2E_BASE_URL || 'http://127.0.0.1:5173',
    trace: 'on',
    video: 'on',
    screenshot: 'on',
    actionTimeout: 30_000,
  },
  projects: [
    {
      name: 'visible-google-chrome',
      use: {
        ...devices['Desktop Chrome'],
        channel: 'chrome',
        headless: false,
        launchOptions: { slowMo: 150 },
        viewport: { width: 1440, height: 900 },
      },
    },
  ],
});
