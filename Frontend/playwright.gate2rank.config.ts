import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  testMatch: /gate2-weights-change-the-rank\.spec\.ts/,
  outputDir: 'test-results/gate2rank',
  fullyParallel: false,
  forbidOnly: false,
  retries: 0,
  workers: 1,
  timeout: 180_000,
  expect: { timeout: 20_000 },
  reporter: [['list']],
  use: {
    baseURL: process.env.E2E_BASE_URL || 'http://127.0.0.1:5373',
    actionTimeout: 20_000,
    navigationTimeout: 30_000,
    ...devices['Desktop Chrome'],
    viewport: { width: 2400, height: 1400 },
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
  },
});
