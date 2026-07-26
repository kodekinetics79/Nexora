import { expect, test, type Page } from "@playwright/test";

const product = {
  productId: 701, partNumber: "NXR-R02-OOS-001", productName: "Qualified flight control module",
  periodFrom: "2026-01-01T00:00:00Z", periodTo: "2026-07-26T00:00:00Z",
  timesRequested: 38, timesQuoted: 25, decidedCount: 19, wonCount: 4, lostCount: 15,
  pendingCount: 6, lineWinRatePercent: 21.05, stockoutBlockedCount: 8,
  typicalWinningLeadTimeDays: 12,
  lastWonContext: { customerQuoteId: 81, customerQuoteNumber: "CQ-81", quantity: 12, unitPrice: 420,
    currencyId: 1, currencyCode: "USD", deliveryLeadTimeDays: 12, outcomeOn: "2026-07-20T00:00:00Z" },
  wonSellingPrices: [{ currencyId: 1, currencyCode: "USD", lastValue: 420, medianValue: 405,
    minimumValue: 390, maximumValue: 420, sampleSize: 4 }],
  supplierLandedCosts: [{ currencyId: 1, currencyCode: "USD", lastValue: 284.5, medianValue: 279,
    minimumValue: 270, maximumValue: 284.5, sampleSize: 6 }],
  lossReasons: [{ code: "PRICE", label: "Price too high", count: 8 }], evidence: [],
};
const studio = { generatedAt: "2026-07-26T16:00:00Z", approvedCorrections: 7,
  conflictingCorrections: 1, supplierQuoteTemplates: 3, productMemoriesWithDecisions: 4,
  productMemoriesBelowThreshold: 9, recentSignals: [{ signalType: "SUPPLIER_QUOTE_CORRECTION",
    subject: "UnitPrice", value: "284.50", sampleSize: 3, lastObservedOn: "2026-07-26T14:00:00Z",
    status: "REUSABLE", evidenceReference: "SupplierQuoteEvidence:2601" }] };
const supplier = { supplierId: 901, supplierName: "Certified Components Inc.", quoteRevisions: 3,
  selectedOfferCount: 2, supportedWonCount: 1, completeCurrentOfferCount: 1,
  averageResponseDays: 1.4, averageReliabilitySnapshot: 92,
  landedCosts: [{ currencyId: 1, currencyCode: "USD", medianValue: 279, sampleSize: 6 }], evidence: [{ recordId: 1 }] };
const salesRep = { salesRepUserId: 41, salesRepName: "Release Manager", ownedOpportunities: 8,
  decidedCount: 6, wonCount: 3, lostCount: 3, commercialConstraintLosses: 2,
  customerDecisionLosses: 1, executionReviewLosses: 0, followUpsDue: 1, followUpsCompleted: 7,
  conversionRatePercent: 50, evidence: [] };
const customer = { customerId: 301, customerName: "Authorized Test Customer", inquiryCount: 4,
  quoteCount: 3, decidedCount: 2, wonCount: 1, lostCount: 1, pendingCount: 1,
  conversionRatePercent: 50, wonValues: [{ currencyId: 1, currencyCode: "USD", medianValue: 5040, sampleSize: 1 }],
  lossReasons: [{ code: "PRICE", label: "Price too high", count: 1 }], evidence: [{ recordId: 11 }] };
const card = { nexoraSerial: "NXR-2026-000077", rfqId: 77, rfqItemId: 402, product,
  inventory: { productId: 701, partNumber: product.partNumber, productName: product.productName,
    observedDemand: 120, qualifiedDemand: 120, quotedDemand: 90, probabilityWeightedDemand: 18.95,
    committedDemand: 12, fulfilledDemand: 10, decidedOpportunities: 19, wonOpportunities: 4,
    conversionRatePercent: 21.05, stockingRecommendationEligible: true,
    recommendation: "Review stocking economics with margin, supplier lead time, MOQ, carrying cost, shelf life, and demand consistency.", evidence: [] },
  suppliers: [{ supplierId: 901, supplierName: "Certified Components Inc.", quoteRevisions: 3,
    selectedOfferCount: 2, supportedWonCount: 1, completeCurrentOfferCount: 1,
    averageResponseDays: 1.4, averageReliabilitySnapshot: 92, landedCosts: [], evidence: [] }],
  nextAction: "Use the evidence ranges as decision support; current quote facts remain authoritative." };

