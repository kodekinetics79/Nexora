import React from 'react';
import { Box, Button, ButtonBase, Chip, Stack, Tooltip, Typography } from '@mui/material';
import type { ClientCandidateDTO } from '../../api/services/leadService';

/**
 * CLIENT ORGANISATION IDENTITY — shared vocabulary + the list-grid cell.
 *
 * A rep looking at a lead must never have to ask "who is this from?". The
 * backend exposes eight raw match statuses; a rep only ever needs three:
 *
 *   resolved    — a client company is linked. Show its name.
 *   suggested   — the machine has one or more candidates but nothing is linked.
 *                 Show the best one, clearly marked as a suggestion, one click
 *                 from being confirmed. (AMBIGUOUS renders here too, as
 *                 "N possible clients".)
 *   unresolved  — nothing usable. Show that plainly, with a live action.
 *
 * This module is the leaf of the client-identity UI: `ClientIdentityPanel` and
 * `ResolveClientDialog` import the vocabulary from here, and nothing here
 * imports them, so the three files stay acyclic.
 *
 * HARD RULE inherited from the resolver: a wrong client on a lead is worse than
 * an unresolved one. Nothing in this file may present a suggestion as a fact —
 * suggested state is always visually and textually distinct from resolved, and
 * the confidence is always shown.
 */

export type ClientIdentityState = 'resolved' | 'suggested' | 'unresolved';

/**
 * The client-identity slice of a lead row. Every field is optional because the
 * list endpoints that predate this feature (and /api/UnAssignedLead) do not
 * send any of it — those rows degrade to `unresolved`, which is honest.
 */
export interface ClientIdentityLike {
  customerId?: number | null;
  customerName?: string | null;
  customerMatchStatus?: string | null;
  customerMatchReasonCode?: string | null;
  customerMatchConfidence?: number | null;
  clientCandidates?: ClientCandidateDTO[] | null;
}

/**
 * Statuses that must NEVER be rendered as a linked client, whatever else the
 * payload says. The database CHECK constraint already guarantees they carry a
 * null CustomerID; this is the belt-and-braces on the render side, because
 * showing an unconfirmed guess as a fact is the one failure this whole feature
 * exists to prevent. (The five statuses that MAY carry a customer are
 * AUTO_MATCHED, AUTO_MATCHED_CONTACT_UNRESOLVED, CONFIRMED,
 * CUSTOMER_CONFIRMED_CONTACT_UNRESOLVED and VERIFIED_EMAIL.)
 */
const PROPOSED_STATUSES: ReadonlySet<string> = new Set(['SUGGESTED', 'AMBIGUOUS']);

/** Statuses that mean a PERSON chose this client, not the machine. */
const HUMAN_CONFIRMED_STATUSES: ReadonlySet<string> = new Set([
  'CONFIRMED',
  'CUSTOMER_CONFIRMED_CONTACT_UNRESOLVED',
]);

/**
 * True when a human confirmed the link. Worth distinguishing in the UI: "a
 * colleague checked this" and "the machine matched this" carry very different
 * weight when a rep is about to quote against it.
 */
export const isHumanConfirmedStatus = (status?: string | null): boolean =>
  HUMAN_CONFIRMED_STATUSES.has((status ?? '').trim().toUpperCase());

export const normalizedStatus = (lead: ClientIdentityLike): string =>
  (lead.customerMatchStatus ?? '').trim().toUpperCase();

export const clientCandidates = (lead: ClientIdentityLike): ClientCandidateDTO[] => {
  const candidates = lead.clientCandidates ?? [];
  if (!Array.isArray(candidates)) return [];
  return [...candidates]
    .filter((c) => c && typeof c.customerId === 'number')
    .sort((a, b) => (a.rank ?? 0) - (b.rank ?? 0));
};

/**
 * Collapses the eight backend statuses into the three states a rep reasons about.
 *
 * `customerId` is the authority for "resolved" — it is the field that makes the
 * Lead → RFQ → Quote → Order lineage commercially meaningful. SUGGESTED and
 * AMBIGUOUS are excluded defensively: if a payload ever contradicts the DB
 * invariant, we refuse to show an unconfirmed link as a fact.
 */
export const clientIdentityState = (lead: ClientIdentityLike): ClientIdentityState => {
  const status = normalizedStatus(lead);
  if (lead.customerId != null && !PROPOSED_STATUSES.has(status)) return 'resolved';
  if (clientCandidates(lead).length > 0 || PROPOSED_STATUSES.has(status)) return 'suggested';
  return 'unresolved';
};

