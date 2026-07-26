import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  testMatch: /commercial-journey-v2\.spec\.ts/,
  outputDir: 'test-results/commercial-journey-v2',
  fullyParallel: false,
  forbidOnly: true,
  retries: 0,
  workers: 1,
  timeout: 90_000,
  expect: { timeout: 15_000 },
  reporter: [
    ['list'],
    ['./e2e/support/zero-skips-reporter.ts'],
    ['html', { outputFolder: 'playwright-report-commercial-journey-v2', open: 'never' }],
  ],
  use: {
    baseURL: process.env.E2E_BASE_URL || 'http://127.0.0.1:5173',
    actionTimeout: 15_000,
    navigationTimeout: 20_000,
    ...devices['Desktop Chrome'],
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
});
