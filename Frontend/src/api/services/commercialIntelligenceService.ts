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
  getRoutingQueue: async (): Promise<RoutingQueueItemDTO[]> =>
    (await axiosInstance.get<RoutingQueueItemDTO[]>(`${commercialRoot}/routing-queue`)).data,
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
