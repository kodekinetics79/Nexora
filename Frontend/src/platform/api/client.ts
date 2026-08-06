import { jwtDecode } from 'jwt-decode';
import platformHttp from './platformHttp';
import type {
  AuditEntry,
  BillingStatement,
  CreatePlatformOperatorInput,
  CreateRateCardInput,
  ExtractionJob,
  ImpersonationSession,
  ImpersonationTicket,
  JobStatus,
  OverviewMetrics,
  Plan,
  PlatformOperator,
  PlatformOperatorRole,
  ProvisionTenantInput,
  ProvisionTenantResult,
  QueueStats,
  RateCard,
  Tenant,
  TenantCostReport,
  TenantUsageReadout,
  UpdateRateCardInput,
  UpsertPlanInput,
} from '../types';

export interface AuditQuery {
  action?: string;
  tenantId?: string;
  result?: 'success' | 'failure';
  search?: string;
}

export interface JobQuery {
  tenantId?: string;
  status?: JobStatus | 'all';
}

export interface StatementQuery {
  tenantId?: string;
  status?: string;
}

export interface PlatformApi {
  getOverview(): Promise<OverviewMetrics>;
  listTenants(): Promise<Tenant[]>;
  getTenant(id: string): Promise<Tenant>;
  provisionTenant(input: ProvisionTenantInput): Promise<ProvisionTenantResult>;
  suspendTenant(id: string, reason: string): Promise<Tenant>;
  resumeTenant(id: string, reason: string): Promise<Tenant>;
  archiveTenant(id: string, reason: string): Promise<Tenant>;
  restoreTenant(id: string, reason: string): Promise<Tenant>;
  changeTenantPlan(id: string, planId: string, reason?: string): Promise<Tenant>;
  impersonateTenant(id: string, reason: string): Promise<ImpersonationTicket>;
  listImpersonationSessions(): Promise<ImpersonationSession[]>;
  revokeImpersonation(jti: string): Promise<ImpersonationSession>;
  getQueueStats(): Promise<QueueStats>;
  listJobs(query?: JobQuery): Promise<ExtractionJob[]>;
  listPlans(): Promise<Plan[]>;
  createPlan(input: UpsertPlanInput): Promise<Plan>;
  updatePlan(id: string, input: UpsertPlanInput): Promise<Plan>;
  listPlatformUsers(): Promise<PlatformOperator[]>;
  createPlatformUser(input: CreatePlatformOperatorInput): Promise<PlatformOperator>;
  changePlatformUserRole(id: string, role: PlatformOperatorRole): Promise<PlatformOperator>;
  deactivatePlatformUser(id: string): Promise<PlatformOperator>;
  reactivatePlatformUser(id: string): Promise<PlatformOperator>;
  resetPlatformUserPassword(id: string, newPassword: string): Promise<PlatformOperator>;
  getBillingUsage(tenantId: string, period: string): Promise<TenantUsageReadout>;
  listRateCards(): Promise<RateCard[]>;
  createRateCard(input: CreateRateCardInput): Promise<RateCard>;
  updateRateCard(id: string, input: UpdateRateCardInput): Promise<RateCard>;
  listStatements(query?: StatementQuery): Promise<BillingStatement[]>;
  computeStatement(tenantId: string, period: string): Promise<BillingStatement>;
  finalizeStatement(id: string): Promise<BillingStatement>;
  getBillingCost(tenantId: string, period: string): Promise<TenantCostReport>;
  listAudit(query?: AuditQuery): Promise<AuditEntry[]>;
}

// --- backend wire shapes (ids arrive as numbers) ----------------------------

type BackendTenant = {
  id: string | number;
  name: string;
  slug: string;
  status?: string | null;
  planId?: string | number | null;
  planCode?: string | null;
  createdOn?: string | null;
  statusReason?: string | null;
};

type BackendImpersonation = {
  tenantId: string | number;
  token: string;
  expiresAtUtc: string;
};

type BackendPlan = Omit<Plan, 'id'> & { id: string | number };

type BackendOperator = Omit<PlatformOperator, 'id'> & { id: string | number };

type BackendSession = Omit<ImpersonationSession, 'tenantId' | 'actorPlatformUserId'> & {
  tenantId: string | number;
  actorPlatformUserId: string | number;
};

type BackendRateCard = Omit<RateCard, 'id' | 'lines'> & {
  id: string | number;
  lines: (Omit<RateCard['lines'][number], 'id'> & { id: string | number })[];
};

