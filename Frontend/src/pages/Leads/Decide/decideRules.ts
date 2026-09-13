import type {
  FitAssessmentDTO,
  LeadDecisionLineDTO,
  LeadDecisionWorkbenchDTO,
  PromotionReceiptDTO,
  SaveFitAssessmentRequest,
  SaveParticipationRequest,
} from '../../../api/services/leadDecisionService';
import type { LifecycleState, LifecycleTransitionOption } from '../../../api/services/commercialLifecycleService';
import { formatDateSafe, parseDateSafe } from '../../../utils/dates';
import { leadStatusWords } from '../../../utils/leadStatusWords';
import {
  countDecisions,
  decisionRecordIsLocked,
  TERMINAL_BLOCKERS,
  type DecisionMap,
  type EditableLineDecision,
} from '../Workbench/workbenchRules';
import { readUnit, tenantUnitCode, type UnitOption, type UnitReading } from './unitRules';

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
  /** What the request said about the unit, on the needs whose sentence depends on it. */
  unit?: UnitReading;
}

const validCode = (value: string | undefined, allowed: ReadonlySet<string>): boolean =>
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
    const unit = readUnit(line, unitCodes);
    if (!decision?.quantity || !Number.isFinite(decision.quantity) || decision.quantity <= 0) needs.push({ kind: 'quantity', line });
    if (unitCodes.size === 0) needs.push({ kind: 'unit-unconfigured', line });
    else if (!validCode(decision?.unitOfMeasure, unitCodes)) needs.push({ kind: 'unit', line, unit });
    if (currencyCodes.size === 0) needs.push({ kind: 'currency-unconfigured', line });
    else if (!validCode(decision?.currency, currencyCodes)) needs.push({ kind: 'currency', line });
    if (line.verificationStatus === 'MISSING_SOURCE') needs.push({ kind: 'missing-source', line });
    else if (line.verificationStatus !== 'VERIFIED') needs.push({ kind: 'source', line, unit });
    if (line.needsAttention && (decision?.note?.trim().length ?? 0) < 5) needs.push({ kind: 'attention', line });
  } else if (!decision?.reasonCode?.trim()) {
    needs.push({ kind: 'reason', line });
  }
  return needs;
};

export interface NextAction {
  label: string;
  path: string;
  /**
   * Set when the page can satisfy the action in place instead of navigating: open the document
   * check, or take the rep to the unit picker (one line's, or the one for every line).
   */
  intent?: 'check-document' | 'choose-unit';
  /** The line a `choose-unit` action goes to; absent for the picker that sets every line. */
  lineId?: number;
}

