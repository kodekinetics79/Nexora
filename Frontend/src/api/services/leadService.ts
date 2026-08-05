import axios from 'axios';
import axiosInstance from '../axiosInstance';

export interface LeadFilters {
  businessUnitId?: number;
  pageNumber?: number;
  pageSize?: number;
  id?: number;
  rfqno?: string;
  buyersName?: string;
  leadSource?: string;
  startDate?: string;
  endDate?: string;
  emailSource?: string;
  clientemail?: string;
  search?: string;
  view?: string;
}

/**
 * One machine-proposed client organisation for a lead, ranked best-first.
 *
 * Produced by the deterministic customer resolver — never by an LLM — so every
 * candidate carries the evidence that produced it (`reasonCode`) and, when the
 * backend can phrase it, a human sentence (`explanation`, e.g. "shares sender
 * domain se.com.sa"). A candidate is a SUGGESTION: the lead's `customerId`
 * stays null until a person confirms one. Contract: §7 ClientCandidateDTO.
 */
export interface ClientCandidateDTO {
  /** 1-based; 1 is the strongest candidate. */
  rank: number;
  customerId: number;
  customerName?: string | null;
  /** 0..1. */
  confidence: number;
  /** See `LeadResponseDTO.customerMatchReasonCode` for the vocabulary. */
  reasonCode?: string | null;
  /** Backend-authored plain sentence; the UI falls back to reason-code copy. */
  explanation?: string | null;
}

export interface LeadResponseDTO {
  id: number;
  commercialCaseId?: number | null;
  commercialCaseReference?: string | null;
  nexoraSerial?: string | null;
  customerId?: number | null;
  contactId?: number | null;
  customerName?: string | null;
  accountOwnerName?: string | null;
  /**
   * UNRESOLVED | SUGGESTED | AMBIGUOUS | AUTO_MATCHED |
   * AUTO_MATCHED_CONTACT_UNRESOLVED | CONFIRMED |
   * CUSTOMER_CONFIRMED_CONTACT_UNRESOLVED | VERIFIED_EMAIL.
   *
   * Invariant enforced by the database: `customerId` is non-null only for the
   * last five. SUGGESTED/AMBIGUOUS never carry a customer link.
   */
  customerMatchStatus: string;
  /**
   * Why the machine reached that status. SENDER_EMAIL_EXACT | SENDER_DOMAIN |
   * ERP_ACCOUNT_EXACT | TAX_REG_EXACT | LEARNED_ALIAS | LEARNED_PORTAL_ACCOUNT |
   * NAME_EXACT_UNVERIFIED | NAME_FUZZY | RFQ_PATTERN | PRIOR_SENDER |
   * CONTACT_PERSON | NO_EVIDENCE | NO_MATCH | AMBIGUOUS.
   * Absent on payloads that predate client-organisation identity.
   */
  customerMatchReasonCode?: string | null;
  /** 0..1 confidence behind `customerMatchStatus`. */
  customerMatchConfidence?: number | null;
  /** The buying organisation as printed on the document (never our own name). */
  customerCompanyNameExtracted?: string | null;
  /** ≤120-char verbatim snippet that names the buying organisation. */
  customerCompanyEvidence?: string | null;
  /** e.g. "MATERIALS E-BIDDING SYSTEM", "Ariba", "Etimad". */
  customerPortalNameExtracted?: string | null;
  /** OUR vendor code at the customer's portal, e.g. "2004414". */
  supplierAccountRefOnDocument?: string | null;
  /** ≤3 on list responses, ≤5 on detail. Empty/absent when nothing was proposed. */
  clientCandidates?: ClientCandidateDTO[] | null;
  rfqno: string;
  buyersName: string;
  leadSource: string;
  recDate: string;
  bidClosingDate: string;
  emailSource: string;
  clientemail: string;
  status: string;
  isAccepted: boolean;
  isRejected: boolean;
  aiconfidence: number;
  itemCount: number;
  reviewVersion: number;
  requiresCommercialReview: boolean;
  commercialFactsVerified: boolean;
  currentRevisionNumber: number;
  ingestedAtUtc?: string | null;
  // Ingestion audit (owner requirement): when the lead actually ENTERED Nexora —
  // the earliest source-document received_on, falling back server-side to
  // createdDate for manual leads. Distinct from recDate/bidClosingDate.
  ingestedOn?: string | null;
  // Server-computed: ingestedOn is after the business due date (bidClosingDate,
  // falling back to subDate). Late-ingested leads are excluded from Nexora's
  // response-time / aging performance metrics.
  lateIngested?: boolean;
  headerRemarks?: string;
  opportunityNo?: string;
  biddingDecision?: string;
  acknowledgmentDate?: string;
  subDate?: string;
  rfqtype?: string;
  durationAgreement?: string;
  createdBy?: string;
  createdDate?: string;
  businessUnitId: number;
  businessUnitName?: string;
  lifecycleVersion: number;
  assignedToFullName?: string;
  assignComment?: string | null;
  assignmentReason?: string | null;
  // WP-A3 duplicate flag: null | 'suspected' | 'confirmed' | 'not_duplicate'.
  // Conversion is blocked while suspected/confirmed.
  duplicateStatus?: string | null;
  duplicateOfLeadId?: number | null;
  duplicateResolvedBy?: string | null;
  leadItems?: LeadItemResponseDTO[];
  attachments?: AttachmentResponseDTO[];
}

