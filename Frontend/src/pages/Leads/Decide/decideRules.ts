import type {
  FitAssessmentDTO,
  LeadDecisionLineDTO,
  LeadDecisionWorkbenchDTO,
  SaveFitAssessmentRequest,
  SaveParticipationRequest,
} from '../../../api/services/leadDecisionService';
import type { LifecycleState, LifecycleTransitionOption } from '../../../api/services/commercialLifecycleService';
import { parseDateSafe } from '../../../utils/dates';
import { TERMINAL_BLOCKERS, type DecisionMap, type EditableLineDecision } from '../Workbench/workbenchRules';

export { TERMINAL_BLOCKERS };

/**
 * The rules behind the one-screen lead decision.
 *
 * Everything here is pure so the page can stay a thin renderer and the order of the "next
 * thing" sentence can be pinned by tests. The server is still the authority: these rules mirror
 * its refusals so the page never advertises a button the API would reject, and never prints a
 * list where one sentence will do.
 */

/** The five governed fit criteria, in the order the server names them. */
export const DEFAULT_CRITERION_CODES = ['ELIGIBILITY', 'CAPABILITY', 'DELIVERY', 'COMPLIANCE', 'COMMERCIAL'] as const;

/** How a salesperson would say each criterion, not how the policy names it. */
export const CONCERN_LABELS: Record<string, string> = {
  ELIGIBILITY: 'We may not be eligible to bid',
  CAPABILITY: 'We may not be able to supply this',
  DELIVERY: 'We may miss the delivery date',
  COMPLIANCE: 'Approval or certification risk',
  COMMERCIAL: 'Payment, margin or currency risk',
};

/** A fresh idempotency suffix; falls back where the Web Crypto UUID helper is missing. */
export const newId = (): string =>
  globalThis.crypto?.randomUUID?.() ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;

/** Recorded as the rationale when nobody raised a concern: a human still clicked. */
export const NO_CONCERN_RATIONALE = 'No concerns raised at review.';

export interface ConcernState {
  raised: boolean;
  codes: string[];
  note: string;
}

export const NO_CONCERN: ConcernState = { raised: false, codes: [], note: '' };

export const criterionCodes = (fit?: FitAssessmentDTO | null): string[] =>
  fit && fit.criteria.length > 0 ? fit.criteria.map((criterion) => criterion.code) : [...DEFAULT_CRITERION_CODES];

/** Rebuilds the concern controls from a fit assessment somebody already saved. */
export const concernFromSaved = (fit?: FitAssessmentDTO | null): ConcernState => {
  if (!fit || fit.version <= 0) return NO_CONCERN;
  const concerns = fit.criteria.filter((criterion) => criterion.decision === 'CONCERN');
  if (concerns.length === 0) return NO_CONCERN;
  const note = concerns.find((criterion) => criterion.note?.trim())?.note?.trim() ?? fit.rationale ?? '';
  return { raised: true, codes: concerns.map((criterion) => criterion.code), note };
};

export const buildFitRequest = (
  workbench: Pick<LeadDecisionWorkbenchDTO, 'leadRevisionId' | 'decisionVersion' | 'fitAssessment'>,
  concern: ConcernState,
  codes: string[],
): SaveFitAssessmentRequest => {
  const flagged = new Set(concern.raised ? concern.codes : []);
  const note = concern.note.trim();
  return {
    expectedLeadRevisionId: workbench.leadRevisionId,
    expectedDecisionVersion: workbench.decisionVersion,
    expectedFitVersion: workbench.fitAssessment && workbench.fitAssessment.version > 0
      ? workbench.fitAssessment.version
      : undefined,
    overallDecision: concern.raised ? 'CONDITIONAL' : 'FIT',
    rationale: concern.raised ? note : NO_CONCERN_RATIONALE,
    criteria: codes.map((code) => ({
      code,
      decision: flagged.has(code) ? 'CONCERN' : 'PASS',
      note: flagged.has(code) ? note : undefined,
    })),
  };
};

/**
 * True when the saved assessment already says what the controls say, so a second save would
 * only mint a new version and force participation to be recommitted for nothing. A saved
 * NOT_FIT verdict never matches "no concerns": the server treats it as non-actionable whatever
 * its criteria say, so it must be re-recorded as the rep now sees it.
 */
