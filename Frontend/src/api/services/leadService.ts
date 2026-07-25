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
}

export interface LeadResponseDTO {
  id: number;
  commercialCaseId?: number | null;
  commercialCaseReference?: string | null;
  nexoraSerial?: string | null;
  customerId?: number | null;
  contactId?: number | null;
  customerMatchStatus: string;
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
  fileName: string;
  outcome: string;
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
  externalCost: number;
  items: BatchReconciliationItemDTO[];
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

  getIngestionBatch: async (batchId: string): Promise<BatchReconciliationDTO> => {
    const r = await axiosInstance.get<BatchReconciliationDTO>(`/api/LeadIngestion/batches/${encodeURIComponent(batchId)}`);
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
