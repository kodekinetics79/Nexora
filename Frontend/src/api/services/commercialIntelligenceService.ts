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
  actionRoute?: string | null;
  requiredModule?: string | null;
}

export interface SalesTodayDTO {
  generatedAt: string;
  scope: 'tenant' | 'managed_scope' | 'assigned_to_me';
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

export interface RoutingWorkloadDTO {
  activeLeadCount: number;
  leadLineCount: number;
  overdueDeadlineCount: number;
  urgentDeadlineCount: number;
  approachingDeadlineCount: number;
  openRfqCount: number;
  openQuoteCount: number;
  followUpCount: number;
  workloadPoints: number;
}

export interface RoutingOwnerOptionDTO {
  userId: number;
  name: string;
  email: string;
  roleName?: string | null;
  isAvailable: boolean;
  capacityPercent: number;
  workload: RoutingWorkloadDTO;
  hasGovernedProfile: boolean;
  eligibilityReason: string;
  measuredAtUtc: string;
  policyVersion: string;
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
  decidedQuotes: number;
  conversionEligible: boolean;
  performanceFrom: string;
  performanceTo: string;
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
  matchConfidence: number;
  policyVersion: string;
  recommendationMeasuredAt?: string | null;
  recommendedOwnerAvailable?: boolean | null;
  recommendedOwnerCapacityPercent?: number | null;
  recommendedOwnerWorkloadPoints?: number | null;
  claimedByUserId?: number | null;
  claimedByName?: string | null;
  claimedUntil?: string | null;
  /** True when `claimedUntil` has passed. Nothing ever flips `status` back to Open, so a row
   *  rendered from `status` alone would read "Claimed" for ever. Render from this. */
  claimExpired: boolean;
  priority: number;
  status: string;
  overdue: boolean;
  version: number;
}

/**
 * A rep's governed routing profile, as returned by
 * `GET /api/commercial-intelligence/reps/routing-profiles`.
 *
 * Every active user in the tenant appears, whether or not a profile row exists — a fail-closed
 * table with no rows means routing can assign nobody, so the people who are MISSING a profile are
 * the important ones on this screen.
 *
 * The stored row and the engine's live verdict are separate on purpose. `isRoutingEligible` and
 * `capacityPercent` are what a manager configured; `isAvailable` and `eligibilityReason` are what
 * routing will actually do today, which also accounts for measured workload and for a profile
 * whose effective window has closed.
 */
export interface RepRoutingProfileDTO {
  userId: number;
  name: string;
  email?: string | null;
  roleName?: string | null;
  hasProfile: boolean;
  profileEffectiveNow: boolean;
  isRoutingEligible?: boolean | null;
  capacityPercent?: number | null;
  distributionWeight?: number | null;
  territoryKeys: string[];
  productCategoryKeys: string[];
  effectiveFromUtc?: string | null;
  effectiveToUtc?: string | null;
  /** 0 when no profile exists — the create sentinel the write endpoint expects. */
  version: number;
  updatedAtUtc?: string | null;
  updatedBy?: string | null;
  isAvailable: boolean;
  eligibilityReason: string;
  measuredCapacityPercent?: number | null;
  workloadPoints?: number | null;
  policyVersion?: string | null;
  measuredAtUtc?: string | null;
}

/** Body of POST reps/{userId}/routing-profile — matches `UpsertRepRoutingProfileRequest`. */
export interface UpsertRepRoutingProfileRequest {
  isRoutingEligible: boolean;
  capacityPercent: number;
  distributionWeight: number;
  territoryKeys: string[];
  productCategoryKeys: string[];
  /** Omit on create: the server floors its own default to midnight UTC so retries stay
   *  idempotent. Round-trip the stored value on update so editing capacity does not silently
   *  restart the effective period. */
  effectiveFromUtc?: string | null;
  effectiveToUtc?: string | null;
  expectedVersion: number;
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
  scope: 'tenant' | 'assigned_to_me';
  minimumConversionSample: number;
  outcomeReconciliation: {
    recordedOutcomes?: number | null;
    attributedOutcomes: number;
    unattributedOutcomes?: number | null;
    completenessPercent?: number | null;
    isTenantComplete: boolean;
  };
  representatives: Array<RepSummaryDTO & {
    wonQuotes: number;
    lostQuotes: number;
    decidedQuotes: number;
    conversionEligible: boolean;
    conversionRate?: number | null;
    activityCount: number;
    opportunities: number;
    quoteSent: number;
    customerResponses: number;
    averageResponseHours?: number | null;
    followUpsCreated: number;
    completedFollowUps: number;
    followUpsCompletedOnTime: number;
    openFollowUps: number;
    overdueFollowUps: number;
    revenueByCurrency: CurrencyAmountGroupDTO[];
  }>;
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
  inventoryId: number;
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
  /**
   * FR-INV-04. Null means the level has NOT been configured for this item and warehouse — it does
   * not mean zero, and the screen must not render it as a blank cell that reads like one. A null
   * minimum is why an item that has run out raises nothing: nobody has said what "enough" is.
   */
  minimumLevel?: number | null;
  maximumLevel?: number | null;
  safetyStock: number;
  leadTimeDays?: number | null;
}

/** FR-INV-04. One stock row's live position against the levels configured for it. */
export interface StockLevelRowDTO {
  inventoryId: number;
  productId: number;
  partNumber: string;
  productName: string;
  warehouseId: number;
  warehouseName: string;
  onHand: number;
  available: number;
  incoming: number;
  projected: number;
  minimumLevel?: number | null;
  maximumLevel?: number | null;
  reorderPoint: number;
  /** The single worst condition this row qualifies for, or null when it breaches nothing. */
  breach?: 'OUT_OF_STOCK' | 'BELOW_MINIMUM' | 'REORDER_POINT' | 'OVERSTOCK' | null;
  thresholdQuantity: number;
  shortfallQuantity: number;
  /** False when no level is configured at all. "Nothing is wrong" and "nobody is looking" are
   * different answers and the screen states which one it is showing. */
  monitored: boolean;
}

export interface StockLevelsDTO {
  generatedAt: string;
  rowCount: number;
  configuredCount: number;
  breachedCount: number;
  unmonitoredCount: number;
  rows: StockLevelRowDTO[];
}

/** FR-INV-04. One raised reorder or overstock alert. */
export interface ReorderAlertDTO {
  id: number;
  inventoryId: number;
  productId: number;
  warehouseId: number;
  partNumber: string;
  productName?: string | null;
  warehouseName: string;
  kind: 'OUT_OF_STOCK' | 'BELOW_MINIMUM' | 'REORDER_POINT' | 'OVERSTOCK';
  status: 'OPEN' | 'ACKNOWLEDGED' | 'RESOLVED';
  severity: string;
  onHandQuantity: number;
  availableQuantity: number;
  incomingQuantity: number;
  projectedQuantity: number;
  thresholdQuantity: number;
  shortfallQuantity: number;
  raisedOn: string;
  /** How many people were mailed a copy. Zero is a real state — the alert is on the ledger and
   * nobody has been told — and is rendered as such rather than left blank. */
  notifiedCount: number;
  acknowledgedOn?: string | null;
  acknowledgedBy?: string | null;
  acknowledgementReason?: string | null;
  resolvedOn?: string | null;
  resolutionReason?: string | null;
  version: number;
}

export interface ReorderAlertsDTO {
  generatedAt: string;
  alertCount: number;
  rows: ReorderAlertDTO[];
}

/** FR-INV-05. One counted line: what the book said, what the counter found, and the gap. */
export interface StockCountVarianceRowDTO {
  inventoryId: number;
  productId: number;
  partNumber: string;
  productName: string;
  warehouseId: number;
  warehouseName: string;
  bookQuantity: number;
  countedQuantity: number;
  variance: number;
  /** Null when the book value was zero: a count finding 5 units where the system knew of none is
   * an infinite percentage, and reporting 0% or 500% would both be inventions. */
  variancePercent?: number | null;
  countedOn: string;
  countedBy: string;
  reason?: string | null;
}

export interface StockCountVarianceDTO {
  countedRows: number;
  netVariance: number;
  absoluteVariance: number;
  rows: StockCountVarianceRowDTO[];
}

/** FR-INV-06. One inventory row's ageing position. */
export interface StockAgeingRowDTO {
  inventoryId: number;
  productId: number;
  partNumber: string;
  productName: string;
  warehouseId: number;
  warehouseName: string;
  onHand: number;
  unitCost: number;
  carryingValue: number;
  lastReceiptOn?: string | null;
  lastIssueOn?: string | null;
  daysSinceLastIssue?: number | null;
  daysSinceLastReceipt?: number | null;
  band: 'CURRENT' | 'SLOW_MOVING' | 'VERY_SLOW' | 'OBSOLETE' | 'UNDATED';
}

export interface StockAgeingDTO {
  rowCount: number;
  carryingValue: number;
  bands: Array<{ band: string; rowCount: number; units: number; carryingValue: number }>;
  rows: StockAgeingRowDTO[];
}

/** The state of one inventory row after a stock write. */
export interface StockLedgerResultDTO {
  inventoryId: number;
  productId: number;
  warehouseId: number;
  onHand: number;
  quarantine: number;
  damaged: number;
  expired: number;
  safetyStock: number;
  movementId?: number | null;
  /** FR-INV-05. What the system believed was on hand immediately BEFORE a count. */
  bookQuantity?: number | null;
  countedQuantity?: number | null;
  variance?: number | null;
  countAgreed?: boolean;
  minimumLevel?: number | null;
  maximumLevel?: number | null;
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
  /**
   * FR-INV-01. The material lot this hold physically names. Null is a real state, not a loading
   * one: stock that entered by opening count, adjustment or transfer has no goods receipt behind
   * it and therefore no lot, so a recall cannot reach it. The screen must say so.
   */
  materialLotId?: number | null;
  lotNumber?: string | null;
  version: number;
}

/** FR-INV-01. One lot's reservable position on an inventory row, in warehouse pick order. */
export interface ReservableLotDTO {
  materialLotId: number;
  lotNumber: string;
  remaining: number;
  held: number;
  reservable: number;
  expiryDate?: string | null;
  receivedOn: string;
}

export interface InventoryLotAvailabilityDTO {
  inventoryId: number;
  onHand: number;
  reserved: number;
  available: number;
  lotControlledQuantity: number;
  unlottedQuantity: number;
  lots: ReservableLotDTO[];
}

/** FR-INV-01. The recall population for one lot: who holds it and who has already had it. */
export interface LotCommitmentDTO {
  reservationId: number;
  orderId?: number | null;
  orderItemId?: number | null;
  quantity: number;
  status: string;
  createdOn: string;
}

export interface LotCommitmentsDTO {
  materialLotId: number;
  lotNumber: string;
  status: string;
  quantityReceived: number;
  quantityConsumed: number;
  heldQuantity: number;
  consumedQuantity: number;
  affectedOrders: { orderId: number; orderNo?: string | null }[];
  held: LotCommitmentDTO[];
  consumed: LotCommitmentDTO[];
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
  rfqItemId?: number | null;
  productId?: number | null;
  requestedPartNumber: string;
  requestedQuantity: number;
  classification: 'KnownInStock' | 'KnownIncoming' | 'KnownShortage' | 'UnknownProduct' | 'PossibleMatchReview' | 'NonInventoryService';
  availableToPromise: number;
  incomingAvailable: number;
  projectedShortage: number;
  leadTimeDays?: number | null;
  expectedAvailableOn?: string | null;
  unitCost?: number | null;
  costCurrencyCode?: string | null;
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
  getRepProfile: async (userId: number, from?: string, to?: string): Promise<RepProfileDTO> =>
    (await axiosInstance.get<RepProfileDTO>(`${commercialRoot}/reps/${userId}`, { params: { from, to } })).data,
  getAccountOwnership: async (params: ListParams = {}): Promise<AccountOwnershipDTO[]> =>
    (await axiosInstance.get<AccountOwnershipDTO[]>(`${commercialRoot}/account-ownership`, { params })).data,
  assignAccount: async (customerId: number, ownerUserId: number, expectedVersion: number, reason: string | undefined, idempotencyKey: string): Promise<AccountOwnershipDTO> =>
    (await axiosInstance.post<AccountOwnershipDTO>(`${commercialRoot}/account-ownership/${customerId}/assign`, { ownerUserId, expectedVersion, reason }, { headers: { 'Idempotency-Key': idempotencyKey } })).data,
  getRoutingQueue: async (params: { sourceId?: number } = {}): Promise<RoutingQueueItemDTO[]> =>
    (await axiosInstance.get<RoutingQueueItemDTO[]>(`${commercialRoot}/routing-queue`, { params })).data,
  getRepRoutingProfiles: async (): Promise<RepRoutingProfileDTO[]> =>
    (await axiosInstance.get<RepRoutingProfileDTO[]>(`${commercialRoot}/reps/routing-profiles`)).data,
  upsertRepRoutingProfile: async (userId: number, body: UpsertRepRoutingProfileRequest, idempotencyKey: string): Promise<void> => {
    await axiosInstance.post(`${commercialRoot}/reps/${userId}/routing-profile`, body, { headers: { 'Idempotency-Key': idempotencyKey } });
  },
  getRoutingOwnerOptions: async (): Promise<RoutingOwnerOptionDTO[]> =>
    (await axiosInstance.get<RoutingOwnerOptionDTO[]>(`${commercialRoot}/routing-owner-options`)).data,
  getAccountOwnerOptions: async (): Promise<RoutingOwnerOptionDTO[]> =>
    (await axiosInstance.get<RoutingOwnerOptionDTO[]>(`${commercialRoot}/account-owner-options`)).data,
  assignRoutingItem: async (sourceId: number, ownerUserId: number, expectedVersion: number, reason: string | undefined, idempotencyKey: string): Promise<void> => {
    await axiosInstance.post(`${commercialRoot}/routing-queue/${sourceId}/assign`, { ownerUserId, expectedVersion, reason }, { headers: { 'Idempotency-Key': idempotencyKey } });
  },
  getFollowUps: async (params: ListParams = {}): Promise<FollowUpDTO[]> =>
    (await axiosInstance.get<FollowUpDTO[]>(`${commercialRoot}/follow-ups`, { params })).data,
  /**
   * A follow-up the rep sets deliberately on one quote — "call them Thursday about the price".
   * POST /api/commercial-intelligence/follow-ups (Quotations: Edit). Assigned to the caller; the
   * reason is what the Follow-ups list shows in its Reason column (80 characters).
   */
  createFollowUp: async (
    body: { quoteId: number; dueAt: string; reason: string },
    idempotencyKey: string,
  ): Promise<{ id: number; quoteId: number; dueAt: string; reason: string; status: string; version: number }> =>
    (await axiosInstance.post(`${commercialRoot}/follow-ups`, body, { headers: { 'Idempotency-Key': idempotencyKey } })).data,
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
  getReservableLots: async (inventoryId: number): Promise<InventoryLotAvailabilityDTO> =>
    (await axiosInstance.get<InventoryLotAvailabilityDTO>(
      `${inventoryRoot}/inventory/${inventoryId}/lots`)).data,
  getLotCommitments: async (materialLotId: number): Promise<LotCommitmentsDTO> =>
    (await axiosInstance.get<LotCommitmentsDTO>(
      `${inventoryRoot}/lots/${materialLotId}/commitments`)).data,
  releaseReservation: async (id: number, expectedVersion: number, idempotencyKey: string): Promise<void> => {
    await axiosInstance.post(`${inventoryRoot}/reservations/${id}/release`, { expectedVersion }, { headers: { 'Idempotency-Key': idempotencyKey } });
  },
  getIncoming: async (params: ListParams = {}): Promise<IncomingStockDTO[]> =>
    (await axiosInstance.get<IncomingStockDTO[]>(`${inventoryRoot}/incoming`, { params })).data,
  getMovements: async (params: ListParams = {}): Promise<InventoryMovementDTO[]> =>
    (await axiosInstance.get<InventoryMovementDTO[]>(`${inventoryRoot}/movements`, { params })).data,
  getDemand: async (params: ListParams = {}): Promise<DemandDTO[]> =>
    (await axiosInstance.get<DemandDTO[]>(`${inventoryRoot}/demand`, { params })).data,
  // ---- FR-INV-04: minimum/maximum levels and reorder alerts ----
  getStockLevels: async (breachedOnly = false): Promise<StockLevelsDTO> =>
    (await axiosInstance.get<StockLevelsDTO>(`${inventoryRoot}/stock/levels`, { params: { breachedOnly } })).data,
  setStockLevels: async (
    productId: number, warehouseId: number,
    minimumLevel: number | null, maximumLevel: number | null,
    idempotencyKey: string,
  ): Promise<StockLedgerResultDTO> =>
    // Both levels are always sent. There is no partial update: a payload that silently kept a
    // stale maximum would be indistinguishable on screen from one that cleared it.
    (await axiosInstance.post<StockLedgerResultDTO>(`${inventoryRoot}/stock/levels`,
      { productId, warehouseId, minimumLevel, maximumLevel },
      { headers: { 'Idempotency-Key': idempotencyKey } })).data,
  getReorderAlerts: async (params: { status?: string; kind?: string } = {}): Promise<ReorderAlertsDTO> =>
    (await axiosInstance.get<ReorderAlertsDTO>(`${inventoryRoot}/reorder-alerts`, { params })).data,
  acknowledgeReorderAlert: async (id: number, expectedVersion: number, reason: string): Promise<void> => {
    await axiosInstance.post(`${inventoryRoot}/reorder-alerts/${id}/acknowledge`, { expectedVersion, reason });
  },

