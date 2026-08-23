import axiosInstance from '../axiosInstance';

/**
 * Master-data configuration for RFQ routing (FR-RFQ-07).
 *
 * Binds ONLY to endpoints that exist on `CommercialRoutingController`:
 *   GET  /api/commercial-routing/customers/{customerId}   → Customers: View
 *   POST /api/commercial-routing/customer-ownerships      → Customers: Edit + manager role
 *   POST /api/commercial-routing/customer-identifiers     → Customers: Edit + manager role
 *
 * There is no list-all, update or delete endpoint for ownerships, so the screen reads one
 * customer's rules at a time and creates only. Do not invent the missing verbs client-side.
 *
 * Enums cross the wire as NUMBERS. The API registers no `JsonStringEnumConverter`, so
 * System.Text.Json serialises these as their integer values and rejects the string names.
 */

/** Mirrors `CommercialRouting.OwnershipScope` — order is the declaration order. */
export const OWNERSHIP_SCOPE = {
  CustomerException: 0,
  ProductCategory: 1,
  Branch: 2,
  Territory: 3,
  KeyAccountTeam: 4,
  GeneralCustomer: 5,
} as const;

export type OwnershipScope = (typeof OWNERSHIP_SCOPE)[keyof typeof OWNERSHIP_SCOPE];

/** Mirrors `CommercialRouting.CustomerIdentifierType`. */
export const CUSTOMER_IDENTIFIER_TYPE = {
  ErpAccount: 0,
  TaxRegistration: 1,
  Email: 2,
  Domain: 3,
  Phone: 4,
  Alias: 5,
  CustomerName: 6,
  HistoricalInference: 7,
  Portal: 8,
  PortalAccount: 9,
  RfqNumberPattern: 10,
} as const;

export type CustomerIdentifierType =
  (typeof CUSTOMER_IDENTIFIER_TYPE)[keyof typeof CUSTOMER_IDENTIFIER_TYPE];

export interface CustomerOwnershipDTO {
  id: number;
  businessUnitId: number;
  customerId: number;
  primaryUserId: number;
  backupUserId?: number | null;
  scope: OwnershipScope;
  scopeKey?: string | null;
  priority: number;
  effectiveFrom: string;
  effectiveTo?: string | null;
  isActive: boolean;
  source: string;
  reason?: string | null;
  mutationIdempotencyKey?: string | null;
  version: number;
}

export interface CustomerIdentifierDTO {
  id: number;
  businessUnitId: number;
  customerId: number;
  identifierType: CustomerIdentifierType;
  normalizedValue: string;
  displayValue: string;
  isVerified: boolean;
  confidence: number;
  source: string;
  effectiveFrom: string;
  effectiveTo?: string | null;
  learnedFromLeadId?: number | null;
  learnedFromReviewAuditId?: number | null;
  observationCount: number;
  lastObservedOn?: string | null;
}

export interface CustomerRoutingProfileDTO {
  customerId: number;
  identifiers: CustomerIdentifierDTO[];
  ownerships: CustomerOwnershipDTO[];
}

/** Body of POST /customer-ownerships — matches `CreateCustomerOwnershipCommand`. */
export interface CreateCustomerOwnershipRequest {
  customerId: number;
  primaryUserId: number;
  backupUserId: number | null;
  scope: OwnershipScope;
  scopeKey: string | null;
  priority: number;
  /** ISO-8601 instant. Required by the server (non-nullable DateTime). */
  effectiveFrom: string;
  effectiveTo: string | null;
  source: string;
  reason: string | null;
}

/** Body of POST /customer-identifiers — matches `UpsertCustomerIdentifierCommand`. */
export interface UpsertCustomerIdentifierRequest {
  customerId: number;
  identifierType: CustomerIdentifierType;
  value: string;
  isVerified: boolean;
  /** Server rejects anything outside 0–1 inclusive. */
  confidence: number;
  source: string;
}

export interface RoutingOwnerOption {
  userId: number;
  name: string;
  email: string;
  roleName?: string | null;
  isAvailable: boolean;
  capacityPercent: number;
  eligibilityReason: string;
}

export const LEAD_OWNERSHIP_ACTION = {
  Assign: 0,
  Unassign: 1,
  ReturnToAutomatic: 2,
} as const;

export interface ChangeLeadOwnerRequest {
  action: (typeof LEAD_OWNERSHIP_ACTION)[keyof typeof LEAD_OWNERSHIP_ACTION];
  assignedToUserId?: number | null;
  expectedAssignmentVersion: number;
  idempotencyKey: string;
  correlationId: string;
  comment?: string | null;
}

export interface LeadOwnershipResponse {
  leadId: number;
  assignedToUserId?: number | null;
  assignmentMethod: 'AUTOMATIC' | 'MANUAL';
  manualAssignmentOverride: boolean;
  assignmentVersion: number;
}

const root = '/api/commercial-routing';

const commercialRoutingService = {
  /** Returns null when the customer has no routing profile in this tenant (404). */
  getCustomerProfile: async (customerId: number): Promise<CustomerRoutingProfileDTO> =>
    (await axiosInstance.get<CustomerRoutingProfileDTO>(`${root}/customers/${customerId}`)).data,

  createOwnership: async (body: CreateCustomerOwnershipRequest): Promise<CustomerOwnershipDTO> =>
    (await axiosInstance.post<CustomerOwnershipDTO>(`${root}/customer-ownerships`, body)).data,

  upsertIdentifier: async (body: UpsertCustomerIdentifierRequest): Promise<CustomerIdentifierDTO> =>
    (await axiosInstance.post<CustomerIdentifierDTO>(`${root}/customer-identifiers`, body)).data,

  getOwnerOptions: async (): Promise<RoutingOwnerOption[]> =>
    (await axiosInstance.get<RoutingOwnerOption[]>(`${root}/owner-options`)).data,

  changeLeadOwner: async (leadId: number, body: ChangeLeadOwnerRequest): Promise<LeadOwnershipResponse> =>
    (await axiosInstance.put<LeadOwnershipResponse>(`${root}/leads/${leadId}/owner`, body)).data,
};

export default commercialRoutingService;