export interface LeadItemResponseDTO {
  id: number;
  lineItemNo?: string;
  itemMaterialCode?: string;
  productShortName?: string;
  productShortDescription?: string;
  quantity?: number;
  unitOfMeasure?: string;
  currency?: string;
  unitPrice?: number;
  aiconfidence: number;
  manufacturerName?: string;
  manufacturerPartNumber?: string;
  customerRfqno?: string;
  storageLocation?: string;
  commodityProduct?: string;
  // Additional extraction fields surfaced by GET /api/Lead/{id} and edited in
  // the Extraction Review workbench (all optional / additive).
  alternateProductName?: string;
  alternatePartNumber?: string;
  itemText?: string;
  leadTime?: string;
  // Unrecognized customer-document columns preserved verbatim at extraction time
  // ({"original column header": "cell value"}); absent/null when none.
  extraFields?: Record<string, string> | null;
}

export interface AttachmentResponseDTO {
  id: number;
  fileName: string;
  fileSize: number;
  mimeType: string;
}

export interface AcceptedLeadResponseDTO {
  id: number;
  leadId: number;
  rfqno: string;
  buyersName: string;
  clientemail?: string | null;
  leadSource?: string;
  // Client organisation identity. Optional across the board: /api/UnAssignedLead
  // predates client-organisation identity, so these queues degrade to the
  // "unresolved" cell (an explicit "Set client" affordance) rather than
  // guessing a link. Same vocabulary as LeadResponseDTO.
  customerId?: number | null;
  customerName?: string | null;
  customerMatchStatus?: string | null;
  customerMatchReasonCode?: string | null;
  customerMatchConfidence?: number | null;
  clientCandidates?: ClientCandidateDTO[] | null;
  acceptedDate: string;
  assignedToFullName?: string;
  assignedToId?: number;
  assignedOn?: string;
  comment?: string;
  // WP-A1 unassigned-aging: whole hours the lead has sat unassigned (null when
  // assigned) and whether that exceeds the tenant's SLA threshold (default 2h).
  unassignedHours?: number | null;
  isUnassignedOverdue?: boolean;
  // WP-A3 duplicate flag
  duplicateStatus?: string | null;
  duplicateOfLeadId?: number | null;
  // Add other fields as needed
}

