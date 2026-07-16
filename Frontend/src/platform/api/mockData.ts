// ---------------------------------------------------------------------------
// Mock dataset backing the platform API adapter.
//
// This module is the ONLY place that fabricates data. When the real
// `/api/platform/*` endpoints exist, `client.ts` stops importing from here and
// calls axios instead — this file can then be deleted wholesale.
// ---------------------------------------------------------------------------

import type {
  AuditEntry,
  ExtractionJob,
  FeatureFlag,
  JobStatus,
  Plan,
  PlatformUser,
  Tenant,
  TenantUsage,
} from '../types';

const iso = (daysAgo: number, hour = 12): string => {
  const d = new Date();
  d.setDate(d.getDate() - daysAgo);
  d.setHours(hour, (daysAgo * 7) % 60, 0, 0);
  return d.toISOString();
};

export const FEATURE_FLAGS: FeatureFlag[] = [
  { key: 'layout_aware_extraction', label: 'Layout-Aware Extraction', description: 'Geometry-aware document parsing for tables and multi-column layouts.', category: 'entitlement' },
  { key: 'ocr_all_formats', label: 'OCR — All Attachment Formats', description: 'Optical character recognition across every supported attachment type.', category: 'entitlement' },
  { key: 'auto_supplier_match', label: 'Auto Supplier Matching', description: 'AI-assisted matching of line items to catalog suppliers.', category: 'entitlement' },
  { key: 'bulk_rfq_import', label: 'Bulk RFQ Import', description: 'Folder / batch upload of RFQs with background processing.', category: 'entitlement' },
  { key: 'priority_queue', label: 'Priority Extraction Queue', description: 'Elevated dispatcher weight for time-sensitive tenants.', category: 'operational' },
  { key: 'sandbox_models', label: 'Sandbox LLM Routing', description: 'Route this tenant to the experimental model pool.', category: 'operational' },
  { key: 'audit_export', label: 'Audit Log Export', description: 'Self-service export of tenant audit history.', category: 'entitlement' },
];

export const PLANS: Plan[] = [
  {
    id: 'plan_free',
    name: 'Free',
    tier: 'free',
    weight: 1,
    concurrencyCap: 1,
    monthlyDocQuota: 100,
    seatQuota: 3,
    priceMonthlyUsd: 0,
    entitlements: ['layout_aware_extraction'],
  },
  {
    id: 'plan_pro',
    name: 'Pro',
    tier: 'pro',
    weight: 5,
    concurrencyCap: 8,
    monthlyDocQuota: 5000,
    seatQuota: 25,
    priceMonthlyUsd: 499,
    entitlements: ['layout_aware_extraction', 'ocr_all_formats', 'auto_supplier_match', 'bulk_rfq_import', 'audit_export'],
  },
  {
    id: 'plan_enterprise',
    name: 'Enterprise',
    tier: 'enterprise',
    weight: 20,
    concurrencyCap: 40,
    monthlyDocQuota: null,
    seatQuota: null,
    priceMonthlyUsd: 2500,
    entitlements: ['layout_aware_extraction', 'ocr_all_formats', 'auto_supplier_match', 'bulk_rfq_import', 'audit_export', 'priority_queue'],
  },
];

const usage = (u: Partial<TenantUsage>): TenantUsage => ({
  docsProcessedMtd: 0,
  docQuota: null,
  seatsUsed: 0,
  seatQuota: null,
  llmCostMtdUsd: 0,
  storageUsedGb: 0,
  ...u,
});

