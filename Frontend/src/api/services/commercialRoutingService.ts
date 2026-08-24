import axiosInstance from '../axiosInstance';

/**
 * Master-data configuration for RFQ routing (FR-RFQ-07).
 *
 * Binds ONLY to endpoints that exist on `CommercialRoutingController`:
 *   GET  /api/commercial-routing/customers/{customerId}   → Customers: View
 *   POST /api/commercial-routing/customer-ownerships      → Customers: Edit + manager role
 *   POST /api/commercial-routing/customer-identifiers     → Customers: Edit + manager role
 *   POST /api/commercial-routing/queue/{id}/claim         → Leads: Edit
 *   POST /api/commercial-routing/queue/{id}/release       → Leads: Edit
 *   POST /api/commercial-routing/leads/{id}/route         → Leads: Edit + manager role
 *
 * One exception, at the bottom of the file: `getLeadAssignmentHistory` binds
 * `GET /api/commercial-intelligence/leads/{id}/assignment-history`. It is the audit trail of
 * `changeLeadOwner`'s own write, so it belongs beside it rather than a module away.
 *
 * Note the permission split on the queue verbs, because it is the whole design of the pull lane:
 * claim and release are the only routing verbs a plain sales rep can reach. Assignment — on both
 * this controller and `CommercialIntelligenceController` — is manager-only. A claim is therefore
 * a reservation, not ownership; a manager still confirms it. Do not paper over that in the UI.
 *
 * The queue LIST is deliberately not bound here. `GET /api/commercial-intelligence/routing-queue`
 * already reads the same `UnassignedWorkItem` rows for the same `Leads: View` permission and adds
 * the recommendation, the owner workload and the policy version on top, so a second list caller
 * would be a second projection of one table rather than a missing capability.
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
  /**
   * Why the owner changed. Persisted verbatim onto `LeadAssignment.Comment` and read back by
   * the owner history panel, so this is the sentence a manager sees months later next to
   * "Aisha → Tariq".
   *
   * REQUIRED — at least 5 characters after trimming — when, and only when, the lead already has
   * an owner AND the new assignee is a different person. Not for a self-assign, not for an
   * unowned lead, not for re-confirming the current owner. The server answers 400 with a
   * plain-English sentence when it is missing or too short, and `apiErrors.ts` lets a 400's
   * server text through verbatim, so that sentence is what the user reads.
   *
   * The client-side half of the same rule is `LeadOwnerPicker.assignmentNeedsReason`, which is
   * what decides whether the field is even shown. A form that asks for a justification it does
   * not need is the difference between considerate and bureaucratic.
   */
  comment?: string | null;
}

/**
 * One recorded change of a lead's owner, as returned by
 * `GET /api/commercial-intelligence/leads/{leadId}/assignment-history` (Leads: View).
 *
 * `scope` and `reasonCode` cross the wire as raw enum / decision codes
 * (`LeadOnly`, `MANUAL_ASSIGNMENT`, `PRIMARY_OWNER_ASSIGNED`, …). Neither is ever rendered as
 * itself — see `utils/routingDecisionReasons.ts` and `assignmentScopeLabel`.
 */
export interface LeadAssignmentHistoryEntry {
  id: number;
  leadId: number;
  previousOwnerUserId?: number | null;
  ownerUserId: number;
  previousOwnerName?: string | null;
  ownerName?: string | null;
  scope: string;
  reasonCode?: string | null;
  comment?: string | null;
  effectiveFrom: string;
  effectiveTo?: string | null;
  correlationId?: string | null;
  idempotencyKey?: string | null;
}

export interface LeadOwnershipResponse {
  leadId: number;
  assignedToUserId?: number | null;
  assignmentMethod: 'AUTOMATIC' | 'MANUAL';
  manualAssignmentOverride: boolean;
  assignmentVersion: number;
}

/** Mirrors `CommercialRouting.UnassignedWorkItem` as returned by the lease verbs. */
export interface QueueLeaseResultDTO {
  id: number;
  leadId: number;
  commercialCaseReference: string;
  status: number;
  claimedByUserId?: number | null;
  claimedUntil?: string | null;
  version: number;
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

  /**
   * Every recorded owner change on one lead, newest first.
   *
   * The one read bound here that does NOT live on `CommercialRoutingController`: it is the audit
   * trail of `changeLeadOwner` above, it answers for that write, and splitting it into the
   * intelligence service would put a lead's ownership history a module away from the control
   * that writes it. The endpoint had zero callers until this panel — a complete reassignment
   * trail was being recorded for nobody to read.
   */
  getLeadAssignmentHistory: async (leadId: number): Promise<LeadAssignmentHistoryEntry[]> =>
    (await axiosInstance.get<LeadAssignmentHistoryEntry[]>(
      `/api/commercial-intelligence/leads/${leadId}/assignment-history`)).data,

  /**
   * Takes a lease on a queue item so two reps cannot work the same inquiry.
   * The claimant is taken from the bearer token server-side — there is no user id to send.
   */
  claimQueueItem: async (workItemId: number, expectedVersion: number, leaseMinutes = 15): Promise<QueueLeaseResultDTO> =>
    (await axiosInstance.post<QueueLeaseResultDTO>(
      `${root}/queue/${workItemId}/claim`, { expectedVersion, leaseMinutes })).data,

  releaseQueueItem: async (workItemId: number, expectedVersion: number): Promise<QueueLeaseResultDTO> =>
    (await axiosInstance.post<QueueLeaseResultDTO>(
      `${root}/queue/${workItemId}/release`, { expectedVersion })).data,

  /**
   * Re-runs the routing engine over a lead.
   *
   * The reconciliation worker only picks up leads that have NO routing decision at all, so a lead
   * that was already decided as Unassigned — because nobody had a governed profile at the time —
   * will never be reconsidered on its own. This is the trigger that reopens those. The
   * idempotency key must be fresh for the re-run to be a new decision rather than a replay of the
   * old one, so it is minted per click.
   */
  routeLead: async (leadId: number): Promise<{ decisionId: number; selectedUserId?: number | null; decisionCode: string }> => {
    const operationId = crypto.randomUUID();
    return (await axiosInstance.post(`${root}/leads/${leadId}/route`, {
      idempotencyKey: `manual-reroute:${operationId}`,
      correlationId: operationId,
    })).data;
  },
};

export default commercialRoutingService;
