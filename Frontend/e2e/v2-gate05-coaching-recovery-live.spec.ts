import { expect, test } from "@playwright/test";
import { loginThroughUi } from "./support/login";
import { requireEnv } from "./support/environment";

// Resolved inside the test, not at module scope — see requireEnv.
const env = () => {
  const values = requireEnv("V2.5 live browser acceptance", "E2E_MANAGER_EMAIL",
    "E2E_MANAGER_PASSWORD", "E2E_MANAGER_BUSINESS_UNIT_ID", "E2E_CUSTOMER_ID");
  if (!/^\d+$/.test(values.E2E_CUSTOMER_ID))
    throw new Error("E2E_CUSTOMER_ID must be a numeric customer id.");
  return values;
};

test("manager reviews real coaching and records a governed acknowledgement", async ({ page }) => {
  const { E2E_MANAGER_EMAIL: email, E2E_MANAGER_PASSWORD: password,
    E2E_MANAGER_BUSINESS_UNIT_ID: businessUnitId, E2E_CUSTOMER_ID: customerId } = env();
  const consoleErrors: string[] = [];
  page.on("console", (message) => { if (message.type() === "error") consoleErrors.push(message.text()); });
  await loginThroughUi(page, { email, password, businessUnitId });

  const coachingResponse = page.waitForResponse((response) =>
    response.request().method() === "GET" &&
    response.url().includes("/api/commercial-intelligence/coaching-recovery?"),
  );
  await page.goto("/sales/today");

  await expect(page.getByRole("heading", { name: "Coaching and recovery" })).toBeVisible();
  const response = await coachingResponse;
  expect(response.status()).toBe(200);
  const payload = await response.json() as {
    dataCompleteness: { status: string; incompleteSources: string[] };
    coachingFindings: Array<{ recommendation: string }>;
    recoveryOpportunities: Array<{ title: string }>;
  };
  expect(payload.dataCompleteness).toEqual({ status: "complete", incompleteSources: [] });
  expect(payload.coachingFindings.length).toBeGreaterThan(0);
  expect(payload.recoveryOpportunities.length).toBeGreaterThan(0);
  await expect(page.getByText(payload.coachingFindings[0].recommendation, { exact: false })).toBeVisible();

  await page.getByRole("tab", { name: /Recovery opportunities/ }).click();
  await expect(page.getByText(payload.recoveryOpportunities[0].title, { exact: true })).toBeVisible();
  await page.getByRole("tab", { name: /Coaching findings/ }).click();

  await page.getByRole("button", { name: "Acknowledge finding" }).first().click();
  const dialog = page.getByRole("dialog", { name: "Acknowledge coaching finding" });
  await dialog.getByLabel("Decision reason").fill(
    "Manager verified the persisted evidence during authorized V2.5 browser acceptance.",
  );
  const acknowledgementResponse = page.waitForResponse((candidate) =>
    candidate.request().method() === "POST" &&
    candidate.url().includes("/api/commercial-intelligence/coaching/") &&
    candidate.url().endsWith("/acknowledgements"),
  );
  await dialog.getByRole("button", { name: "Record acknowledgement" }).click();
  expect((await acknowledgementResponse).status()).toBe(200);
  await expect(dialog).toHaveCount(0);
  await expect(page.getByText(/Acknowledged: Manager verified the persisted evidence/).last()).toBeVisible();
  expect(consoleErrors).toEqual([]);
});

test("authenticated Customer 360 renders server-authoritative health", async ({ page }) => {
  const consoleErrors: string[] = [];
  page.on("console", (message) => { if (message.type() === "error") consoleErrors.push(message.text()); });
  await loginThroughUi(page, { email, password, businessUnitId });

  const healthResponse = page.waitForResponse((response) =>
    response.request().method() === "GET" &&
    response.url().includes(`/api/intelligence/customers/${customerId}/health?`),
  );
  await page.goto(`/customers/${customerId}`);

  const response = await healthResponse;
  expect(response.status()).toBe(200);
  const payload = await response.json() as {
    customerId: number;
    healthBand: string;
    dataCompleteness: { status: string; incompleteSources: string[] };
    nextBestAction: { title: string } | null;
  };
  expect(payload.customerId).toBe(Number(customerId));
  expect(payload.dataCompleteness).toEqual({ status: "complete", incompleteSources: [] });
  await expect(page.getByText("Account health", { exact: true })).toBeVisible();
  await expect(page.getByText(payload.healthBand, { exact: true })).toBeVisible();
  if (payload.nextBestAction) {
    await expect(page.getByText(payload.nextBestAction.title, { exact: true })).toBeVisible();
  }

  const dimensions = await page.evaluate(() => ({
    documentWidth: document.documentElement.scrollWidth,
    viewportWidth: window.innerWidth,
  }));
  expect(dimensions.documentWidth).toBeLessThanOrEqual(dimensions.viewportWidth);
  expect(consoleErrors).toEqual([]);
});