export const needSentence = (need: LineNeed, leadId: number): { sentence: string; action?: NextAction } => {
  const label = lineLabel(need.line);
  switch (need.kind) {
    case 'choice': return { sentence: `Choose Quote or Skip for line ${label}.` };
    case 'clarify': return { sentence: `Line ${label} is waiting on the customer. Choose Quote or Skip once they answer.` };
    case 'quantity': return { sentence: `Enter the quantity for line ${label}.` };
    case 'unit': {
      // The sentence says what the request did say, so the rep knows why the picker is empty,
      // and the button takes them to it. Nothing is chosen for them.
      const action: NextAction = {
        label: 'Choose the unit',
        path: `/procurement/leads/${leadId}/workbench`,
        intent: 'choose-unit',
        lineId: need.line.revisionLineId,
      };
      if (need.unit?.kind === 'absent') return { sentence: `The request gives no unit for line ${label}. Choose it beside the quantity.`, action };
      if (need.unit?.kind === 'unrecognised') return { sentence: `Line ${label} says "${need.unit.asWritten}". Choose the unit you'll quote it in.`, action };
      return { sentence: `Choose the unit for line ${label}.`, action };
    }
    case 'currency': return { sentence: `Choose the currency for line ${label}.` };
    case 'unit-unconfigured': return {
      sentence: 'Your organisation has no units of measure set up, so nothing can be quoted yet. Ask an administrator to add them under Setup.',
    };
    case 'currency-unconfigured': return {
      sentence: 'Your organisation has no currencies set up, so nothing can be quoted yet. Ask an administrator to add them under Setup.',
    };
    case 'source': return {
      // A unit the rep chose here is only proof once a person confirms it against the document;
      // the check carries the chosen unit, so it is confirmed there, not typed again.
      sentence: need.unit && need.unit.kind !== 'mapped'
        ? `Check line ${label} against the document and confirm it. The unit you chose goes with it.`
        : `Check what Nexora read for line ${label} against the document.`,
      action: { label: 'Check the document', path: `/procurement/extraction/review/${leadId}`, intent: 'check-document' },
    };
    case 'missing-source': return { sentence: `Line ${label} has no source document on file, so it cannot be quoted. Skip it, or upload the document again.` };
    case 'attention': return { sentence: `Say how you handled the ${isCatalogueWarning(need.line) ? 'catalogue warning' : 'warning'} on line ${label}.` };
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

/**
 * Where the request stands. The first five kinds are finished or stopped records the page only
 * reports; the last four are an open request and what it needs.
 */
export type NextThing =
  | { kind: 'rfq'; rfqId: number; rfqRef: string; changed: boolean }
  | { kind: 'legacy'; path?: string }
  | { kind: 'inconsistent' }
  | { kind: 'declined' }
  | { kind: 'closed'; statusCode: string; canReopen: boolean }
  | { kind: 'ready' }
  | { kind: 'decline' }
  | { kind: 'concern' }
  | { kind: 'blocked'; sentence: string; action?: NextAction };

/** The kinds an open request can be in: the ones the decision section and its button serve. */
export const isOpenKind = (next: NextThing): next is Extract<NextThing, { kind: 'ready' | 'decline' | 'concern' | 'blocked' }> =>
  next.kind === 'ready' || next.kind === 'decline' || next.kind === 'concern' || next.kind === 'blocked';

/** How an RFQ names itself: its number, or its id when it has none yet. */
export const rfqRefOf = (promotion: { rfqNumber?: string | null; rfqId: number }): string =>
  promotion.rfqNumber?.trim() || `#${promotion.rfqId}`;

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
/**
 * From this many unverified quoted lines on, the next step is one approval of the extraction,
 * not a document check per line. A 1,500-line bid list read from a table is certified by
 * approving it once in Documents to check; asking for line 8, then line 9, then line 10 is how
 * the rep gives up.
 */
export const MANY_LINES_TO_CHECK = 6;

/**
 * The acknowledgement "Quote all" writes on a line that carries a catalogue warning. The
 * commit refuses a warned line without a meaningful note, and a rep who quotes the whole
 * request has decided exactly this; the words are on the line, and can be changed there.
 */
export const QUOTED_AS_READ_NOTE = 'Quoted as read; no catalogue match yet, sourcing will resolve it.';

/** The server's warning reasons for a line (LeadConversionIntelligence joins them with "; "). */
export const warningReasons = (line: Pick<LeadDecisionLineDTO, 'attentionReason'>): string[] =>
  (line.attentionReason ?? '').split(';').map((reason) => reason.trim()).filter(Boolean);

const UNIT_MISSING_REASON = /^unit of measure missing$/i;
const UNIT_REFUSED_REASON = /^unit of measure "(.+?)" needs review/i;
const QUANTITY_MISSING_REASON = /^quantity missing$/i;

/**
 * True when the warning is about the catalogue (or says nothing more specific). A missing or
 * refused unit is a unit problem; calling it a catalogue warning, and writing "no catalogue match
 * yet" into the decision record for it, told the audit trail something that did not happen.
 */
export const isCatalogueWarning = (line: Pick<LeadDecisionLineDTO, 'attentionReason'>): boolean => {
  const reasons = warningReasons(line);
  return reasons.length === 0 || reasons.some((reason) =>
    !UNIT_MISSING_REASON.test(reason) && !UNIT_REFUSED_REASON.test(reason) && !QUANTITY_MISSING_REASON.test(reason));
};

/**
 * The acknowledgement a quoted, warned line carries, written from what actually happened on the
 * line: the catalogue sentence for a catalogue warning, and for a unit the request did not give
 * (or gave as a word Nexora would not quote in) the unit the rep chose. Editable on the line.
 */
export const acknowledgementFor = (
  line: Pick<LeadDecisionLineDTO, 'attentionReason'>,
  decision?: Pick<EditableLineDecision, 'unitOfMeasure' | 'quantity'>,
): string => {
  const unit = decision?.unitOfMeasure?.trim();
  const parts: string[] = [];
  for (const reason of warningReasons(line)) {
    const refused = UNIT_REFUSED_REASON.exec(reason);
    if (UNIT_MISSING_REASON.test(reason)) {
      parts.push(unit ? `The request gave no unit; quoted in ${unit}.` : 'The request gave no unit; the unit is chosen on the line.');
    } else if (refused) {
      parts.push(unit ? `The request said "${refused[1]}"; quoted in ${unit}.` : `The request said "${refused[1]}"; the unit is chosen on the line.`);
    } else if (QUANTITY_MISSING_REASON.test(reason)) {
      parts.push(decision?.quantity ? `The request gave no quantity; quoted for ${decision.quantity}.` : 'The request gave no quantity; the quantity is entered on the line.');
    }
  }
  return (isCatalogueWarning(line) ? [QUOTED_AS_READ_NOTE, ...parts] : parts).join(' ');
};

/**
 * Keeps a warned, quoted line's acknowledgement true as the line changes. Quoting writes it when
 * the rep has not written one; choosing a unit or quantity afterwards rewrites it only while it
 * is still the one Nexora wrote. A note the rep typed is never touched.
 */
export const withAcknowledgement = (
  line: Pick<LeadDecisionLineDTO, 'attentionReason' | 'needsAttention'>,
  before: EditableLineDecision | undefined,
  after: EditableLineDecision,
  quoting: boolean,
): EditableLineDecision => {
  if (after.decision !== 'Bid' || !line.needsAttention) return after;
  const note = before?.note?.trim() ?? '';
  const writtenByNexora = note !== '' && note === acknowledgementFor(line, before);
  if ((quoting && (note.length < 5 || writtenByNexora)) || (!quoting && writtenByNexora)) {
    return { ...after, note: acknowledgementFor(line, after) };
  }
  return after;
};

/**
 * A name per line that survives the new line ids a document check mints: its line number, with a
 * counter when a messy document repeats one.
 */
export const lineKeys = (lines: Array<Pick<LeadDecisionLineDTO, 'lineItemNo' | 'id' | 'revisionLineId'>>): Map<number, string> => {
  const seen = new Map<string, number>();
  const keys = new Map<number, string>();
  for (const line of lines) {
    const label = lineLabel(line);
    const count = (seen.get(label) ?? 0) + 1;
    seen.set(label, count);
    keys.set(line.revisionLineId, count === 1 ? label : `${label}#${count}`);
  }
  return keys;
};

/** The rep's choices keyed by line number instead of by this revision's line ids. */
export const decisionsByLineKey = (
  lines: Array<Pick<LeadDecisionLineDTO, 'lineItemNo' | 'id' | 'revisionLineId'>>,
  decisions: DecisionMap,
): Record<string, EditableLineDecision> => {
  const normalized = normalizeDecisions(decisions);
  const keys = lineKeys(lines);
  const byKey: Record<string, EditableLineDecision> = {};
  for (const line of lines) {
    const value = normalized[line.revisionLineId];
    if (value) byKey[keys.get(line.revisionLineId)!] = value;
  }
  return byKey;
};

/** The inverse of {@link decisionsByLineKey}, onto the lines now on screen; unknown keys are ignored. */
export const decisionsFromLineKeys = (
  lines: Array<Pick<LeadDecisionLineDTO, 'lineItemNo' | 'id' | 'revisionLineId'>>,
  byKey: Record<string, EditableLineDecision>,
  onto: DecisionMap,
): DecisionMap => {
  const keys = lineKeys(lines);
  const next: DecisionMap = { ...onto };
  for (const line of lines) {
    const value = byKey[keys.get(line.revisionLineId)!];
    if (value) next[line.revisionLineId] = value;
  }
  return next;
};

/**
 * Carries what a person chose onto a NEW revision of the same request. Quote/Skip, the reason and
 * the note carry onto a line the server has no choice for yet. A quantity, unit or currency the
 * rep picked carries onto a line whose new value is still missing or not one of the tenant's —
 * so a unit chosen for a line the customer gave none is not wiped by the document check — while
 * a real value on the new revision (a person just corrected it) wins. Nothing is invented.
 */
export const carryChoices = (
  lines: Array<Pick<LeadDecisionLineDTO, 'lineItemNo' | 'id' | 'revisionLineId'>>,
  initial: DecisionMap,
  carried: ReadonlyMap<string, EditableLineDecision>,
  unitCodes: ReadonlySet<string>,
  currencyCodes: ReadonlySet<string>,
): DecisionMap => {
  const keys = lineKeys(lines);
  const next: DecisionMap = { ...initial };
  for (const line of lines) {
    const from = carried.get(keys.get(line.revisionLineId)!);
    const base = next[line.revisionLineId];
    if (!from || !base) continue;
    const merged: EditableLineDecision = base.decision === 'Pending'
      ? {
          ...base,
          decision: from.decision,
          ...(from.reasonCode ? { reasonCode: from.reasonCode } : {}),
          ...(from.note ? { note: from.note } : {}),
        }
      : { ...base };
    const validQuantity = (value?: number) => value != null && Number.isFinite(value) && value > 0;
    if (!validQuantity(base.quantity) && validQuantity(from.quantity)) merged.quantity = from.quantity;
    if (!validCode(base.unitOfMeasure, unitCodes) && validCode(from.unitOfMeasure, unitCodes)) merged.unitOfMeasure = from.unitOfMeasure;
    if (!validCode(base.currency, currencyCodes) && validCode(from.currency, currencyCodes)) merged.currency = from.currency;
    next[line.revisionLineId] = merged;
  }
  return next;
};

/**
 * Only a unit the tenant quotes in is pre-selected, spelled the tenant's way. A word Nexora kept
 * as written (Roll, Pack, or EA for a tenant without it) stays on screen as "as written", not as
 * a picker value that renders blank; the picker then asks. Nothing is put in its place.
 */
export const keepTenantUnits = (decisions: DecisionMap, options: UnitOption[]): DecisionMap => {
  const next: DecisionMap = {};
  for (const [id, value] of Object.entries(decisions)) {
    const code = tenantUnitCode(value.unitOfMeasure, options);
    const rest = { ...value };
    delete rest.unitOfMeasure;
    next[Number(id)] = code ? { ...rest, unitOfMeasure: code } : rest;
  }
  return next;
};

/**
 * Sets one unit, chosen by the rep, on every quoted line that has no unit the tenant quotes in.
 * A line that already has one keeps it.
 */
export const applyUnitToUnitless = (
  lines: Array<Pick<LeadDecisionLineDTO, 'revisionLineId' | 'attentionReason' | 'needsAttention'>>,
  decisions: DecisionMap,
  code: string,
  unitCodes: ReadonlySet<string>,
): DecisionMap => {
  const next: DecisionMap = { ...decisions };
  for (const line of lines) {
    const existing = decisions[line.revisionLineId];
    if (existing?.decision !== 'Bid' || validCode(existing.unitOfMeasure, unitCodes)) continue;
    next[line.revisionLineId] = withAcknowledgement(line, existing, { ...existing, unitOfMeasure: code }, false);
  }
  return next;
};

export const nextThing = ({ workbench, decisions, concern, lifecycle, leadId }: NextThingInput): NextThing => {
  // What the decision record itself says comes first and never waits for the status read: a
  // request that became an RFQ is that, whatever the lifecycle read says or whether it came back.
  if (workbench.promotion) {
    return {
      kind: 'rfq',
      rfqId: workbench.promotion.rfqId,
      rfqRef: rfqRefOf(workbench.promotion),
      changed: workbench.blockers.some((blocker) => blocker.code === 'RFQ_REVISION_REQUIRED'),
    };
  }
  const legacy = workbench.blockers.find((blocker) => blocker.code === 'LEGACY_RFQ');
  if (legacy) return { kind: 'legacy', ...(legacy.actionPath ? { path: legacy.actionPath } : {}) };
  if (workbench.blockers.some((blocker) => blocker.code === 'INCONSISTENT_CONVERTED_STATE')) return { kind: 'inconsistent' };
  const counts = countDecisions(decisions);
  if (decisionRecordIsLocked(workbench, decisions) && counts.total > 0 && counts.noBid === counts.total) return { kind: 'declined' };

  if (lifecycle && qualificationStep(lifecycle) === 'impossible') {
    return { kind: 'closed', statusCode: lifecycle.currentStatusCode, canReopen: lifecycle.canReopen === true };
  }
  if (!workbench.customerId) {
    return { kind: 'blocked', sentence: 'Choose the customer this request came from.' };
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

  // Units before the document check. A tenant with no units cannot quote anything, and saying so
  // after sending the rep off to approve the extraction wasted the trip.
  const quotedLines = workbench.lines.filter((line) => decisions[line.revisionLineId]?.decision === 'Bid');
  if (quotedLines.length > 0 && unitCodes.size === 0) {
    return { kind: 'blocked', ...needSentence({ kind: 'unit-unconfigured', line: quotedLines[0] }, leadId) };
  }
  // A bid list with no unit column leaves every line without one. One sentence and one picker
  // for all of them, before the certification step, so the rep is told units are what is missing.
  const unitless = quotedLines.filter((line) => !validCode(decisions[line.revisionLineId]?.unitOfMeasure, unitCodes));
  if (unitless.length >= 2) {
    return {
      kind: 'blocked',
      sentence: `${unitless.length} lines marked to quote need a unit. Choose one for all ${unitless.length} above the lines, or line by line.`,
      action: { label: 'Choose the unit', path: `/procurement/leads/${leadId}/workbench`, intent: 'choose-unit' },
    };
  }

  const unverifiedQuoted = workbench.lines.filter((line) =>
    decisions[line.revisionLineId]?.decision === 'Bid'
    && lineNeeds(line, decisions[line.revisionLineId], unitCodes, currencyCodes).some((need) => need.kind === 'source'));
  if (unverifiedQuoted.length >= MANY_LINES_TO_CHECK) {
    // Units the rep chose on this screen exist nowhere else: approving the extraction on its own
    // page would approve the lines without them, and then no approval could ever cover them. The
    // check on this screen carries the chosen units into the approval.
    if (unverifiedQuoted.some((line) => readUnit(line, unitCodes).kind !== 'mapped')) {
      return {
        kind: 'blocked',
        sentence: `Check the ${unverifiedQuoted.length} lines marked to quote against the document and confirm them. The units you chose go with them.`,
        action: { label: 'Check the document', path: `/procurement/extraction/review/${leadId}`, intent: 'check-document' },
      };
    }
    return {
      kind: 'blocked',
      sentence: `Nexora could not certify ${unverifiedQuoted.length} of the lines marked to quote against the document. Approve the extraction once in Documents to check, then come back here.`,
      action: { label: 'Approve the extraction', path: `/procurement/extraction/review/${leadId}` },
    };
  }

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

/** "line" for one, "lines" otherwise. */
export const lineWord = (count: number): string => (count === 1 ? 'line' : 'lines');

export type StepTone = 'info' | 'warning' | 'error' | 'success';

/** The one control a finished or stopped request offers beside its sentence. */
export interface StepAction {
  label: string;
  /** Where the control goes; absent when the page does it in place. */
  path?: string;
  /** Opens the review of a customer's change on this screen. */
  intent?: 'review-change';
}

export interface StepCopy {
  sentence: string;
  tone: StepTone;
  action?: StepAction;
}

export interface StepCopyContext {
  leadId: number;
  /** The viewer may open RFQs. */
  canViewRfq: boolean;
  /** The viewer may record the review of a customer's change after the RFQ was created. */
  canReviewChange: boolean;
  /** The viewer may reopen a closed request on the lead page. */
  mayReopen: boolean;
  currentRevisionNumber: number;
  /** The revision the RFQ was created from, when there is one. */
  promotedRevisionNumber?: number | null;
  /** The tenant's own label for the status, for codes Nexora has no words for. */
  statusLabel?: string | null;
}

/** Closed statuses a manager can bring back, with the words that open their sentence. */
const REOPENABLE_OPENINGS: Readonly<Record<string, string>> = {
  DISQUALIFIED: 'This request was declined.',
  CANCELLED: 'This request was cancelled.',
  LOST: 'This request was lost.',
};

const MOVED_ON = new Set(['QUOTED', 'NEGOTIATION', 'AWARDED', 'PARTIALLY_AWARDED']);

/**
 * The sentence for a request whose status allows no qualification, so no RFQ. It says what the
 * status means in job words and names the way back only where one exists for this viewer: the
 * server reopens a declined, cancelled or lost request (a manager's action on the lead page), and
 * nothing else.
 */
export const closedSentence = (
  statusCode: string,
  { canReopen, mayReopen, fallbackLabel }: { canReopen: boolean; mayReopen: boolean; fallbackLabel?: string | null },
): string => {
  const code = (statusCode ?? '').trim().toUpperCase();
  const words = leadStatusWords(code) ?? (fallbackLabel?.trim() || 'not recorded');
  const opening = REOPENABLE_OPENINGS[code];
  if (opening) {
    if (!canReopen) return `${opening} Nothing more can be decided here.`;
    return mayReopen
      ? `${opening} To work on it again, reopen it on the lead page.`
      : `${opening} Ask a manager to reopen it if the customer still wants a price.`;
  }
  if (code === 'DUPLICATED') return 'This request was marked as a duplicate of another request. Nothing can be decided here; work on the other one.';
  if (code === 'COMPLETED') return 'This request is completed. Nothing more can be decided here.';
  if (MOVED_ON.has(code)) return `This request has already moved on (${words}). Nothing more can be decided here.`;
  if (code === 'CONVERTED_TO_RFQ') return 'This request already became an RFQ. Nothing more can be decided here.';
  return `This request can't be marked qualified from its current status (${words}), so no RFQ can be created. Ask an administrator to check the lead statuses under Setup.`;
};

/**
 * The next-step sentence, tone and control for a request that is finished or stopped. Null for an
 * open request, whose sentence depends on who is deciding and what the lines still need.
 */
export const nextStepCopy = (next: NextThing, ctx: StepCopyContext): StepCopy | null => {
  switch (next.kind) {
    case 'rfq': {
      const openRfq: StepAction = { label: 'Open the RFQ', path: `/procurement/rfqs/view/${next.rfqId}` };
      if (next.changed) {
        if (ctx.canReviewChange) {
          return {
            tone: 'warning',
            sentence: `The customer changed this request after it became RFQ ${next.rfqRef}. Compare revision ${ctx.currentRevisionNumber} with the RFQ, then press Review the change to record what you did.`,
            action: { label: 'Review the change', intent: 'review-change' },
          };
        }
        return {
          tone: 'warning',
          sentence: `The customer changed this request after it became RFQ ${next.rfqRef}. Ask a manager to review the change.`,
          ...(ctx.canViewRfq ? { action: openRfq } : {}),
        };
      }
      if (!ctx.canViewRfq) {
        return { tone: 'success', sentence: `This request became RFQ ${next.rfqRef}. Your role can't open RFQs; ask a manager what happens next.` };
      }
      const fromOlderRevision = ctx.promotedRevisionNumber != null && ctx.promotedRevisionNumber !== ctx.currentRevisionNumber;
      return {
        tone: 'success',
        sentence: fromOlderRevision
          ? `This request became RFQ ${next.rfqRef} from revision ${ctx.promotedRevisionNumber}. The work carries on from the RFQ.`
          : `This request became RFQ ${next.rfqRef}. The work carries on from the RFQ.`,
        action: openRfq,
      };
    }
    case 'legacy':
      if (!ctx.canViewRfq) {
        return { tone: 'info', sentence: "This request already has an RFQ, created before decisions were recorded on this screen. Your role can't open RFQs; ask a manager what happens next." };
      }
      return {
        tone: 'info',
        sentence: 'This request already has an RFQ, created before decisions were recorded on this screen. The work carries on from the RFQ.',
        ...(next.path?.startsWith('/') ? { action: { label: 'Open the RFQ', path: next.path } } : {}),
      };
    case 'inconsistent':
      return { tone: 'error', sentence: 'This request is marked as an RFQ, but no RFQ exists. Ask an administrator to repair it; nothing can be decided here.' };
    case 'declined':
      return { tone: 'info', sentence: 'Every line was skipped and the request was declined. No RFQ was created, and there is nothing more to do here.' };
    case 'closed': {
      const code = next.statusCode.trim().toUpperCase();
      const sentence = closedSentence(code, { canReopen: next.canReopen, mayReopen: ctx.mayReopen, fallbackLabel: ctx.statusLabel });
      const reopenHere = code in REOPENABLE_OPENINGS && next.canReopen && ctx.mayReopen;
      return {
        tone: 'info',
        sentence,
        ...(reopenHere ? { action: { label: 'Open the lead', path: `/procurement/leads/view/${ctx.leadId}` } } : {}),
      };
    }
    default:
      return null;
  }
};

/**
 * The Next step for a record the server has locked but this screen has no finished state to report
 * for: a newer revision after an RFQ whose promotion receipt did not come back, say. There is no
 * decision and no button, so the sentence names only the control beside it.
 */
export const lockedStepCopy = (leadId: number): StepCopy => ({
  tone: 'info',
  sentence: 'Nothing can be decided here right now. Open the lead to see where it stands.',
  action: { label: 'Open the lead', path: `/procurement/leads/view/${leadId}` },
});

/** A promotion receipt with the read-only fields a newer server adds; both are optional. */
/** Who created the RFQ, as a person reads it: their name, "You", or the login they used. */
export const receiptActor = (promotion: PromotionReceiptDTO, viewerEmail?: string | null): string | null => {
  const name = promotion.promotedByName?.trim();
  if (name) return name;
  const login = promotion.promotedBy?.trim();
  const viewer = viewerEmail?.trim();
  if (login && viewer && login.toLowerCase() === viewer.toLowerCase()) return 'You';
  return login || null;
};

/**
 * The receipt under "Became an RFQ": who created it and when, and how many lines went in. The
 * total is the line count of the revision the RFQ came from; the lines on screen are only that
 * revision's when the numbers match, so a newer revision's total is never put beside it.
 */
export const receiptSentence = (
  promotion: PromotionReceiptDTO,
  current: { revisionNumber: number; lineCount: number },
  viewerEmail?: string | null,
): string => {
  const actor = receiptActor(promotion, viewerEmail);
  const date = formatDateSafe(promotion.promotedAtUtc, '');
  const when = date ? ` on ${date}` : '';
  const created = actor ? `${actor} created it${when}.` : `Created${when}.`;
  const went = promotion.promotedLineCount;
  const outOf = promotion.promotedRevisionLineCount
    ?? (promotion.leadRevisionNumber === current.revisionNumber ? current.lineCount : null);
  const lines = outOf != null
    ? `${went} of ${outOf} ${lineWord(outOf)} went into the RFQ.`
    : `${went} ${lineWord(went)} went into the RFQ.`;
  return `${created} ${lines}`;
};

/** What one click of the button had already written when a later step failed. */
export interface WrittenSteps {
  fitSaved: boolean;
  qualifiedNow: boolean;
  /** The line choices this click saved, as a draft or committed. */
  choicesSavedNow: boolean;
  /** The committed choices the record already held, so this click did not write them again. */
  choicesAlreadyCommitted: boolean;
  /** The request was qualified before the click. */
  alreadyQualified: boolean;
}

/**
 * What to tell a person when the button's chain stops half-way. Null when this click wrote
 * nothing, so "Nothing was changed" stays true; otherwise the steps that did go through, and the
 * one control that finishes the job.
 */
export const partialFailureSentence = (
  mode: 'rfq' | 'draft' | 'decline',
  steps: WrittenSteps,
  draftLabel: 'Save for review' | 'Save for a manager',
): string | null => {
  if (!steps.fitSaved && !steps.qualifiedNow && !steps.choicesSavedNow) return null;
  if (mode === 'draft') {
    return steps.choicesSavedNow
      ? "Your choices are saved as a draft, but Nexora couldn't read the request back. Refresh the page to see them."
      : `Your line choices were not saved. Your concern answer is saved. Press ${draftLabel} again.`;
  }
  if (mode === 'decline') {
    return steps.choicesSavedNow
      ? "The request is declined and recorded, but Nexora couldn't read it back. Refresh the page to see it."
      : 'The request was not declined. Your concern answer is saved. Press Decline request again.';
  }
  const qualified = steps.qualifiedNow || steps.alreadyQualified;
  if (steps.choicesSavedNow || steps.choicesAlreadyCommitted) {
    return qualified
      ? 'The RFQ was not created. Your choices are saved and the request is marked qualified. Press Create RFQ again.'
      : 'The RFQ was not created. Your choices are saved. Press Create RFQ again.';
  }
  if (steps.qualifiedNow) {
    return 'The RFQ was not created. The request is marked qualified, but your line choices are not saved. Press Create RFQ again.';
  }
  return steps.alreadyQualified
    ? 'The RFQ was not created. Your concern answer is saved, but your line choices are not saved. Press Create RFQ again.'
    : 'The RFQ was not created. Your concern answer is saved, but the request is not marked qualified and your line choices are not saved. Press Create RFQ again.';
};
