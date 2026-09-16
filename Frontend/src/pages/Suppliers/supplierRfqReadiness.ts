/**
 * The one place the frontend states which supplier verdicts let a supplier be asked for quotes.
 *
 * Mirrors the server twice over: ProcurementApplicationService.SupplierRfqBlockingReasons (what
 * blocks a candidate on a sourcing case) and SupplierGovernanceService's READY rule (what the
 * governance endpoint accepts). A supplier can be asked when it is active, has a contact email, and
 *   approval     in APPROVED | PREFERRED | PROVISIONAL
 *   verification VERIFIED
 *   compliance   CLEARED
 *   risk         LOW | MEDIUM   (the server refuses READY with UNKNOWN, HIGH or BLOCKED risk)
 *   readiness    READY
 * Nothing here decides anything: the server re-checks every write.
 */

export interface GovernanceDecision {
  governanceStatus: string;
  verificationStatus: string;
  complianceStatus: string;
  riskStatus: string;
  readinessStatus: string;
}

export const APPROVE_FOR_RFQS_LABEL = 'Approve for RFQs';

/** The rule in the words a manager reads before pressing the button. */
export const RFQ_READY_RULE =
  'A supplier can be asked for quotes once it is Approved (or Provisional), Verified, Compliance cleared, '
  + 'Risk Low or Medium and marked Ready for RFQs, and its record is active with a contact email.';

const APPROVING = new Set(['APPROVED', 'PREFERRED', 'PROVISIONAL']);
const RISK_TOO_HIGH = new Set(['HIGH', 'BLOCKED']);

/**
 * The combination "Approve for RFQs" records. Keeps a Preferred verdict and a Medium risk verdict
 * a person already recorded; fills the rest with the working values. Returns null when risk is
 * HIGH or BLOCKED: the preset never quietly lowers a risk verdict — that is a decision to take by
 * hand under Advanced.
 */
export function approveForRfqs(current: Partial<GovernanceDecision> | null | undefined): GovernanceDecision | null {
  const risk = current?.riskStatus ?? 'UNKNOWN';
  if (RISK_TOO_HIGH.has(risk)) return null;
  return {
    governanceStatus: current?.governanceStatus === 'PREFERRED' ? 'PREFERRED' : 'APPROVED',
    verificationStatus: 'VERIFIED',
    complianceStatus: 'CLEARED',
    riskStatus: risk === 'MEDIUM' ? 'MEDIUM' : 'LOW',
    readinessStatus: 'READY',
  };
}

/** What still stops this combination of verdicts from being askable, in plain words; empty when nothing does. */
export function rfqReadinessGaps(decision: GovernanceDecision): string[] {
  const gaps: string[] = [];
  if (!APPROVING.has(decision.governanceStatus)) gaps.push('not approved');
  if (decision.verificationStatus !== 'VERIFIED') gaps.push('not verified');
  if (decision.complianceStatus !== 'CLEARED') gaps.push('compliance not cleared');
  if (decision.riskStatus === 'UNKNOWN') gaps.push('risk not assessed');
  else if (RISK_TOO_HIGH.has(decision.riskStatus)) gaps.push('risk too high');
  if (decision.readinessStatus !== 'READY') gaps.push('not marked ready for RFQs');
  return gaps;
}

/** The audit reason the preset records. `where` names the screen it was pressed on, if not the supplier page. */
export function approveForRfqsReason(actor: string | null | undefined, where?: string): string {
  const who = actor?.trim() || 'a manager';
  return `Approved for RFQs by ${who}${where ? ` ${where}` : ''}`;
}

/**
 * The server's blocking reasons, in the buyer's words. Keys are the exact strings
 * SupplierRfqBlockingReasons emits; an unknown reason is shown as sent.
 */
const BLOCKER_WORDS: Record<string, string> = {
  'Supplier must be active': 'Supplier record is inactive',
  'A verified dispatch contact is required': 'No contact email',
  'Supplier approval or explicit provisional approval is required': 'Not approved',
  'Supplier verification status must be VERIFIED': 'Not verified',
  'Supplier outreach readiness must be READY': 'Not marked ready for RFQs',
  'Supplier compliance status must be CLEARED': 'Compliance not cleared',
  'Supplier risk status blocks outreach': 'Risk is High or Blocked',
};

export const blockerWords = (reason: string): string => BLOCKER_WORDS[reason] ?? reason;

/** The blockers "Approve for RFQs" clears. Not the record being inactive, a missing email, or high risk. */
const PRESET_CLEARS = new Set([
  'Supplier approval or explicit provisional approval is required',
  'Supplier verification status must be VERIFIED',
  'Supplier outreach readiness must be READY',
  'Supplier compliance status must be CLEARED',
]);

/** True when every blocker on this candidate is one the preset clears — so approval alone makes it askable. */
export const presetClearsAllBlockers = (reasons: readonly string[] | null | undefined): boolean =>
  Array.isArray(reasons) && reasons.length > 0 && reasons.every((reason) => PRESET_CLEARS.has(reason));
