import axiosInstance from "../axiosInstance";

export type SolicitationStatus =
  | "PENDING_DISPATCH"
  | "DISPATCHING"
  | "SENT"
  | "DELIVERY_FAILED"
  | "RESPONDED"
  | "DECLINED"
  | "EXPIRED";

export interface SourcingLine {
  id: number;
  rfqId: number;
  sourcingCaseId?: number | null;
  demandLineId?: number | null;
  productId?: number | null;
  partNumber?: string | null;
  description: string;
  requestedQuantity: number;
  availableQuantity: number;
  reservedQuantity: number;
  shortfallQuantity: number;
  requiredOn?: string | null;
  resolution:
    | "IN_STOCK"
    | "PARTIAL"
    | "INCOMING"
    | "SHORTAGE"
    | "UNKNOWN"
    | "POSSIBLE_MATCH";
  resolutionCheckedOn?: string | null;
}

export interface SupplierSolicitation {
  id: number;
  rfqId: number;
  supplierId: number;
  supplierName: string;
  supplierEmail?: string | null;
  status: SolicitationStatus;
  channel: string;
  attemptCount: number;
  providerReference?: string | null;
  lastErrorCode?: string | null;
  sentOn?: string | null;
  respondedOn?: string | null;
  updatedOn: string;
  requestedRfqItemIds: number[];
}

export interface SupplierOffer {
  id: number;
  solicitationId: number;
  rfqItemId: number;
  supplierId: number;
  supplierName: string;
  quoteReference?: string | null;
  quoteRevision: number;
  currencyId: number;
  currencyCode: string;
  quantity: number;
  availableQuantity?: number | null;
  unitPrice: number;
  freightCost: number;
  dutyCost: number;
  otherCost: number;
  landedUnitCost?: number | null;
  leadTimeDays?: number | null;
  reliabilitySnapshot?: number | null;
  validUntil?: string | null;
  eligible: boolean;
  blockingReasons: string[];
  awarded: boolean;
  version: number;
}

export interface QuoteComparisonLine {
  supplierQuotedItemId: number;
  supplierId: number;
  quantity: number;
  availableQuantity?: number | null;
  unitPrice: number;
  landedUnitCost?: number | null;
  currencyId: number;
  leadTimeDays?: number | null;
  reliability?: number | null;
  validUntil?: string | null;
  blockers: string[];
  eligible: boolean;
}

export interface QuoteComparisonResult {
  rfqItemId: number;
  lines: QuoteComparisonLine[];
  recommendedSupplierQuotedItemId?: number | null;
}

export interface SourcingAward {
  id: number;
  rfqItemId: number;
  supplierQuotedItemId: number;
  supplierName: string;
  supplierId: number;
  quantity: number;
  landedUnitCost: number;
  currencyCode: string;
  currencyId: number;
  status: string;
  rationale?: string | null;
  purchaseOrderId?: number | null;
  version: number;
}

export interface PurchaseOrderLine {
  id: number;
  rfqItemId: number;
  productId: number;
  description: string;
  orderedQuantity: number;
  receivedQuantity: number;
  openQuantity: number;
  unitCost: number;
  landedUnitCost: number;
  warehouseId: number;
}

export interface SupplierPurchaseOrder {
  id: number;
  rfqId: number;
  purchaseOrderNumber: string;
  supplierId: number;
  supplierName: string;
  currencyId: number;
  currencyCode: string;
  status: "DRAFT" | "ISSUED" | "PARTIALLY_RECEIVED" | "RECEIVED" | "CANCELLED";
  totalValue: number;
  expectedOn?: string | null;
  version: number;
  lines: PurchaseOrderLine[];
}

export interface SupplierPurchaseOrderSummary {
  id: number;
  purchaseOrderNumber: string;
  rfqId: number;
  rfqNumber: string;
  nexoraSerial: string;
  supplierId: number;
  supplierName: string;
  currencyCode: string;
  status: SupplierPurchaseOrder["status"];
  totalValue: number;
  expectedOn?: string | null;
  createdOn: string;
  lineCount: number;
  openQuantity: number;
}

