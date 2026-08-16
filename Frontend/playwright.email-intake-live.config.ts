import { defineConfig, devices } from '@playwright/test';

const required = ['E2E_BASE_URL', 'DEMO_EMAIL', 'DEMO_PASSWORD', 'DEMO_RUN'] as const;
const missing = required.filter((name) => !process.env[name]);
if (missing.length > 0) {
  throw new Error(`Live Email→Lead acceptance requires: ${missing.join(', ')}`);
}

export default defineConfig({
  testDir: './e2e',
  testMatch: /email-intake-demo\.spec\.ts/,
  outputDir: 'test-results/email-intake-live',
  fullyParallel: false,
  forbidOnly: true,
  retries: 0,
  workers: 1,
  timeout: 180_000,
  expect: { timeout: 30_000 },
  reporter: [
    ['list'],
    ['./e2e/support/zero-skips-reporter.ts'],
    ['html', { outputFolder: 'playwright-report-email-intake-live', open: 'never' }],
  ],
  use: {
    ...devices['Desktop Chrome'],
    baseURL: process.env.E2E_BASE_URL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
});
