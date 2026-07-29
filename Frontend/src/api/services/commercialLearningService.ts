import axiosInstance from "../axiosInstance";

export interface CurrencyValueSummary { currencyId: number; currencyCode: string; lastValue?: number | null; medianValue?: number | null; minimumValue?: number | null; maximumValue?: number | null; sampleSize: number }
export interface CommercialReasonCount { code: string; label: string; count: number }
export interface CommercialEvidenceLink { recordType: string; recordId: number; reference: string; occurredOn?: string | null; role: string }
export interface ProductWonContext { customerQuoteId: number; customerQuoteNumber: string; quantity: number; unitPrice: number; currencyId: number; currencyCode: string; deliveryLeadTimeDays?: number | null; outcomeOn: string }
export interface ProductCommercialMemory {
  productId: number; partNumber: string; productName: string; periodFrom: string; periodTo: string;
  timesRequested: number; timesQuoted: number; decidedCount: number; wonCount: number; lostCount: number;
  pendingCount: number; lineWinRatePercent?: number | null; stockoutBlockedCount: number;
  typicalWinningLeadTimeDays?: number | null; lastWonContext?: ProductWonContext | null; wonSellingPrices: CurrencyValueSummary[];
  supplierLandedCosts: CurrencyValueSummary[]; lossReasons: CommercialReasonCount[];
  evidence: CommercialEvidenceLink[];
}
export interface BidQualityFlag { supplierQuotedItemId: number; code: string; severity: string; explanation: string; confidence: number; evidence: CommercialEvidenceLink; reviewAction: string }
export interface SupplierBidQualitySummary { offerCount: number; completeOfferCount: number; eligibleOfferCount: number; completenessPercent?: number | null; missingTermCount: number; priceOutlierCount: number; revisionVolatilityCount: number; flags: BidQualityFlag[] }
export interface SupplierCommercialEvaluation { supplierId: number; supplierName: string; quoteRevisions: number; selectedOfferCount: number; supportedWonCount: number; completeCurrentOfferCount: number; averageResponseDays?: number | null; averageReliabilitySnapshot?: number | null; landedCosts: CurrencyValueSummary[]; evidence: CommercialEvidenceLink[]; bidQuality: SupplierBidQualitySummary }
export interface CustomerCommercialMemory { customerId: number; customerName: string; inquiryCount: number; quoteCount: number; decidedCount: number; wonCount: number; lostCount: number; pendingCount: number; conversionRatePercent?: number | null; wonValues: CurrencyValueSummary[]; lossReasons: CommercialReasonCount[]; evidence: CommercialEvidenceLink[] }
export interface SalesRepCommercialMemory { salesRepUserId: number; salesRepName: string; ownedOpportunities: number; decidedCount: number; wonCount: number; lostCount: number; commercialConstraintLosses: number; customerDecisionLosses: number; executionReviewLosses: number; followUpsDue: number; followUpsCompleted: number; conversionRatePercent?: number | null; weightedCoverage: number; firstMeaningfulActionHours?: number | null; quoteTurnaroundHours?: number | null; followUpCompletionPercent?: number | null; insightCaptureCount: number; valueConversionPercent?: number | null; coachingOpportunity: string; evidence: CommercialEvidenceLink[] }
export interface InventoryDemandMemory { productId: number; partNumber: string; productName: string; observedDemand: number; qualifiedDemand: number; quotedDemand: number; probabilityWeightedDemand: number; committedDemand: number; fulfilledDemand: number; decidedOpportunities: number; wonOpportunities: number; conversionRatePercent?: number | null; stockingRecommendationEligible: boolean; recommendation: string; evidence: CommercialEvidenceLink[] }
export interface CommercialMemoryCard { nexoraSerial: string; rfqId: number; rfqItemId: number; product?: ProductCommercialMemory | null; inventory?: InventoryDemandMemory | null; suppliers: SupplierCommercialEvaluation[]; nextAction: string }
export type LearningGovernanceAction = "approve" | "disable" | "rollback";
export interface LearningSignal {
  signalId: string;
  signalType: string;
  subject: string;
  value: string;
  sampleSize: number;
  lastObservedOn: string;
  status: string;
  evidenceReference: string;
  governanceVersion: number;
  governanceStatus?: string | null;
  governanceAction?: string | null;
  governedOn?: string | null;
  governedByUserId?: number | null;
}
export interface LearningStudioSummary { generatedAt: string; approvedCorrections: number; conflictingCorrections: number; supplierQuoteTemplates: number; productMemoriesWithDecisions: number; productMemoriesBelowThreshold: number; recentSignals: LearningSignal[] }
export interface LearningGovernanceCommand { reason: string; expectedVersion: number; revertsVersion?: number }
export interface ExplainableRecommendation { code: string; label: string; explanation: string; confidence: number; userOverrideAllowed: boolean; overrideAction: string; evidence: CommercialEvidenceLink[] }
export interface RfqLineIntelligence { rfqItemId: number; partNumber: string; requestedQuantity: number; stockQuantity: number; unfulfilledQuantity: number; fulfilmentRoute: string; offerCount: number; eligibleOfferCount: number; blockers: string[]; bidQualityFlags: BidQualityFlag[] }
export interface ScenarioQuantityAllocation { rfqItemId: number; requestedQuantity: number; immediateStockQuantity: number; supplierQuantity: number; supplierQuotedItemId?: number | null; expectedDeliveryOn?: string | null }
export interface ScenarioCostSource { sourceType: string; label: string; amount?: number | null; currencyId?: number | null; status: string; evidence?: CommercialEvidenceLink | null }
export interface OpportunityScenario { code: string; label: string; eligible: boolean; explanation: string; estimatedLandedCost?: number | null; currencyId?: number | null; currencyCode?: string | null; estimatedLeadTimeDays?: number | null; expectedDeliveryOn?: string | null; validUntil?: string | null; grossMarginPercent?: number | null; riskBand: string; riskExplanation: string; confidence: number; quantities: ScenarioQuantityAllocation[]; costSources: ScenarioCostSource[]; assumptions: string[]; approvalRequirements: string[]; evidence: CommercialEvidenceLink[] }
export interface CustomerTargetBridge { rfqItemId: number; status: string; customerTargetUnitPrice: number; requiredGrossMarginPercent: number; maximumLandedUnitCost?: number | null; freightDutyTaxOtherPerUnit?: number | null; maximumSupplierUnitCost?: number | null; currencyId: number; formula: string; evidence: CommercialEvidenceLink }
export interface PredictivePriceLine { rfqItemId: number; status: string; mode: string; recommendedUnitPrice?: number | null; lastWonUnitPrice?: number | null; winningRangeLow?: number | null; winningRangeHigh?: number | null; estimatedWinProbability?: number | null; quoteSampleSize: number; customerOrderSampleSize: number; backtestMeanAbsolutePercentError?: number | null; backtestHoldoutCount: number; cohortConversionBaseline?: number | null; currencyId?: number | null; currencyCode?: string | null; context: string; limitations: string[]; evidence: CommercialEvidenceLink[] }
export interface PricingBacktestSummary { status: string; holdoutCount: number; meanAbsolutePercentError?: number | null; cohort: string; limitation: string }
export interface OpportunityDigitalTwin { calculatedOn: string; validity: string; mode: string; policyVersion: string; scenarios: OpportunityScenario[]; customerTargetBridges: CustomerTargetBridge[]; predictivePricing: PredictivePriceLine[]; backtest: PricingBacktestSummary; overrideAction: string }
export interface RfqCommercialIntelligence { rfqId: number; rfqNumber: string; nexoraSerial: string; readinessScore: number; commercialDecision: string; slaRisk: string; clarificationRequired: boolean; nextBestAction: ExplainableRecommendation; lines: RfqLineIntelligence[]; digitalTwin: OpportunityDigitalTwin }

