/** Stable deep link used by the tenant list's direct offboarding action. */
export const tenantOffboardingPath = (tenantId: string): string =>
  `/platform/tenants/${encodeURIComponent(tenantId)}?tab=lifecycle`;

/**
 * What is LEFT of the twelve tabs, and why each one is still here.
 *
 * The customer screen now owns the company record and the contract, so `overview` and
 * `profile-access` are gone from the strip — the page that replaced them is where an operator
 * lands, and leaving a second way to do the same edit is how two screens drift apart. Their URL
 * keys still resolve (see TenantDetailPage) because they are pasted into support tickets; they
 * redirect rather than render.
 *
 * Everything below owns a write that has NOT moved yet. Each disappears from this list when its
 * function lands on the customer page, and the whole screen goes when the list is empty. That is
 * the order: move the function, then delete the tab. Deleting the tab first would delete working
 * behaviour and call it progress.
 */
export const TENANT_DETAIL_TABS = [
  { key: 'activation', label: 'Activation' },
  { key: 'lifecycle', label: 'Offboarding & deletion' },
  { key: 'users', label: 'Users' },
  { key: 'provisioning', label: 'Provisioning' },
  // Plan and billing-mode changes only; the contract half moved to the customer page.
  { key: 'commercial', label: 'Plan & billing mode' },
  // The URL key remains `entitlements` so existing ticket links keep working.
  { key: 'entitlements', label: 'Modules' },
  { key: 'support', label: 'Support' },
  { key: 'audit', label: 'Audit' },
  { key: 'ai-governance', label: 'AI governance' },
  { key: 'data-storage', label: 'Data & storage' },
] as const;

/** Tab keys that no longer exist here, and where their work went. */
export const RETIRED_TENANT_TABS: Record<string, string> = {
  overview: 'The customer screen shows status, blockers and recent activity.',
  'profile-access': 'Company details are edited on the customer screen.',
};

export type TenantDetailTabKey = (typeof TENANT_DETAIL_TABS)[number]['key'];
