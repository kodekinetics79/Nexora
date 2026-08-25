/** Stable deep link used by the tenant list's direct offboarding action. */
export const tenantOffboardingPath = (tenantId: string): string =>
  `/platform/tenants/${encodeURIComponent(tenantId)}?tab=lifecycle`;

export const TENANT_DETAIL_TABS = [
  { key: 'overview', label: 'Overview' },
  { key: 'activation', label: 'Activation' },
  // Keep the legacy key for bookmarks, but name and place this after the operator's task.
  { key: 'lifecycle', label: 'Offboarding & deletion' },
  { key: 'profile-access', label: 'Profile & access' },
  { key: 'users', label: 'Users' },
  { key: 'provisioning', label: 'Provisioning' },
  { key: 'commercial', label: 'Commercial' },
  // The URL key remains `entitlements` so existing ticket links keep working.
  { key: 'entitlements', label: 'Modules' },
  { key: 'support', label: 'Support' },
  { key: 'audit', label: 'Audit' },
  { key: 'ai-governance', label: 'AI governance' },
  { key: 'data-storage', label: 'Data & storage' },
] as const;

export type TenantDetailTabKey = (typeof TENANT_DETAIL_TABS)[number]['key'];
