import axiosInstance from '../axiosInstance';

export interface IntelligenceMetric {
  key: string;
  label: string;
  value: number;
  unit: 'count' | 'currency' | 'percentage' | 'hours' | string;
  currencyCode?: string | null;
}

export interface CommercialAttentionItem {
  id: number;
  recordType: string;
  recordId: number;
  nexoraSerial?: string | null;
  reference: string;
  customerName?: string | null;
  ownerName?: string | null;
  reason: string;
  dueAt?: string | null;
  priority: string;
}

export interface SalesTodayDTO {
  generatedAt: string;
  scope: 'tenant' | 'assigned_to_me';
  metrics: IntelligenceMetric[];
  attentionItems: CommercialAttentionItem[];
}

export interface RepSummaryDTO {
  userId: number;
  name: string;
  email?: string | null;
  roleName?: string | null;
  activeLeads: number;
  overdueLeads: number;
  openRfqs: number;
  draftQuotes: number;
  followUpsDue: number;
  pipelineGroups: CurrencyPipelineGroupDTO[];
}

export interface CurrencyPipelineGroupDTO {
  currencyId?: number | null;
  currencyCode?: string | null;
  quoteCount: number;
  pipelineValue: number;
  weightedPipeline: number;
}

export interface CurrencyAmountGroupDTO {
  currencyCode: string;
  value: number;
}

export interface TeamOverviewDTO {
  generatedAt: string;
  metrics: IntelligenceMetric[];
  representatives: RepSummaryDTO[];
}

export interface RepProfileDTO extends RepSummaryDTO {
  accountCount: number;
  wonValueGroups: CurrencyAmountGroupDTO[];
  conversionRate?: number | null;
  recentActivity: CommercialAttentionItem[];
}

export interface AccountOwnershipDTO {
  customerId: number;
  customerName: string;
  ownerUserId?: number | null;
  ownerName?: string | null;
  openLeads: number;
  openQuotes: number;
  pipelineGroups: CurrencyPipelineGroupDTO[];
  lastActivityAt?: string | null;
  version: number;
}

export interface RoutingQueueItemDTO {
  sourceId: number;
  leadId: number;
  nexoraSerial: string;
  customerName?: string | null;
  receivedAt: string;
  dueAt?: string | null;
  reason: string;
  recommendedOwnerUserId?: number | null;
  recommendedOwnerName?: string | null;
  recommendationReason?: string | null;
  version: number;
}

export interface FollowUpDTO {
  id: number;
  quoteId: number;
  quoteNo: string;
  nexoraSerial?: string | null;
  customerName: string;
  ownerUserId?: number | null;
  ownerName?: string | null;
  dueAt: string;
  status: string;
  reason: string;
  daysSinceContact?: number | null;
  version: number;
}

export interface PerformanceDTO {
  generatedAt: string;
  from: string;
  to: string;
  metrics: IntelligenceMetric[];
  representatives: Array<RepSummaryDTO & { wonQuotes: number; lostQuotes: number; conversionRate?: number | null }>;
}

export interface CommercialIntelligenceEvidenceDTO {
  recordType: string;
  recordId: number;
  reference: string;
  occurredOn?: string | null;
  role: string;
}

export interface CoachingAcknowledgementDTO {
  disposition: string;
  reason: string;
  acknowledgedByName?: string | null;
  acknowledgedAt: string;
}

export interface CoachingFindingDTO {
  findingKey: string;
  code: string;
  salesRepUserId: number;
  salesRepName: string;
  customerId?: number | null;
  customerName?: string | null;
  aggregateType: string;
  aggregateId: number;
  sourceVersion?: string | null;
  reference: string;
  nexoraSerial?: string | null;
  severity: string;
  observedValue?: number | null;
  observedUnit?: string | null;
  thresholdValue?: number | null;
  sampleSize: number;
  confidence: number;
  asOf: string;
  recommendation: string;
  actionRoute: string;
  policyVersion: string;
  evidence: CommercialIntelligenceEvidenceDTO[];
  latestAcknowledgement?: CoachingAcknowledgementDTO | null;
}

export interface RecoveryOpportunityDTO {
  recoveryKey: string;
  code: string;
  sourceType: string;
  sourceId: number;
  sourceVersion?: string | null;
  customerId?: number | null;
  customerName?: string | null;
  ownerUserId?: number | null;
  ownerName?: string | null;
  nexoraSerial?: string | null;
  severity: string;
  title: string;
  explanation: string;
  recommendedAction: string;
  actionRoute: string;
  dueAt?: string | null;
  sampleSize: number;
  confidence: number;
  evidence: CommercialIntelligenceEvidenceDTO[];
}

