// ---------------------------------------------------------------------------
// Who at a customer is invoiced, where, and on what terms.
//
// The server is the authority — `PlatformBillingController.ValidateAccountContact`
// enforces every rule below and will 400 regardless of what this file says. This
// exists so the operator is told at the form rather than at the response, and it
// lives beside `commercialTermsValidation` rather than inside the component for
// the same reason that one does: a rule buried in JSX cannot be tested, and an
// untested rule drifts away from the server's.
// ---------------------------------------------------------------------------

export interface AccountContactForm {
  billingContactName: string;
  billingContactEmail: string;
  billingAddress: string;
  purchaseOrderReference: string;
  paymentTermsDays: string;
  accountOwnerEmail: string;
  contractStartOn: string;
  contractEndOn: string;
}

export const EMPTY_ACCOUNT_CONTACT: AccountContactForm = {
  billingContactName: '',
  billingContactEmail: '',
  billingAddress: '',
  purchaseOrderReference: '',
  paymentTermsDays: '',
  accountOwnerEmail: '',
  contractStartOn: '',
  contractEndOn: '',
};

/**
 * The ceiling the due date can carry. `SubscriptionInvoiceService` computes
 * `DueAtUtc = IssuedAtUtc.AddDays(PaymentTermsDays ?? 30)`, so a mistyped 3650 produces an
 * invoice that falls due in ten years and drops out of every collections view without ever
 * looking overdue. Zero is allowed and means "due on receipt", which is a real commercial term.
 */
export const MAX_PAYMENT_TERMS_DAYS = 365;

/** A shape check, not an RFC 5322 parser — it catches the transposed address and the pasted
 *  display name, which are the failures that actually happen. Deliverability is proven by
 *  sending, and the invitation surface already reports whether a provider accepted a message. */
const plausibleEmail = (value: string): boolean => /^[^\s@]+@[^\s@.][^\s@]*\.[^\s@.]+$/.test(value);

/**
 * The first problem with this form, or null. Mirrors the server's checks in order, so the
 * message an operator reads before submitting is the message they would have read after.
 */
export const accountContactProblem = (
  form: AccountContactForm,
  billingMode: string,
): string | null => {
  const email = form.billingContactEmail.trim();

  // Null and empty are VALUES, and this is the one that matters: invoicing refuses to issue
  // without a recipient, so clearing it stops the tenant being billed — and, because the
  // offboarding readiness gate requires a finalized invoice, also strands it.
  if (!email && billingMode.toLowerCase() !== 'internal')
    return 'An invoice recipient is required: invoicing refuses to issue without one, so clearing '
      + 'it stops this tenant being billed and also blocks offboarding, which needs a finalized invoice.';
  if (email && !plausibleEmail(email)) return 'The invoice recipient is not an email address.';

  const owner = form.accountOwnerEmail.trim();
  if (owner && !plausibleEmail(owner)) return 'The account owner is not an email address.';

  const terms = form.paymentTermsDays.trim();
  if (terms) {
    const days = Number(terms);
    if (!Number.isInteger(days) || days < 0 || days > MAX_PAYMENT_TERMS_DAYS)
      return `Payment terms must be a whole number of days between 0 and ${MAX_PAYMENT_TERMS_DAYS}. `
        + 'The figure is added to the issue date to compute the due date, so anything outside that '
        + 'makes an invoice either overdue on issue or effectively never due.';
  }

  if (form.contractStartOn && form.contractEndOn && form.contractEndOn <= form.contractStartOn)
    return 'The contract end date must fall after the start date.';

  return null;
};
