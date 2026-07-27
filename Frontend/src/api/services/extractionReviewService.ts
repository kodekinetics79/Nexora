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
}

export interface NeedsReviewParams {
  pageNumber?: number;
  pageSize?: number;
  search?: string;
}

// Editable header fields the reviewer can correct before saving/approving.
export interface ReviewHeaderPayload {
  rfqno?: string;
  buyersName?: string;
  bidClosingDate?: string;
  opportunityNo?: string;
  headerRemarks?: string;
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
