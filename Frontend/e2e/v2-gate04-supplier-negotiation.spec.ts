import { expect, test, type Page, type Request } from "@playwright/test";

const quoteId = 2401;
const revisionId = 2501;
const evidenceId = 2601;

const detail = {
  supplierQuoteId: quoteId,
  supplierId: 901,
  supplierName: "Certified Components Inc.",
  supplierSolicitationId: 12001,
  sourcingCaseId: 8801,
  rfqId: 77,
  nexoraSerial: "NXR-2026-000077",
  supplierQuoteReference: "SQ-2026-184",
  currentRevisionNumber: 2,
  inboxStatus: "READY_FOR_COMPARISON",
  version: 3,
  revisions: [{
    revisionId,
    revisionNumber: 2,
    captureChannel: "UPLOAD",
    currencyId: 1,
    validUntil: "2026-08-31T23:59:59Z",
    requiresReview: true,
    capturedOn: "2026-07-26T14:00:00Z",
    lines: [{
      id: 2701,
      lineNumber: 1,
      rfqItemId: 402,
      partNumber: "NXR-R02-OOS-001",
      description: "Qualified flight control module",
      quantity: 12,
      availableQuantity: 12,
      unitPrice: 284.5,
      leadTimeDays: 14,
    }],
    evidence: [{
      id: evidenceId,
      supplierQuoteLineId: 2701,
      fieldName: "UnitPrice",
      originalValue: "$284.50",
      normalizedValue: "284.50",
      confidence: 0.61,
      method: "LOCAL_PARSER",
      critical: true,
      reviewRequired: true,
      latestReviewStatus: null,
      correctedValue: null,
    }],
  }],
};

const negotiation = {
  supplierQuoteId: quoteId,
  supplierQuoteRevisionId: revisionId,
  revisionNumber: 2,
  quoteVersion: 7,
  mode: "SHADOW",
  policyVersion: "supplier-negotiation-shadow-v1",
  currentRound: {
    roundNumber: 2,
    currencyCode: "USD",
    validUntil: "2026-08-31T23:59:59Z",
    incoterms: "FCA Chicago",
    paymentTerms: "Net 30",
    freightAmount: 96,
    taxAmount: 0,
    capturedOn: "2026-07-26T14:00:00Z",
  },
  evaluatedCategories: ["PRICE_OUTLIER"],
  bidFlags: [{
    code: "PRICE_OUTLIER",
    severity: "WARNING",
    blocking: false,
    explanation: "The current unit price is above the tenant-qualified comparison cohort.",
    confidence: 0.82,
    sampleSize: 4,
    evidence: ["Cohort median USD 261.20; n=4"],
  }],
  recommendations: [{
    code: "BEST_AND_FINAL_PRICE",
    title: "Request best-and-final price",
    rationale: "The current offer is complete but priced above comparable current offers.",
    confidence: 0.82,
    sampleSize: 4,
    evidence: ["Current landed USD 284.50; cohort median USD 261.20; n=4"],
    limitations: ["Guidance does not authorize Supplier contact or award selection."],
    mode: "SHADOW",
  }],
  priorDecisions: [],
  priorDecisionTotal: 0,
  priorDecisionsTruncated: false,
};

async function setPermissions(page: Page, options: { historyEdit: boolean; negotiationEdit: boolean }) {
  await page.addInitScript((values) => {
    const existing = JSON.parse(localStorage.getItem("userData") ?? "{}");
    localStorage.setItem("userData", JSON.stringify({
      ...existing,
      isSuperAdmin: false,
      permissions: [
        { id: 101, roleId: 1, moduleId: 101, moduleName: "Supplier History", canCreate: values.historyEdit, canEdit: values.historyEdit, canDelete: false },
        { id: 102, roleId: 1, moduleId: 102, moduleName: "Supplier Negotiation", canCreate: values.negotiationEdit, canEdit: values.negotiationEdit, canDelete: false },
      ],
    }));
  }, options);
}

async function installApi(
  page: Page,
  negotiationHandler?: (request: Request, count: number) => Promise<{ status: number; body: unknown }>,
  decisionHandler?: (request: Request) => Promise<{ status: number; body: unknown }>,
) {
  let decision: Request | null = null;
  let negotiationReads = 0;
  await page.route("**/api/processing-evidence/supplier-quotes/**", (route) =>
    route.fulfill({ status: 200, contentType: "application/json", body: "null" }),
  );
  await page.route("**/api/supplier-quote-inbox/**", async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (request.method() === "GET" && path.endsWith(`/${quoteId}/negotiation`)) {
      negotiationReads += 1;
      const result = negotiationHandler
        ? await negotiationHandler(request, negotiationReads)
        : { status: 200, body: negotiation };
      return route.fulfill({
        status: result.status,
        contentType: "application/json",
        body: JSON.stringify(result.body),
      });
    }
    if (request.method() === "POST" && path.endsWith(`/${quoteId}/negotiation-decisions`)) {
      decision = request;
      const result = decisionHandler
        ? await decisionHandler(request)
        : {
            status: 200,
            body: {
              decisionId: 9101,
              supplierQuoteRevisionId: revisionId,
              ...request.postDataJSON(),
              decidedOn: "2026-07-29T12:00:00Z",
              actor: "Release Manager",
            },
          };
      return route.fulfill({
        status: result.status,
        contentType: "application/json",
        body: JSON.stringify(result.body),
      });
    }
    if (request.method() === "GET" && path.endsWith(`/${quoteId}`)) {
      return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(detail) });
    }
    return route.fulfill({ status: 404, contentType: "application/json", body: JSON.stringify({ message: "Unexpected test route" }) });
  });
  return { decision: () => decision, negotiationReads: () => negotiationReads };
}

