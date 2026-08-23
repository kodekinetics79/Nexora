/**
 * Turns a routing engine decision code into the sentence it stands for.
 *
 * `PersistRoutingResultAsync` writes the raw code into `Lead.AssignComment`, which the lead
 * detail screen surfaces verbatim as "Assignment reason". A user was therefore being told
 * `PRIMARY_OWNER_ASSIGNED` and asked to make sense of it. The stored code is deliberately left
 * alone — it is the audit value, it is what `LeadRoutingDecision.DecisionCode` matches, and
 * rewriting persisted history to make a label read better would be the wrong repair. This is a
 * presentation mapping over it.
 *
 * The codes are the complete set emitted by `DeterministicRoutingEngine` plus the one written by
 * manual assignment. An unrecognised value is returned unchanged rather than hidden, so a code
 * added later shows up as itself instead of silently disappearing.
 */
const ROUTING_DECISION_SENTENCES: Record<string, string> = {
  PRIMARY_OWNER_ASSIGNED: 'Assigned automatically to the account’s primary owner.',
  BACKUP_OWNER_ASSIGNED: 'Assigned automatically to the backup owner because the primary owner was unavailable.',
  BACKUP_OWNER_ASSIGNED_FOR_WORKLOAD: 'Assigned automatically to the backup owner to relieve the primary owner’s workload.',
  NO_MATCH_EVIDENCE: 'Left unassigned: nothing on this inquiry identified a known customer.',
  MATCH_BELOW_THRESHOLD: 'Left unassigned: the customer match was too weak to route on.',
  AMBIGUOUS_CUSTOMER: 'Left unassigned: two customers matched this inquiry equally well.',
  NO_EFFECTIVE_OWNERSHIP: 'Left unassigned: the customer was identified but nobody owns this account.',
  OWNER_UNAVAILABLE: 'Left unassigned: the account owner is not eligible for routing or is out of capacity.',
  MANUAL_ASSIGNMENT: 'Assigned by hand.',
};

export const routingDecisionSentence = (code?: string | null): string =>
  (code && ROUTING_DECISION_SENTENCES[code.trim()]) || code || '';