export interface CreatePurchaseOrderResult {
  id: number;
  purchaseOrderNumber: string;
  status: string;
  replayed: boolean;
}

export interface SourcingWorkbench {
  rfqId?: number | null;
  rfqNumber?: string | null;
  nexoraSerial?: string | null;
  customerName?: string | null;
  currencyCode?: string | null;
  lines: SourcingLine[];
  solicitations: SupplierSolicitation[];
  offers: SupplierOffer[];
  awards: SourcingAward[];
  purchaseOrders: SupplierPurchaseOrder[];
  customerQuoteDraft?: {
    quoteId: number;
    quoteNumber: string;
    currencyId?: number | null;
    lines: Array<{ quoteItemId: number; rfqItemId: number; quantity: number; unitPrice: number; totalAmount: number }>;
  } | null;
}

export interface SourcingCaseCandidate {
  id: number;
  supplierId: number;
  supplierName: string;
  contactEmail?: string | null;
  rank: number;
  evidenceType: string;
  recommendationReason: string;
  evidenceScore: number;
  evidenceFreshOn?: string | null;
  selected: boolean;
  approvalStatus?: string | null;
  governanceStatus?: string | null;
  verificationStatus?: string | null;
  complianceStatus?: string | null;
  riskStatus?: string | null;
  readinessStatus?: string | null;
  eligibleForSupplierRfq: boolean;
  blockingReasons?: string[];
}

export interface SourcingCase {
  id: number;
  commercialDemandLineId: number;
  rfqId: number;
  rfqItemId: number;
  nexoraSerial: string;
  productId?: number | null;
  requestedPartNumber?: string | null;
  description: string;
  requestedQuantity: number;
  stockQuantity: number;
  unfulfilledQuantity: number;
  requiredOn?: string | null;
  searchLimit: 10 | 20 | 50;
  status: string;
  nextAction: string;
  version: number;
  candidates: SourcingCaseCandidate[];
}

export interface SourcingCandidateSearchResult {
  sourcingCaseId: number;
  requestedLimit: 10 | 20 | 50;
  resultCount: number;
  version: number;
  replayed: boolean;
  candidates: SourcingCaseCandidate[];
}

export interface PreparedSupplierRfqResult {
  sourcingCaseId: number;
  supplierSolicitationId: number;
  status: string;
  sourcingCaseVersion: number;
  solicitationVersion: number;
  replayed: boolean;
}

export interface QueuedSupplierRfqResult {
  sourcingCaseId: number;
  supplierSolicitationId: number;
  status: string;
  sourcingCaseVersion: number;
  solicitationVersion: number;
  replayed: boolean;
}

export interface SupplierRfqPreparationOutcome {
  supplierId: number;
  succeeded: boolean;
  queued?: QueuedSupplierRfqResult;
  error?: unknown;
}

export interface CreateSolicitationsRequest {
  supplierIds: number[];
  rfqItemIds: number[];
  dueOn?: string;
  operationId: string;
}

export interface SolicitationBatchResult {
  supplierId: number;
  succeeded: boolean;
  value?: unknown;
  error?: unknown;
}

export interface CaptureSupplierResponseRequest {
  quoteReference: string;
  quoteRevision: number;
  validUntil: string;
  lines: Array<{
    rfqItemId: number;
    productId?: number | null;
    quantity: number;
    unitPrice: number;
    leadTimeDays?: number;
    availableQuantity?: number;
    freightCost: number;
    dutyCost: number;
    otherCost: number;
    taxAmount: number;
    discountAmount: number;
    reliabilitySnapshot?: number;
    currencyId: number;
    minimumOrderQuantity?: number;
  }>;
}

const unwrap = <T>(response: { data: T }): T => response.data;
const commandHeaders = (key: string) => ({
  "Idempotency-Key": key,
  "X-Correlation-ID": crypto.randomUUID(),
});

