import { defineConfig, devices } from '@playwright/test';

const baseURL = process.env.E2E_BASE_URL ?? 'http://127.0.0.1:5174';
const apiURL = process.env.E2E_API_URL ?? 'http://127.0.0.1:5292';

export default defineConfig({
  testDir: './e2e',
  outputDir: 'test-results/pilot-readiness',
  testMatch: /pilot-readiness-dead-letter\.spec\.ts/,
  fullyParallel: false,
  forbidOnly: true,
  retries: 0,
  workers: 1,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [['list'], ['./e2e/support/zero-skips-reporter.ts']],
  use: {
    ...devices['Desktop Chrome'],
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  webServer: {
    command: `npm run dev -- --host 127.0.0.1 --port ${new URL(baseURL).port}`,
    url: baseURL,
    reuseExistingServer: false,
    timeout: 120_000,
    env: { ...process.env, VITE_API_BASE_URL: apiURL },
  },
});
