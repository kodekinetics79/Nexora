import axiosInstance from "../axiosInstance";

export type SupplierQuoteInboxStatus = "REVIEW_REQUIRED" | "READY_FOR_COMPARISON";
export type SupplierQuoteReviewStatus = "ACCEPTED" | "CORRECTED" | "REJECTED";

export interface SupplierQuoteInboxItem {
  supplierQuoteId: number;
  supplierId: number;
  supplierName: string;
  supplierQuoteReference: string;
  nexoraSerial: string;
  sourcingCaseId: number;
  currentRevisionNumber: number;
  inboxStatus: SupplierQuoteInboxStatus;
  updatedOn: string;
  reviewRequiredCount: number;
}

export interface SupplierQuoteEvidence {
  id: number;
  supplierQuoteLineId: number | null;
  fieldName: string;
  originalValue: string | null;
  normalizedValue: string | null;
  confidence: number;
  method: string;
  critical: boolean;
  reviewRequired: boolean;
  latestReviewStatus: SupplierQuoteReviewStatus | null;
  correctedValue: string | null;
}

export interface SupplierQuoteLine {
  id: number;
  lineNumber: number;
  rfqItemId: number;
  partNumber: string | null;
  description: string;
  quantity: number;
  availableQuantity: number | null;
  unitPrice: number;
  leadTimeDays: number | null;
  /**
   * What the supplier wrote about warranty, and the number a person read out of it — carried here so
   * the review screen can show back what capture recorded. A value that can be captured and never
   * seen again is the wiring gap this codebase keeps paying for: the reviewer is the one person
   * positioned to challenge either field, and they cannot challenge what the screen does not show.
   */
  warranty: string | null;
  warrantyMonths: number | null;
}

export type NegotiationDisposition = "PREPARED" | "DEFERRED" | "DISMISSED";

export interface NegotiationEvidence {
  label: string;
  value: string;
  source?: string | null;
}

export interface SupplierBidQualityFlag {
  code: string;
  label: string;
  severity: "INFO" | "WARNING" | "CRITICAL";
  blocking: boolean;
  explanation: string;
  evidence: NegotiationEvidence[];
  limitations: string[];
  confidence?: number | null;
  sampleSize?: number | null;
}

export interface SupplierNegotiationRecommendation {
  code: string;
  title: string;
  summary: string;
  rationale: string;
  confidence: number | null;
  targetValue?: number | null;
  targetUnit?: string | null;
  constraints: string[];
  evidence: NegotiationEvidence[];
  limitations: string[];
  expiresOn?: string | null;
  sampleSize?: number | null;
  mode?: string | null;
}

export interface SupplierNegotiationDecision {
  id: number;
  supplierQuoteRevisionId: number;
  recommendationCode: string;
  disposition: NegotiationDisposition;
  reason: string;
  decidedOn: string;
  decidedBy?: string | null;
}

export interface SupplierNegotiationRound {
  roundNumber: number;
  currencyCode: string;
  validUntil?: string | null;
  incoterms?: string | null;
  paymentTerms?: string | null;
  freightAmount?: number | null;
  taxAmount?: number | null;
  capturedOn?: string | null;
}

export interface SupplierQuoteNegotiation {
  currentRound: SupplierNegotiationRound | null;
  bidQuality: SupplierBidQualityFlag[];
  recommendations: SupplierNegotiationRecommendation[];
  decisions: SupplierNegotiationDecision[];
  decisionTotal: number;
  decisionsTruncated: boolean;
  quoteVersion: number;
}

export interface RecordNegotiationDecisionRequest {
  recommendationCode: string;
  disposition: NegotiationDisposition;
  reason: string;
  expectedQuoteVersion: number;
}

interface SupplierQuoteNegotiationWire {
  currentRound: SupplierNegotiationRound;
  quoteVersion: number;
  mode: string;
  policyVersion: string;
  priorDecisionTotal: number;
  priorDecisionsTruncated: boolean;
  recommendations: Array<Omit<SupplierNegotiationRecommendation, "summary" | "constraints" | "evidence"> & {
    summary?: string;
    constraints?: string[];
    evidence: Array<NegotiationEvidence | string>;
  }>;
  bidFlags: Array<Omit<SupplierBidQualityFlag, "label" | "evidence" | "limitations"> & {
    label?: string;
    evidence: Array<NegotiationEvidence | string>;
    limitations?: string[];
  }>;
  priorDecisions: Array<Omit<SupplierNegotiationDecision, "id" | "decidedBy"> & {
    decisionId: number;
    actor?: string | null;
  }>;
}