export interface CoachingRecoveryDTO {
  generatedAt: string;
  policyVersion: string;
  scope: 'tenant' | 'assigned_to_me' | string;
  dataCompleteness: { status: 'complete' | 'partial' | string; incompleteSources: string[] };
  coachingFindings: CoachingFindingDTO[];
  recoveryOpportunities: RecoveryOpportunityDTO[];
}

export interface CustomerHealthAcceptedPriceDTO {
  productId?: number | null;
  partNumber?: string | null;
  description?: string | null;
  currencyCode: string;
  quantity: number;
  unitPrice: number;
  acceptedOn?: string | null;
  evidence: CommercialIntelligenceEvidenceDTO;
}

export interface CustomerHealthStatusDTO {
  status: string;
  reason?: string | null;
}

export interface CustomerHealthDTO {
  customerId: number;
  generatedAt: string;
  dataCompleteness: { status: 'complete' | 'partial' | string; incompleteSources: string[] };
  period: { from: string; to: string; previousFrom?: string | null; previousTo?: string | null };
  rfqTrend: CustomerHealthStatusDTO & { currentCount: number; previousCount: number; changePercent?: number | null };
  quoteCoverage: CustomerHealthStatusDTO & { rfqCount: number; quotedRfqCount: number; coveragePercent?: number | null };
  quoteDecisions: CustomerHealthStatusDTO & { decidedCount: number; wonCount: number; lostCount: number; pendingCount?: number | null };
  conversion: CustomerHealthStatusDTO & { ratePercent?: number | null; sampleSize: number };
  acceptedPrices: CustomerHealthAcceptedPriceDTO[];
  margin: CustomerHealthStatusDTO & { grossMarginPercent?: number | null };
  revisionBurden: CustomerHealthStatusDTO & {
    revisionCount: number;
    inquiryCount: number;
    changedFieldCount: number;
    comparedFieldCount: number;
    fieldChangePercent?: number | null;
    changedLineCount: number;
    comparedLineCount: number;
    lineChangePercent?: number | null;
  };
  followUp: CustomerHealthStatusDTO & { openCount: number; overdueCount: number; effectivenessPercent?: number | null };
  lastCommercialActivity?: CommercialIntelligenceEvidenceDTO | null;
  healthBand: string;
  healthReasons: string[];
  opportunities: Array<{ code: string; title: string; explanation: string; actionRoute: string; evidence: CommercialIntelligenceEvidenceDTO[] }>;
  nextBestAction?: { title: string; explanation: string; actionRoute: string; evidence: CommercialIntelligenceEvidenceDTO[] } | null;
}

export interface InventoryOverviewDTO {
  generatedAt: string;
  metrics: IntelligenceMetric[];
  exceptions: InventoryExceptionDTO[];
}

export interface InventoryExceptionDTO {
  id: string;
  productId?: number | null;
  partNumber: string;
  productName: string;
  warehouseName?: string | null;
  exceptionType: string;
  availableQuantity: number;
  requiredQuantity?: number | null;
  dueAt?: string | null;
}

export interface AvailabilityDTO {
  productId: number;
  partNumber: string;
  productName: string;
  warehouseId: number;
  warehouseName: string;
  onHand: number;
  reserved: number;
  available: number;
  incoming: number;
  reorderPoint?: number | null;
  leadTimeDays?: number | null;
}

export interface WarehouseIntelligenceDTO {
  warehouseId: number;
  code: string;
  name: string;
  location?: string | null;
  active: boolean;
  skuCount: number;
  onHandUnits: number;
  reservedUnits: number;
  availableUnits: number;
  exceptionCount: number;
}

export interface ReservationDTO {
  id: number;
  productId: number;
  partNumber: string;
  productName: string;
  warehouseName: string;
  quantity: number;
  status: string;
  demandType: string;
  demandReference: string;
  nexoraSerial?: string | null;
  requiredAt?: string | null;
  version: number;
}

export interface IncomingStockDTO {
  id: number;
  purchaseOrderId?: number | null;
  purchaseOrderNumber: string;
  supplierName: string;
  partNumber: string;
  productName: string;
  warehouseName: string;
  orderedQuantity: number;
  receivedQuantity: number;
  expectedAt?: string | null;
  status: string;
}