const procurementService = {
  getPurchaseOrders: async (
    search = "",
    limit = 50,
  ): Promise<SupplierPurchaseOrderSummary[]> =>
    unwrap(
      await axiosInstance.get<SupplierPurchaseOrderSummary[]>(
        "/api/procurement/purchase-orders",
        { params: { search: search.trim(), limit } },
      ),
    ),

  getWorkbench: async (rfqId?: number): Promise<SourcingWorkbench> =>
    rfqId
      ? unwrap(
          await axiosInstance.get<SourcingWorkbench>(
            `/api/procurement/rfqs/${rfqId}/workbench`,
          ),
        )
      : Promise.reject(
          new Error("An RFQ is required for the sourcing workbench."),
        ),

  getSourcingCase: async (sourcingCaseId: number): Promise<SourcingCase> =>
    unwrap(
      await axiosInstance.get<SourcingCase>(
        `/api/procurement/sourcing-cases/${sourcingCaseId}`,
      ),
    ),

  createOrOpenSourcingCase: async (
    rfqId: number,
    rfqItemId: number,
    searchLimit: 10 | 20 | 50 = 10,
  ): Promise<SourcingCase> =>
    unwrap(
      await axiosInstance.post<SourcingCase>(
        "/api/procurement/sourcing-cases",
        { rfqId, rfqItemId, searchLimit, sourceEntireQuantity: false },
        {
          headers: commandHeaders(
            `sourcing-case:${rfqItemId}:${crypto.randomUUID()}`,
          ),
        },
      ),
    ),

  refreshSourcingCaseCandidates: async (
    sourcingCaseId: number,
    limit: 10 | 20 | 50,
    expectedVersion: number,
  ): Promise<SourcingCandidateSearchResult> =>
    unwrap(
      await axiosInstance.post<SourcingCandidateSearchResult>(
        `/api/procurement/sourcing-cases/${sourcingCaseId}/supplier-candidates/search`,
        { limit, expectedVersion },
        {
          headers: commandHeaders(
            `sourcing-candidates:${sourcingCaseId}:${limit}:${crypto.randomUUID()}`,
          ),
        },
      ),
    ),

  prepareSupplierRfqs: async (
    sourcingCaseId: number,
    supplierIds: number[],
    expectedVersion: number,
    operationId: string,
    /**
     * Supplier response deadline, ISO-8601 UTC. The API rejects past or
     * non-UTC values, so callers must send an instant (trailing `Z`), not a
     * local date string. Omit for no deadline.
     */
    dueOn?: string | null,
  ): Promise<SupplierRfqPreparationOutcome[]> => {
    const results: SupplierRfqPreparationOutcome[] = [];
    let version = expectedVersion;
    for (const supplierId of supplierIds) {
      try {
        const prepared = unwrap(
          await axiosInstance.post<PreparedSupplierRfqResult>(
          `/api/procurement/sourcing-cases/${sourcingCaseId}/supplier-rfqs`,
          { supplierId, expectedVersion: version, dueOn: dueOn ?? null },
          {
            headers: commandHeaders(
              `prepare-supplier-rfq:${sourcingCaseId}:${supplierId}:${operationId}`,
            ),
          },
          ),
        );
        const queued = unwrap(
          await axiosInstance.post<QueuedSupplierRfqResult>(
            `/api/procurement/sourcing-cases/${sourcingCaseId}/supplier-rfqs/${prepared.supplierSolicitationId}/queue`,
            {
              expectedSourcingCaseVersion: prepared.sourcingCaseVersion,
              expectedSolicitationVersion: prepared.solicitationVersion,
            },
            {
              headers: commandHeaders(
                `queue-supplier-rfq:${sourcingCaseId}:${supplierId}:${operationId}`,
              ),
            },
          ),
        );
        results.push({ supplierId, succeeded: true, queued });
        version = queued.sourcingCaseVersion;
      } catch (error) {
        results.push({ supplierId, succeeded: false, error });
        break;
      }
    }
    return results;
  },

  getQuoteComparison: async (
    rfqItemId: number,
  ): Promise<QuoteComparisonResult> =>
    unwrap(
      await axiosInstance.get<QuoteComparisonResult>(
        `/api/procurement/rfq-items/${rfqItemId}/quote-comparison`,
      ),
    ),

  createSolicitations: async (
    rfqId: number,
    request: CreateSolicitationsRequest,
  ): Promise<SolicitationBatchResult[]> => {
    const results = await Promise.allSettled(
      request.supplierIds.map((supplierId) =>
        axiosInstance.post(
          "/api/procurement/solicitations",
          {
            rfqId,
            supplierId,
            rfqItemIds: request.rfqItemIds,
            dueOn: request.dueOn,
          },
          {
            headers: commandHeaders(
              `solicit:${rfqId}:${supplierId}:${request.operationId}`,
            ),
          },
        ),
      ),
    );
    return results.map((result, index) =>
      result.status === "fulfilled"
        ? {
            supplierId: request.supplierIds[index],
            succeeded: true,
            value: result.value.data,
          }
        : {
            supplierId: request.supplierIds[index],
            succeeded: false,
            error: result.reason,
          },
    );
  },

  retrySolicitation: async (solicitationId: number, idempotencyKey: string) =>
    unwrap(
      await axiosInstance.post(
        `/api/procurement/solicitations/${solicitationId}/retry`,
        {},
        {
          headers: commandHeaders(idempotencyKey),
        },
      ),
    ),

  captureSupplierResponse: async (
    solicitationId: number,
    request: CaptureSupplierResponseRequest,
    idempotencyKey: string,
  ) =>
    unwrap(
      await axiosInstance.post(
        "/api/procurement/supplier-quotes",
        {
          solicitationId,
          supplierQuoteReference: request.quoteReference,
          revision: request.quoteRevision,
          validUntil: request.validUntil,
          lines: request.lines,
        },
        {
          headers: commandHeaders(idempotencyKey),
        },
      ),
    ),

  approveAward: async (request: {
    supplierQuotedItemId: number;
    quantity: number;
    rationale: string;
    expectedQuoteVersion: number;
    idempotencyKey: string;
  }) =>
    unwrap(
      await axiosInstance.post("/api/procurement/awards", request, {
        headers: commandHeaders(request.idempotencyKey),
      }),
    ),

  applyCustomerQuotePricing: async (request: {
    quoteItemId: number;
    sourcingAwardId: number;
    targetMarginPercent: number;
    rationale: string;
    idempotencyKey: string;
  }) => {
    const { idempotencyKey, ...body } = request;
    return unwrap(await axiosInstance.post("/api/supplier-quote-inbox/customer-quote-pricing", body, {
      headers: commandHeaders(idempotencyKey),
    }));
  },

  createPurchaseOrder: async (
    rfqId: number,
    request: {
      awardIds: number[];
      supplierId: number;
      currencyId: number;
      warehouseId: number;
      expectedOn: string;
      idempotencyKey: string;
    },
  ): Promise<CreatePurchaseOrderResult> => {
    const { idempotencyKey, ...body } = request;
    return unwrap(
      await axiosInstance.post(
        "/api/procurement/purchase-orders",
        { rfqId, ...body },
        {
          headers: commandHeaders(idempotencyKey),
        },
      ),
    );
  },

  issuePurchaseOrder: async (
    purchaseOrderId: number,
    request: {
      expectedVersion: number;
      deliveryEvidenceReference: string;
      deliveryEvidenceSha256: string;
      deliveredOn: string;
      idempotencyKey: string;
    },
  ): Promise<CreatePurchaseOrderResult> => {
    const { idempotencyKey, ...body } = request;
    return unwrap(
      await axiosInstance.post(
        `/api/procurement/purchase-orders/${purchaseOrderId}/issue`,
        body,
        { headers: commandHeaders(idempotencyKey) },
      ),
    );
  },

  postReceipt: async (
    purchaseOrderId: number,
    request: {
      warehouseId: number;
      receivedOn: string;
      receiptNumber: string;
      expectedPurchaseOrderVersion: number;
      idempotencyKey: string;
      lines: Array<{ purchaseOrderLineId: number; quantity: number }>;
    },
  ) => {
    const { idempotencyKey, ...body } = request;
    return unwrap(
      await axiosInstance.post(
        "/api/procurement/goods-receipts",
        { purchaseOrderId, ...body },
        {
          headers: commandHeaders(idempotencyKey),
        },
      ),
    );
  },
};

export default procurementService;
