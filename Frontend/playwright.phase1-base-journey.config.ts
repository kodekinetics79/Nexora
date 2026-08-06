import { defineConfig, devices } from '@playwright/test';

/**
 * Phase 1 base journey: Lead → RFQ → Customer Quote Draft.
 *
 * Driven by `scripts/e2e/run-phase1-base-journey.sh`, which owns the lifecycle of PostgreSQL,
 * the backend (which hosts the extraction worker in-process) and the frontend, and which exports
 * every id this suite needs from the golden seed manifest.
 *
 * Deliberately its own config rather than a project inside `playwright.config.ts`: that config
 * resolves a fixture contract and starts its own web server, both of which fight the script.
 *
 * `retries: 0` is not an oversight. A retry here would mask exactly the defect class this suite
 * exists to catch — a second Convert or Prepare Quote Draft attempt is the idempotency assertion,
 * so an automatic replay would turn a duplicate-record bug into a green run.
 */
export default defineConfig({
  testDir: './e2e',
  testMatch: /phase1-base-journey\.spec\.ts/,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 90_000,
  expect: { timeout: 15_000 },
  reporter: [
    ['list'],
    ['html', { outputFolder: 'playwright-report', open: 'never' }],
  ],
  use: {
    baseURL: process.env.E2E_BASE_URL || 'http://127.0.0.1:5173',
    // Trace and video on every attempt, not just failures: a GO claim needs positive evidence
    // that the journey ran, not only a post-mortem when it does not.
    trace: 'on',
    video: 'on',
    screenshot: 'on',
    actionTimeout: 20_000,
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
