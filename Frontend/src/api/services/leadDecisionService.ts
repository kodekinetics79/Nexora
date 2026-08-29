import axiosInstance from '../axiosInstance';

export type LineParticipationDecision = 'Pending' | 'Bid' | 'NoBid' | 'Clarify';
export type ParticipationStatus = 'NONE' | 'DRAFT' | 'COMMITTED' | 'STALE';
export type FitCriterionDecision = 'PASS' | 'CONCERN' | 'UNKNOWN' | 'NOT_APPLICABLE';
export type OverallFitDecision = 'FIT' | 'CONDITIONAL' | 'NOT_FIT';

export interface DecisionReasonCodeDTO {
  code: string;
  label: string;
  appliesTo: Array<'NoBid' | 'Clarify'>;
  description?: string | null;
}

export interface LeadDecisionEvidenceDTO {
  occurrenceId: number;
  sourceDocumentId?: number | null;
  kind: 'EMAIL_BODY' | 'ATTACHMENT' | 'DOCUMENT' | string;
  name: string;
  mediaType?: string | null;
  receivedAtUtc?: string | null;
  status: string;
  sourceAvailable: boolean;
  downloadUrl?: string | null;
  contentUrl?: string | null;
  detail?: string | null;
}

export interface LeadDecisionLineDTO {
  id: number;
  revisionLineId: number;
  lineItemNo?: string | null;
  sourceText?: string | null;
  sourceField?: string | null;
  sourceAddress?: string | null;
  sourceFields?: Array<{ field: string; rawValue: string; sourceAddress?: string | null }>;
  productName?: string | null;
  description?: string | null;
  manufacturerName?: string | null;
  manufacturerPartNumber?: string | null;
  quantity?: number | null;
  unitOfMeasure?: string | null;
  currency?: string | null;
  normalizedQuantity?: number | null;
  normalizedUom?: string | null;
  catalogResolution?: string | null;
  catalogMatches?: Array<{
    productId: number;
    productName?: string | null;
    materialCode?: string | null;
    manufacturerPartNumber?: string | null;
    score: number;
    reason: string;
  }>;
  bestMatchProductId?: number | null;
  catalogConfidence?: number;
  needsAttention?: boolean;
  attentionReason?: string | null;
  catalogPolicyVersion?: string | null;
  warningSnapshotJson?: string | null;
  verificationStatus: 'VERIFIED' | 'NEEDS_CHECK' | 'MISSING_SOURCE' | 'MACHINE_SUGGESTION' | string;
  verificationDetail?: string | null;
  participation?: {
    decision: LineParticipationDecision;
    reasonCode?: string | null;
    note?: string | null;
    productId?: number | null;
    quantity?: number | null;
    unitOfMeasure?: string | null;
    currency?: string | null;
    catalogPolicyVersion?: string | null;
    warningSnapshotJson?: string | null;
  } | null;
}

export interface FitCriterionDTO {
  code: string;
  label: string;
  description?: string | null;
  decision: FitCriterionDecision;
  note?: string | null;
}

export interface FitAssessmentDTO {
  version: number;
  overallDecision: OverallFitDecision;
  rationale: string;
  criteria: FitCriterionDTO[];
  assessedBy?: string | null;
  assessedAtUtc?: string | null;
}

export interface PromotionReceiptDTO {
  rfqId: number;
  rfqNumber?: string | null;
  leadRevisionNumber: number;
  participationVersion: number;
  promotedLineCount: number;
  promotedAtUtc: string;
  promotedBy?: string | null;
}

