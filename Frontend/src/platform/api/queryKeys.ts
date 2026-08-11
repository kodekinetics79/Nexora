// Centralised TanStack Query keys for the platform console, so invalidation
// after a mutation stays consistent across pages.

export const platformKeys = {
  all: ['platform'] as const,
  overview: () => [...platformKeys.all, 'overview'] as const,
  tenants: () => [...platformKeys.all, 'tenants'] as const,
  tenant: (id: string) => [...platformKeys.all, 'tenant', id] as const,
  tenantOperations: (id: string) => [...platformKeys.all, 'tenant', id, 'operations'] as const,
  tenantInvitations: (id: string) => [...platformKeys.all, 'tenant', id, 'admin-invitations'] as const,
  queue: () => [...platformKeys.all, 'queue'] as const,
  jobs: (filter?: unknown) => [...platformKeys.all, 'jobs', filter ?? null] as const,
  plans: () => [...platformKeys.all, 'plans'] as const,
  flags: () => [...platformKeys.all, 'flags'] as const,
  audit: (filter?: unknown) => [...platformKeys.all, 'audit', filter ?? null] as const,
  platformUsers: () => [...platformKeys.all, 'users'] as const,
  impersonationSessions: () => [...platformKeys.all, 'impersonation-sessions'] as const,
  billingUsage: (tenantId: string, period: string) =>
    [...platformKeys.all, 'billing', 'usage', tenantId, period] as const,
  billingCost: (tenantId: string, period: string) =>
    [...platformKeys.all, 'billing', 'cost', tenantId, period] as const,
  billingMeterCatalog: () => [...platformKeys.all, 'billing', 'meter-catalog'] as const,
  billingReadiness: (tenantId: string, period: string) =>
    [...platformKeys.all, 'billing', 'readiness', tenantId, period] as const,
  rateCards: () => [...platformKeys.all, 'billing', 'rate-cards'] as const,
  statements: (filter?: unknown) => [...platformKeys.all, 'billing', 'statements', filter ?? null] as const,
  statement: (id: string) => [...platformKeys.all, 'billing', 'statement', id] as const,
  revenueRisk: (filter?: unknown) => [...platformKeys.all, 'billing', 'revenue-risk', filter ?? null] as const,
  tenantBilling: (tenantId: string) => [...platformKeys.all, 'billing', 'tenant', tenantId] as const,
  subscriptionInvoices: (tenantId: string) =>
    [...platformKeys.all, 'billing', 'invoices', tenantId] as const,

  // Durable provisioning. The execution key is polled, so it is per-id rather than a list.
  provisioningExecution: (id: string) => [...platformKeys.all, 'provisioning', 'execution', id] as const,
  provisioningExecutions: (filter?: unknown) =>
    [...platformKeys.all, 'provisioning', 'executions', filter ?? null] as const,
  provisioningDrafts: () => [...platformKeys.all, 'provisioning', 'drafts'] as const,
  provisioningDraft: (id: string) => [...platformKeys.all, 'provisioning', 'draft', id] as const,
  slugCheck: (slug: string) => [...platformKeys.all, 'provisioning', 'slug-check', slug] as const,
  // Keyed per tenant because the whole provisioning tab — status, classification, recovery
  // safety and both blocker lists — is driven by this one payload.
  provisioningDiagnostics: (tenantId: string) =>
    [...platformKeys.all, 'provisioning', 'diagnostics', tenantId] as const,

  // Offboarding. Keyed per tenant because every button on the lifecycle tab is driven by
  // the server's `can*` flags in this one payload.
  offboarding: (tenantId: string) => [...platformKeys.all, 'offboarding', tenantId] as const,
  offboardingPending: () => [...platformKeys.all, 'offboarding', 'pending'] as const,
  purgePreview: (tenantId: string) => [...platformKeys.all, 'offboarding', tenantId, 'purge-preview'] as const,
  legalHolds: (tenantId: string) => [...platformKeys.all, 'legal-holds', tenantId] as const,
  tenantAiPolicy: (tenantId: string) => [...platformKeys.all, 'ai-policy', tenantId] as const,
  tenantAiProviders: (tenantId: string) => [...platformKeys.all, 'ai-providers', tenantId] as const,
  tenantDataAssets: (tenantId: string) => [...platformKeys.all, 'data-assets', tenantId] as const,
  tenantActivationDataDecision: (tenantId: string) =>
    [...platformKeys.all, 'data-assets', tenantId, 'activation-decision'] as const,
  tenantActivationDecision: (tenantId: string) =>
    [...platformKeys.all, 'tenant', tenantId, 'activation-policy'] as const,
  tenantRecoveryEvidence: (tenantId: string) =>
    [...platformKeys.all, 'tenant', tenantId, 'recovery-evidence'] as const,
  tenantDeletionCertification: (tenantId: string) =>
    [...platformKeys.all, 'tenant', tenantId, 'deletion-certification'] as const,
  platformMfa: () => [...platformKeys.all, 'auth', 'mfa'] as const,

  // Platform MFA enforcement. The effective read is separate from the Owner read model
  // because the chrome polls it on every screen to decide whether a banner is owed, while
  // the full model — operator counts, available modes — is only wanted on one page.
  platformAuthPolicy: () => [...platformKeys.all, 'auth', 'policy'] as const,
  platformAuthPolicyEffective: () => [...platformKeys.all, 'auth', 'policy', 'effective'] as const,

  supportTickets: (filter?: unknown) => [...platformKeys.all, 'support', 'tickets', filter ?? null] as const,
  supportTicket: (id: string) => [...platformKeys.all, 'support', 'ticket', id] as const,
  supportTicketTimeline: (id: string) => [...platformKeys.all, 'support', 'ticket', id, 'timeline'] as const,

  // Outbound email. Status is separate from settings because it is the one the screen
  // polls after a test — settings only change when somebody saves.
  emailSettings: () => [...platformKeys.all, 'email', 'settings'] as const,
  emailStatus: () => [...platformKeys.all, 'email', 'status'] as const,
  emailProviders: () => [...platformKeys.all, 'email', 'providers'] as const,

  auditQuery: (filter?: unknown) => [...platformKeys.all, 'audit', 'query', filter ?? null] as const,
  auditEntry: (id: string) => [...platformKeys.all, 'audit', 'entry', id] as const,
  auditActions: (filter?: unknown) => [...platformKeys.all, 'audit', 'actions', filter ?? null] as const,
  tenantTimeline: (tenantId: string, filter?: unknown) =>
    [...platformKeys.all, 'audit', 'tenant-timeline', tenantId, filter ?? null] as const,
};
