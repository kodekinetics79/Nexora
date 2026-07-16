// Centralised TanStack Query keys for the platform console, so invalidation
// after a mutation stays consistent across pages.

export const platformKeys = {
  all: ['platform'] as const,
  overview: () => [...platformKeys.all, 'overview'] as const,
  tenants: () => [...platformKeys.all, 'tenants'] as const,
  tenant: (id: string) => [...platformKeys.all, 'tenant', id] as const,
  queue: () => [...platformKeys.all, 'queue'] as const,
  jobs: (filter?: unknown) => [...platformKeys.all, 'jobs', filter ?? null] as const,
  plans: () => [...platformKeys.all, 'plans'] as const,
  flags: () => [...platformKeys.all, 'flags'] as const,
  audit: (filter?: unknown) => [...platformKeys.all, 'audit', filter ?? null] as const,
};
