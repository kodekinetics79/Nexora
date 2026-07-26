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
}

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
  capture: async (request: CaptureSupplierQuoteRequest) => {
    const response = await axiosInstance.post("/api/supplier-quote-inbox", request, {
      headers: headers(crypto.randomUUID()),
    });
    return response.data;
  },
  upload: async (request: UploadSupplierQuoteRequest) => {
    const form = new FormData();
    for (const [key, value] of Object.entries(request)) form.append(key, value instanceof File ? value : String(value));
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
};

export default supplierQuoteService;
