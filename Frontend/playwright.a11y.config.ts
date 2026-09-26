import { defineConfig, devices } from '@playwright/test';
import contract from './e2e/support/fixture-contract.json' with { type: 'json' };

/**
 * Standalone config for the WCAG 2.1 AA axe smoke test (`npm run e2e:a11y`).
 *
 * Mirrors playwright.config.ts's fixture-server + dev-server + auth-setup
 * wiring, but only runs e2e/a11y-axe.spec.ts so the accessibility gate can be
 * executed on its own in CI without the full acceptance suite.
 */

const baseURL = process.env.E2E_BASE_URL || contract.baseUrl;
const apiURL = process.env.E2E_API_URL || contract.apiUrl;
const webPort = new URL(baseURL).port || '4173';
const authDir = 'node_modules/.cache/nexora-e2e';
const startWebServer = process.env.E2E_SKIP_WEB_SERVER !== 'true';

export default defineConfig({
  testDir: './e2e',
  testMatch: /(a11y-axe\.spec|auth\.setup)\.ts/,
  outputDir: 'test-results/a11y',
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  workers: 1,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [['list']],
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    // The dashboard's bands rise into place over 280ms, staggered per band, and `nx-rise` animates
    // opacity. axe reads the BLENDED colour, so a scan that lands mid-rise measures a contrast the
    // reader never sees: the quiet caption on the clay is 4.89:1 at rest and axe reported 4.09
    // from a half-faded frame — passing on one CI run and failing the next on the same commit.
    // Reduce-motion is the product's own answer to that (ThemeContext switches every decorative
    // animation off under it), so the gate scans the settled screen an assistive-tech user reads.
    reducedMotion: 'reduce',
  },
  projects: [
    { name: 'auth-setup', testMatch: /auth\.setup\.ts/ },
    {
      name: 'a11y',
      testMatch: /a11y-axe\.spec\.ts/,
      use: { ...devices['Desktop Chrome'], storageState: `${authDir}/manager.json` },
      dependencies: ['auth-setup'],
    },
  ],
  webServer: startWebServer
    ? [
        {
          command: 'node e2e/support/fixture-server.mjs',
          url: `${apiURL}/health`,
          reuseExistingServer: !process.env.CI,
          timeout: 30_000,
        },
        {
          command: `npm run dev -- --host 127.0.0.1 --port ${webPort}`,
          url: baseURL,
          reuseExistingServer: !process.env.CI,
          timeout: 120_000,
          env: { ...process.env, VITE_API_BASE_URL: apiURL },
        },
      ]
    : undefined,
});