const readableCode = (value: string) =>
  value.replaceAll("_", " ").toLowerCase().replace(/(^|\s)\S/g, (letter) => letter.toUpperCase());

const normalizeEvidence = (items?: Array<NegotiationEvidence | string>): NegotiationEvidence[] =>
  (items ?? []).map((item) =>
    typeof item === "string" ? { label: "Evidence", value: item } : item,
  );

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);

const normalizeNegotiation = (value: unknown): SupplierQuoteNegotiation => {
  if (!isRecord(value) || !isRecord(value.currentRound) ||
    !Number.isInteger(value.quoteVersion) || Number(value.quoteVersion) <= 0 ||
    !Number.isInteger(value.priorDecisionTotal) || Number(value.priorDecisionTotal) < 0 ||
    typeof value.priorDecisionsTruncated !== "boolean" ||
    !Array.isArray(value.bidFlags) || !Array.isArray(value.recommendations) ||
    !Array.isArray(value.priorDecisions) || typeof value.mode !== "string" ||
    typeof value.policyVersion !== "string") {
    throw new Error("Supplier negotiation response does not match the required contract.");
  }
  const data = value as unknown as SupplierQuoteNegotiationWire;
  if (!Number.isInteger(data.currentRound.roundNumber) || !data.currentRound.currencyCode) {
    throw new Error("Supplier negotiation round is incomplete.");
  }
  return {
    currentRound: data.currentRound,
    quoteVersion: data.quoteVersion,
    decisionTotal: data.priorDecisionTotal,
    decisionsTruncated: data.priorDecisionsTruncated,
    bidQuality: data.bidFlags.map((flag) => ({
      ...flag,
      label: flag.label ?? readableCode(flag.code),
      evidence: normalizeEvidence(flag.evidence),
      limitations: flag.limitations ?? [],
    })),
    recommendations: data.recommendations.map((item) => ({
      ...item,
      summary: item.summary ?? item.rationale,
      constraints: item.constraints ?? [],
      evidence: normalizeEvidence(item.evidence),
      limitations: item.limitations ?? [],
    })),
    decisions: data.priorDecisions.map((item) => ({
      ...item,
      id: item.decisionId,
      decidedBy: item.actor ?? null,
    })),
  };
};

export interface SupplierQuoteRevision {
  revisionId: number;
  revisionNumber: number;
  captureChannel: string;
  currencyId: number;
  validUntil: string | null;
  requiresReview: boolean;
  capturedOn: string;
  lines: SupplierQuoteLine[];
  evidence: SupplierQuoteEvidence[];
  // The charge block. A reviewer cannot spot missing freight or missing duty on a screen that
  // never showed either, and this detail carried lines and evidence only.
  incoterms?: string | null;
  freightAmount?: number;
  taxAmount?: number;
  dutyAmount?: number;
  otherAmount?: number;
  discountAmount?: number;
}

export interface SupplierQuoteDetail {
  supplierQuoteId: number;
  supplierId: number;
  supplierName: string;
  supplierSolicitationId: number;
  sourcingCaseId: number;
  rfqId: number;
  nexoraSerial: string;
  supplierQuoteReference: string;
  currentRevisionNumber: number;
  inboxStatus: SupplierQuoteInboxStatus;
  version: number;
  revisions: SupplierQuoteRevision[];
}

export interface CaptureSupplierQuoteLine {
  lineNumber: number;
  rfqItemId: number;
  commercialDemandLineId: number;
  partNumber?: string | null;
  manufacturer?: string | null;
  supplierPartNumber?: string | null;
  description: string;
  quantity: number;
  availableQuantity?: number | null;
  unitOfMeasure: string;
  unitPrice: number;
  minimumOrderQuantity?: number | null;
  leadTimeDays?: number | null;
  availabilityType?: string | null;
  originCountry?: string | null;
  warranty?: string | null;
  /**
   * The numeric companion to the free-text warranty above. It is typed, never parsed out of the
   * wording, and null means nobody recorded a period rather than a stated zero months.
   */
  warrantyMonths?: number | null;
  isAlternate: boolean;
  exceptions?: string | null;
  evidence: never[];
}

