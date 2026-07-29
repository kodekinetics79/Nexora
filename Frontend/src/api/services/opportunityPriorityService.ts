import axiosInstance from '../axiosInstance';

export type OpportunityFeedbackCode = 'Accepted' | 'Rejected' | 'Replaced' | 'Deferred' | 'Reverted';

export interface OpportunityPriorityItem {
  recommendationId: number;
  commercialCaseId: number;
  nexoraSerial: string;
  leadId: number;
  ownerUserId?: number | null;
  ownerName?: string | null;
  rank: number;
  priorityScore: number;
  priorityBand: string;
  confidence: number;
  completeness: number;
  sampleSize: number;
  insufficientEvidence: boolean;
  recommendedActionCode: string;
  recommendedActionLabel: string;
  reasons: string[];
  evidenceJson: string;
  policyVersion: string;
  featureSchemaVersion: string;
  generatedAtUtc: string;
  evidenceCutoffAtUtc: string;
  mode: 'Shadow';
  isCurrent: boolean;
  outcomes: Array<{
    id: number;
    outcomeCode: string;
    observedAtUtc: string;
    sourceType: string;
    sourceId: number;
    sourceVersion: number;
    evidenceJson: string;
  }>;
  latestFeedback?: {
    id: number;
    decision: OpportunityFeedbackCode;
    replacementActionCode?: string | null;
    reason: string;
    actorId: string;
    occurredAtUtc: string;
    supersedesFeedbackId?: number | null;
  } | null;
}

export interface OpportunityPriorityResponse {
  items: OpportunityPriorityItem[];
  total: number;
  pageNumber: number;
  pageSize: number;
  generatedAtUtc: string;
  accessScope: string;
  policyVersion: string;
  mode: 'Shadow';
  cohort: {
    currentRecommendations: number;
    eligibleRecommendations: number;
    insufficientEvidenceRecommendations: number;
    recommendationsWithObservedOutcome: number;
    recommendationsWithFeedback: number;
    accuracyPercent?: number | null;
    accuracyStatus: string;
  };
}

export interface OpportunityCommandIdentity {
  idempotencyKey: string;
  correlationId: string;
}

export interface OpportunityReconcileResult {
  evaluated: number;
  created: number;
  replayed: number;
  outcomesRecorded: number;
  terminalCasesSkipped: number;
  hasMore: boolean;
  nextAfterCommercialCaseId?: number | null;
  reconciledAtUtc: string;
  policyVersion: string;
  mode: 'Shadow';
}

export interface OpportunityFeedbackRequest extends OpportunityCommandIdentity {
  expectedRecommendationId: number;
  decision: OpportunityFeedbackCode;
  replacementActionCode?: string;
  reason: string;
  supersedesFeedbackId?: number;
}

export const createOpportunityCommandIdentity = (): OpportunityCommandIdentity => ({
  idempotencyKey: crypto.randomUUID(),
  correlationId: crypto.randomUUID(),
});

const commandConfig = (identity: OpportunityCommandIdentity) => ({
  headers: {
    'Idempotency-Key': identity.idempotencyKey,
    'X-Correlation-ID': identity.correlationId,
  },
});

const root = '/api/opportunity-priorities';

const opportunityPriorityService = {
  getPriorities: async (): Promise<OpportunityPriorityResponse> =>
    (await axiosInstance.get<OpportunityPriorityResponse>(root)).data,

  reconcileAll: async (initialIdentity: OpportunityCommandIdentity): Promise<OpportunityReconcileResult> => {
    let identity = initialIdentity;
    let afterCommercialCaseId: number | null = null;
    let aggregate: OpportunityReconcileResult | null = null;

    for (let batch = 0; batch < 10_000; batch += 1) {
      const result: OpportunityReconcileResult = (await axiosInstance.post<OpportunityReconcileResult>(
        `${root}/reconcile`,
        { ...identity, afterCommercialCaseId, batchSize: 100 },
        commandConfig(identity),
      )).data;
      aggregate = aggregate
        ? {
            ...result,
            evaluated: aggregate.evaluated + result.evaluated,
            created: aggregate.created + result.created,
            replayed: aggregate.replayed + result.replayed,
            outcomesRecorded: aggregate.outcomesRecorded + result.outcomesRecorded,
            terminalCasesSkipped: aggregate.terminalCasesSkipped + result.terminalCasesSkipped,
          }
        : result;
      if (!result.hasMore) return aggregate ?? result;
      if (!result.nextAfterCommercialCaseId || result.nextAfterCommercialCaseId === afterCommercialCaseId) {
        throw new Error('Opportunity reconciliation did not advance its durable cursor.');
      }
      afterCommercialCaseId = result.nextAfterCommercialCaseId;
      identity = createOpportunityCommandIdentity();
    }
    throw new Error('Opportunity reconciliation exceeded the bounded batch limit.');
  },

  getForCommercialCase: async (commercialCaseId: number): Promise<OpportunityPriorityItem> =>
    (await axiosInstance.get<OpportunityPriorityItem>(`${root}/commercial-cases/${commercialCaseId}`)).data,

  recordFeedback: async (
    recommendationId: number,
    request: OpportunityFeedbackRequest,
  ): Promise<void> => {
    await axiosInstance.post(
      `${root}/${recommendationId}/feedback`,
      request,
      commandConfig(request),
    );
  },
};

export default opportunityPriorityService;