/** Display name for a linked client, never blank (the id is better than nothing). */
export const clientDisplayName = (lead: ClientIdentityLike): string => {
  const name = (lead.customerName ?? '').trim();
  if (name) return name;
  return lead.customerId != null ? `Customer #${lead.customerId}` : 'Unknown client';
};

/**
 * Plain-language rendering of a raw match status, for places that echo the
 * status itself (the ingestion batch page). Never shows the enum to a user.
 */
export const clientStatusLabel = (status?: string | null): string => {
  switch ((status ?? '').trim().toUpperCase()) {
    case 'AUTO_MATCHED': return 'Linked automatically';
    case 'AUTO_MATCHED_CONTACT_UNRESOLVED': return 'Linked automatically, contact still unknown';
    case 'CONFIRMED': return 'Confirmed by a person';
    case 'CUSTOMER_CONFIRMED_CONTACT_UNRESOLVED': return 'Confirmed by a person, contact still unknown';
    case 'VERIFIED_EMAIL': return "Linked from the sender's email address";
    case 'SUGGESTED': return 'Suggested, waiting for someone to confirm';
    case 'AMBIGUOUS': return 'Several possible clients, needs a decision';
    case 'UNRESOLVED': return 'Not linked to a client yet';
    case '': return 'Not linked to a client yet';
    default: return 'Not linked to a client yet';
  }
};

/**
 * The evidence behind a match, phrased for a sales rep. Keep every sentence
 * fragment usable both after "Matched because " and standing alone in a
 * candidate row.
 */
export const matchReasonText = (reasonCode?: string | null): string | null => {
  switch ((reasonCode ?? '').trim().toUpperCase()) {
    case 'SENDER_EMAIL_EXACT': return "the sender's email address is on file for this client";
    case 'SENDER_DOMAIN': return "the sender's email domain belongs to this client";
    case 'ERP_ACCOUNT_EXACT': return 'the account reference on the document is this client’s';
    case 'TAX_REG_EXACT': return 'the tax / commercial registration number on the document is this client’s';
    case 'LEARNED_ALIAS': return 'someone previously confirmed this company name for this client';
    case 'LEARNED_PORTAL_ACCOUNT': return 'the portal and vendor code pair was confirmed for this client before';
    case 'NAME_EXACT_UNVERIFIED': return 'the company name on the document matches this client’s name';
    case 'NAME_FUZZY': return 'the company name on the document is a close match';
    case 'RFQ_PATTERN': return 'the RFQ number follows this client’s numbering pattern';
    case 'PRIOR_SENDER': return 'earlier leads from this sender were linked to this client';
    case 'CONTACT_PERSON': return 'the buyer named on the document is a contact at this client';
    case 'AMBIGUOUS': return 'more than one client matches the evidence equally well';
    case 'NO_EVIDENCE': return 'the document carries nothing that identifies the buying company';
    case 'NO_MATCH': return 'nothing on file matches the evidence on this document';
    default: return null;
  }
};

/** "Matched because the sender's email domain belongs to this client." */
export const matchExplanation = (lead: ClientIdentityLike): string | null => {
  const reason = matchReasonText(lead.customerMatchReasonCode);
  if (!reason) return null;
  return `Matched because ${reason}.`;
};

/** Whole-percent confidence, or null when the backend did not send one. */
export const confidencePercent = (confidence?: number | null): number | null => {
  if (confidence == null || !Number.isFinite(confidence)) return null;
  // Tolerate a backend that ever sends 0-100 instead of 0-1.
  const ratio = confidence > 1 ? confidence / 100 : confidence;
  return Math.round(Math.max(0, Math.min(1, ratio)) * 100);
};

/**
 * Addresses Nexora writes to label its own intake paths — a folder drop, a
 * manual upload, a spreadsheet import, the extraction pipeline itself. They are
 * Nexora bookkeeping, never a client, and showing one as "the sender" is how a
 * rep ends up chasing `sec@system.com` instead of the real buyer. Mirrors the
 * synthetic senders the backend resolver refuses to learn from.
 *
 * Note `sec@system.com` is NOT Saudi Electricity Company — SEC is `se.com.sa`.
 */
const SYNTHETIC_SENDER_DOMAINS: readonly string[] = [
  'pipeline.local',
  'system.com',
  'upload.com',
  'excel.upload',
  'rfq.com',
];

/**
 * The sender address only when it is a real one. Returns null for Nexora's own
 * synthetic placeholders and for blanks, so callers can say "no sender on file"
 * rather than presenting bookkeeping as evidence.
 */
export const realSenderAddress = (email?: string | null): string | null => {
  const trimmed = (email ?? '').trim();
  if (!trimmed || !trimmed.includes('@')) return null;
  const domain = trimmed.slice(trimmed.lastIndexOf('@') + 1).toLowerCase();
  if (SYNTHETIC_SENDER_DOMAINS.includes(domain)) return null;
  return trimmed;
};

