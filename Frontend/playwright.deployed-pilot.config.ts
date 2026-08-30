import { defineConfig, devices } from '@playwright/test';
import { deployedContract } from './e2e/support/acceptance-boundary.mjs';

const contract = deployedContract();

export default defineConfig({
  testDir: './e2e',
  testMatch: /deployed-pilot\.spec\.ts/,
  outputDir: 'test-results/deployed-pilot-browser',
  fullyParallel: false,
  forbidOnly: true,
  retries: 0,
  workers: 1,
  timeout: 120_000,
  expect: { timeout: 20_000 },
  reporter: [['list'], ['json', { outputFile: 'test-results/deployed-pilot-browser/results.json' }],
    ['./e2e/support/zero-skips-reporter.ts', { expectedTests: 4 }]],
  use: {
    ...devices['Desktop Chrome'],
    baseURL: contract.baseURL,
    navigationTimeout: 30_000,
    actionTimeout: 15_000,
    serviceWorkers: 'block',
    // Production credentials/customer data must not enter traces, videos or screenshots.
    trace: 'off', screenshot: 'off', video: 'off',
  },
  // Deliberately no webServer or fixture server: the only target is the reviewed deployment.
});