export const TENANTS: Tenant[] = [
  {
    id: 'tnt_acme', name: 'Acme Manufacturing', slug: 'acme', planTier: 'enterprise', status: 'active',
    region: 'us-east-1', primaryContactEmail: 'ops@acme.example', createdAt: iso(420),
    extractionSuccessRate: 0.982, pipelineHealth: 'healthy',
    usage: usage({ docsProcessedMtd: 41280, docQuota: null, seatsUsed: 63, seatQuota: null, llmCostMtdUsd: 8420.55, storageUsedGb: 214.3 }),
  },
  {
    id: 'tnt_northwind', name: 'Northwind Traders', slug: 'northwind', planTier: 'pro', status: 'active',
    region: 'us-west-2', primaryContactEmail: 'procurement@northwind.example', createdAt: iso(295),
    extractionSuccessRate: 0.947, pipelineHealth: 'healthy',
    usage: usage({ docsProcessedMtd: 3820, docQuota: 5000, seatsUsed: 18, seatQuota: 25, llmCostMtdUsd: 612.10, storageUsedGb: 41.8 }),
  },
  {
    id: 'tnt_globex', name: 'Globex Logistics', slug: 'globex', planTier: 'pro', status: 'active',
    region: 'eu-west-1', primaryContactEmail: 'it@globex.example', createdAt: iso(210),
    extractionSuccessRate: 0.889, pipelineHealth: 'degraded',
    usage: usage({ docsProcessedMtd: 4870, docQuota: 5000, seatsUsed: 24, seatQuota: 25, llmCostMtdUsd: 940.72, storageUsedGb: 58.1 }),
  },
  {
    id: 'tnt_initech', name: 'Initech Components', slug: 'initech', planTier: 'free', status: 'trial',
    region: 'us-east-1', primaryContactEmail: 'admin@initech.example', createdAt: iso(12),
    extractionSuccessRate: 0.915, pipelineHealth: 'healthy',
    usage: usage({ docsProcessedMtd: 74, docQuota: 100, seatsUsed: 2, seatQuota: 3, llmCostMtdUsd: 9.40, storageUsedGb: 0.8 }),
  },
  {
    id: 'tnt_umbrella', name: 'Umbrella Supply Co', slug: 'umbrella', planTier: 'enterprise', status: 'active',
    region: 'ap-southeast-1', primaryContactEmail: 'platform@umbrella.example', createdAt: iso(510),
    extractionSuccessRate: 0.968, pipelineHealth: 'healthy',
    usage: usage({ docsProcessedMtd: 28910, docQuota: null, seatsUsed: 47, seatQuota: null, llmCostMtdUsd: 6130.00, storageUsedGb: 176.9 }),
  },
  {
    id: 'tnt_soylent', name: 'Soylent Foods', slug: 'soylent', planTier: 'pro', status: 'suspended',
    region: 'us-east-1', primaryContactEmail: 'billing@soylent.example', createdAt: iso(160),
    extractionSuccessRate: 0.803, pipelineHealth: 'down',
    usage: usage({ docsProcessedMtd: 0, docQuota: 5000, seatsUsed: 11, seatQuota: 25, llmCostMtdUsd: 0, storageUsedGb: 22.4 }),
  },
  {
    id: 'tnt_hooli', name: 'Hooli Procurement', slug: 'hooli', planTier: 'free', status: 'active',
    region: 'us-west-2', primaryContactEmail: 'ops@hooli.example', createdAt: iso(48),
    extractionSuccessRate: 0.932, pipelineHealth: 'healthy',
    usage: usage({ docsProcessedMtd: 88, docQuota: 100, seatsUsed: 3, seatQuota: 3, llmCostMtdUsd: 11.20, storageUsedGb: 1.1 }),
  },
  {
    id: 'tnt_stark', name: 'Stark Industrial', slug: 'stark', planTier: 'enterprise', status: 'provisioning',
    region: 'us-east-1', primaryContactEmail: 'setup@stark.example', createdAt: iso(1),
    extractionSuccessRate: 0, pipelineHealth: 'healthy',
    usage: usage({ docsProcessedMtd: 0, docQuota: null, seatsUsed: 0, seatQuota: null, llmCostMtdUsd: 0, storageUsedGb: 0 }),
  },
];

const firstNames = ['Ava', 'Liam', 'Noah', 'Mia', 'Ethan', 'Sofia', 'Lucas', 'Isla', 'Mason', 'Aria'];
const lastNames = ['Reyes', 'Chen', 'Patel', 'Novak', 'Okafor', 'Silva', 'Haas', 'Bauer', 'Ito', 'Mbeki'];
const roles: PlatformUser['role'][] = ['owner', 'admin', 'member', 'member', 'viewer'];