export interface AcceptedLeadFullResponseDTO {
  id: number;
  rfqno?: string;
  buyersName?: string;
  recDate: string;
  bidClosingDate?: string;
  biddingDecision?: string;
  acknowledgmentDate?: string;
  subDate?: string;
  headerRemarks?: string;
  opportunityNo?: string;
  noOfLineItems?: number;
  rfqtype?: string;
  durationAgreement?: string;
  aiconfidence?: number;
  leadSource: string;
  emailSource?: string;
  clientemail?: string;
  createdDate: string;
  modifiedDate?: string;
  assignedToId?: number;
  assignedToFullName?: string;
  assignedOn?: string;
  assignComment?: string;
  leadItems: AcceptedLeadItemDTO[];
  attachments: AttachmentResponseDTO[];
}

export interface AcceptedLeadItemDTO {
  id: number;
  companyRef?: string;
  customerAccountPortalId?: string;
  customerRfqno?: string;
  itemMaterialCode?: string;
  commodityProduct?: string;
  buyerName?: string;
  lineItemNo?: string;
  productShortName?: string;
  alternative?: string;
  productShortDescription?: string;
  currency?: string;
  unitOfMeasure?: string;
  unitPrice?: number;
  quantity: number;
  storageLocation?: string;
  manufacturerName?: string;
  manufacturerPartNumber?: string;
  alternateProductName?: string;
  alternatePartNumber?: string;
  itemText?: string;
  materialPotext?: string;
  leadTime?: string;
  receivedDate?: string;
  bidClosingDateLine?: string;
  aiconfidence?: number;
}

export interface PaginatedResponse<T> {
  items: T[];
  totalCount: number;
  pageNumber: number;
  pageSize: number;
}

export type LeadOccurrenceClassification =
  | 'Pending'
  | 'New'
  | 'ExactDuplicate'
  | 'Revision'
  | 'PossibleMatchReviewRequired'
  | 'RejectedOrUnprocessable';

export interface GovernedUploadJobDTO {
  jobId: number;
  occurrenceId?: number | null;
  fileName: string;
  outcome: string;
  errorCode?: string | null;
  reason?: string | null;
}

export interface GovernedUploadResponseDTO {
  batchId?: string | null;
  jobs: GovernedUploadJobDTO[];
}

export interface BatchReconciliationItemDTO {
  occurrenceId: number;
  leadId?: number | null;
  nexoraSerial?: string | null;
  classification: LeadOccurrenceClassification | string;
  revisionNumber?: number | null;
  fileName?: string | null;
  ingestedAtUtc: string;
  processingPath: string;
  externalAiUsed: boolean;
  confidence: number;
  reasons: string[];
  matchCandidates: LeadMatchCandidateDTO[];
  customerResolutionStatus: string;
  assignedOpportunityOwner?: string | null;
  intakeStatus?: string;
  errorCode?: string | null;
  sourceDocumentOccurrenceId?: number | null;
  securityStatus?: string | null;
  securityScanUpdatedAtUtc?: string | null;
  lastUpdatedAtUtc?: string | null;
  extractionStatus?: string | null;
  extractionUpdatedAtUtc?: string | null;
  /**
   * The backend's durable "our infrastructure failed" signal: this file is held by a malware
   * scanner that never produced a verdict, so it can be replayed from its immutable source with no
   * re-upload. Distinct from a real malware detection or a malformed document, neither of which is
   * replayable. Source: BatchReconciliationItemDto in
   * Backend/ERP_RFQ_Automation/LeadIdentity/LeadIdentityContracts.cs.
   *
   * Optional because older responses predate the field — treat `undefined` as "unknown" and fall
   * back to the error code (see src/utils/intakeErrors.ts).
   */
  recoverableSecurityHold?: boolean;
}

export interface LeadMatchCandidateDTO {
  candidateId: number;
  candidateLeadId: number;
  nexoraSerial: string;
  customerRfqReference?: string | null;
  confidence: number;
  matchEvidenceJson: string;
  differencesJson: string;
  downstreamImpactJson: string;
  reviewState: string;
  version: number;
}

