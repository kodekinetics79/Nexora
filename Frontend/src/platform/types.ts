// ---------------------------------------------------------------------------
// Nexora Platform Owner Console — domain types
//
// These types mirror the ADR-0005 platform API contract (`/api/platform/*`).
// They are the single source of truth shared by the typed API client, the
// mock adapter, and every page in `src/platform`. When the real backend lands,
// only `src/platform/api/*` changes — these types stay put.
// ---------------------------------------------------------------------------

export type PlanTier = 'free' | 'pro' | 'enterprise' | 'unassigned';

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

export type PlatformUserRole = 'owner' | 'admin' | 'member' | 'viewer';

export type UserStatus = 'active' | 'invited' | 'disabled';

export type AuditResult = 'success' | 'failure';

// --- Plans & entitlements ---------------------------------------------------

export interface Plan {
  id: string;
  name: string;
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
  /** Feature-flag keys granted by default on this plan. */
  entitlements: string[];
}

export interface FeatureFlag {
  key: string;
  label: string;
  description: string;
  /** Whether this flag is a paid entitlement or an operational toggle. */
  category: 'entitlement' | 'operational';
}

// --- Tenants ----------------------------------------------------------------

export interface TenantUsage {
  docsProcessedMtd: number;
  docQuota: number | null;
  seatsUsed: number;
  seatQuota: number | null;
  llmCostMtdUsd: number;
  storageUsedGb: number;
}

export interface Tenant {
  id: string;
  name: string;
  slug: string;
  planTier: PlanTier;
  status: TenantStatus;
  region: string;
  primaryContactEmail: string;
  createdAt: string; // ISO
  usage: TenantUsage;
  /** Rolling extraction success rate over the trailing 30 days (0–1). */
  extractionSuccessRate: number;
  pipelineHealth: HealthStatus;
}

export interface PlatformUser {
  id: string;
  tenantId: string;
  name: string;
  email: string;
  role: PlatformUserRole;
  status: UserStatus;
  lastActiveAt: string | null; // ISO
}

export interface UsageMeter {
  metric: string;
  label: string;
  used: number;
  limit: number | null; // null = unlimited
  unit: string;
  period: string; // e.g. "2026-07"
}

export interface TenantDetail extends Tenant {
  users: PlatformUser[];
  /** Effective flags for this tenant (plan entitlements ± per-tenant overrides). */
  flags: Record<string, boolean>;
  usageMeters: UsageMeter[];
  recentAudit: AuditEntry[];
  /** Per-tenant slice of the extraction pipeline. */
  queue: QueueStats;
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
  seatsInUse: number;
  services: ServiceHealth[];
  /** Documents processed per day for the trailing 14 days. */
  throughput: { date: string; docs: number; failures: number }[];
  /** LLM spend per day (USD) for the trailing 14 days. */
  costTrend: { date: string; costUsd: number }[];
  tenantsByPlan: { tier: PlanTier; count: number }[];
}

// --- Command results --------------------------------------------------------

export interface ProvisionTenantInput {
  name: string;
  slug: string;
  planTier: PlanTier;
}

export interface ImpersonationTicket {
  tenantId: string;
  /** Short-lived token the app would exchange to enter the tenant workspace. */
  token: string;
  expiresAt: string; // ISO
}
