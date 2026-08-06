// ---------------------------------------------------------------------------
// Nexora Platform Owner Console — domain types
//
// These types mirror the platform API contract (`/api/platform/*`). They are
// the single source of truth shared by the typed API client and every page in
// `src/platform`. Casing matches the backend JSON (camelCased DTOs).
// ---------------------------------------------------------------------------

/**
 * A plan's tier is its own lowercased plan code straight from the backend
 * ("free", "pro", "enterprise", custom codes, …). Tenants without a plan are
 * reported as "none" — nothing is ever silently bucketed.
 */
export type PlanTier = string;

export type TenantStatus =
  | 'active'
  | 'trial'
  | 'suspended'
  | 'provisioning'
  | 'archived';

export type HealthStatus = 'healthy' | 'degraded' | 'down';

export type JobStatus =
  | 'queued'
  | 'in_flight'
  | 'succeeded'
  | 'failed'
  | 'dead_letter';

export type AuditResult = 'success' | 'failure';

// --- Plans & entitlements ---------------------------------------------------

export interface Plan {
  id: string;
  name: string;
  /** Canonical lowercased plan code (unique). */
  code: string;
  tier: PlanTier;
  /** Scheduling weight used by the extraction dispatcher (higher = more share). */
  weight: number;
  /** Max concurrent extraction jobs allowed for a tenant on this plan. */
  concurrencyCap: number;
  /** Monthly document processing quota. `null` = unlimited. */
  monthlyDocQuota: number | null;
  /** Seat quota. `null` = unlimited. */
  seatQuota: number | null;
  priceMonthlyUsd: number | null;
  isActive: boolean;
  /** Feature-flag keys enabled (true) in the plan's features JSON. */
  entitlements: string[];
}

/** Body for POST /api/platform/plans and PUT /api/platform/plans/{id}. */
export interface UpsertPlanInput {
  code: string;
  name: string;
  weight: number;
  maxConcurrentExtractionJobs: number;
  maxDocsPerMonth: number;
  maxSeats: number;
  monthlyPriceUsd: number | null;
  /** JSON object of feature entitlements, e.g. `{"copilot": true}`. */
  features: string;
  isActive: boolean;
}

// --- Tenants ----------------------------------------------------------------

export interface Tenant {
  id: string;
  name: string;
  slug: string;
  planId: string | null;
  /** Lowercased plan code, or null when the tenant has no plan. */
  planCode: string | null;
  status: TenantStatus;
  statusReason: string | null;
  createdAt: string | null; // ISO
}

// --- Platform operators (control-plane accounts) ----------------------------

export type PlatformOperatorRole =
  | 'Owner'
  | 'SupportAdmin'
  | 'BillingAdmin'
  | 'ReadOnlyOps';

export const PLATFORM_OPERATOR_ROLES: PlatformOperatorRole[] = [
  'Owner',
  'SupportAdmin',
  'BillingAdmin',
  'ReadOnlyOps',
];

export interface PlatformOperator {
  id: string;
  email: string;
  platformRole: string;
  isActive: boolean;
  displayName: string | null;
  lastLogin: string | null; // ISO
  createdOn: string; // ISO
}

export interface CreatePlatformOperatorInput {
  email: string;
  password: string;
  role: PlatformOperatorRole;
  displayName?: string;
}

// --- Extraction pipeline ----------------------------------------------------

export interface QueueStats {
  queueDepth: number;
  inFlight: number;
  deadLetter: number;
  processedLast24h: number;
  avgLatencyMs: number;
  successRate: number; // 0–1
}

export interface ExtractionJob {
  id: string;
  tenantId: string;
  tenantName: string;
  documentName: string;
  status: JobStatus;
  attempts: number;
  maxAttempts: number;
  enqueuedAt: string; // ISO
  updatedAt: string; // ISO
  latencyMs: number | null;
  /** Present when status is `failed` or `dead_letter`. */
  error: string | null;
}

// --- Audit ------------------------------------------------------------------

export interface AuditEntry {
  id: string;
  timestamp: string; // ISO
  actor: string;
  actorEmail: string;
  action: string;
  targetType: string;
  targetId: string;
  tenantId: string | null;
  tenantName: string | null;
  ipAddress: string;
  result: AuditResult;
  detail?: string;
}

// --- Overview / system health ----------------------------------------------

export interface ServiceHealth {
  key: string;
  name: string;
  status: HealthStatus;
  latencyMs: number;
  detail: string;
}

