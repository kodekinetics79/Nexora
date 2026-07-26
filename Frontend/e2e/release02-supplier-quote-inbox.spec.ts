import { expect, test, type Page, type Request } from "@playwright/test";

const quoteId = 2401;
const revisionId = 2501;
const evidenceId = 2601;
const inboxItem = {
  supplierQuoteId: quoteId, supplierId: 901, supplierName: "Certified Components Inc.",
  supplierQuoteReference: "SQ-2026-184", nexoraSerial: "NXR-2026-000077",
  sourcingCaseId: 8801, currentRevisionNumber: 2, inboxStatus: "REVIEW_REQUIRED",
  updatedOn: "2026-07-26T14:00:00Z", reviewRequiredCount: 1,
};
const detail = {
  supplierQuoteId: quoteId, supplierId: 901, supplierName: inboxItem.supplierName,
  supplierSolicitationId: 12001, sourcingCaseId: 8801, rfqId: 77,
  nexoraSerial: inboxItem.nexoraSerial, supplierQuoteReference: inboxItem.supplierQuoteReference,
  currentRevisionNumber: 2, inboxStatus: "REVIEW_REQUIRED",
  revisions: [{
    revisionId, revisionNumber: 2, captureChannel: "UPLOAD", currencyId: 1,
    validUntil: "2026-08-31T23:59:59Z", requiresReview: true,
    capturedOn: "2026-07-26T14:00:00Z",
    lines: [{ id: 2701, lineNumber: 1, rfqItemId: 402, partNumber: "NXR-R02-OOS-001",
      description: "Qualified flight control module", quantity: 12, availableQuantity: 12,
      unitPrice: 284.5, leadTimeDays: 14 }],
    evidence: [{ id: evidenceId, supplierQuoteLineId: 2701, fieldName: "UnitPrice",
      originalValue: "$284.50", normalizedValue: "284.50", confidence: 0.61,
      method: "LOCAL_PARSER", critical: true, reviewRequired: true,
      latestReviewStatus: null, correctedValue: null }],
  }],
};

async function installApi(page: Page) {
  let capture: Request | null = null;
  let review: Request | null = null;
  await page.route("**/api/supplier-quote-inbox**", async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (request.method() === "GET" && path.endsWith(`/${quoteId}`))
      return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(detail) });
    if (request.method() === "GET")
      return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify([inboxItem]) });
    if (request.method() === "POST" && path.endsWith("/reviews")) {
      review = request;
      return route.fulfill({ status: 204 });
    }
    if (request.method() === "POST") {
      capture = request;
      return route.fulfill({ status: 201, contentType: "application/json", body: JSON.stringify({ supplierQuoteId: 3001 }) });
    }
    return route.fulfill({ status: 404 });
  });
  return { capture: () => capture, review: () => review };
}

test("Supplier Quote Inbox exposes persisted commercial lineage", async ({ page }) => {
  await installApi(page);
  await page.goto("/procurement/supplier-quotes");
  await expect(page.getByRole("heading", { name: "Supplier Quote Inbox" })).toBeVisible();
  await expect(page.getByText(inboxItem.supplierName)).toBeVisible();
  await expect(page.getByText(inboxItem.nexoraSerial)).toBeVisible();
  await expect(page.getByText("1 fields")).toBeVisible();
  await page.getByRole("button", { name: "Review" }).click();
  await expect(page).toHaveURL(new RegExp(`/procurement/supplier-quotes/${quoteId}$`));
});

test("manual capture carries authoritative sourcing and line identity", async ({ page }) => {
  const api = await installApi(page);
  await page.goto("/procurement/supplier-quotes");
  await page.getByRole("button", { name: "Capture Supplier Quote" }).click();
  const values: Record<string, string> = { "Supplier ID": "901", "Supplier RFQ ID": "12001",
    "Sourcing Case ID": "8801", "Nexora Serial": "NXR-2026-000077",
    "Supplier Quote reference": "SQ-MANUAL-001", "Source reference": "authorized offline response",
    "Currency ID": "1", "RFQ line ID": "402", "Demand line ID": "7001",
    "Description": "Qualified flight control module", "Unit price": "281.75" };
  for (const [label, value] of Object.entries(values)) await page.getByLabel(label).fill(value);
  await page.getByRole("button", { name: "Capture revision" }).click();
  await expect.poll(() => api.capture()).not.toBeNull();
  const payload = api.capture()!.postDataJSON();
  expect(payload).toMatchObject({ supplierSolicitationId: 12001, sourcingCaseId: 8801,
    nexoraSerial: "NXR-2026-000077", currencyId: 1 });
  expect(payload.lines[0]).toMatchObject({ rfqItemId: 402, commercialDemandLineId: 7001, unitPrice: 281.75 });
  expect(payload.sourceSha256).toMatch(/^[0-9a-f]{64}$/);
  expect(api.capture()!.headers()["idempotency-key"]).toBeTruthy();
});

test("field review appends an evidenced decision", async ({ page }) => {
  const api = await installApi(page);
  await page.goto(`/procurement/supplier-quotes/${quoteId}`);
  await expect(page.getByText("$284.50")).toBeVisible();
  await expect(page.getByText("61%")).toBeVisible();
  await page.getByRole("button", { name: "Review" }).click();
  await page.getByRole("combobox", { name: /Decision/ }).click();
  await page.getByRole("option", { name: "Record correction" }).click();
  await page.getByLabel("Corrected value").fill("284.50");
  await page.getByLabel("Review reason").fill("Verified against the immutable Supplier response");
  await page.getByRole("button", { name: "Record decision" }).click();
  await expect.poll(() => api.review()).not.toBeNull();
  expect(api.review()!.postDataJSON()).toEqual({ status: "CORRECTED", correctedValue: "284.50",
    reason: "Verified against the immutable Supplier response" });
  expect(new URL(api.review()!.url()).pathname).toContain(`/${quoteId}/revisions/${revisionId}/evidence/${evidenceId}/reviews`);
});
