import { expect, test } from "@playwright/test";
import { loginThroughUi } from "./support/login";

const supplierQuoteId = process.env.E2E_V24_SUPPLIER_QUOTE_ID;
const email = process.env.E2E_MANAGER_EMAIL;
const password = process.env.E2E_MANAGER_PASSWORD;
const businessUnitId = process.env.E2E_MANAGER_BUSINESS_UNIT_ID;

if (!supplierQuoteId || !/^\d+$/.test(supplierQuoteId) || !email || !password || !businessUnitId) {
  throw new Error("V2.4 live browser acceptance requires quote ID and manager login environment values.");
}

test("authenticated user reviews real bid guidance and records a governed decision", async ({ page }) => {
  await loginThroughUi(page, { email, password, businessUnitId });

  const guidanceResponse = page.waitForResponse((response) =>
    response.request().method() === "GET" &&
    response.url().endsWith(`/api/supplier-quote-inbox/${supplierQuoteId}/negotiation`),
  );
  await page.goto(`/procurement/supplier-quotes/${supplierQuoteId}`);

  await expect(page.getByRole("heading", { name: "Bid quality and negotiation guidance" })).toBeVisible();
  await expect(page.getByText("Current commercial terms")).toBeVisible();
  await expect(page.getByText("Round 1", { exact: true })).toBeVisible();
  await expect(page.getByText("FCA", { exact: true })).toBeVisible();
  await expect(page.getByText("Net 30", { exact: true })).toBeVisible();
  expect((await guidanceResponse).status()).toBe(200);

  const recordButtons = page.getByRole("button", { name: "Record decision" });
  await expect(recordButtons.first()).toBeVisible();
  await recordButtons.first().click();
  await page.getByLabel("Disposition").click();
  await page.getByRole("option", { name: "Prepare for negotiation" }).click();
  await page.getByLabel("Decision reason").fill(
    "Prepared from verified local bid evidence during authorized V2.4 browser acceptance.",
  );

  const decisionResponse = page.waitForResponse((response) =>
    response.request().method() === "POST" &&
    response.url().endsWith(`/api/supplier-quote-inbox/${supplierQuoteId}/negotiation-decisions`),
  );
  await page.getByRole("dialog").getByRole("button", { name: "Record decision" }).click();
  expect((await decisionResponse).status()).toBe(200);
  await expect(page.getByText("PREPARED", { exact: true })).toBeVisible();
});