export const buildUsers = (tenantId: string, count: number): PlatformUser[] =>
  Array.from({ length: Math.max(count, 1) }, (_, i) => {
    const fn = firstNames[(i * 3 + tenantId.length) % firstNames.length];
    const ln = lastNames[(i * 5 + tenantId.length) % lastNames.length];
    const slug = tenantId.replace('tnt_', '');
    return {
      id: `${tenantId}_u${i + 1}`,
      tenantId,
      name: `${fn} ${ln}`,
      email: `${fn.toLowerCase()}.${ln.toLowerCase()}@${slug}.example`,
      role: i === 0 ? 'owner' : roles[i % roles.length],
      status: i % 7 === 6 ? 'invited' : i % 11 === 10 ? 'disabled' : 'active',
      lastActiveAt: i % 7 === 6 ? null : iso((i % 9) + 1, 9 + (i % 8)),
    };
  });

const jobStatuses: JobStatus[] = ['queued', 'in_flight', 'succeeded', 'succeeded', 'succeeded', 'failed', 'dead_letter'];
const docTypes = ['RFQ', 'Quote', 'Invoice', 'PO', 'Spec Sheet', 'Packing List'];
const errors = [
  'LLM timeout after 30000ms',
  'Unsupported attachment MIME: application/x-cpio',
  'OCR confidence below threshold (0.42)',
  'Schema validation failed: missing line_items[]',
  'Rate limit exceeded on model pool',
];

export const buildJobs = (count: number): ExtractionJob[] => {
  const activeTenants = TENANTS.filter((t) => t.status === 'active' || t.status === 'trial');
  return Array.from({ length: count }, (_, i) => {
    const tenant = activeTenants[i % activeTenants.length];
    const status = jobStatuses[i % jobStatuses.length];
    const failed = status === 'failed' || status === 'dead_letter';
    const attempts = status === 'dead_letter' ? 5 : failed ? 2 : status === 'queued' ? 0 : 1;
    return {
      id: `job_${(10_000 + i).toString(36)}`,
      tenantId: tenant.id,
      tenantName: tenant.name,
      documentName: `${docTypes[i % docTypes.length]}-${2026}-${(4200 + i).toString()}.pdf`,
      status,
      attempts,
      maxAttempts: 5,
      enqueuedAt: iso(0, (i % 12) + 6),
      updatedAt: iso(0, (i % 12) + 7),
      latencyMs: status === 'queued' ? null : 900 + ((i * 137) % 8200),
      error: failed ? errors[i % errors.length] : null,
    };
  });
};

const auditActions = [
  { action: 'tenant.provisioned', targetType: 'tenant' },
  { action: 'tenant.suspended', targetType: 'tenant' },
  { action: 'tenant.resumed', targetType: 'tenant' },
  { action: 'plan.changed', targetType: 'tenant' },
  { action: 'feature_flag.toggled', targetType: 'flag' },
  { action: 'user.impersonated', targetType: 'tenant' },
  { action: 'deadletter.requeued', targetType: 'job' },
  { action: 'quota.overridden', targetType: 'tenant' },
];

const operators = [
  { name: 'Zack Khan', email: 'zack@kodekinetics.com' },
  { name: 'Platform Bot', email: 'automation@nexora.platform' },
  { name: 'Dana Whitfield', email: 'dana@nexora.platform' },
];

export const buildAudit = (count: number): AuditEntry[] =>
  Array.from({ length: count }, (_, i) => {
    const a = auditActions[i % auditActions.length];
    const op = operators[i % operators.length];
    const tenant = TENANTS[i % TENANTS.length];
    const attachTenant = a.targetType === 'tenant' || i % 2 === 0;
    return {
      id: `aud_${(50_000 + i).toString(36)}`,
      timestamp: iso(Math.floor(i / 3), 23 - (i % 20)),
      actor: op.name,
      actorEmail: op.email,
      action: a.action,
      targetType: a.targetType,
      targetId: a.targetType === 'tenant' ? tenant.id : `${a.targetType}_${(900 + i).toString(36)}`,
      tenantId: attachTenant ? tenant.id : null,
      tenantName: attachTenant ? tenant.name : null,
      ipAddress: `10.${(i % 200) + 1}.${(i * 7) % 255}.${(i * 13) % 255}`,
      result: i % 9 === 8 ? 'failure' : 'success',
      detail:
        a.action === 'plan.changed'
          ? 'pro → enterprise'
          : a.action === 'feature_flag.toggled'
          ? `${FEATURE_FLAGS[i % FEATURE_FLAGS.length].key} = ${i % 2 === 0}`
          : undefined,
    };
  });