export interface BatchReconciliationDTO {
  batchId: string;
  filesReceived: number;
  logicalInquiries: number;
  newLeads: number;
  exactDuplicates: number;
  revisions: number;
  possibleMatches: number;
  rejected: number;
  externalOccurrences: number;
  externalCost?: number | null;
  awaitingSecurityScan: number;
  localFirstOccurrences: number;
  items: BatchReconciliationItemDTO[];
}

export interface SecurityScanRetryResultDTO {
  batchId: string;
  eligible: number;
  queued: number;
  stillAwaiting: number;
  rejected: number;
  sourceObjectUnavailable: number;
  items: Array<{
    sourceDocumentOccurrenceId: number;
    fileName: string;
    status: string;
    errorCode?: string | null;
    extractionJobId?: number | null;
  }>;
  /** Every batch touched by the sweep. Single-element for a batch-scoped retry. */
  batches?: string[];
  /** True when the per-call cap was hit and the sweep should be invoked again. */
  moreRemaining?: boolean;
}

/** GET /api/LeadIngestion/blocked-files — operator discovery of scanner-blocked files. */
export interface BlockedBatchSummaryDTO {
  batchId: string;
  blockedFiles: number;
  oldestReceivedOn: string;
  newestReceivedOn: string;
}

export interface BlockedFilesDTO {
  blockedFiles: number;
  batches: BlockedBatchSummaryDTO[];
}

export interface DuplicateUploadDTO {
  occurrenceId: number;
  fileName: string;
  uploadBatch: string;
  ingestedAt: string;
  uploadedBy: string;
  source: string;
  duplicateType: string;
  originalOccurrenceId?: number | null;
  canonicalLeadId?: number | null;
  nexoraSerial?: string | null;
  securityStatus: string;
  processingReused: boolean;
  resources: {
    bytesUploaded: number;
    hashingDurationMs: number;
    storagePhysicalBytes: number;
    storageLogicalBytes: number;
    malwareScanReused: boolean;
    malwareScanRerun: boolean;
    parserReused: boolean;
    ocrReused: boolean;
    localModelReused: boolean;
    externalModelReused: boolean;
    localComputeCost: number;
    externalCost: number;
    totalActualCost: number;
    estimatedProcessingAvoided: number;
    costStatus: string;
  };
  actions: string[];
}

export interface PossibleMatchQueueItemDTO {
  batchId: string;
  occurrenceId: number;
  fileName?: string | null;
  ingestedAtUtc: string;
  confidence: number;
  matchCandidates: LeadMatchCandidateDTO[];
}

export interface LeadRevisionDifferenceDTO {
  changeType: 'Added' | 'Removed' | 'Modified' | 'Unchanged' | string;
  scope: string;
  path: string;
  previousValueJson?: string | null;
  currentValueJson?: string | null;
}

export interface LeadRevisionImpactDTO {
  aggregateType: string;
  aggregateId: number;
  impactType: string;
  status: string;
  detailsJson: string;
}

export interface LeadRevisionDTO {
  id: number;
  revisionNumber: number;
  createdAtUtc: string;
  fingerprint: string;
  customerRfqReference?: string | null;
  processingPath: string;
  externalAiUsed: boolean;
  differences: LeadRevisionDifferenceDTO[];
  impacts: LeadRevisionImpactDTO[];
}

export type MatchReviewDecisionAction = 'exact_duplicate' | 'revision' | 'link' | 'create_new' | 'defer' | 'reject';

export interface MatchReviewDecisionRequestDTO {
  action: MatchReviewDecisionAction;
  candidateLeadId?: number | null;
  expectedVersion: number;
  reason: string;
  idempotencyKey: string;
}

export interface LeadReconciliationResultDTO {
  leadId: number;
  nexoraSerial: string;
  occurrenceId: number;
  revisionId?: number | null;
  revisionNumber: number;
  classification: LeadOccurrenceClassification | string;
  confidence: number;
  reasons: string[];
  shouldRoute: boolean;
}

