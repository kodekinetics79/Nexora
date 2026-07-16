// ---------------------------------------------------------------------------
// Platform API client (typed)
//
// Every `/api/platform/*` call in the owner console goes through the
// `PlatformApi` interface below. Today it is fulfilled by an in-memory MOCK
// adapter so the UI can be built and demoed before the ADR-0005 backend ships.
//
// ============================ MOCK → REAL SWAP =============================
// When the real endpoints are live, flip `USE_MOCK` to `false` (or drive it
// from an env flag) and fill in `httpPlatformApi` below. Each method already
// documents its target endpoint. No page or hook changes are required — they
// import `platformApi` and never touch axios directly.
// ==========================================================================

import axiosInstance from '../../api/axiosInstance';
import type {
  AuditEntry,
  ExtractionJob,
  FeatureFlag,
  ImpersonationTicket,
  JobStatus,
  OverviewMetrics,
  Plan,
  PlanTier,
  ProvisionTenantInput,
  QueueStats,
  Tenant,
  TenantDetail,
  UsageMeter,
} from '../types';
import {
  buildAudit,
  buildJobs,
  buildUsers,
  FEATURE_FLAGS,
  PLANS,
  TENANTS,
} from './mockData';

// TODO(platform-api): drive this from an env flag once the backend is live, e.g.
//   const USE_MOCK = import.meta.env.VITE_PLATFORM_API_MOCK !== 'false';
const USE_MOCK = true;

export interface AuditQuery {
  action?: string;
  tenantId?: string;
  result?: 'success' | 'failure' | 'all';
  search?: string;
}

export interface JobQuery {
  tenantId?: string;
  status?: JobStatus | 'all';
}

export interface PlatformApi {
  getOverview(): Promise<OverviewMetrics>;

  listTenants(): Promise<Tenant[]>;
  getTenant(id: string): Promise<TenantDetail>;
  provisionTenant(input: ProvisionTenantInput): Promise<Tenant>;
  suspendTenant(id: string): Promise<Tenant>;
  resumeTenant(id: string): Promise<Tenant>;
  impersonateTenant(id: string): Promise<ImpersonationTicket>;
  setTenantFlag(tenantId: string, flagKey: string, enabled: boolean): Promise<Record<string, boolean>>;

  getQueueStats(): Promise<QueueStats>;
  listJobs(query?: JobQuery): Promise<ExtractionJob[]>;
  requeueJob(jobId: string): Promise<ExtractionJob>;
  discardJob(jobId: string): Promise<void>;

  listPlans(): Promise<Plan[]>;
  listFeatureFlags(): Promise<FeatureFlag[]>;

  listAudit(query?: AuditQuery): Promise<AuditEntry[]>;
}

// ---------------------------------------------------------------------------
// MOCK adapter
// ---------------------------------------------------------------------------

const delay = <T>(value: T, ms = 320): Promise<T> =>
  new Promise((resolve) => setTimeout(() => resolve(value), ms));

// Mutable in-memory stores so provisioning / suspend / flag toggles feel live
// across the session. Cloned from the seed data on module load.
const tenantStore: Tenant[] = TENANTS.map((t) => ({ ...t, usage: { ...t.usage } }));
let jobStore: ExtractionJob[] = buildJobs(140);
const auditStore: AuditEntry[] = buildAudit(120);
// Per-tenant flag overrides on top of plan entitlements.
const flagOverrides: Record<string, Record<string, boolean>> = {};

const planFor = (tier: PlanTier): Plan =>
  PLANS.find((p) => p.tier === tier) ?? PLANS[0];

const effectiveFlags = (tenant: Tenant): Record<string, boolean> => {
  const entitlements = new Set(planFor(tenant.planTier).entitlements);
  const base: Record<string, boolean> = {};
  for (const flag of FEATURE_FLAGS) base[flag.key] = entitlements.has(flag.key);
  return { ...base, ...(flagOverrides[tenant.id] ?? {}) };
};

const buildUsageMeters = (tenant: Tenant): UsageMeter[] => {
  const period = new Date().toISOString().slice(0, 7);
  return [
    { metric: 'docs', label: 'Documents Processed', used: tenant.usage.docsProcessedMtd, limit: tenant.usage.docQuota, unit: 'docs', period },
    { metric: 'seats', label: 'Seats', used: tenant.usage.seatsUsed, limit: tenant.usage.seatQuota, unit: 'seats', period },
    { metric: 'storage', label: 'Storage', used: Math.round(tenant.usage.storageUsedGb), limit: tenant.planTier === 'enterprise' ? null : 250, unit: 'GB', period },
    { metric: 'llm_cost', label: 'LLM Spend', used: Math.round(tenant.usage.llmCostMtdUsd), limit: null, unit: 'USD', period },
  ];
};

