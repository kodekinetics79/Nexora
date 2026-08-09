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
  /** FR-SPO-06. Customs data. A KSA import cannot be cleared without both. */
  hsCode?: string | null;
  /** Seeded from the product master when the order is raised; correctable until dispatch. */
  countryOfOrigin?: string | null;
}

export interface SupplierPurchaseOrder {
  id: number;
  rfqId: number;
  purchaseOrderNumber: string;
  supplierId: number;
  supplierName: string;
  currencyId: number;
  currencyCode: string;
  /**
   * FR-SPO-04. APPROVED sits between DRAFT and release. ISSUED is retained because rows raised
   * before the approval gate existed still carry it.
   */
  status:
    | "DRAFT"
    | "APPROVED"
    | "SENT"
    | "ACKNOWLEDGED"
    | "IN_PRODUCTION"
    | "SHIPPED"
    | "ISSUED"
    | "PARTIALLY_RECEIVED"
    | "RECEIVED"
    | "CLOSED"
    | "CANCELLED";
  totalValue: number;
  expectedOn?: string | null;
  version: number;
  lines: PurchaseOrderLine[];
  /** FR-SPO-01. Null on orders raised before approval existed — genuinely unapproved, not missing. */
  approvedBy?: string | null;
  approvedOn?: string | null;
  /**
   * FR-SPO-03. The supplier's answer. Null means unanswered — which for a dispatched order is the
   * state the escalation sweep is chasing, not a blank to be ignored.
   *
   * Only ACCEPTED moves `status` to ACKNOWLEDGED. A COUNTERED or REJECTED order keeps the status it
   * had, so these fields are the ONLY signal that the supplier has said something the buyer must
   * act on.
   */
  acknowledgementStatus?: SupplierAcknowledgementStatus | null;
  acknowledgedBy?: string | null;
  acknowledgedOn?: string | null;
  revisedLeadTimeDays?: number | null;
  committedShipDate?: string | null;
  acknowledgementNote?: string | null;
  /** FR-SPO-06. Incoterms 2020 code. */
  incoterm?: string | null;
  portOfLoading?: string | null;
  portOfDischarge?: string | null;
}

/**
 * FR-SPO-03. The supplier's answer to a dispatched order.
 *
 * COUNTERED is "accepted on different terms" — normally a revised lead time. It and REJECTED are
 * answers but not agreement, so neither moves the order to ACKNOWLEDGED.
 */
export type SupplierAcknowledgementStatus =
  | "ACCEPTED"
  | "REJECTED"
  | "COUNTERED";

export interface SupplierPurchaseOrderAcknowledgementResult {
  id: number;
  number: string;
  /** ACKNOWLEDGED only for ACCEPTED; a counter or a rejection leaves the status where it was. */
  status: SupplierPurchaseOrder["status"];
  acknowledgementStatus: SupplierAcknowledgementStatus;
  /** The supplier's own person, not the Nexora user who recorded the answer. */
  acknowledgedBy: string;
  acknowledgedOn: string;
  revisedLeadTimeDays?: number | null;
  committedShipDate?: string | null;
  note?: string | null;
  version: number;
  replayed: boolean;
}

/**
 * FR-SPO-06. Incoterms 2020, the closed set the server validates against. Anything else is refused
 * — free text here is a contractual allocation of freight and risk that no system can interpret.
 */
export const INCOTERMS_2020 = [
  "EXW",
  "FCA",
  "FAS",
  "FOB",
  "CFR",
  "CIF",
  "CPT",
  "CIP",
  "DAP",
  "DPU",
  "DDP",
] as const;

export type Incoterm = (typeof INCOTERMS_2020)[number];

export interface PurchaseOrderLineTradeTerms {
  lineId: number;
  hsCode?: string | null;
  countryOfOrigin?: string | null;
}

