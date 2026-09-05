import { defineConfig, devices } from '@playwright/test';
import { requireDisposableTargets } from './e2e/support/acceptance-boundary.mjs';

// Scenario testing of the quote-to-cash half of the spine against a kept disposable stack
// (scripts/e2e/run-enterprise-commercial-journey.sh with E2E_KEEP_STACK=1, then
// `set -a; . .enterprise-e2e-run/commercial-v2/stack.env; set +a`). Same boundary guard as the
// commercial-v2 suite: it refuses to point at anything that is not a disposable target.
requireDisposableTargets();

export default defineConfig({
  testDir: './e2e',
  testMatch: /scenarios-quote-to-cash\.spec\.ts/,
  outputDir: 'test-results/scenarios-quote-to-cash',
  fullyParallel: false,
  forbidOnly: true,
  retries: 0,
  workers: 1,
  timeout: 120_000,
  expect: { timeout: 15_000 },
  reporter: [
    ['list'],
    ['json', { outputFile: 'test-results/scenarios-quote-to-cash/results.json' }],
    ['html', { outputFolder: 'playwright-report-scenarios-quote-to-cash', open: 'never' }],
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