export interface CaptureSupplierQuoteRequest {
  supplierId: number;
  supplierSolicitationId: number;
  sourcingCaseId: number;
  nexoraSerial: string;
  supplierQuoteReference: string;
  revisionNumber: number;
  captureChannel: "MANUAL" | "OFFLINE";
  sourceDocumentId: null;
  sourceIdentity: string;
  sourceSha256: string;
  currencyId: number;
  validUntil?: string | null;
  incoterms?: string | null;
  freightAmount: number;
  taxAmount: number;
  paymentTerms?: string | null;
  notes?: string | null;
  lines: CaptureSupplierQuoteLine[];
  evidence: never[];
}

export interface UploadSupplierQuoteRequest {
  file: File;
  supplierId: number;
  supplierSolicitationId: number;
  sourcingCaseId: number;
  nexoraSerial: string;
  supplierQuoteReference: string;
  revisionNumber: number;
  currencyId: number;
  // A spreadsheet of part numbers and unit prices does not state the round's freight, and it never
  // states the duty the buyer will pay at the border. Intake used to send neither — it hardcoded
  // zero — so an uploaded quote's landed cost was its bare unit price and every price derived from
  // it was short by the omission divided by (1 - margin).
  incoterms?: string;
  freightAmount?: number;
  taxAmount?: number;
  dutyAmount?: number;
  otherAmount?: number;
  discountAmount?: number;
}

const headers = (idempotencyKey?: string) => ({
  "X-Correlation-ID": crypto.randomUUID(),
  ...(idempotencyKey ? { "Idempotency-Key": idempotencyKey } : {}),
});

const supplierQuoteService = {
  getInbox: async (status?: SupplierQuoteInboxStatus): Promise<SupplierQuoteInboxItem[]> => {
    const response = await axiosInstance.get<SupplierQuoteInboxItem[]>("/api/supplier-quote-inbox", {
      params: { status, limit: 200 },
    });
    return response.data;
  },
  getById: async (id: number): Promise<SupplierQuoteDetail> => {
    const response = await axiosInstance.get<SupplierQuoteDetail>(`/api/supplier-quote-inbox/${id}`);
    return response.data;
  },
  getNegotiation: async (id: number): Promise<SupplierQuoteNegotiation> => {
    const response = await axiosInstance.get<unknown>(
      `/api/supplier-quote-inbox/${id}/negotiation`,
    );
    return normalizeNegotiation(response.data);
  },
  capture: async (request: CaptureSupplierQuoteRequest) => {
    const response = await axiosInstance.post("/api/supplier-quote-inbox", request, {
      headers: headers(crypto.randomUUID()),
    });
    return response.data;
  },
  upload: async (request: UploadSupplierQuoteRequest) => {
    const form = new FormData();
    // Undefined optional fields are omitted rather than sent as the string "undefined", which the
    // multipart model binder would reject outright.
    for (const [key, value] of Object.entries(request)) {
      if (value === undefined || value === null || value === "") continue;
      form.append(key, value instanceof File ? value : String(value));
    }
    const response = await axiosInstance.post("/api/supplier-quote-inbox/documents", form, {
      headers: headers(crypto.randomUUID()),
    });
    return response.data;
  },
  reviewEvidence: async (
    supplierQuoteId: number,
    revisionId: number,
    evidenceId: number,
    request: { status: SupplierQuoteReviewStatus; correctedValue?: string | null; reason: string },
  ) => {
    await axiosInstance.post(
      `/api/supplier-quote-inbox/${supplierQuoteId}/revisions/${revisionId}/evidence/${evidenceId}/reviews`,
      request,
      { headers: headers() },
    );
  },
  projectForComparison: async (supplierQuoteId: number, expectedVersion: number) => {
    const response = await axiosInstance.post(
      `/api/supplier-quote-inbox/${supplierQuoteId}/comparison-projections`,
      { expectedVersion },
      { headers: headers(crypto.randomUUID()) },
    );
    return response.data;
  },
  recordNegotiationDecision: async (
    supplierQuoteId: number,
    request: RecordNegotiationDecisionRequest,
    idempotencyKey: string,
  ) => {
    const response = await axiosInstance.post<SupplierNegotiationDecision>(
      `/api/supplier-quote-inbox/${supplierQuoteId}/negotiation-decisions`,
      request,
      { headers: headers(idempotencyKey) },
    );
    return response.data;
  },
};

export default supplierQuoteService;