type BackendStatement = Omit<BillingStatement, 'id' | 'tenantId' | 'rateCardId'> & {
  id: string | number;
  tenantId: string | number;
  rateCardId: string | number;
};

type BackendUsage = Omit<TenantUsageReadout, 'tenantId' | 'businessUnitId'> & {
  tenantId: string | number;
  businessUnitId: string | number | null;
};

type BackendCost = Omit<TenantCostReport, 'tenantId' | 'businessUnitId'> & {
  tenantId: string | number;
  businessUnitId: string | number | null;
};

// --- normalizers ------------------------------------------------------------

const normalizeTenantStatus = (status?: string | null): Tenant['status'] => {
  const normalized = (status ?? 'provisioning').toLowerCase();
  if (normalized === 'active' || normalized === 'trial' || normalized === 'suspended' || normalized === 'archived') return normalized;
  return 'provisioning';
};

/**
 * Maps the backend TenantSummaryDto 1:1. Fields the backend does not send
 * (usage, region, contact, pipeline health) are NOT fabricated here — the UI
 * renders "—" for anything it does not have.
 */
const normalizeTenant = (tenant: BackendTenant): Tenant => ({
  id: String(tenant.id),
  name: tenant.name,
  slug: tenant.slug,
  planId: tenant.planId == null ? null : String(tenant.planId),
  planCode: tenant.planCode ? tenant.planCode.toLowerCase() : null,
  status: normalizeTenantStatus(tenant.status),
  statusReason: tenant.statusReason ?? null,
  createdAt: tenant.createdOn ?? null,
});

const normalizePlan = (plan: BackendPlan): Plan => ({ ...plan, id: String(plan.id) });

const normalizeOperator = (user: BackendOperator): PlatformOperator => ({
  ...user,
  id: String(user.id),
});

const normalizeSession = (session: BackendSession): ImpersonationSession => ({
  ...session,
  tenantId: String(session.tenantId),
  actorPlatformUserId: String(session.actorPlatformUserId),
});

const normalizeRateCard = (card: BackendRateCard): RateCard => ({
  ...card,
  id: String(card.id),
  lines: card.lines.map((line) => ({ ...line, id: String(line.id) })),
});

const normalizeStatement = (statement: BackendStatement): BillingStatement => ({
  ...statement,
  id: String(statement.id),
  tenantId: String(statement.tenantId),
  rateCardId: String(statement.rateCardId),
});

const readJti = (token: string): string | null => {
  try {
    const { jti } = jwtDecode<{ jti?: string }>(token);
    return typeof jti === 'string' && jti.length > 0 ? jti : null;
  } catch {
    return null;
  }
};