export interface PurchaseOrderTradeTermsResult {
  id: number;
  number: string;
  incoterm?: string | null;
  portOfLoading?: string | null;
  portOfDischarge?: string | null;
  lines: PurchaseOrderLineTradeTerms[];
  version: number;
  replayed: boolean;
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
      /** FR-SPO-06. Optional at creation; correctable until the order is dispatched. */
      incoterm?: Incoterm | null;
      portOfLoading?: string | null;
      portOfDischarge?: string | null;
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

  /**
   * FR-SPO-01. Approves a drafted supplier purchase order for release.
   *
   * The approver is never sent in the body — the server takes it from the authenticated
   * principal, because a client that could name its own approver could name the award approver
   * and satisfy the segregation-of-duties check by assertion.
   */
  approvePurchaseOrder: async (
    purchaseOrderId: number,
    request: { expectedVersion: number; idempotencyKey: string },
  ): Promise<{
    id: number;
    number: string;
    status: string;
    approvedBy: string;
    approvedOn: string;
    version: number;
    segregationOfDutiesEnforced: boolean;
  }> => {
    const { idempotencyKey, ...body } = request;
    return unwrap(
      await axiosInstance.post(
        `/api/procurement/purchase-orders/${purchaseOrderId}/approve`,
        body,
        { headers: commandHeaders(idempotencyKey) },
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

  /**
   * FR-SPO-06. Sets or corrects Incoterm, ports and per-line customs data before dispatch.
   *
   * OMITTING a field leaves it unchanged; it does NOT clear it. Callers must send only what they
   * are actually changing — passing `null` or an empty string for a field the buyer did not touch
   * is indistinguishable from leaving it out, so nothing here can be used to blank a value. The
   * server refuses an amendment after the order has gone to the supplier.
   */
  amendPurchaseOrderTradeTerms: async (
    purchaseOrderId: number,
    request: {
      expectedVersion: number;
      incoterm?: Incoterm | null;
      portOfLoading?: string | null;
      portOfDischarge?: string | null;
      lines?: PurchaseOrderLineTradeTerms[];
      idempotencyKey: string;
    },
  ): Promise<PurchaseOrderTradeTermsResult> => {
    const { idempotencyKey, ...body } = request;
    return unwrap(
      await axiosInstance.post<PurchaseOrderTradeTermsResult>(
        `/api/procurement/purchase-orders/${purchaseOrderId}/trade-terms`,
        body,
        { headers: commandHeaders(idempotencyKey) },
      ),
    );
  },

  /**
   * FR-SPO-03. Records what the supplier said about a dispatched order.
   *
   * `acknowledgedBy` is the SUPPLIER's person and is sent in the body; the Nexora user who keyed
   * it in is taken by the server from the authenticated principal. Nexora has no supplier portal,
   * so these are always two different people and the audit trail keeps them apart.
   *
   * Nothing is validated here that the server does not validate itself — a counter needs a revised
   * lead time or a committed ship date, a rejection needs a note and cannot carry a ship date, a
   * revised lead time is COUNTERED-only and must be positive, and a committed ship date must not be
   * in the past. The caller surfaces the server's own refusal rather than paraphrasing it.
   */
  acknowledgePurchaseOrder: async (
    purchaseOrderId: number,
    request: {
      expectedVersion: number;
      acknowledgementStatus: SupplierAcknowledgementStatus;
      acknowledgedBy: string;
      revisedLeadTimeDays?: number | null;
      /** Calendar date, `YYYY-MM-DD`. The server holds this as a date, not an instant. */
      committedShipDate?: string | null;
      note?: string | null;
      idempotencyKey: string;
    },
  ): Promise<SupplierPurchaseOrderAcknowledgementResult> => {
    const { idempotencyKey, ...body } = request;
    return unwrap(
      await axiosInstance.post<SupplierPurchaseOrderAcknowledgementResult>(
        `/api/procurement/purchase-orders/${purchaseOrderId}/acknowledge`,
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
