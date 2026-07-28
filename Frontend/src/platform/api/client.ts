import platformHttp from './platformHttp';
import type {
  AuditEntry,
  ExtractionJob,
  ImpersonationTicket,
  JobStatus,
  OverviewMetrics,
  Plan,
  PlanTier,
  ProvisionTenantInput,
  QueueStats,
  Tenant,
} from '../types';

export interface AuditQuery {
  action?: string;
  tenantId?: string;
  search?: string;
}

export interface JobQuery {
  tenantId?: string;
  status?: JobStatus | 'all';
}

export interface PlatformApi {
  getOverview(): Promise<OverviewMetrics>;
  listTenants(): Promise<Tenant[]>;
  getTenant(id: string): Promise<Tenant>;
  provisionTenant(input: ProvisionTenantInput): Promise<Tenant>;
  suspendTenant(id: string, reason: string): Promise<Tenant>;
  resumeTenant(id: string, reason: string): Promise<Tenant>;
  impersonateTenant(id: string, reason: string): Promise<ImpersonationTicket>;
  getQueueStats(): Promise<QueueStats>;
  listJobs(query?: JobQuery): Promise<ExtractionJob[]>;
  listPlans(): Promise<Plan[]>;
  listAudit(query?: AuditQuery): Promise<AuditEntry[]>;
}

type BackendTenant = {
  id: string | number;
  name: string;
  slug: string;
  status?: string | null;
  planCode?: string | null;
  createdOn?: string | null;
};

type BackendImpersonation = {
  tenantId: string | number;
  token: string;
  expiresAtUtc: string;
};

const normalizePlanTier = (planCode?: string | null): PlanTier => {
  const tier = (planCode ?? '').toLowerCase();
  return tier === 'free' || tier === 'pro' || tier === 'enterprise' ? tier : 'unassigned';
};

const normalizeTenantStatus = (status?: string | null): Tenant['status'] => {
  const normalized = (status ?? 'provisioning').toLowerCase();
  if (normalized === 'active' || normalized === 'trial' || normalized === 'suspended' || normalized === 'archived') return normalized;
  return 'provisioning';
};

const normalizeTenant = (tenant: BackendTenant): Tenant => ({
  id: String(tenant.id),
  name: tenant.name,
  slug: tenant.slug,
  planTier: normalizePlanTier(tenant.planCode),
  status: normalizeTenantStatus(tenant.status),
  region: 'Not recorded',
  primaryContactEmail: '',
  createdAt: tenant.createdOn ?? '',
  usage: {
    docsProcessedMtd: 0,
    docQuota: null,
    seatsUsed: 0,
    seatQuota: null,
    llmCostMtdUsd: 0,
    storageUsedGb: 0,
  },
  extractionSuccessRate: 0,
  pipelineHealth: 'degraded',
});

const httpPlatformApi: PlatformApi = {
  getOverview: async () => (await platformHttp.get<OverviewMetrics>('/api/platform/overview')).data,
  listTenants: async () =>
    (await platformHttp.get<BackendTenant[]>('/api/platform/tenants')).data.map(normalizeTenant),
  getTenant: async (id) =>
    normalizeTenant((await platformHttp.get<BackendTenant>(`/api/platform/tenants/${id}`)).data),
  provisionTenant: async (input) => {
    const plans = (await platformHttp.get<Plan[]>('/api/platform/plans')).data;
    const planId = plans.find((plan) => plan.tier === input.planTier)?.id;
    if (!planId) throw new Error(`No persisted ${input.planTier} plan is available.`);
    return normalizeTenant((await platformHttp.post<BackendTenant>('/api/platform/tenants', {
      name: input.name,
      slug: input.slug,
      planId: Number(planId),
    })).data);
  },
  suspendTenant: async (id, reason) =>
    normalizeTenant((await platformHttp.post<BackendTenant>(`/api/platform/tenants/${id}/suspend`, { reason })).data),
  resumeTenant: async (id, reason) =>
    normalizeTenant((await platformHttp.post<BackendTenant>(`/api/platform/tenants/${id}/resume`, { reason })).data),
  impersonateTenant: async (id, reason) => {
    const response = (await platformHttp.post<BackendImpersonation>(`/api/platform/tenants/${id}/impersonate`, { reason })).data;
    return { tenantId: String(response.tenantId), token: response.token, expiresAt: response.expiresAtUtc };
  },
  getQueueStats: async () => (await platformHttp.get<QueueStats>('/api/platform/pipeline/queue')).data,
  listJobs: async (query) =>
    (await platformHttp.get<ExtractionJob[]>('/api/platform/pipeline/jobs', { params: query })).data,
  listPlans: async () => (await platformHttp.get<Plan[]>('/api/platform/plans')).data,
  listAudit: async (query) =>
    (await platformHttp.get<AuditEntry[]>('/api/platform/audit', { params: query })).data,
};

export const platformApi: PlatformApi = httpPlatformApi;