export interface InventoryMovementDTO {
  id: number;
  occurredAt: string;
  movementType: string;
  partNumber: string;
  productName: string;
  warehouseName: string;
  quantity: number;
  referenceType?: string | null;
  reference?: string | null;
  actorName?: string | null;
}

export interface DemandDTO {
  productId: number;
  partNumber: string;
  productName: string;
  openDemand: number;
  available: number;
  shortfall: number;
  incoming: number;
  earliestNeedAt?: string | null;
  demandSources: number;
}

export interface InventoryResourceDTO {
  key: string;
  label: string;
  description: string;
  recordCount?: number | null;
  route?: string | null;
  requiredModule?: string | null;
}

export interface CommercialLineResolutionDTO {
  id: number;
  leadId: number;
  leadRevisionId: number;
  leadLineId: number;
  rfqId?: number | null;
  productId?: number | null;
  requestedPartNumber: string;
  requestedQuantity: number;
  classification: 'KnownInStock' | 'KnownIncoming' | 'KnownShortage' | 'UnknownProduct' | 'PossibleMatchReview' | 'NonInventoryService';
  availableToPromise: number;
  incomingAvailable: number;
  fulfilment: { classification?: string; allocatedQuantity?: number; shortageQuantity?: number };
  relatedResources: Array<{ resourceId: string; displayName: string; matchReason: string; score: number; evidenceReference: string }>;
  productResolution: { confidence?: number; method?: string; decisionState?: string };
  evidenceReference?: string | null;
  inventoryAsOfUtc: string;
  resolvedOn: string;
  externalDiscoveryUsed: boolean;
}

export interface ListParams {
  search?: string;
  status?: string;
  customerId?: number;
  sourceId?: number;
  ownerUserId?: number;
  warehouseId?: number;
  from?: string;
  to?: string;
}

const commercialRoot = '/api/commercial-intelligence';
const inventoryRoot = '/api/inventory-intelligence';