const perTenantQueue = (tenant: Tenant): QueueStats => {
  const jobs = jobStore.filter((j) => j.tenantId === tenant.id);
  return {
    queueDepth: jobs.filter((j) => j.status === 'queued').length,
    inFlight: jobs.filter((j) => j.status === 'in_flight').length,
    deadLetter: jobs.filter((j) => j.status === 'dead_letter').length,
    processedLast24h: jobs.filter((j) => j.status === 'succeeded').length,
    avgLatencyMs: Math.round(
      jobs.reduce((s, j) => s + (j.latencyMs ?? 0), 0) / Math.max(jobs.length, 1),
    ),
    successRate: tenant.extractionSuccessRate,
  };
};

const trailingSeries = (days: number) =>
  Array.from({ length: days }, (_, i) => {
    const d = new Date();
    d.setDate(d.getDate() - (days - 1 - i));
    return d.toISOString().slice(0, 10);
  });

const mockPlatformApi: PlatformApi = {
  async getOverview() {
    const active = tenantStore.filter((t) => t.status === 'active' || t.status === 'trial');
    const docsProcessedMtd = tenantStore.reduce((s, t) => s + t.usage.docsProcessedMtd, 0);
    const llmCostMtdUsd = tenantStore.reduce((s, t) => s + t.usage.llmCostMtdUsd, 0);
    const totalQueue = jobStore.filter((j) => j.status === 'queued').length;
    const inFlight = jobStore.filter((j) => j.status === 'in_flight').length;
    const deadLetter = jobStore.filter((j) => j.status === 'dead_letter').length;
    const succeeded = jobStore.filter((j) => j.status === 'succeeded').length;
    const failed = jobStore.filter((j) => j.status === 'failed' || j.status === 'dead_letter').length;

    const dates = trailingSeries(14);
    const throughput = dates.map((date, i) => ({
      date,
      docs: 2600 + Math.round(Math.sin(i / 2) * 700) + i * 40,
      failures: 40 + ((i * 7) % 55),
    }));
    const costTrend = dates.map((date, i) => ({
      date,
      costUsd: 520 + Math.round(Math.cos(i / 3) * 120) + i * 12,
    }));

    const tenantsByPlan: OverviewMetrics['tenantsByPlan'] = (['free', 'pro', 'enterprise'] as PlanTier[]).map((tier) => ({
      tier,
      count: tenantStore.filter((t) => t.planTier === tier).length,
    }));

    return delay({
      tenantCount: tenantStore.length,
      activeTenants: active.length,
      docsProcessedMtd,
      extractionSuccessRate: succeeded / Math.max(succeeded + failed, 1),
      queueDepth: totalQueue,
      inFlight,
      deadLetter,
      llmCostMtdUsd,
      llmCostTrendPct: 12.4,
      seatsInUse: tenantStore.reduce((s, t) => s + t.usage.seatsUsed, 0),
      services: [
        { key: 'api', name: 'Platform API', status: 'healthy', latencyMs: 42, detail: 'p95 42ms · 0 errors/min' },
        { key: 'extractor', name: 'Extraction Workers', status: deadLetter > 20 ? 'degraded' : 'healthy', latencyMs: 1820, detail: `${inFlight} in-flight · ${deadLetter} dead-letter` },
        { key: 'llm', name: 'LLM Gateway', status: 'healthy', latencyMs: 940, detail: 'Primary pool nominal' },
        { key: 'ocr', name: 'OCR Service', status: 'degraded', latencyMs: 3100, detail: 'Elevated latency on eu-west-1' },
        { key: 'db', name: 'Primary Database', status: 'healthy', latencyMs: 6, detail: 'Replication lag 0.2s' },
        { key: 'queue', name: 'Message Queue', status: 'healthy', latencyMs: 3, detail: `${totalQueue} queued` },
      ],
      throughput,
      costTrend,
      tenantsByPlan,
    } satisfies OverviewMetrics);
  },

  async listTenants() {
    return delay(tenantStore.map((t) => ({ ...t, usage: { ...t.usage } })));
  },

  async getTenant(id) {
    const tenant = tenantStore.find((t) => t.id === id);
    if (!tenant) throw new Error(`Tenant ${id} not found`);
    const detail: TenantDetail = {
      ...tenant,
      usage: { ...tenant.usage },
      users: buildUsers(tenant.id, Math.max(tenant.usage.seatsUsed, 1)),
      flags: effectiveFlags(tenant),
      usageMeters: buildUsageMeters(tenant),
      recentAudit: auditStore.filter((a) => a.tenantId === tenant.id).slice(0, 12),
      queue: perTenantQueue(tenant),
    };
    return delay(detail);
  },

  async provisionTenant(input) {
    const plan = planFor(input.planTier);
    const tenant: Tenant = {
      id: `tnt_${input.slug}`,
      name: input.name,
      slug: input.slug,
      planTier: input.planTier,
      status: 'provisioning',
      region: input.region,
      primaryContactEmail: input.primaryContactEmail,
      createdAt: new Date().toISOString(),
      extractionSuccessRate: 0,
      pipelineHealth: 'healthy',
      usage: {
        docsProcessedMtd: 0,
        docQuota: plan.monthlyDocQuota,
        seatsUsed: 0,
        seatQuota: plan.seatQuota,
        llmCostMtdUsd: 0,
        storageUsedGb: 0,
      },
    };
    tenantStore.unshift(tenant);
    return delay(tenant, 600);
  },

  async suspendTenant(id) {
    const tenant = tenantStore.find((t) => t.id === id);
    if (!tenant) throw new Error(`Tenant ${id} not found`);
    tenant.status = 'suspended';
    tenant.pipelineHealth = 'down';
    return delay({ ...tenant, usage: { ...tenant.usage } });
  },

  async resumeTenant(id) {
    const tenant = tenantStore.find((t) => t.id === id);
    if (!tenant) throw new Error(`Tenant ${id} not found`);
    tenant.status = 'active';
    tenant.pipelineHealth = 'healthy';
    return delay({ ...tenant, usage: { ...tenant.usage } });
  },

  async impersonateTenant(id) {
    const tenant = tenantStore.find((t) => t.id === id);
    if (!tenant) throw new Error(`Tenant ${id} not found`);
    const expires = new Date();
    expires.setMinutes(expires.getMinutes() + 15);
    return delay({
      tenantId: id,
      token: `imp_${btoa(id).replace(/=/g, '')}_${Date.now().toString(36)}`,
      expiresAt: expires.toISOString(),
    });
  },

  async setTenantFlag(tenantId, flagKey, enabled) {
    const tenant = tenantStore.find((t) => t.id === tenantId);
    if (!tenant) throw new Error(`Tenant ${tenantId} not found`);
    flagOverrides[tenantId] = { ...(flagOverrides[tenantId] ?? {}), [flagKey]: enabled };
    return delay(effectiveFlags(tenant));
  },

  async getQueueStats() {
    const totals: QueueStats = {
      queueDepth: jobStore.filter((j) => j.status === 'queued').length,
      inFlight: jobStore.filter((j) => j.status === 'in_flight').length,
      deadLetter: jobStore.filter((j) => j.status === 'dead_letter').length,
      processedLast24h: jobStore.filter((j) => j.status === 'succeeded').length,
      avgLatencyMs: Math.round(
        jobStore.reduce((s, j) => s + (j.latencyMs ?? 0), 0) / Math.max(jobStore.length, 1),
      ),
      successRate:
        jobStore.filter((j) => j.status === 'succeeded').length /
        Math.max(jobStore.filter((j) => j.status !== 'queued' && j.status !== 'in_flight').length, 1),
    };
    return delay(totals);
  },

  async listJobs(query) {
    let jobs = jobStore;
    if (query?.tenantId) jobs = jobs.filter((j) => j.tenantId === query.tenantId);
    if (query?.status && query.status !== 'all') jobs = jobs.filter((j) => j.status === query.status);
    return delay(jobs.map((j) => ({ ...j })));
  },

  async requeueJob(jobId) {
    const job = jobStore.find((j) => j.id === jobId);
    if (!job) throw new Error(`Job ${jobId} not found`);
    job.status = 'queued';
    job.attempts = 0;
    job.error = null;
    job.updatedAt = new Date().toISOString();
    return delay({ ...job });
  },

  async discardJob(jobId) {
    jobStore = jobStore.filter((j) => j.id !== jobId);
    return delay(undefined);
  },

  async listPlans() {
    return delay(PLANS.map((p) => ({ ...p })));
  },

  async listFeatureFlags() {
    return delay(FEATURE_FLAGS.map((f) => ({ ...f })));
  },

  async listAudit(query) {
    let rows = auditStore;
    if (query?.action) rows = rows.filter((a) => a.action === query.action);
    if (query?.tenantId) rows = rows.filter((a) => a.tenantId === query.tenantId);
    if (query?.result && query.result !== 'all') rows = rows.filter((a) => a.result === query.result);
    if (query?.search) {
      const q = query.search.toLowerCase();
      rows = rows.filter(
        (a) =>
          a.actor.toLowerCase().includes(q) ||
          a.actorEmail.toLowerCase().includes(q) ||
          a.action.toLowerCase().includes(q) ||
          (a.tenantName ?? '').toLowerCase().includes(q) ||
          a.targetId.toLowerCase().includes(q),
      );
    }
    return delay(rows.map((a) => ({ ...a })));
  },
};