export interface LeadDecisionWorkbenchDTO {
  leadId: number;
  leadRevisionId: number;
  leadRevisionNumber: number;
  decisionVersion: number;
  participationVersion?: number | null;
  participationStatus: ParticipationStatus;
  lifecycleStatusCode: string;
  lifecycleStatusLabel?: string | null;
  nexoraSerial?: string | null;
  customerRfqReference?: string | null;
  customerId?: number | null;
  customerName?: string | null;
  buyerName?: string | null;
  senderEmail?: string | null;
  emailSubject?: string | null;
  emailMessageId?: string | null;
  receivedAtUtc?: string | null;
  bidClosingDate?: string | null;
  requiredDeliveryDate?: string | null;
  deliveryLocation?: string | null;
  agreementReference?: string | null;
  assignedToName?: string | null;
  verificationStatus: 'VERIFIED' | 'NEEDS_REVIEW' | 'SOURCE_UNAVAILABLE' | string;
  verifiedBy?: string | null;
  verifiedAtUtc?: string | null;
  sourceCoverage?: { coveredLines: number; totalLines: number } | null;
  evidence: LeadDecisionEvidenceDTO[];
  lines: LeadDecisionLineDTO[];
  reasonCodes: DecisionReasonCodeDTO[];
  unitOptions?: Array<{ code: string; label: string }>;
  currencyOptions?: Array<{ code: string; label: string }>;
  fitAssessment?: FitAssessmentDTO | null;
  promotion?: PromotionReceiptDTO | null;
  blockers: Array<{ code: string; message: string; actionLabel?: string | null; actionPath?: string | null }>;
}

export interface ParticipationLineInput {
  revisionLineId: number;
  decision: LineParticipationDecision;
  reasonCode?: string;
  note?: string;
  productId?: number;
  quantity?: number;
  unitOfMeasure?: string;
  currency?: string;
}

export interface SaveParticipationRequest {
  expectedLeadRevisionId: number;
  expectedDecisionVersion: number;
  expectedParticipationVersion?: number | null;
  commit: boolean;
  reasonCode?: string;
  notes?: string;
  lines: ParticipationLineInput[];
}

export interface SaveParticipationResponse {
  decisionVersion: number;
  participationVersion: number;
  participationStatus: ParticipationStatus;
}

export interface SaveFitAssessmentRequest {
  expectedLeadRevisionId: number;
  expectedDecisionVersion: number;
  expectedFitVersion?: number | null;
  overallDecision: OverallFitDecision;
  rationale: string;
  criteria: Array<Pick<FitCriterionDTO, 'code' | 'decision' | 'note'>>;
}

export interface ResolveRfqRevisionImpactRequest {
  rfqId: number;
  expectedLeadRevisionId: number;
  reconciliationReason: string;
  confirmedHistoricalRfqUnchanged: boolean;
}

export interface RfqRevisionImpactResolutionResult {
  rfqId: number;
  reviewedThroughLeadRevisionId: number;
  resolvedImpactCount: number;
  replayed: boolean;
}

const leadDecisionService = {
  getWorkbench: async (leadId: number): Promise<LeadDecisionWorkbenchDTO> => {
    const response = await axiosInstance.get<LeadDecisionWorkbenchDTO>(`/api/leads/${leadId}/decision-workbench`);
    return response.data;
  },

  saveFitAssessment: async (
    leadId: number,
    request: SaveFitAssessmentRequest,
    idempotencyKey: string,
  ): Promise<FitAssessmentDTO> => {
    const response = await axiosInstance.put<FitAssessmentDTO>(`/api/leads/${leadId}/fit-assessment`, request, {
      headers: { 'Idempotency-Key': idempotencyKey },
    });
    return response.data;
  },

  saveParticipation: async (
    leadId: number,
    request: SaveParticipationRequest,
    idempotencyKey: string,
  ): Promise<SaveParticipationResponse> => {
    const response = await axiosInstance.put<SaveParticipationResponse>(`/api/leads/${leadId}/participation`, request, {
      headers: { 'Idempotency-Key': idempotencyKey },
    });
    return response.data;
  },

  promoteToRfq: async (
    leadId: number,
    request: {
      expectedLeadRevisionId: number;
      expectedDecisionVersion: number;
      expectedParticipationVersion: number;
      idempotencyKey: string;
    },
  ): Promise<PromotionReceiptDTO> => {
    const response = await axiosInstance.post<PromotionReceiptDTO>(`/api/leads/${leadId}/promote-to-rfq`, request, {
      headers: { 'Idempotency-Key': request.idempotencyKey },
    });
    return response.data;
  },

  resolveRfqRevisionImpact: async (
    leadId: number,
    request: ResolveRfqRevisionImpactRequest,
    idempotencyKey: string,
  ): Promise<RfqRevisionImpactResolutionResult> => {
    const response = await axiosInstance.post<RfqRevisionImpactResolutionResult>(
      `/api/leads/${leadId}/rfq-revision-impact/resolve`,
      request,
      { headers: { 'Idempotency-Key': idempotencyKey } },
    );
    return response.data;
  },
};

export default leadDecisionService;