  // ---- FR-INV-05 / FR-INV-06: variance and ageing ----
  getCountVariance: async (params: { from?: string; to?: string; varianceOnly?: boolean } = {}): Promise<StockCountVarianceDTO> =>
    (await axiosInstance.get<StockCountVarianceDTO>(`${inventoryRoot}/stock/count-variance`, { params })).data,
  getStockAgeing: async (params: { warehouseId?: number; band?: string } = {}): Promise<StockAgeingDTO> =>
    (await axiosInstance.get<StockAgeingDTO>(`${inventoryRoot}/stock/ageing`, { params })).data,

  // ---- the stock-mutation half of the controller, which until now had no caller at all ----
  recordStockCount: async (productId: number, warehouseId: number, countedQuantity: number, reason: string | undefined, idempotencyKey: string): Promise<StockLedgerResultDTO> =>
    (await axiosInstance.post<StockLedgerResultDTO>(`${inventoryRoot}/stock/count`,
      { productId, warehouseId, countedQuantity, reason },
      { headers: { 'Idempotency-Key': idempotencyKey } })).data,
  adjustStock: async (productId: number, warehouseId: number, delta: number, reason: string | undefined, idempotencyKey: string): Promise<StockLedgerResultDTO> =>
    (await axiosInstance.post<StockLedgerResultDTO>(`${inventoryRoot}/stock/adjust`,
      { productId, warehouseId, delta, reason },
      { headers: { 'Idempotency-Key': idempotencyKey } })).data,
  reclassifyStock: async (productId: number, warehouseId: number, bucket: 'Quarantine' | 'Damaged' | 'Expired', quantity: number, reason: string | undefined, idempotencyKey: string): Promise<StockLedgerResultDTO> =>
    (await axiosInstance.post<StockLedgerResultDTO>(`${inventoryRoot}/stock/reclassify`,
      { productId, warehouseId, bucket, quantity, reason },
      { headers: { 'Idempotency-Key': idempotencyKey } })).data,
  setSafetyStock: async (productId: number, warehouseId: number, safetyStock: number, idempotencyKey: string): Promise<StockLedgerResultDTO> =>
    (await axiosInstance.post<StockLedgerResultDTO>(`${inventoryRoot}/stock/safety-stock`,
      { productId, warehouseId, safetyStock },
      { headers: { 'Idempotency-Key': idempotencyKey } })).data,
  transferStock: async (productId: number, fromWarehouseId: number, toWarehouseId: number, quantity: number, reason: string | undefined, idempotencyKey: string): Promise<{ from: StockLedgerResultDTO; to: StockLedgerResultDTO }> =>
    (await axiosInstance.post<{ from: StockLedgerResultDTO; to: StockLedgerResultDTO }>(`${inventoryRoot}/stock/transfer`,
      { productId, fromWarehouseId, toWarehouseId, quantity, reason },
      { headers: { 'Idempotency-Key': idempotencyKey } })).data,

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
