/**
 * Product capabilities offered in the current release.
 *
 * Routes and implementation may remain for staged activation, but release-visible navigation,
 * search and contextual actions must read this contract instead of leaking deferred modules.
 */
export const RELEASE_SCOPE = {
  suppliers: true,
  fulfilment: false,
  receivables: false,
  reservations: false,
} as const;