const commercialLearningService = {
  getProducts: async (limit = 100): Promise<ProductCommercialMemory[]> =>
    (await axiosInstance.get("/api/commercial-learning/products", { params: { limit } })).data,
  getSuppliers: async (limit = 100): Promise<SupplierCommercialEvaluation[]> =>
    (await axiosInstance.get("/api/commercial-learning/suppliers", { params: { limit } })).data,
  getCustomers: async (limit = 100): Promise<CustomerCommercialMemory[]> =>
    (await axiosInstance.get("/api/commercial-learning/customers", { params: { limit } })).data,
  getCustomer: async (customerId: number): Promise<CustomerCommercialMemory> =>
    (await axiosInstance.get(`/api/commercial-learning/customers/${customerId}`)).data,
  getSalesReps: async (limit = 100): Promise<SalesRepCommercialMemory[]> =>
    (await axiosInstance.get("/api/commercial-learning/sales-reps", { params: { limit } })).data,
  getLineCard: async (rfqItemId: number): Promise<CommercialMemoryCard> =>
    (await axiosInstance.get(`/api/commercial-learning/rfq-items/${rfqItemId}/memory-card`)).data,
  getRfqIntelligence: async (rfqId: number): Promise<RfqCommercialIntelligence> =>
    (await axiosInstance.get(`/api/commercial-learning/rfqs/${rfqId}/intelligence`)).data,
  getStudio: async (): Promise<LearningStudioSummary> =>
    (await axiosInstance.get("/api/commercial-learning/learning-studio")).data,
  governSignal: async (
    signalId: string,
    action: LearningGovernanceAction,
    command: LearningGovernanceCommand,
  ): Promise<void> => {
    const body = action === "rollback"
      ? { ...command, revertsVersion: command.expectedVersion }
      : command;
    await axiosInstance.post(
      `/api/commercial-learning/learning-studio/${encodeURIComponent(signalId)}/${action}`,
      body,
      { headers: { "Idempotency-Key": crypto.randomUUID() } },
    );
  },
};
export default commercialLearningService;
