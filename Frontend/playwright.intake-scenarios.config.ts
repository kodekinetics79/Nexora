import { defineConfig, devices } from '@playwright/test';
import { requireDisposableTargets } from './e2e/support/acceptance-boundary.mjs';

// Intake scenario testing: document → extraction → assembly → Lead, with varied and hostile
// inputs, against the disposable enterprise fixture stack. No webServer: the runner
// (scripts/e2e/run-intake-scenarios.sh) owns the stack and passes E2E_BASE_URL / E2E_API_URL.
requireDisposableTargets();

export default defineConfig({
  testDir: './e2e',
  testMatch: /scenarios-intake\.spec\.ts/,
  outputDir: 'test-results/intake-scenarios',
  fullyParallel: false,
  forbidOnly: true,
  retries: 0,
  workers: 1,
  // A document that fails retryably is re-tried five times on 2^n-second backoff (~62 s) before
  // it is dead-lettered; the terminal-outcome scenarios wait for that.
  // A 500-line bid list persists one evidence line per SaveChanges and takes minutes (F13).
  timeout: 720_000,
  expect: { timeout: 15_000 },
  reporter: [
    ['list'],
    ['json', { outputFile: process.env.E2E_INTAKE_JSON || 'test-results/intake-scenarios/results.json' }],
  ],
  use: {
    baseURL: process.env.E2E_BASE_URL || 'http://127.0.0.1:5181',
    actionTimeout: 15_000,
    navigationTimeout: 20_000,
    ...devices['Desktop Chrome'],
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
});