test("V2.4 renders bid quality and records an idempotent governed decision", async ({ page }) => {
  await setPermissions(page, { historyEdit: true, negotiationEdit: true });
  const api = await installApi(page);

  await page.goto(`/procurement/supplier-quotes/${quoteId}`);

  await expect(page.getByRole("heading", { name: "Bid quality and negotiation guidance" })).toBeVisible();
  await expect(page.getByText("FCA Chicago")).toBeVisible();
  await expect(page.getByText("Net 30")).toBeVisible();
  await expect(page.getByText("$284.50").first()).toBeVisible();
  await expect(page.getByText("Price Outlier")).toBeVisible();

  await page.getByText("Price Outlier").click();
  await expect(page.getByText(/Cohort median USD 261.20/)).toBeVisible();
  await page.getByText("Evidence and limitations").click();
  await expect(page.getByText("Guidance does not authorize Supplier contact or award selection.")).toBeVisible();

  await page.getByRole("button", { name: "Record decision" }).click();
  await page.getByLabel("Disposition").click();
  await page.getByRole("option", { name: "Defer" }).click();
  await page.getByLabel("Decision reason").fill("Awaiting an authorized engineering response on the delivery constraint.");
  await page.getByRole("dialog").getByRole("button", { name: "Record decision" }).click();

  await expect.poll(() => api.decision()).not.toBeNull();
  expect(api.decision()!.postDataJSON()).toEqual({
    recommendationCode: "BEST_AND_FINAL_PRICE",
    disposition: "DEFERRED",
    reason: "Awaiting an authorized engineering response on the delivery constraint.",
    expectedQuoteVersion: 7,
  });
  expect(api.decision()!.headers()["idempotency-key"]).toBeTruthy();
});

test("V2.4 keeps review, projection, and negotiation decisions behind edit permissions", async ({ page }) => {
  await setPermissions(page, { historyEdit: false, negotiationEdit: false });
  await installApi(page);

  await page.goto(`/procurement/supplier-quotes/${quoteId}`);

  await expect(page.getByText("Supplier History edit permission is required")).toBeVisible();
  await expect(page.getByText("Supplier Negotiation edit permission is required")).toBeVisible();
  await expect(page.getByRole("button", { name: "Compare offer" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Review" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Record decision" })).toHaveCount(0);
  await expect(page.getByText("Request best-and-final price")).toBeVisible();
});

test("V2.4 exposes an actionable negotiation error and recovers on retry", async ({ page }) => {
  await setPermissions(page, { historyEdit: true, negotiationEdit: true });
  const api = await installApi(page, async (_request, count) =>
    count <= 2
      ? { status: 503, body: { message: "Temporary failure" } }
      : { status: 200, body: negotiation },
  );

  await page.goto(`/procurement/supplier-quotes/${quoteId}`);
  await expect(page.getByText("Negotiation guidance could not be loaded")).toBeVisible();
  await page.getByRole("button", { name: "Retry" }).click();
  await expect(page.getByText("Request best-and-final price")).toBeVisible();
  expect(api.negotiationReads()).toBe(3);
});

test("V2.4 discloses capped negotiation decision history", async ({ page }) => {
  await setPermissions(page, { historyEdit: true, negotiationEdit: true });
  const priorDecisions = Array.from({ length: 100 }, (_, index) => ({
    decisionId: index + 1,
    supplierQuoteRevisionId: revisionId,
    recommendationCode: "BEST_AND_FINAL_PRICE",
    disposition: "DEFERRED",
    reason: "Reviewed negotiation evidence.",
    policyVersion: "supplier-negotiation-shadow-v1",
    expectedQuoteVersion: 7,
    actor: "Release Manager",
    decidedOn: "2026-07-29T12:00:00Z",
    correlationId: `history-${index + 1}`,
  }));
  await installApi(page, async () => ({
    status: 200,
    body: {
      ...negotiation,
      priorDecisions,
      priorDecisionTotal: 101,
      priorDecisionsTruncated: true,
    },
  }));

  await page.goto(`/procurement/supplier-quotes/${quoteId}`);

  await expect(page.getByText("Showing the latest 100 of 101 negotiation decisions.")).toBeVisible();
});

test("V2.4 rejects a malformed successful negotiation contract", async ({ page }) => {
  await setPermissions(page, { historyEdit: true, negotiationEdit: true });
  await installApi(page, async () => ({ status: 200, body: { quoteVersion: 7 } }));

  await page.goto(`/procurement/supplier-quotes/${quoteId}`);

  await expect(page.getByText("Negotiation guidance could not be loaded")).toBeVisible();
  await expect(page.getByRole("button", { name: "Record decision" })).toHaveCount(0);
});

test("V2.4 closes stale decision input and refreshes guidance after a conflict", async ({ page }) => {
  await setPermissions(page, { historyEdit: true, negotiationEdit: true });
  const api = await installApi(
    page,
    undefined,
    async () => ({ status: 409, body: { detail: "Supplier Quote changed." } }),
  );

  await page.goto(`/procurement/supplier-quotes/${quoteId}`);
  await page.getByRole("button", { name: "Record decision" }).click();
  await page.getByLabel("Decision reason").fill("Request current evidence before negotiation.");
  await page.getByRole("dialog").getByRole("button", { name: "Record decision" }).click();

  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page.getByText("Supplier Quote changed; guidance refreshed. Review and try again.")).toBeVisible();
  await expect.poll(() => api.negotiationReads()).toBeGreaterThanOrEqual(2);
});