const httpPlatformApi: PlatformApi = {
  getOverview: async () => (await platformHttp.get<OverviewMetrics>('/api/platform/overview')).data,
  listTenants: async () =>
    (await platformHttp.get<BackendTenant[]>('/api/platform/tenants')).data.map(normalizeTenant),
  getTenant: async (id) =>
    normalizeTenant((await platformHttp.get<BackendTenant>(`/api/platform/tenants/${id}`)).data),
  provisionTenant: async (input) => {
    // The response is ProvisionTenantResponse, not a bare tenant: it carries the founding
    // admin and, when the server generated it, a one-time credential that exists in this
    // payload and nowhere else.
    const { data } = await platformHttp.post<{
      tenant: BackendTenant;
      foundingAdmin: { userId: number; email: string; roleName: string; generatedPassword: string | null };
    }>('/api/platform/tenants', {
      name: input.name,
      slug: input.slug,
      planId: input.planId == null ? null : Number(input.planId),
      adminEmail: input.adminEmail,
      adminFirstName: input.adminFirstName,
      adminLastName: input.adminLastName,
      adminPassword: input.adminPassword || null,
    });
    return { tenant: normalizeTenant(data.tenant), foundingAdmin: data.foundingAdmin };
  },
  suspendTenant: async (id, reason) =>
    normalizeTenant((await platformHttp.post<BackendTenant>(`/api/platform/tenants/${id}/suspend`, { reason })).data),
  resumeTenant: async (id, reason) =>
    normalizeTenant((await platformHttp.post<BackendTenant>(`/api/platform/tenants/${id}/resume`, { reason })).data),
  archiveTenant: async (id, reason) =>
    normalizeTenant((await platformHttp.post<BackendTenant>(`/api/platform/tenants/${id}/archive`, { reason })).data),
  restoreTenant: async (id, reason) =>
    normalizeTenant((await platformHttp.post<BackendTenant>(`/api/platform/tenants/${id}/restore`, { reason })).data),
  changeTenantPlan: async (id, planId, reason) =>
    normalizeTenant((await platformHttp.put<BackendTenant>(`/api/platform/tenants/${id}/plan`, {
      planId: Number(planId),
      reason,
    })).data),
  impersonateTenant: async (id, reason) => {
    const response = (await platformHttp.post<BackendImpersonation>(`/api/platform/tenants/${id}/impersonate`, { reason })).data;
    return {
      tenantId: String(response.tenantId),
      token: response.token,
      expiresAt: response.expiresAtUtc,
      jti: readJti(response.token),
    };
  },
  listImpersonationSessions: async () =>
    (await platformHttp.get<BackendSession[]>('/api/platform/impersonation/sessions')).data.map(normalizeSession),
  revokeImpersonation: async (jti) =>
    normalizeSession((await platformHttp.post<BackendSession>(`/api/platform/impersonation/${jti}/revoke`)).data),
  getQueueStats: async () => (await platformHttp.get<QueueStats>('/api/platform/pipeline/queue')).data,
  listJobs: async (query) =>
    (await platformHttp.get<ExtractionJob[]>('/api/platform/pipeline/jobs', { params: query })).data,
  listPlans: async () =>
    (await platformHttp.get<BackendPlan[]>('/api/platform/plans')).data.map(normalizePlan),
  createPlan: async (input) =>
    normalizePlan((await platformHttp.post<BackendPlan>('/api/platform/plans', input)).data),
  updatePlan: async (id, input) =>
    normalizePlan((await platformHttp.put<BackendPlan>(`/api/platform/plans/${id}`, input)).data),
  listPlatformUsers: async () =>
    (await platformHttp.get<BackendOperator[]>('/api/platform/users')).data.map(normalizeOperator),
  createPlatformUser: async (input) =>
    normalizeOperator((await platformHttp.post<BackendOperator>('/api/platform/users', input)).data),
  changePlatformUserRole: async (id, role) =>
    normalizeOperator((await platformHttp.put<BackendOperator>(`/api/platform/users/${id}/role`, { role })).data),
  deactivatePlatformUser: async (id) =>
    normalizeOperator((await platformHttp.post<BackendOperator>(`/api/platform/users/${id}/deactivate`)).data),
  reactivatePlatformUser: async (id) =>
    normalizeOperator((await platformHttp.post<BackendOperator>(`/api/platform/users/${id}/reactivate`)).data),
  resetPlatformUserPassword: async (id, newPassword) =>
    normalizeOperator((await platformHttp.post<BackendOperator>(`/api/platform/users/${id}/password`, { newPassword })).data),
  getBillingUsage: async (tenantId, period) => {
    const usage = (await platformHttp.get<BackendUsage>(`/api/platform/billing/usage/${tenantId}`, { params: { period } })).data;
    return {
      ...usage,
      tenantId: String(usage.tenantId),
      businessUnitId: usage.businessUnitId == null ? null : String(usage.businessUnitId),
    };
  },
  listRateCards: async () =>
    (await platformHttp.get<BackendRateCard[]>('/api/platform/billing/rate-cards')).data.map(normalizeRateCard),
  createRateCard: async (input) =>
    normalizeRateCard((await platformHttp.post<BackendRateCard>('/api/platform/billing/rate-cards', input)).data),
  updateRateCard: async (id, input) =>
    normalizeRateCard((await platformHttp.put<BackendRateCard>(`/api/platform/billing/rate-cards/${id}`, input)).data),
  listStatements: async (query) =>
    (await platformHttp.get<BackendStatement[]>('/api/platform/billing/statements', { params: query })).data.map(normalizeStatement),
  computeStatement: async (tenantId, period) =>
    normalizeStatement((await platformHttp.post<BackendStatement>('/api/platform/billing/statements/compute', {
      tenantId: Number(tenantId),
      period,
    })).data),
  finalizeStatement: async (id) =>
    normalizeStatement((await platformHttp.post<BackendStatement>(`/api/platform/billing/statements/${id}/finalize`)).data),
  getBillingCost: async (tenantId, period) => {
    const cost = (await platformHttp.get<BackendCost>(`/api/platform/billing/cost/${tenantId}`, { params: { period } })).data;
    return {
      ...cost,
      tenantId: String(cost.tenantId),
      businessUnitId: cost.businessUnitId == null ? null : String(cost.businessUnitId),
    };
  },
  listAudit: async (query) =>
    (await platformHttp.get<AuditEntry[]>('/api/platform/audit', { params: query })).data,
};

export const platformApi: PlatformApi = httpPlatformApi;
