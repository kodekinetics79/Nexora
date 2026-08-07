// ---------------------------------------------------------------------------
// The client half of the typed-confirmation contract for the irreversible tenant
// operations (purge, personal-data erasure).
//
// Kept free of React so it can be asserted directly: these two predicates are the
// last thing standing between an operator and a customer's records, and "the button
// was disabled" has to be a property that can be tested rather than a claim about a
// component tree.
//
// The rules mirror `TenantOffboardingService.RequireConfirmation` /
// `RequireReason` exactly. They are duplicated rather than shared because the server
// is the authority — this copy exists so the operator is stopped BEFORE the request,
// not so the server can trust the client.
// ---------------------------------------------------------------------------

/**
 * The floor the platform applies to a reason for destroying a customer's records.
 * Mirrors `TenantOffboardingService.MinimumDestructionReasonLength`.
 */
export const MIN_DESTRUCTION_REASON_LENGTH = 15;

/**
 * Ordinal and CASE-SENSITIVE, matching the server. "acme" is a different customer
 * from "ACME" often enough to matter, and a confirmation that quietly accepts the
 * wrong casing is a confirmation that was not read.
 *
 * <p>Surrounding whitespace is forgiven on both sides because the server trims too —
 * a name pasted from a spreadsheet arrives with a trailing space and refusing it
 * teaches the operator to paste less carefully, not more.</p>
 */
export const confirmationMatches = (typed: string, required: string): boolean =>
  typed.trim() === required.trim() && required.trim().length > 0;

export const destructiveReasonProblem = (reason: string): string | null => {
  const trimmed = reason.trim();
  if (trimmed.length === 0) return 'A reason is required.';
  if (trimmed.length < MIN_DESTRUCTION_REASON_LENGTH) {
    return `At least ${MIN_DESTRUCTION_REASON_LENGTH} characters, so the decision to destroy a customer's records is attributable to something a reader can understand later.`;
  }
  return null;
};

export const confirmationProblem = (typed: string, required: string): string | null => {
  if (typed.trim().length === 0) return null;
  return confirmationMatches(typed, required)
    ? null
    : `This does not match. Type the tenant's name exactly — ${required} — including its capitalisation.`;
};
