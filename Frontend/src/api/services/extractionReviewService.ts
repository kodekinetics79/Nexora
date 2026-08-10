import axiosInstance from '../axiosInstance';
import axios from 'axios';
import leadService from './leadService';
import type { LeadResponseDTO, PaginatedResponse } from './leadService';

// A single row in the "needs review" queue. Mirrors the backend contract for
// GET /api/Lead/needs-review — items whose AI extraction requires a human to
// verify/correct before they can flow downstream.
export interface NeedsReviewItem {
  id: number;
  rfqno: string | null;
  buyersName: string | null;
  recDate: string;
  bidClosingDate: string | null;
  leadSource: string;
  aiconfidence: number | null;
  itemCount: number;
  reviewReason: string | null;
  receivedOn: string | null;
  reviewVersion: number;
  /**
   * Lines on this document that a human still has to look at, served from the
   * evidence ledger's own per-line validation status — the same verdict the
   * review screen renders, so the queue and the workbench cannot disagree.
   *
   * Null when the document's extraction path wrote no ledger (PDF/OCR/model),
   * because there is then no per-line verdict at all; the queue shows the bare
   * line count rather than claiming "0 of 8 need a check".
   *
   * NOTE: `aiconfidence` is still on the wire but is deliberately not rendered
   * anywhere. It is not a measured accuracy — see pages/ExtractionReview/needsCheck.ts.
   */
  linesNeedingCheck?: number | null;
}

export interface NeedsReviewParams {
  pageNumber?: number;
  pageSize?: number;
  search?: string;
}

// Editable header fields the reviewer can correct before saving/approving.
// Only fields present in the payload are applied server-side (LeadReviewHeaderDTO:
// "Only non-null fields are applied to the lead on save/approve").
export interface ReviewHeaderPayload {
  rfqno?: string;
  buyersName?: string;
  bidClosingDate?: string;
  /**
   * FR-RFQ-04. The delivery date the BUYER asked for — never a supplier lead time.
   * Correctable here because extraction is the only thing that has ever written it.
   */
  requiredDeliveryDate?: string;
  opportunityNo?: string;
  headerRemarks?: string;
  /**
   * The client organisation this lead came from. Accepted by the backend since
   * LeadReviewHeaderDTO gained CustomerId/ContactId, and the ONLY write path that
   * sets a lead's customer — a human confirming it here is also what seeds the
   * alias learning loop. Omit to leave the current link untouched; the endpoint
   * has no "clear the customer" instruction.
   */
  customerId?: number;
  /**
   * The buying contact inside that customer. The backend rejects a contact
   * without a customer (unless the lead already has one) and rejects a contact
   * that belongs to a different customer.
   */
  contactId?: number;
}

// Editable line-item fields. `id` is present for existing rows and omitted for
// rows the reviewer adds during review.
export interface ReviewItemPayload {
  id?: number;
  lineItemNo?: string;
  productShortName?: string;
  productShortDescription?: string;
  commodityProduct?: string;
  itemMaterialCode?: string;
  currency?: string;
  unitOfMeasure?: string;
  unitPrice?: number;
  quantity?: number;
  manufacturerName?: string;
  manufacturerPartNumber?: string;
  alternateProductName?: string;
  alternatePartNumber?: string;
  itemText?: string;
  leadTime?: string;
}

export interface SubmitReviewPayload {
  action: 'save' | 'approve';
  expectedVersion: number;
  reason?: string;
  header: ReviewHeaderPayload;
  items: ReviewItemPayload[];
}

export interface ProcessingAiAttemptEvidence {
  attemptNumber: number;
  provider: string;
  model: string;
  result: string;
  httpStatus?: number | null;
  errorCode?: string | null;
  inputTokens: number;
  outputTokens: number;
  latencyMilliseconds: number;
  startedOn: string;
  completedOn: string;
}

export interface ProcessingAiRequestEvidence {
  requestId: string;
  extractionJobId?: number | null;
  sourceDocumentOccurrenceId?: number | null;
  provider: string;
  providerClass: string;
  model: string;
  version: string;
  reason: string;
  result: string;
  costStatus: string;
  estimatedCost?: number | null;
  costCurrency?: string | null;
  costPricingVersion?: string | null;
  budgetWarning: boolean;
  inputTokens: number;
  outputTokens: number;
  attempts: ProcessingAiAttemptEvidence[];
}