export const fitMatchesSaved = (fit: FitAssessmentDTO | null | undefined, concern: ConcernState, codes: string[]): boolean => {
  if (!fit || fit.version <= 0) return false;
  const byCode = new Map(fit.criteria.map((criterion) => [criterion.code, criterion]));
  if (!codes.every((code) => byCode.has(code))) return false;
  if (!concern.raised) {
    return fit.overallDecision !== 'NOT_FIT'
      && fit.criteria.every((criterion) => criterion.decision === 'PASS' || criterion.decision === 'NOT_APPLICABLE');
  }
  const saved = new Set(fit.criteria.filter((criterion) => criterion.decision === 'CONCERN').map((criterion) => criterion.code));
  const wanted = new Set(concern.codes);
  if (saved.size !== wanted.size || [...wanted].some((code) => !saved.has(code))) return false;
  return (fit.rationale ?? '').trim() === concern.note.trim();
};

export const buildParticipationRequest = (
  workbench: Pick<LeadDecisionWorkbenchDTO, 'leadRevisionId' | 'decisionVersion' | 'participationVersion' | 'lines'>,
  decisions: DecisionMap,
  commit: boolean,
  header?: { reasonCode?: string; notes?: string },
): SaveParticipationRequest => ({
  expectedLeadRevisionId: workbench.leadRevisionId,
  expectedDecisionVersion: workbench.decisionVersion,
  expectedParticipationVersion: workbench.participationVersion,
  commit,
  reasonCode: header?.reasonCode,
  notes: header?.notes,
  lines: workbench.lines.map((line) => {
    const decision = decisions[line.revisionLineId];
    return {
      revisionLineId: line.revisionLineId,
      decision: decision?.decision ?? 'Pending',
      reasonCode: decision?.reasonCode,
      note: decision?.note,
      productId: decision?.productId,
      quantity: decision?.quantity,
      unitOfMeasure: decision?.unitOfMeasure,
      currency: decision?.currency,
    };
  }),
});

/**
 * One canonical shape for what the rep has entered, so two objects that mean the same thing
 * serialise the same way. The unsaved-work guard compares JSON strings, and the decision map is
 * rebuilt from the server after every save with keys in a different order.
 */
export const normalizeDecisions = (decisions: DecisionMap): DecisionMap => {
  const normalized: DecisionMap = {};
  for (const key of Object.keys(decisions).map(Number).sort((a, b) => a - b)) {
    const value = decisions[key];
    if (!value) continue;
    normalized[key] = {
      decision: value.decision,
      ...(value.reasonCode ? { reasonCode: value.reasonCode } : {}),
      ...(value.note?.trim() ? { note: value.note.trim() } : {}),
      ...(value.productId ? { productId: value.productId } : {}),
      ...(value.quantity != null && Number.isFinite(value.quantity) ? { quantity: value.quantity } : {}),
      ...(value.unitOfMeasure ? { unitOfMeasure: value.unitOfMeasure } : {}),
      ...(value.currency ? { currency: value.currency } : {}),
    };
  }
  return normalized;
};

export const normalizeConcern = (concern: ConcernState): ConcernState => (concern.raised
  ? { raised: true, codes: [...concern.codes].sort(), note: concern.note.trim() }
  : NO_CONCERN);

/** How a line names itself to a person. */
export const lineLabel = (line: Pick<LeadDecisionLineDTO, 'lineItemNo' | 'id'>): string =>
  line.lineItemNo?.trim() || String(line.id);

export const lineTitle = (line: LeadDecisionLineDTO): string =>
  line.productName?.trim() || line.description?.trim() || line.sourceText?.trim() || `Line ${lineLabel(line)}`;

export type LineNeedKind =
  | 'choice' | 'clarify' | 'quantity' | 'unit' | 'currency'
  | 'unit-unconfigured' | 'currency-unconfigured'
  | 'source' | 'missing-source' | 'attention' | 'reason';

export interface LineNeed {
  kind: LineNeedKind;
  line: LeadDecisionLineDTO;
}

const validCode = (value: string | undefined, allowed: Set<string>): boolean =>
  Boolean(value?.trim()) && allowed.has(value!.trim().toUpperCase());

/**
 * What one line still needs before it can be part of a committed decision, in fix order. The
 * server requires an active tenant unit and currency on every Bid line; a tenant that has
 * configured none cannot pass, and the sentence must say so rather than let the commit fail.
 */