async function installLearningApi(page: Page) {
  await page.route("**/api/commercial-learning/products**", route => route.fulfill({ status: 200,
    contentType: "application/json", body: JSON.stringify([product]) }));
  await page.route("**/api/commercial-learning/suppliers**", route => route.fulfill({ status: 200,
    contentType: "application/json", body: JSON.stringify([supplier]) }));
  await page.route("**/api/commercial-learning/customers**", route => route.fulfill({ status: 200,
    contentType: "application/json", body: JSON.stringify([customer]) }));
  await page.route("**/api/commercial-learning/sales-reps**", route => route.fulfill({ status: 200,
    contentType: "application/json", body: JSON.stringify([salesRep]) }));
  await page.route("**/api/commercial-learning/learning-studio", route => route.fulfill({ status: 200,
    contentType: "application/json", body: JSON.stringify(studio) }));
}

test("Commercial Memory shows periods samples pending outcomes and currency-separated evidence", async ({ page }) => {
  await installLearningApi(page);
  await page.goto("/intelligence/commercial-memory");
  await expect(page.getByRole("heading", { name: "Commercial Memory" })).toBeVisible();
  await expect(page.getByText(product.partNumber)).toBeVisible();
  await expect(page.getByText("4 / 15 / 6")).toBeVisible();
  await expect(page.getByText("USD 405 (4)")).toBeVisible();
  await expect(page.getByText("CQ-81: 12 at USD 420")).toBeVisible();
  await page.getByRole("tab", { name: "Supplier evaluation" }).click();
  await expect(page.getByText("Certified Components Inc.")).toBeVisible();
  await expect(page.getByText("1 linked records")).toBeVisible();
  await page.getByRole("tab", { name: "Sales Rep evaluation" }).click();
  await expect(page.getByRole("cell", { name: "Release Manager", exact: true })).toBeVisible();
  await page.getByRole("tab", { name: "Customer outcomes" }).click();
  await expect(page.getByRole("cell", { name: "Authorized Test Customer", exact: true })).toBeVisible();
  await page.getByRole("tab", { name: "Learning Studio" }).click();
  await expect(page.getByText("Conflicting approved corrections require human review before reuse.")).toBeVisible();
  await expect(page.getByText("SupplierQuoteEvidence:2601")).toBeVisible();
});

test("Commercial Memory Card drills into Product Inventory and Supplier evidence", async ({ page }) => {
  await page.route("**/api/commercial-learning/rfq-items/402/memory-card", route => route.fulfill({ status: 200,
    contentType: "application/json", body: JSON.stringify(card) }));
  await page.route("**/api/procurement/rfqs/77/workbench", route => route.fulfill({ status: 200,
    contentType: "application/json", body: JSON.stringify({ rfqId: 77, rfqNumber: "CRFQ-77",
      nexoraSerial: card.nexoraSerial, customerName: "Authorized Test Customer", currencyCode: "USD",
      lines: [{ id: 402, rfqId: 77, productId: 701, partNumber: product.partNumber,
        description: product.productName, requestedQuantity: 12, availableQuantity: 0, reservedQuantity: 0,
        shortfallQuantity: 12, resolution: "SHORTAGE", resolutionCheckedOn: "2026-07-26T12:00:00Z" }],
      solicitations: [], offers: [], awards: [], purchaseOrders: [], customerQuoteDraft: null }) }));
  await page.goto("/procurement/rfqs/77/sourcing");
  await page.getByRole("button", { name: "Commercial memory" }).click();
  await expect(page.getByText("38 requests · 25 quoted · 19 decided · 4 won · 6 pending")).toBeVisible();
  await expect(page.getByText(/Observed 120 · Quoted 90/)).toBeVisible();
  await expect(page.getByText(/Certified Components Inc.: 3 revisions, 2 selected, 1 supported wins/)).toBeVisible();
});
