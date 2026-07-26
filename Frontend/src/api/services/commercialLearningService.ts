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
export interface SupplierCommercialEvaluation { supplierId: number; supplierName: string; quoteRevisions: number; selectedOfferCount: number; supportedWonCount: number; completeCurrentOfferCount: number; averageResponseDays?: number | null; averageReliabilitySnapshot?: number | null; landedCosts: CurrencyValueSummary[]; evidence: CommercialEvidenceLink[] }
export interface CustomerCommercialMemory { customerId: number; customerName: string; inquiryCount: number; quoteCount: number; decidedCount: number; wonCount: number; lostCount: number; pendingCount: number; conversionRatePercent?: number | null; wonValues: CurrencyValueSummary[]; lossReasons: CommercialReasonCount[]; evidence: CommercialEvidenceLink[] }
export interface SalesRepCommercialMemory { salesRepUserId: number; salesRepName: string; ownedOpportunities: number; decidedCount: number; wonCount: number; lostCount: number; commercialConstraintLosses: number; customerDecisionLosses: number; executionReviewLosses: number; followUpsDue: number; followUpsCompleted: number; conversionRatePercent?: number | null; evidence: CommercialEvidenceLink[] }
export interface InventoryDemandMemory { productId: number; partNumber: string; productName: string; observedDemand: number; qualifiedDemand: number; quotedDemand: number; probabilityWeightedDemand: number; committedDemand: number; fulfilledDemand: number; decidedOpportunities: number; wonOpportunities: number; conversionRatePercent?: number | null; stockingRecommendationEligible: boolean; recommendation: string; evidence: CommercialEvidenceLink[] }
export interface CommercialMemoryCard { nexoraSerial: string; rfqId: number; rfqItemId: number; product?: ProductCommercialMemory | null; inventory?: InventoryDemandMemory | null; suppliers: SupplierCommercialEvaluation[]; nextAction: string }
export interface LearningSignal { signalType: string; subject: string; value: string; sampleSize: number; lastObservedOn: string; status: string; evidenceReference: string }
export interface LearningStudioSummary { generatedAt: string; approvedCorrections: number; conflictingCorrections: number; supplierQuoteTemplates: number; productMemoriesWithDecisions: number; productMemoriesBelowThreshold: number; recentSignals: LearningSignal[] }

const commercialLearningService = {
  getProducts: async (limit = 100): Promise<ProductCommercialMemory[]> =>
    (await axiosInstance.get("/api/commercial-learning/products", { params: { limit } })).data,
  getSuppliers: async (limit = 100): Promise<SupplierCommercialEvaluation[]> =>
    (await axiosInstance.get("/api/commercial-learning/suppliers", { params: { limit } })).data,
  getCustomers: async (limit = 100): Promise<CustomerCommercialMemory[]> =>
    (await axiosInstance.get("/api/commercial-learning/customers", { params: { limit } })).data,
  getSalesReps: async (limit = 100): Promise<SalesRepCommercialMemory[]> =>
    (await axiosInstance.get("/api/commercial-learning/sales-reps", { params: { limit } })).data,
  getLineCard: async (rfqItemId: number): Promise<CommercialMemoryCard> =>
    (await axiosInstance.get(`/api/commercial-learning/rfq-items/${rfqItemId}/memory-card`)).data,
  getStudio: async (): Promise<LearningStudioSummary> =>
    (await axiosInstance.get("/api/commercial-learning/learning-studio")).data,
};
export default commercialLearningService;