// ---------------------------------------------------------------------------
// REAL (HTTP) adapter — endpoints per ADR-0005. Reuses the app axios instance
// (auth-token interceptor already wired). Fill in as the backend lands.
// ---------------------------------------------------------------------------

const httpPlatformApi: PlatformApi = {
  // GET /api/platform/overview
  getOverview: async () => (await axiosInstance.get<OverviewMetrics>('/api/platform/overview')).data,

  // GET /api/platform/tenants
  listTenants: async () => (await axiosInstance.get<Tenant[]>('/api/platform/tenants')).data,
  // GET /api/platform/tenants/:id
  getTenant: async (id) => (await axiosInstance.get<TenantDetail>(`/api/platform/tenants/${id}`)).data,
  // POST /api/platform/tenants
  provisionTenant: async (input) => (await axiosInstance.post<Tenant>('/api/platform/tenants', input)).data,
  // POST /api/platform/tenants/:id/suspend
  suspendTenant: async (id) => (await axiosInstance.post<Tenant>(`/api/platform/tenants/${id}/suspend`)).data,
  // POST /api/platform/tenants/:id/resume
  resumeTenant: async (id) => (await axiosInstance.post<Tenant>(`/api/platform/tenants/${id}/resume`)).data,
  // POST /api/platform/tenants/:id/impersonate
  impersonateTenant: async (id) => (await axiosInstance.post<ImpersonationTicket>(`/api/platform/tenants/${id}/impersonate`)).data,
  // PUT /api/platform/tenants/:id/flags/:flagKey
  setTenantFlag: async (tenantId, flagKey, enabled) =>
    (await axiosInstance.put<Record<string, boolean>>(`/api/platform/tenants/${tenantId}/flags/${flagKey}`, { enabled })).data,

  // GET /api/platform/pipeline/queue
  getQueueStats: async () => (await axiosInstance.get<QueueStats>('/api/platform/pipeline/queue')).data,
  // GET /api/platform/pipeline/jobs
  listJobs: async (query) => (await axiosInstance.get<ExtractionJob[]>('/api/platform/pipeline/jobs', { params: query })).data,
  // POST /api/platform/pipeline/jobs/:id/requeue
  requeueJob: async (jobId) => (await axiosInstance.post<ExtractionJob>(`/api/platform/pipeline/jobs/${jobId}/requeue`)).data,
  // DELETE /api/platform/pipeline/jobs/:id
  discardJob: async (jobId) => {
    await axiosInstance.delete(`/api/platform/pipeline/jobs/${jobId}`);
  },

  // GET /api/platform/plans
  listPlans: async () => (await axiosInstance.get<Plan[]>('/api/platform/plans')).data,
  // GET /api/platform/feature-flags
  listFeatureFlags: async () => (await axiosInstance.get<FeatureFlag[]>('/api/platform/feature-flags')).data,

  // GET /api/platform/audit
  listAudit: async (query) => (await axiosInstance.get<AuditEntry[]>('/api/platform/audit', { params: query })).data,
};

// The single client the whole console imports. Swap the assignment (or the
// `USE_MOCK` flag) to go live — nothing else changes.
export const platformApi: PlatformApi = USE_MOCK ? mockPlatformApi : httpPlatformApi;
