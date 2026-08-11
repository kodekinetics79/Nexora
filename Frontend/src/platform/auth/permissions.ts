// ---------------------------------------------------------------------------
// What the signed-in operator is allowed to do.
//
// The server is the authority — every `/api/platform/*` route carries an
// `[Authorize(Policy = …)]` attribute and will 403 regardless of what this file
// says. This mirrors those policies so the console does not offer a control that
// is guaranteed to fail: a button that always 403s is a bug, and worse, it tells
// an operator they have authority they do not have.
//
// The four tiers exist to separate duties (ADR-0005 §3):
//   Owner        — everything, including the irreversible verbs
//   SupportAdmin — operate tenants and the support desk; never touches money
//   BillingAdmin — decides what a customer is charged; never operates tenants
//   ReadOnlyOps  — cross-tenant observability and nothing else
// ---------------------------------------------------------------------------

import type { PlatformOperatorRole } from '../types';
import { PLATFORM_OPERATOR_ROLES } from '../types';

/**
 * The role as the server named it, or null when the session carries something this
 * console does not recognise.
 *
 * Null is deliberately NOT treated as "probably fine". An unrecognised role means the
 * console cannot reason about authority at all, and the safe reading of "I do not know
 * what you are" is ReadOnlyOps — the server will refuse the rest anyway, so guessing
 * upward would only produce buttons that 403.
 */
export const normalizePlatformRole = (role: string | null | undefined): PlatformOperatorRole | null =>
  PLATFORM_OPERATOR_ROLES.find(
    (known) => known.toLowerCase() === (role ?? '').trim().toLowerCase(),
  ) ?? null;

/**
 * Mirrors `PlatformPolicies` in `Platform/Auth/PlatformAuthConstants.cs`. Each field is
 * one server policy, named after it, so a policy change on the backend has exactly one
 * place to land here.
 */
export interface PlatformPermissions {
  /** The recognised role, or null. Rendered in the chrome so the operator can see it. */
  role: PlatformOperatorRole | null;

  /** `Platform.Owner`. The irreversible lifecycle verbs and nothing less. */
  isOwner: boolean;

  /**
   * `Platform.TenantAdmin` (Owner | SupportAdmin). Provisioning, the reversible tenant
   * lifecycle, the support desk, and the tenant data export.
   */
  canAdministerTenants: boolean;

  /**
   * `Platform.Billing` (Owner | BillingAdmin). Rate cards, statements, commercial terms,
   * plan assignment — everything that decides what a customer pays.
   */
  canAdministerBilling: boolean;

  /** `Platform.Impersonate` (Owner | SupportAdmin). */
  canImpersonate: boolean;

  /**
   * True when the session carried no role this console understands. Surfaced rather than
   * silently swallowed: an operator who sees nothing but read-only screens deserves to
   * know it is their session, not the product.
   */
  roleUnknown: boolean;
}

export const permissionsForRole = (role: string | null | undefined): PlatformPermissions => {
  const normalized = normalizePlatformRole(role);
  const isOwner = normalized === 'Owner';
  return {
    role: normalized,
    isOwner,
    canAdministerTenants: isOwner || normalized === 'SupportAdmin',
    canAdministerBilling: isOwner || normalized === 'BillingAdmin',
    canImpersonate: isOwner || normalized === 'SupportAdmin',
    roleUnknown: normalized === null,
  };
};

/**
 * Who the operator would have to be. Used verbatim in the tooltip on a disabled control,
 * because "you cannot do this" without saying who can is the least useful message a
 * console can give — the operator's next move is to find the person who can.
 */
export const REQUIRED_ROLE_COPY = {
  owner: 'Owner only. This action is irreversible, so it is deliberately not available to support.',
  /**
   * The email screen is Owner-gated too, but for a different reason, and the `owner` copy above
   * was written for tenant deletion. Saying "irreversible" on a change that is versioned, audited
   * and reversible teaches operators that the tooltip is boilerplate — and the next one they
   * dismiss is the one that mattered.
   */
  platformEmail:
    'Owner only. Changing the address the product sends from — and the credentials behind it — is a privileged act, so it is deliberately not available to support.',
  /**
   * Owner-gated for a third reason again, and it needs its own words. Neither "irreversible"
   * (it is not) nor "the address we send from" (it is not that either) describes turning
   * platform MFA enforcement off — the honest description is that it is the one change that
   * lowers the value of every other control on this plane.
   */
  platformAuthentication:
    'Owner only. Relaxing platform MFA enforcement weakens every other control on this console, so it is deliberately not available to support.',
  tenantAdmin: 'Requires the Owner or SupportAdmin role.',
  billing: 'Requires the Owner or BillingAdmin role. Billing is separated from tenant operations on purpose.',
  impersonate: 'Requires the Owner or SupportAdmin role.',
} as const;