export interface OverviewMetrics {
  tenantCount: number;
  activeTenants: number;
  docsProcessedMtd: number;
  extractionSuccessRate: number; // 0–1
  queueDepth: number;
  inFlight: number;
  deadLetter: number;
  llmCostMtdUsd: number;
  llmCostTrendPct: number | null; // vs prior comparable period
  /** Fleet-wide total of active tenant users across ALL business units. */
  activeUsersFleetWide: number;
  services: ServiceHealth[];
  /** Documents processed per day for the trailing 14 days. */
  throughput: { date: string; docs: number; failures: number }[];
  /** LLM spend per day (USD) for the trailing 14 days. */
  costTrend: { date: string; costUsd: number }[];
  /** Real plan codes present in the fleet; plan-less tenants appear as "none". */
  tenantsByPlan: { tier: PlanTier; count: number }[];
}

// --- Impersonation ----------------------------------------------------------

export interface ImpersonationTicket {
  tenantId: string;
  /** Short-lived read-only tenant token. */
  token: string;
  expiresAt: string; // ISO
  /** Revocation key decoded from the token's `jti` claim (null if unreadable). */
  jti: string | null;
}

export type ImpersonationSessionStatus = 'active' | 'expired' | 'revoked';

export interface ImpersonationSession {
  jti: string;
  tenantId: string;
  tenantName: string | null;
  actorPlatformUserId: string;
  actorEmail: string | null;
  reason: string;
  issuedAtUtc: string; // ISO
  expiresAtUtc: string; // ISO
  revokedAtUtc: string | null;
  revokedBy: string | null;
  status: ImpersonationSessionStatus;
}

// --- Billing ----------------------------------------------------------------

export interface MeterReading {
  meterKey: string;
  quantity: number;
  unit: string;
  sourceNote: string;
}

export interface TenantUsageReadout {
  tenantId: string;
  businessUnitId: string | null;
  period: string; // YYYY-MM
  periodStartUtc: string;
  periodEndUtc: string;
  meters: MeterReading[];
}

export interface RateCardLine {
  id: string;
  meterKey: string;
  includedQuantity: number;
  unitPrice: number;
  unit: string;
  tierNote: string | null;
}

export interface RateCard {
  id: string;
  code: string;
  currency: string;
  effectiveFromUtc: string;
  effectiveToUtc: string | null;
  isActive: boolean;
  createdOn: string;
  createdBy: string | null;
  version: number;
  lines: RateCardLine[];
}

export interface RateCardLineInput {
  meterKey: string;
  includedQuantity: number;
  unitPrice: number;
  unit: string;
  tierNote: string | null;
}

export interface CreateRateCardInput {
  code: string;
  currency: string;
  effectiveFromUtc: string;
  effectiveToUtc: string | null;
  isActive: boolean;
  lines: RateCardLineInput[];
}

export type UpdateRateCardInput = Omit<CreateRateCardInput, 'code'>;

export type BillingStatementStatus = 'Draft' | 'Final';

export interface BillingStatementLine {
  meterKey: string;
  description: string;
  meteredQuantity: number;
  includedQuantity: number;
  billableQuantity: number;
  unitPrice: number;
  amount: number;
  sourceNote: string | null;
}

export interface BillingStatement {
  id: string;
  tenantId: string;
  periodStartUtc: string;
  periodEndUtc: string;
  rateCardId: string;
  currency: string;
  status: string;
  totalAmount: number;
  computedAtUtc: string;
  finalizedAtUtc: string | null;
  finalizedBy: string | null;
  lines: BillingStatementLine[];
}

/**
 * Cost vs revenue for a tenant-period. `aiCostTotal` and `grossMargin` are
 * null (never fabricated) whenever any settled AI request in the period is
 * unpriceable — the UI must render an honest "not priced" state.
 */
export interface TenantCostReport {
  tenantId: string;
  businessUnitId: string | null;
  period: string;
  statementTotal: number | null;
  statementStatus: string | null;
  statementCurrency: string | null;
  settledAiRequestCount: number;
  unpricedAiRequestCount: number;
  pricedAiCostSubtotal: number;
  aiCostTotal: number | null;
  grossMargin: number | null;
  note: string;
}

// --- Command inputs ---------------------------------------------------------

export interface ProvisionTenantInput {
  name: string;
  slug: string;
  /** Persisted plan id to assign at provisioning time (optional). */
  planId: string | null;
  /**
   * The tenant's founding Super Administrator. Required: a tenant without one is a shell
   * nobody can log into, which is the state every portal-provisioned tenant used to land in.
   */
  adminEmail: string;
  adminFirstName: string;
  adminLastName: string;
  /** Omit to have the server generate one and return it exactly once. */
  adminPassword?: string | null;
}

export interface FoundingAdmin {
  userId: number;
  email: string;
  roleName: string;
  /**
   * Present ONLY when the server generated the password, and only in the provisioning
   * response. It is stored as a BCrypt hash and can never be retrieved again — if it is lost
   * before handover, the credential must be reset rather than looked up.
   */
  generatedPassword: string | null;
}

export interface ProvisionTenantResult {
  tenant: Tenant;
  foundingAdmin: FoundingAdmin;
}
