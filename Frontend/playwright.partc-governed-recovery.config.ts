import { defineConfig, devices } from '@playwright/test';

/**
 * PART C — governed stale-lease recovery under a relaxed platform MFA policy.
 *
 * TEST-ONLY. Its own config rather than a project inside `playwright.config.ts`, for three
 * reasons that are all load-bearing:
 *
 *  1. `playwright.config.ts` THROWS at load time when `E2E_FIXTURE_MODE=false` unless roughly
 *     twenty tenant-plane fixture ids are exported. PART C is a control-plane journey and has
 *     no lead, no RFQ and no quote to name, so attaching to that config would make the suite
 *     undiscoverable rather than runnable.
 *
 *  2. It starts a web server and, in fixture mode, a canned API. PART C attaches to the stack
 *     that `scripts/local/run-platform-console.sh` already owns. Starting a second frontend
 *     here would fight it for port 5173; starting the fixture API would make a watched run
 *     look real while silently replacing PostgreSQL with stubs.
 *
 *  3. It wires `zero-skips-reporter`, which fails any run containing a skip. PART C skips the
 *     steps whose backing feature has not landed — on purpose, loudly. See partc-step-ledger.ts.
 *
 * `retries: 0` is not an oversight. A retry would re-drive a privileged policy change and a
 * provisioning resume, which is precisely the duplicate-side-effect class step 15 exists to
 * detect: an automatic second attempt would turn a duplication defect into a green run.
 */

const baseURL = process.env.E2E_BASE_URL || 'http://127.0.0.1:5173';
const apiURL = process.env.E2E_API_URL || 'http://127.0.0.1:5192';

for (const [name, value] of Object.entries({ E2E_BASE_URL: baseURL, E2E_API_URL: apiURL })) {
  const parsed = new URL(value);
  if (!['http:', 'https:'].includes(parsed.protocol)) throw new Error(`${name} must use HTTP or HTTPS.`);
}

/**
 * ARTIFACTS LIVE OUTSIDE THE VITE ROOT, AND THAT IS NOT TIDINESS.
 *
 * This suite attaches to the dev server that `run-platform-console.sh` starts, and Vite's file
 * watcher covers its whole project root. With `trace: 'on'` and `video: 'on'`, writing the report
 * into `Frontend/playwright-report-partc/` made Vite broadcast a FULL PAGE RELOAD for every trace
 * file the run produced — `[vite] (client) page reload playwright-report-partc/trace/index.html`,
 * sixteen times in one run. Each reload restarted the `React.lazy` import the console was in the
 * middle of resolving, so a code-split route (`/platform/security/authentication`) sat on the
 * Suspense spinner forever and the harness reported a shipped screen as MISSING.
 *
 * That is the worst failure this harness can have: a confident false BLOCKED verdict against
 * another engineer's finished work. `.local-run/` is outside the Vite root and already gitignored.
 */
const ARTIFACTS = process.env.PARTC_ARTIFACT_ROOT || '../.local-run/partc';

export default defineConfig({
  testDir: './e2e',
  testMatch: /partc-governed-recovery\.spec\.ts/,
  outputDir: `${ARTIFACTS}/test-results`,

  // One worker, no parallelism, no retries: eighteen ordered steps against one control plane.
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: Boolean(process.env.CI),

  // Generous, because step 2/7/17 may wait out a real 30-second TOTP replay window rather
  // than weaken the server's replay fence.
  timeout: 240_000,
  expect: { timeout: 15_000 },

  reporter: [
    ['list'],
    ['./e2e/support/partc-step-ledger.ts'],
    ['html', { outputFolder: `${ARTIFACTS}/report`, open: 'never' }],
    ['json', { outputFile: `${ARTIFACTS}/results.json` }],
  ],

  use: {
    baseURL,
    // Evidence on every attempt, not only on failure: a certification needs positive proof
    // the journey ran, not just a post-mortem when it did not.
    trace: 'on',
    video: 'on',
    screenshot: 'on',
    actionTimeout: 30_000,
  },

  projects: [
    {
      name: 'visible-google-chrome',
      use: {
        ...devices['Desktop Chrome'],
        // Real Google Chrome, not bundled Chromium, and visibly: this journey is watched.
        channel: 'chrome',
        headless: false,
        launchOptions: { slowMo: 150 },
        viewport: { width: 1440, height: 900 },
      },
    },
  ],

  // Deliberately no `webServer`. The stack is owned by scripts/local/run-platform-console.sh.
});