const leadService = {
  getAll: async (filters: LeadFilters): Promise<PaginatedResponse<LeadResponseDTO>> => {
    const r = await axiosInstance.get('/api/Lead', { params: filters });
    return r.data;
  },

  getById: async (id: number): Promise<LeadResponseDTO> => {
    const r = await axiosInstance.get(`/api/Lead/${id}`);
    return r.data;
  },

  /**
   * Ranked client-organisation candidates the resolver proposed for this lead.
   * GET /api/Lead/{id}/client-candidates (Leads:View).
   *
   * Returns `[]` rather than throwing when the endpoint is absent (404) or the
   * caller lacks the permission (403): "no suggestion" is a legitimate, common
   * state, and an unresolved client must still render its evidence and its
   * "Set client" action instead of an error. Every other failure propagates so
   * a genuine outage is not silently reported as "nothing to suggest".
   */
  getClientCandidates: async (id: number): Promise<ClientCandidateDTO[]> => {
    try {
      const r = await axiosInstance.get<ClientCandidateDTO[]>(`/api/Lead/${id}/client-candidates`);
      return Array.isArray(r.data) ? r.data : [];
    } catch (error: unknown) {
      const status = axios.isAxiosError(error) ? error.response?.status : undefined;
      if (status === 404 || status === 403) return [];
      throw error;
    }
  },

  getIngestionBatch: async (batchId: string): Promise<BatchReconciliationDTO> => {
    const r = await axiosInstance.get<BatchReconciliationDTO>(`/api/LeadIngestion/batches/${encodeURIComponent(batchId)}`);
    return r.data;
  },

  /**
   * Replays one batch's scanner-blocked files from their stored source objects.
   * POST /api/LeadIngestion/batches/{batchId}/retry-blocked-files
   * (LeadIngestionController.RetryBlockedFiles, requires Leads:Create).
   */
  retryBlockedFiles: async (batchId: string): Promise<SecurityScanRetryResultDTO> => {
    const r = await axiosInstance.post<SecurityScanRetryResultDTO>(
      `/api/LeadIngestion/batches/${encodeURIComponent(batchId)}/retry-blocked-files`,
    );
    return r.data;
  },

  /**
   * Tenant-wide replay — no batch id and no re-upload. The operator escape hatch for holds the
   * batch page can no longer offer a control for.
   * POST /api/LeadIngestion/retry-blocked-files
   * (LeadIngestionController.RetryAllBlockedFiles, requires Leads:Create). Capped per call —
   * re-invoke while `moreRemaining` is true.
   */
  retryAllBlockedFiles: async (): Promise<SecurityScanRetryResultDTO> => {
    const r = await axiosInstance.post<SecurityScanRetryResultDTO>(
      '/api/LeadIngestion/retry-blocked-files',
    );
    return r.data;
  },

  /** GET /api/LeadIngestion/blocked-files (LeadIngestionController.BlockedFiles). */
  getBlockedFiles: async (): Promise<BlockedFilesDTO> => {
    const r = await axiosInstance.get<BlockedFilesDTO>('/api/LeadIngestion/blocked-files');
    return r.data;
  },

  getDuplicateUploads: async (): Promise<DuplicateUploadDTO[]> => {
    const r = await axiosInstance.get<DuplicateUploadDTO[]>('/api/LeadIngestion/duplicates');
    return r.data;
  },

  getPossibleMatches: async (): Promise<PossibleMatchQueueItemDTO[]> => {
    const r = await axiosInstance.get<PossibleMatchQueueItemDTO[]>('/api/LeadIngestion/match-reviews');
    return r.data;
  },

  getRevisions: async (leadId: number): Promise<LeadRevisionDTO[]> => {
    const r = await axiosInstance.get<LeadRevisionDTO[]>(`/api/LeadIngestion/leads/${leadId}/revisions`);
    return r.data;
  },

  decideMatchReview: async (
    occurrenceId: number,
    request: MatchReviewDecisionRequestDTO,
  ): Promise<LeadReconciliationResultDTO> => {
    const r = await axiosInstance.post<LeadReconciliationResultDTO>(
      `/api/LeadIngestion/match-reviews/${occurrenceId}/decision`,
      request,
    );
    return r.data;
  },

  getOutstandingLeads: async (params: any): Promise<PaginatedResponse<AcceptedLeadResponseDTO>> => {
    const r = await axiosInstance.get('/api/UnAssignedLead', { params });
    return r.data;
  },

  getAssignedLeads: async (params: any): Promise<PaginatedResponse<AcceptedLeadResponseDTO>> => {
    const r = await axiosInstance.get('/api/UnAssignedLead/assigned', { params });
    return r.data;
  },

  getAcceptedLeadById: async (id: number): Promise<AcceptedLeadFullResponseDTO> => {
    const r = await axiosInstance.get(`/api/UnAssignedLead/${id}`);
    return r.data;
  },

  getRejectionReasons: async () => {
    const r = await axiosInstance.get('/api/Lead/rejection-reasons');
    return r.data;
  },

  getStats: async () => {
    const r = await axiosInstance.get('/api/Lead/stats');
    return r.data;
  },

  getUsersForAssignment: async (buid: number) => {
    const r = await axiosInstance.get('/api/UnAssignedLead/users-for-assignment', { params: { businessUnitId: buid } });
    return r.data;
  },

  assignLead: async (data: { leadId: number; assignedToUserId: number; expectedAssigneeId?: number | null; comment?: string }) => {
    const operationId = crypto.randomUUID();
    return axiosInstance.post('/api/UnAssignedLead/assign', {
      ...data,
      expectedAssigneeId: data.expectedAssigneeId ?? null,
      idempotencyKey: `manual-assignment:${operationId}`,
      correlationId: operationId,
    });
  },

  // WP-A3: resolve a duplicate flag. 'not_duplicate' clears the conversion
  // block; 'confirm' records a confirmed duplicate (conversion stays blocked).
  resolveDuplicate: async (id: number, action: 'not_duplicate' | 'confirm'): Promise<LeadResponseDTO> => {
    const r = await axiosInstance.post(`/api/Lead/${id}/duplicate-resolution`, { action });
    return r.data;
  },

  // Uploads
  uploadManual: async (formData: FormData) => {
    return axiosInstance.post('/api/ManualUpload/upload', formData, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
  },

  uploadGoverned: async (formData: FormData): Promise<GovernedUploadResponseDTO> => {
    const r = await axiosInstance.post<GovernedUploadResponseDTO>('/api/Extraction/upload', formData, {
      headers: {
        'Content-Type': 'multipart/form-data',
        'Idempotency-Key': `manual-upload:${crypto.randomUUID()}`,
      },
    });
    return r.data;
  },

  uploadRfqExcel: async (formData: FormData) => {
    return axiosInstance.post('/api/ManualUpload/upload-rfq-excel', formData, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
  },

  uploadBulkLeads: async (formData: FormData) => {
    return axiosInstance.post('/api/LeadUploader/upload', formData, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
  },
  uploadToFolder: async (formData: FormData, folderType: string): Promise<any> => {
    const r = await axiosInstance.post(`/api/Email/upload-leads-folder?folderType=${folderType}`, formData, {
      headers: { 'Content-Type': 'multipart/form-data' },
    });
    return r.data;
  },

  processAllFolderLeads: async (): Promise<any> => {
    const r = await axiosInstance.post('/api/Email/process-all-folder-leads');
    return r.data;
  },

  fetchEmails: async (): Promise<any> => {
    const r = await axiosInstance.post('/api/Email/fetch');
    return r.data;
  }
};

export default leadService;