export const lineNeeds = (
  line: LeadDecisionLineDTO,
  decision: EditableLineDecision | undefined,
  unitCodes: Set<string>,
  currencyCodes: Set<string>,
): LineNeed[] => {
  const choice = decision?.decision ?? 'Pending';
  if (choice === 'Pending') return [{ kind: 'choice', line }];
  if (choice === 'Clarify') return [{ kind: 'clarify', line }];
  const needs: LineNeed[] = [];
  if (choice === 'Bid') {
    if (!decision?.quantity || !Number.isFinite(decision.quantity) || decision.quantity <= 0) needs.push({ kind: 'quantity', line });
    if (unitCodes.size === 0) needs.push({ kind: 'unit-unconfigured', line });
    else if (!validCode(decision?.unitOfMeasure, unitCodes)) needs.push({ kind: 'unit', line });
    if (currencyCodes.size === 0) needs.push({ kind: 'currency-unconfigured', line });
    else if (!validCode(decision?.currency, currencyCodes)) needs.push({ kind: 'currency', line });
    if (line.verificationStatus === 'MISSING_SOURCE') needs.push({ kind: 'missing-source', line });
    else if (line.verificationStatus !== 'VERIFIED') needs.push({ kind: 'source', line });
    if (line.needsAttention && (decision?.note?.trim().length ?? 0) < 5) needs.push({ kind: 'attention', line });
  } else if (!decision?.reasonCode?.trim()) {
    needs.push({ kind: 'reason', line });
  }
  return needs;
};

export interface NextAction {
  label: string;
  path: string;
  /** Set when the page can satisfy the action in place instead of navigating. */
  intent?: 'check-document';
}

export const needSentence = (need: LineNeed, leadId: number): { sentence: string; action?: NextAction } => {
  const label = lineLabel(need.line);
  switch (need.kind) {
    case 'choice': return { sentence: `Choose Quote or Skip for line ${label}.` };
    case 'clarify': return { sentence: `Line ${label} is waiting on the customer. Choose Quote or Skip once they answer.` };
    case 'quantity': return { sentence: `Enter the quantity for line ${label}.` };
    case 'unit': return { sentence: `Choose the unit for line ${label}.` };
    case 'currency': return { sentence: `Choose the currency for line ${label}.` };
    case 'unit-unconfigured': return {
      sentence: 'Your organisation has no units of measure set up, so nothing can be quoted yet. Ask an administrator to add them under Setup.',
    };
    case 'currency-unconfigured': return {
      sentence: 'Your organisation has no currencies set up, so nothing can be quoted yet. Ask an administrator to add them under Setup.',
    };
    case 'source': return {
      sentence: `Check what Nexora read for line ${label} against the document.`,
      action: { label: 'Check the document', path: `/procurement/extraction/review/${leadId}`, intent: 'check-document' },
    };
    case 'missing-source': return { sentence: `Line ${label} has no source document on file, so it cannot be quoted. Skip it, or upload the document again.` };
    case 'attention': return { sentence: `Say how you handled the catalogue warning on line ${label}.` };
    case 'reason': return { sentence: `Say why line ${label} is skipped.` };
    default: return { sentence: `Finish line ${label}.` };
  }
};

/** Blocker codes the page resolves itself, so the server's wording must not be shown as a wall. */
const SELF_RESOLVED_BLOCKERS = new Set([
  'FIT_REQUIRED',
  'FIT_NOT_ACTIONABLE',
  'PARTICIPATION_REQUIRED',
  'PARTICIPATION_STALE',
  'PARTICIPATION_DRAFT',
  'PARTICIPATION_UNRESOLVED',
  'SOURCE_CRITICAL_FIELDS_UNVERIFIED',
  'SOURCE_UNAVAILABLE',
]);

/** The one eligibility refusal the page clears itself, by qualifying the lead on the way. */
const NOT_QUALIFIED_MESSAGE = /only a qualified lead/i;

export type NextThing =
  | { kind: 'ready' }
  | { kind: 'decline' }
  | { kind: 'concern' }
  | { kind: 'blocked'; sentence: string; action?: NextAction }
  | { kind: 'closed'; sentence: string };

export interface NextThingInput {
  workbench: LeadDecisionWorkbenchDTO;
  decisions: DecisionMap;
  concern: ConcernState;
  lifecycle?: LifecycleState | null;
  leadId: number;
}

export const QUALIFIED = 'QUALIFIED';