/** Best available sentence for one candidate row. */
export const candidateExplanation = (candidate: ClientCandidateDTO): string | null => {
  const authored = (candidate.explanation ?? '').trim();
  if (authored) return authored;
  const reason = matchReasonText(candidate.reasonCode);
  return reason ? reason.charAt(0).toUpperCase() + reason.slice(1) : null;
};

export interface ClientCellProps {
  lead: ClientIdentityLike;
  /**
   * Opens the resolve dialog. The dialog is owned by the PAGE, not by the cell:
   * one dialog per grid, not one per row (a row-owned dialog would mount a
   * modal per visible row and trap focus unpredictably).
   */
  onResolve: () => void;
  /** False hides every write affordance but still shows the state and evidence. */
  canEdit?: boolean;
}

/**
 * The `client` column. Invariant asserted by ClientCell.test.tsx: this component
 * NEVER renders an empty cell — an unresolved client is a fact worth showing,
 * and a blank cell reads as "no data loaded".
 */
const ClientCell: React.FC<ClientCellProps> = ({ lead, onResolve, canEdit = true }) => {
  const state = clientIdentityState(lead);

  if (state === 'resolved') {
    const explanation = matchExplanation(lead);
    const name = clientDisplayName(lead);
    return (
      <Tooltip title={explanation ?? clientStatusLabel(lead.customerMatchStatus)}>
        <Typography
          component="span"
          sx={{ fontWeight: 700, fontSize: '0.85rem', color: 'text.primary', lineHeight: 1.3, overflowWrap: 'anywhere' }}
        >
          {name}
        </Typography>
      </Tooltip>
    );
  }

  if (state === 'suggested') {
    const candidates = clientCandidates(lead);
    const top = candidates[0];
    const ambiguous = candidates.length > 1 || normalizedStatus(lead) === 'AMBIGUOUS';
    const chipLabel = ambiguous && candidates.length > 1
      ? `${candidates.length} possible clients`
      : 'Suggested';
    const pct = confidencePercent(top?.confidence ?? lead.customerMatchConfidence);
    const label = top?.customerName?.trim()
      ? `Suggested client ${top.customerName.trim()}${pct != null ? `, ${pct}% confident` : ''}. Open to confirm or change it.`
      : 'Several possible clients. Open to choose one.';

    const body = (
      <>
        <Typography
          component="span"
          sx={{ fontStyle: 'italic', fontSize: '0.85rem', color: 'text.primary', lineHeight: 1.3, overflowWrap: 'anywhere' }}
        >
          {top?.customerName?.trim() || 'Client not confirmed'}
        </Typography>
        <Chip
          size="small"
          label={pct != null && !ambiguous ? `${chipLabel} · ${pct}%` : chipLabel}
          sx={{
            height: 18,
            fontSize: '0.65rem',
            fontWeight: 800,
            color: 'warning.main',
            bgcolor: 'transparent',
            border: '1px solid',
            borderColor: 'warning.main',
          }}
        />
      </>
    );

    const stack = {
      display: 'flex',
      flexDirection: 'column',
      alignItems: 'flex-start',
      gap: 0.25,
      py: 0.25,
      minWidth: 0,
      maxWidth: '100%',
    } as const;

    // A real <button>, not a div with a click handler: the whole cell is the
    // affordance, and it has to be reachable and operable from the keyboard.
    if (!canEdit) {
      return <Box title={label} sx={stack}>{body}</Box>;
    }

    return (
      <ButtonBase
        type="button"
        onClick={onResolve}
        aria-label={label}
        sx={{
          all: 'unset',
          cursor: 'pointer',
          ...stack,
          '&:focus-visible': { outline: '2px solid', outlineColor: 'primary.main', outlineOffset: 2, borderRadius: 1 },
        }}
      >
        {body}
      </ButtonBase>
    );
  }

  return (
    <Stack direction="row" spacing={0.75} sx={{ alignItems: 'center', flexWrap: 'wrap', py: 0.25 }}>
      <Chip
        size="small"
        label="Unknown client"
        variant="outlined"
        sx={{ height: 18, fontSize: '0.65rem', fontWeight: 700, color: 'text.secondary' }}
      />
      {canEdit && (
        <Button
          size="small"
          onClick={onResolve}
          aria-label="Set the client company for this lead"
          sx={{ fontWeight: 800, fontSize: '0.7rem', py: 0, px: 0.5, minWidth: 0, textTransform: 'none' }}
        >
          Set client
        </Button>
      )}
    </Stack>
  );
};

export default ClientCell;