export interface ProcessingOccurrenceEvidence {
  occurrenceId: number;
  sourceDocumentId?: number | null;
  extractionJobId?: number | null;
  intakeStatus: string;
  receivedOn: string;
  originalFileName?: string | null;
  contentHash?: string | null;
  classification?: string | null;
  correlationId?: string | null;
}

export interface ProcessingJobEvidence {
  extractionJobId: number;
  sourceDocumentOccurrenceId?: number | null;
  sourceType: string;
  status: string;
  attempts: number;
  maxAttempts: number;
  result?: string | null;
  errorCode?: string | null;
  createdOn: string;
  updatedOn: string;
}

export interface ProcessingRunEvidence {
  extractionRunId: number;
  runId: string;
  sourceDocumentId: number;
  extractionJobId: number;
  attemptNumber: number;
  status: string;
  processingPath: string;
  ocrStatus: string;
  ocrPageCount: number;
  ocrTruncated: boolean;
  processingCostAmount?: number | null;
  processingCostCurrency?: string | null;
  processingCostStatus: string;
  ocrCostStatus: string;
  parserVersion: string;
  schemaVersion: string;
  failureReason?: string | null;
  createdOn: string;
  completedOn?: string | null;
}

export interface LeadProcessingEvidence {
  leadId: number;
  nexoraSerial?: string | null;
  rfqs: { rfqId: number; rfqNumber: string; createdOn: string }[];
  occurrences: ProcessingOccurrenceEvidence[];
  jobs: ProcessingJobEvidence[];
  runs: ProcessingRunEvidence[];
  aiRequests: ProcessingAiRequestEvidence[];
  localRequestCount: number;
  externalRequestCount: number;
  localRequestRate: number;
  externalRequestRate: number;
  externalCostAmount?: number | null;
  externalCostCurrency?: string | null;
  externalCostStatus: string;
}

// ─── Glass-box field evidence ───────────────────────────────────────────────
// One row per extracted value that the evidence ledger could tie back to a
// place in the customer's document: FieldEvidence -> DocumentRegion (bbox +
// SourceAddress, e.g. 'Sheet1'!B7) -> DocumentPage.
//
// The ledger is only written on the structured-spreadsheet path today; PDF/OCR
// runs discard word boxes. Every consumer therefore has to treat "no entry" as
// the normal case and say so plainly ("Source not mapped for this document")
// rather than implying the value is unsourced or wrong.
export interface LeadFieldEvidenceEntry {
  /** Canonical field name, e.g. "quantity" or "productShortDescription". */
  fieldName: string;
  /** The LeadItem this value belongs to; null/absent for header-level fields. */
  lineItemId?: number | null;
  /** Human-quotable address inside the source document, e.g. "Sheet1!B7". */
  sourceAddress?: string | null;
  pageNumber?: number | null;
  sheetName?: string | null;
  rawValue?: string | null;
  normalizedValue?: string | null;
  valueKind?: string | null;
  /** Unvalidated | Valid | Warning | Invalid (FieldValidationStatus). */
  validationStatus?: string | null;
  /**
   * The certainty the extractor recorded for THIS value, 0..1. Captured by the
   * evidence ledger since it was built and omitted from the DTO until now, so a
   * reviewer could see where a value came from but not whether it was read with
   * certainty or salvaged.
   *
   * NOT an accuracy, and never rendered as a percentage: on the deterministic
   * path it is a parse verdict from a closed set (1.0 parsed exactly, 0.2 source
   * text that could not be interpreted, 0 nothing stated). See
   * pages/ExtractionReview/fieldCertainty.ts, which renders its meaning.
   * Absent on older backends and on paths that write no ledger.
   */
  confidence?: number | null;
  extractor?: string | null;
  /**
   * Normalisation steps applied between raw and normalized. The backend stores
   * this as a JSON string (TransformationsJson); accept either shape so the UI
   * works against whichever the endpoint settles on.
   */
  transformations?: string[] | null;
  transformationsJson?: string | null;
  runId?: string | null;
  createdOn?: string | null;
}

export interface LeadFieldEvidenceResponse {
  leadId: number;
  entries: LeadFieldEvidenceEntry[];
  /**
   * The document's own prose OUTSIDE the extracted table, verbatim and unparsed
   * — the instruction block naming required warranty, validity, country of
   * origin, Incoterms and submission method, and any "as per attached
   * specification". It was read and discarded for every document on this path
   * until now, recoverable only by opening the source file by hand.
   *
   * Absent when the document had none, when the extraction path retains none,
   * or on older backends.
   */
  documentNarrative?: string | null;
  /**
   * Whether the extraction path for this document wrote a source-address
   * ledger at all. Absent on older backends — callers should fall back to
   * `entries.length > 0`.
   */
  mapped?: boolean;
}