/** The lifecycle transition that qualifies the lead, when one is needed and allowed. */
export const qualificationTransition = (lifecycle?: LifecycleState | null): LifecycleTransitionOption | null => {
  if (!lifecycle || lifecycle.currentStatusCode === QUALIFIED) return null;
  return lifecycle.allowedTransitions.find((option) => option.statusCode === QUALIFIED) ?? null;
};

/** Whether promotion will need the lead qualified first, and whether the server allows that. */
export const qualificationStep = (lifecycle?: LifecycleState | null): 'none' | 'transition' | 'impossible' => {
  if (!lifecycle) return 'none';
  if (lifecycle.currentStatusCode === QUALIFIED) return 'none';
  return qualificationTransition(lifecycle) ? 'transition' : 'impossible';
};

/**
 * The single next thing, in the order the server would refuse them. Never a list: a rep who
 * reads five bullets fixes none, a rep who reads one sentence fixes that one and gets the next.
 */
export const nextThing = ({ workbench, decisions, concern, lifecycle, leadId }: NextThingInput): NextThing => {
  if (!workbench.customerId) {
    return { kind: 'blocked', sentence: 'Choose the customer this request came from.' };
  }
  const closed = lifecycle && qualificationStep(lifecycle) === 'impossible';
  if (closed) {
    return {
      kind: 'closed',
      sentence: `This request is ${lifecycle.currentStatusCode.toLowerCase().replaceAll('_', ' ')}. Reopen it from the lead page before deciding.`,
    };
  }
  const unitCodes = new Set((workbench.unitOptions ?? []).map((option) => option.code.toUpperCase()));
  const currencyCodes = new Set((workbench.currencyOptions ?? []).map((option) => option.code.toUpperCase()));

  const undecided = workbench.lines
    .map((line) => lineNeeds(line, decisions[line.revisionLineId], unitCodes, currencyCodes))
    .flat()
    .find((need) => need.kind === 'choice' || need.kind === 'clarify');
  if (undecided) return { kind: 'blocked', ...needSentence(undecided, leadId) };

  const choices = workbench.lines.map((line) => decisions[line.revisionLineId]?.decision);
  const allSkipped = workbench.lines.length > 0 && choices.every((choice) => choice === 'NoBid');

  for (const line of workbench.lines) {
    const [need] = lineNeeds(line, decisions[line.revisionLineId], unitCodes, currencyCodes);
    if (need) return { kind: 'blocked', ...needSentence(need, leadId) };
  }

  if (concern.raised) {
    if (concern.codes.length === 0) return { kind: 'blocked', sentence: 'Tick which part concerns you.' };
    if (concern.note.trim().length < 5) return { kind: 'blocked', sentence: 'Say what the concern is, in a few words.' };
  }

  if (allSkipped) return { kind: 'decline' };
  if (concern.raised) return { kind: 'concern' };

  // Eligibility is one server code for several refusals. Not-yet-qualified is cleared by the
  // button itself; a duplicate flag or unapproved facts are not, and must be said in the
  // server's words with the way out.
  const foreign = workbench.blockers.find((blocker) => {
    if (SELF_RESOLVED_BLOCKERS.has(blocker.code) || TERMINAL_BLOCKERS.has(blocker.code)) return false;
    if (blocker.code === 'LEAD_NOT_ELIGIBLE' && NOT_QUALIFIED_MESSAGE.test(blocker.message)) return false;
    return true;
  });
  if (foreign) {
    return {
      kind: 'blocked',
      sentence: foreign.message,
      action: foreign.code === 'LEAD_NOT_ELIGIBLE'
        ? { label: 'Open the lead', path: `/procurement/leads/view/${leadId}` }
        : foreign.actionLabel && foreign.actionPath?.startsWith('/')
          ? { label: foreign.actionLabel, path: foreign.actionPath }
          : undefined,
    };
  }
  return { kind: 'ready' };
};

/** Whole days until a bid closes, negative when it has passed, null when there is no real date. */
export const daysUntil = (iso: string | null | undefined, now: Date = new Date()): number | null => {
  const due = parseDateSafe(iso);
  if (!due) return null;
  return Math.ceil((due.getTime() - now.getTime()) / 86_400_000);
};

export const dueSentence = (days: number | null): string => {
  if (days == null) return 'No deadline stated';
  if (days < 0) return `Closed ${-days} day${days === -1 ? '' : 's'} ago`;
  if (days === 0) return 'Due today';
  return `${days} day${days === 1 ? '' : 's'} left`;
};
