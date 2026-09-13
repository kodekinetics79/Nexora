/**
 * The words a salesperson reads for where a request (lead) stands.
 *
 * The governed lead status codes are a fixed set (LifecycleStatusCatalog on the server), but the
 * label beside each one is the tenant's own setup text, so the same lead read "Became an RFQ" on
 * the Leads list, "Converted to RFQ" on the Deadline board and "converted to rfq" in the Decide
 * screen's next step. One map, used by every screen that names a lead status, keeps them the same.
 *
 * QUOTED reads "Quote sent", not "Quoted": on a lead, "Quoted" was read as "these lines are
 * quoted", which no step before an RFQ ever makes true.
 *
 * Unlike `statusLabel()`, an unknown code returns null so the caller chooses its own fallback
 * (usually the tenant's label) instead of a title-cased guess.
 */
export const LEAD_STATUS_WORDS: Readonly<Record<string, string>> = Object.freeze({
  RECEIVED: 'New',
  PENDING_IDENTIFICATION: 'Customer not matched yet',
  UNASSIGNED: 'Waiting for an owner',
  ASSIGNED: 'Owner assigned',
  UNDER_REVIEW: 'Under review',
  QUALIFIED: 'Qualified',
  DISQUALIFIED: 'Declined',
  CONVERTED_TO_RFQ: 'Became an RFQ',
  QUOTED: 'Quote sent',
  NEGOTIATION: 'In negotiation',
  AWARDED: 'Won',
  PARTIALLY_AWARDED: 'Partly won',
  LOST: 'Lost',
  CANCELLED: 'Cancelled',
  COMPLETED: 'Completed',
  DUPLICATED: 'Duplicate',
});

/** Job words for a lead status code, or null when the code is empty or not a known status. */
export function leadStatusWords(code?: string | null): string | null {
  const key = code?.trim().toUpperCase();
  if (!key) return null;
  return Object.prototype.hasOwnProperty.call(LEAD_STATUS_WORDS, key) ? LEAD_STATUS_WORDS[key] : null;
}

export default leadStatusWords;