/** Normalises the two accepted transformation shapes into a plain string list. */
export const parseTransformations = (entry: LeadFieldEvidenceEntry): string[] => {
  if (Array.isArray(entry.transformations)) {
    return entry.transformations.filter((step): step is string => typeof step === 'string' && step.length > 0);
  }
  const raw = entry.transformationsJson;
  if (!raw) return [];
  try {
    const parsed: unknown = JSON.parse(raw);
    if (Array.isArray(parsed)) {
      return parsed.map((step) => (typeof step === 'string' ? step : JSON.stringify(step))).filter(Boolean);
    }
    if (parsed && typeof parsed === 'object') {
      return Object.entries(parsed as Record<string, unknown>).map(([key, value]) => `${key}: ${String(value)}`);
    }
  } catch {
    // Not JSON — show the stored string verbatim rather than dropping evidence.
    return [raw];
  }
  return [];
};

/** Key used to look an entry up by (line item, field). Header fields use 0. */
export const fieldEvidenceKey = (lineItemId: number | null | undefined, fieldName: string): string =>
  `${lineItemId ?? 0}::${fieldName.toLowerCase()}`;

export type ProcessingEvidenceResource = 'rfqs' | 'supplier-quotes' | 'client-purchase-orders';

const readProcessingEvidence = async (path: string): Promise<LeadProcessingEvidence | null> => {
  try {
    const response = await axiosInstance.get<LeadProcessingEvidence>(path);
    return response.status === 204 ? null : response.data;
  } catch (error: unknown) {
    if (axios.isAxiosError(error) && error.response?.status === 404) return null;
    throw error;
  }
};

const extractionReviewService = {
  // Server-side paginated queue of documents awaiting human review.
  getNeedsReview: async (params: NeedsReviewParams): Promise<PaginatedResponse<NeedsReviewItem>> => {
    const r = await axiosInstance.get('/api/Lead/needs-review', { params });
    return r.data;
  },

  // The full lead used to seed the review workbench. Reuses the existing typed
  // leadService.getById so we keep one source of truth for GET /api/Lead/{id}.
  getLead: (id: number): Promise<LeadResponseDTO> => leadService.getById(id),

  getProcessingEvidence: async (leadId: number): Promise<LeadProcessingEvidence | null> => {
    return readProcessingEvidence(`/api/processing-evidence/leads/${leadId}`);
  },

  /**
   * Per-field source addresses for one lead. Returns null — never throws — when
   * the endpoint is not deployed yet (404), returns no content (204), or the
   * caller is not entitled to it (403), so the review screen degrades to an
   * honest "Source not mapped for this document" instead of an error banner.
   */
  getFieldEvidence: async (leadId: number): Promise<LeadFieldEvidenceResponse | null> => {
    try {
      const response = await axiosInstance.get<LeadFieldEvidenceResponse>(
        // Route is `/fields` — ProcessingEvidenceController.LeadFields. This read
        // `/field-evidence`, which no controller has ever served, so every call 404'd
        // and the 404 was swallowed into `null` by the catch below. The result was a
        // per-field provenance panel that was fully built on both sides and permanently
        // rendered "Source not mapped for this document" for every lead.
        `/api/processing-evidence/leads/${leadId}/fields`,
      );
      if (response.status === 204 || !response.data) return null;
      return { ...response.data, entries: response.data.entries ?? [] };
    } catch (error: unknown) {
      if (axios.isAxiosError(error)) {
        const status = error.response?.status;
        if (status === 404 || status === 403 || status === 501) return null;
      }
      throw error;
    }
  },

  getCommercialProcessingEvidence: (
    resource: ProcessingEvidenceResource,
    id: number,
  ): Promise<LeadProcessingEvidence | null> =>
    readProcessingEvidence(`/api/processing-evidence/${resource}/${id}`),

  // Persist reviewer corrections. `action: 'save'` keeps it in the queue as a
  // draft; `action: 'approve'` clears the review flag and returns the updated
  // lead.
  submitReview: async (id: number, payload: SubmitReviewPayload): Promise<LeadResponseDTO> => {
    const r = await axiosInstance.put(`/api/Lead/${id}/review`, payload);
    return r.data;
  },
};

export default extractionReviewService;
