/**
 * Lead → RFQ conversion gates, mirrored from the server so the page can say "no, and here is
 * why" BEFORE the operator has re-typed quantities, acknowledged warnings and written a note.
 *
 * The authority stays on the server (LeadConversionIntelligence.ConvertCoreAsync). This module
 * only reproduces the same three refusals in the same order so the wording a user reads matches
 * the reason a submit would have failed:
 *
 *   1. Canonical lifecycle state must be QUALIFIED.
 *   2. An unresolved duplicate flag (suspected/confirmed) blocks conversion.
 *   3. AI-extracted commercial facts must be approved when the lead was flagged for review.
 *
 * Nothing here guesses at a field the API does not send: the lifecycle code comes from
 * GET /api/commercial-cases/leads/{id}/lifecycle (already canonicalized by LifecyclePolicy),
 * the rest from LeadResponseDTO.
 */

/**
 * Canonical states that convert. The lifecycle endpoint canonicalizes before it answers, so
 * QUALIFIED is what a healthy tenant returns; ACCEPTED / LEAD_ACCEPTED are the legacy synonyms
 * LifecyclePolicy maps onto QUALIFIED and are accepted here for the same reason the server
 * accepts them — a tenant whose setup rows still carry the old code is not a blocked tenant.
 */
export const CONVERTIBLE_LEAD_STATUS_CODES: readonly string[] = ['QUALIFIED', 'ACCEPTED', 'LEAD_ACCEPTED'];

/** The canonical state the page offers to move a lead into. */
export const QUALIFIED_STATUS_CODE = 'QUALIFIED';

/** True when a lead in this canonical state may be converted to an RFQ. */
export const isConvertibleLeadStatus = (statusCode: string | null | undefined): boolean => {
  if (!statusCode) return false;
  return CONVERTIBLE_LEAD_STATUS_CODES.includes(statusCode.trim().toUpperCase());
};

/** "UNDER_REVIEW" → "Under review". Status codes are never shown raw to a commercial user. */
export const formatLeadStatus = (statusCode: string | null | undefined): string => {
  const raw = (statusCode ?? '').trim();
  if (!raw) return 'Unknown';
  const words = raw.replaceAll('_', ' ').toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
};

export type ConversionBlockerKey = 'lifecycle' | 'duplicate' | 'commercial-review';

/** Where the user goes next to clear this blocker. */
export type ConversionBlockerAction = 'qualify' | 'open-lead' | 'open-extraction-review';

export interface ConversionBlocker {
  key: ConversionBlockerKey;
  severity: 'error' | 'warning';
  /** Headline, in commercial language. */
  title: string;
  /** One or two plain sentences saying what has to happen. */
  detail: string;
  /** Short sentence shown beside the disabled "Create RFQ" button. */
  shortReason: string;
  action: ConversionBlockerAction;
}

export interface ConversionGateInput {
  /**
   * Canonical lifecycle status. `undefined`/`null` means "not known yet" (still loading, or the
   * lifecycle call failed) — that is deliberately NOT treated as a blocker, because refusing on
   * missing information would be a worse lie than letting the server's backstop answer.
   */
  lifecycleStatusCode?: string | null;
  /** LeadResponseDTO.duplicateStatus: null | 'suspected' | 'confirmed' | 'not_duplicate'. */
  duplicateStatus?: string | null;
  duplicateOfLeadId?: number | null;
  requiresCommercialReview?: boolean | null;
  commercialFactsVerified?: boolean | null;
}

/**
 * Every server refusal we can see coming, in the order the server applies them. Empty means the
 * lead-level gates are clear (line-level rules are still evaluated by the page).
 */
export const conversionBlockers = (input: ConversionGateInput): ConversionBlocker[] => {
  const blockers: ConversionBlocker[] = [];

  const status = input.lifecycleStatusCode;
  if (status != null && String(status).trim() !== '' && !isConvertibleLeadStatus(status)) {
    const friendly = formatLeadStatus(status);
    blockers.push({
      key: 'lifecycle',
      severity: 'warning',
      title: 'This lead has to be qualified before it can become an RFQ',
      detail: `It is currently “${friendly}”. Qualifying is where someone decides this inquiry is worth bidding, `
        + 'so the RFQ can always be traced back to that decision. Qualify it and this page carries on where you left off.',
      shortReason: `This lead is “${friendly}” — it has to be qualified first.`,
      action: 'qualify',
    });
  }

  const duplicate = (input.duplicateStatus ?? '').trim().toLowerCase();
  if (duplicate === 'suspected' || duplicate === 'confirmed') {
    const original = input.duplicateOfLeadId != null ? ` of Lead #${input.duplicateOfLeadId}` : '';
    blockers.push({
      key: 'duplicate',
      severity: duplicate === 'confirmed' ? 'error' : 'warning',
      title: duplicate === 'confirmed'
        ? `This lead is a confirmed duplicate${original}`
        : `This lead looks like a duplicate${original}`,
      detail: duplicate === 'confirmed'
        ? 'Converting it would raise a second RFQ for the same inquiry. If the confirmation was a mistake, '
          + 'mark it as not a duplicate on the lead, then come back.'
        : 'Someone has to decide whether it is the same inquiry before it can become an RFQ. Compare it with the '
          + 'original on the lead and record the decision there.',
      shortReason: 'The duplicate flag on this lead has to be resolved first.',
      action: 'open-lead',
    });
  }

  if (input.requiresCommercialReview === true && input.commercialFactsVerified !== true) {
    blockers.push({
      key: 'commercial-review',
      severity: 'warning',
      title: 'The commercial figures on this lead still need approval',
      detail: 'This lead was flagged because its prices, quantities or terms were read by the system rather than '
        + 'confirmed by a person. Approve them in extraction review, then create the RFQ.',
      shortReason: 'The extracted commercial figures have not been approved yet.',
      action: 'open-extraction-review',
    });
  }

  return blockers;
};
