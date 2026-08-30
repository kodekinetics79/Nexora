import { defineConfig, devices } from '@playwright/test';
import { requireDisposableTargets } from './e2e/support/acceptance-boundary.mjs';

requireDisposableTargets();

export default defineConfig({
  testDir: './e2e',
  testMatch: /core-commercial-(sales-force|inventory|journey)\.spec\.ts/,
  outputDir: 'test-results/core-commercial',
  fullyParallel: false,
  forbidOnly: true,
  retries: 0,
  workers: 1,
  timeout: 90_000,
  expect: { timeout: 15_000 },
  reporter: [
    ['list'],
    ['./e2e/support/zero-skips-reporter.ts'],
    ['html', { outputFolder: 'playwright-report-core-commercial', open: 'never' }],
  ],
  use: {
    baseURL: process.env.E2E_BASE_URL || 'http://127.0.0.1:4173',
    actionTimeout: 15_000,
    navigationTimeout: 20_000,
    ...devices['Desktop Chrome'],
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
});
