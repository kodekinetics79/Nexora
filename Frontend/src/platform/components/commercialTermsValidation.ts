// ---------------------------------------------------------------------------
// Client-side mirror of `PlatformBillingController.ValidateCommercialTerms`.
//
// These are the rules that decide whether a customer is charged. They are
// duplicated here — not shared, because there is nothing to share with — so the
// operator is stopped at the form rather than at a 400 they have to read twice.
// The server remains the authority; if the two ever disagree, the server wins and
// its message is what the console displays.
//
// Kept free of React so the money rules can be asserted directly.
// ---------------------------------------------------------------------------

import type { BillingMode } from '../types';

/**
 * Mirrors `MinimumBillingModeReasonLength`. A length rule and not a presence rule
 * because an exemption justified as "x" passes a required-field check while leaving a
 * paper trail worth nothing.
 */
export const MIN_BILLING_MODE_REASON_LENGTH = 15;

export interface CommercialTermsForm {
  billingMode: BillingMode;
  billingModeReason: string;
  /** `yyyy-mm-dd`, as a date input holds it. */
  trialEndsOn: string;
  billingStartsOn: string;
}

export type CommercialTermsErrors = Partial<Record<keyof CommercialTermsForm, string>>;

const isIsoDate = (value: string) => /^\d{4}-\d{2}-\d{2}$/.test(value) && !Number.isNaN(Date.parse(value));

const startOfToday = (): number => {
  const now = new Date();
  return new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
};

/**
 * @param hasPlan whether the tenant currently carries a plan. Billable without one is
 *   refused server-side, because such a tenant produces statements with no base
 *   subscription line and is therefore never charged.
 */
export const validateCommercialTerms = (
  form: CommercialTermsForm,
  hasPlan: boolean,
): CommercialTermsErrors => {
  const errors: CommercialTermsErrors = {};

  if (form.billingMode === 'Billable' && !hasPlan) {
    errors.billingMode =
      'This tenant has no plan, so making it Billable would produce statements with no subscription line and it would never be charged. Assign a plan first.';
  }

  if (form.billingMode !== 'Billable') {
    const reason = form.billingModeReason.trim();
    if (reason.length < MIN_BILLING_MODE_REASON_LENGTH) {
      errors.billingModeReason = `${form.billingMode} means this tenant is not charged. Explain why, in at least ${MIN_BILLING_MODE_REASON_LENGTH} characters, so the exemption is attributable to a real decision.`;
    }
  }

  if (form.billingMode === 'Trial') {
    if (form.trialEndsOn.trim().length === 0) {
      errors.trialEndsOn = 'A trial must carry an end date. An open-ended trial is free service with no conversion date.';
    } else if (!isIsoDate(form.trialEndsOn)) {
      errors.trialEndsOn = 'Enter a valid date.';
    } else if (Date.parse(form.trialEndsOn) <= startOfToday()) {
      errors.trialEndsOn =
        'The trial end date must be in the future. To record that a trial has already ended, convert the tenant to Billable, Internal or Partner instead of back-dating it.';
    }
  }

  if (form.billingStartsOn.trim().length > 0 && !isIsoDate(form.billingStartsOn)) {
    errors.billingStartsOn = 'Enter a valid date.';
  }

  return errors;
};

export const commercialTermsAreValid = (form: CommercialTermsForm, hasPlan: boolean): boolean =>
  Object.keys(validateCommercialTerms(form, hasPlan)).length === 0;

/**
 * Operator-facing text for the machine-readable leak reasons the revenue board serves.
 * Unknown codes fall through to the code itself rather than being dropped — a reason the
 * console cannot name is still a reason the operator needs to see.
 */
export const LEAK_REASON_COPY: Record<string, string> = {
  'no-plan': 'Billable with no plan — nothing charges the subscription.',
  'plan-not-priced': 'The plan carries no monthly price, so the base line is a real zero.',
  'unpinned-rate-card': 'No pinned rate card — whichever card is active at compute time is what they are charged on.',
  'trial-open-ended': 'Trial with no end date — indistinguishable from permanent free service.',
  'trial-expired': 'Trial past its end date and still uncharged. The account needs converting.',
  'never-billed': 'Billable and never billed — no statement has ever been computed.',
  'last-statement-charged-nothing': 'The most recent statement totalled zero.',
  'exemption-unexplained': 'Not billable and no reason recorded — free service nobody signed for.',
  'billing-not-started': 'The billing start date is still in the future.',
};

export const leakReasonLabel = (code: string): string => LEAK_REASON_COPY[code] ?? code;

export const COMMERCIAL_STATE_COPY: Record<string, string> = {
  complete: 'Terms complete',
  'plan-missing': 'Plan missing',
  'exemption-unrecorded': 'Exemption unrecorded',
};

/**
 * The commercial-configuration state for a tenant, from the tenant row.
 *
 * <p>This mirrors `CommercialConfigurationStates.For` exactly, and it is a mirror rather
 * than a second opinion: the server derives the same state from the same three fields
 * — billing mode, plan id, and the written reason — all of which ride on the tenant
 * summary every PlatformScope holder can already read. Deriving it here is what lets a
 * SupportAdmin, who is deliberately refused the billing endpoints, still SEE that a
 * customer is running on terms nobody set.</p>
 *
 * <p>Where the billing profile is readable, prefer its `revenueRisk` — that is the
 * authoritative copy, computed alongside the leak reasons and the statement history.</p>
 */
export const commercialConfigurationState = (tenant: {
  billingMode: BillingMode | null;
  planId: string | null;
  billingModeReason: string | null;
}): string => {
  if (tenant.billingMode === 'Billable' || tenant.billingMode === 'Trial') {
    return tenant.planId === null ? 'plan-missing' : 'complete';
  }
  // A null billing mode is a tenant provisioned before modes existed. It is not claimed
  // to be complete and it is not claimed to be broken — the caller renders "—".
  if (tenant.billingMode === null) return 'complete';
  return (tenant.billingModeReason ?? '').trim().length === 0 ? 'exemption-unrecorded' : 'complete';
};

export const commercialConfigurationRequired = (tenant: {
  billingMode: BillingMode | null;
  planId: string | null;
  billingModeReason: string | null;
}): boolean => commercialConfigurationState(tenant) !== 'complete';
