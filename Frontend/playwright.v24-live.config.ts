import { defineConfig, devices } from "@playwright/test";

const baseURL = process.env.E2E_BASE_URL;
if (!baseURL) throw new Error("E2E_BASE_URL is required for V2.4 live browser acceptance.");

export default defineConfig({
  testDir: "./e2e",
  testMatch: /v2-gate04-supplier-negotiation-live\.spec\.ts/,
  fullyParallel: false,
  forbidOnly: true,
  retries: 0,
  workers: 1,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [["list"], ["./e2e/support/zero-skips-reporter.ts"]],
  use: {
    ...devices["Desktop Chrome"],
    baseURL,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    video: "retain-on-failure",
  },
});
