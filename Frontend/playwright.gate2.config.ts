import { defineConfig, devices } from '@playwright/test';
export default defineConfig({
  testDir: './e2e',
  testMatch: /gate2-supplier-tiers-and-weights\.spec\.ts/,
  outputDir: 'test-results/gate2',
  fullyParallel: false, forbidOnly: false, retries: 0, workers: 1,
  timeout: 90_000, expect: { timeout: 15_000 },
  reporter: [['list']],
  use: {
    baseURL: process.env.E2E_BASE_URL || 'http://127.0.0.1:5373',
    actionTimeout: 15_000, navigationTimeout: 25_000,
    ...devices['Desktop Chrome'],
    trace: 'retain-on-failure', screenshot: 'only-on-failure', video: 'off',
  },
});
