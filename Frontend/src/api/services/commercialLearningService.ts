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
export interface LearningSignal { signalType: string; subject: string; value: string; sampleSize: number; lastObservedOn: string; status: string; evidenceReference: string }
export interface LearningStudioSummary { generatedAt: string; approvedCorrections: number; conflictingCorrections: number; supplierQuoteTemplates: number; productMemoriesWithDecisions: number; productMemoriesBelowThreshold: number; recentSignals: LearningSignal[] }
export interface ExplainableRecommendation { code: string; label: string; explanation: string; confidence: number; userOverrideAllowed: boolean; overrideAction: string; evidence: CommercialEvidenceLink[] }
export interface RfqLineIntelligence { rfqItemId: number; partNumber: string; requestedQuantity: number; stockQuantity: number; unfulfilledQuantity: number; fulfilmentRoute: string; offerCount: number; eligibleOfferCount: number; blockers: string[]; bidQualityFlags: BidQualityFlag[] }
export interface OpportunityScenario { code: string; label: string; eligible: boolean; explanation: string; estimatedLandedCost?: number | null; currencyId?: number | null; estimatedLeadTimeDays?: number | null; confidence: number; assumptions: string[]; evidence: CommercialEvidenceLink[] }
export interface OpportunityDigitalTwin { calculatedOn: string; validity: string; scenarios: OpportunityScenario[]; overrideAction: string }
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
};
export default commercialLearningService;