const commercialIntelligenceService = {
  getSalesToday: async (): Promise<SalesTodayDTO> =>
    (await axiosInstance.get<SalesTodayDTO>(`${commercialRoot}/sales-today`)).data,
  getTeamOverview: async (): Promise<TeamOverviewDTO> =>
    (await axiosInstance.get<TeamOverviewDTO>(`${commercialRoot}/team-overview`)).data,
  getRepDirectory: async (): Promise<RepSummaryDTO[]> =>
    (await axiosInstance.get<RepSummaryDTO[]>(`${commercialRoot}/reps`)).data,
  getRepProfile: async (userId: number): Promise<RepProfileDTO> =>
    (await axiosInstance.get<RepProfileDTO>(`${commercialRoot}/reps/${userId}`)).data,
  getAccountOwnership: async (params: ListParams = {}): Promise<AccountOwnershipDTO[]> =>
    (await axiosInstance.get<AccountOwnershipDTO[]>(`${commercialRoot}/account-ownership`, { params })).data,
  assignAccount: async (customerId: number, ownerUserId: number, expectedVersion: number, idempotencyKey: string): Promise<AccountOwnershipDTO> =>
    (await axiosInstance.post<AccountOwnershipDTO>(`${commercialRoot}/account-ownership/${customerId}/assign`, { ownerUserId, expectedVersion }, { headers: { 'Idempotency-Key': idempotencyKey } })).data,
  getRoutingQueue: async (params: { sourceId?: number } = {}): Promise<RoutingQueueItemDTO[]> =>
    (await axiosInstance.get<RoutingQueueItemDTO[]>(`${commercialRoot}/routing-queue`, { params })).data,
  assignRoutingItem: async (leadId: number, ownerUserId: number, expectedVersion: number, idempotencyKey: string): Promise<void> => {
    await axiosInstance.post(`${commercialRoot}/routing-queue/${leadId}/assign`, { ownerUserId, expectedVersion }, { headers: { 'Idempotency-Key': idempotencyKey } });
  },
  getFollowUps: async (params: ListParams = {}): Promise<FollowUpDTO[]> =>
    (await axiosInstance.get<FollowUpDTO[]>(`${commercialRoot}/follow-ups`, { params })).data,
  completeFollowUp: async (id: number, expectedVersion: number, idempotencyKey: string): Promise<void> => {
    await axiosInstance.post(`${commercialRoot}/follow-ups/${id}/complete`, { expectedVersion }, { headers: { 'Idempotency-Key': idempotencyKey } });
  },
  getPerformance: async (from: string, to: string): Promise<PerformanceDTO> =>
    (await axiosInstance.get<PerformanceDTO>(`${commercialRoot}/performance`, { params: { from, to } })).data,
  getCoachingRecovery: async (from: string, to: string): Promise<CoachingRecoveryDTO> =>
    (await axiosInstance.get<CoachingRecoveryDTO>(`${commercialRoot}/coaching-recovery`, { params: { from, to } })).data,
  acknowledgeCoachingFinding: async (findingKey: string, disposition: string, reason: string, from: string, to: string, idempotencyKey: string): Promise<CoachingAcknowledgementDTO> =>
    (await axiosInstance.post<CoachingAcknowledgementDTO>(
      `${commercialRoot}/coaching/${encodeURIComponent(findingKey)}/acknowledgements`,
      { disposition, reason, from, to },
      { headers: { 'Idempotency-Key': idempotencyKey } },
    )).data,
  getCustomerHealth: async (customerId: number, from: string, to: string): Promise<CustomerHealthDTO> =>
    (await axiosInstance.get<CustomerHealthDTO>(`/api/intelligence/customers/${customerId}/health`, { params: { from, to } })).data,

  getInventoryOverview: async (): Promise<InventoryOverviewDTO> =>
    (await axiosInstance.get<InventoryOverviewDTO>(`${inventoryRoot}/overview`)).data,
  getAvailability: async (params: ListParams = {}): Promise<AvailabilityDTO[]> =>
    (await axiosInstance.get<AvailabilityDTO[]>(`${inventoryRoot}/availability`, { params })).data,
  getWarehouses: async (): Promise<WarehouseIntelligenceDTO[]> =>
    (await axiosInstance.get<WarehouseIntelligenceDTO[]>(`${inventoryRoot}/warehouses`)).data,
  getReservations: async (params: ListParams = {}): Promise<ReservationDTO[]> =>
    (await axiosInstance.get<ReservationDTO[]>(`${inventoryRoot}/reservations`, { params })).data,
  releaseReservation: async (id: number, expectedVersion: number, idempotencyKey: string): Promise<void> => {
    await axiosInstance.post(`${inventoryRoot}/reservations/${id}/release`, { expectedVersion }, { headers: { 'Idempotency-Key': idempotencyKey } });
  },
  getIncoming: async (params: ListParams = {}): Promise<IncomingStockDTO[]> =>
    (await axiosInstance.get<IncomingStockDTO[]>(`${inventoryRoot}/incoming`, { params })).data,
  getMovements: async (params: ListParams = {}): Promise<InventoryMovementDTO[]> =>
    (await axiosInstance.get<InventoryMovementDTO[]>(`${inventoryRoot}/movements`, { params })).data,
  getDemand: async (params: ListParams = {}): Promise<DemandDTO[]> =>
    (await axiosInstance.get<DemandDTO[]>(`${inventoryRoot}/demand`, { params })).data,
  getRelatedResources: async (): Promise<InventoryResourceDTO[]> =>
    (await axiosInstance.get<InventoryResourceDTO[]>(`${inventoryRoot}/related-resources`)).data,
  resolveLeadLines: async (leadId: number, limit: 10 | 20 | 50): Promise<CommercialLineResolutionDTO[]> =>
    (await axiosInstance.post<CommercialLineResolutionDTO[]>(`${inventoryRoot}/leads/${leadId}/resolve`, undefined, { params: { limit } })).data,
  getLeadLineResolutions: async (leadId: number): Promise<CommercialLineResolutionDTO[]> =>
    (await axiosInstance.get<CommercialLineResolutionDTO[]>(`${inventoryRoot}/leads/${leadId}/resolutions`)).data,
  getRfqLineResolutions: async (rfqId: number): Promise<CommercialLineResolutionDTO[]> =>
    (await axiosInstance.get<CommercialLineResolutionDTO[]>(`${inventoryRoot}/rfqs/${rfqId}/resolutions`)).data,
  getQuoteLineResolutions: async (quoteId: number): Promise<CommercialLineResolutionDTO[]> =>
    (await axiosInstance.get<CommercialLineResolutionDTO[]>(`${inventoryRoot}/quotes/${quoteId}/resolutions`)).data,
};

export default commercialIntelligenceService;
