import { describe, expect, it } from 'vitest';
import type { LeadDecisionLineDTO, LeadDecisionWorkbenchDTO } from '../../../api/services/leadDecisionService';
import type { LifecycleState } from '../../../api/services/commercialLifecycleService';
import type { DecisionMap } from '../Workbench/workbenchRules';
import { formatDateSafe } from '../../../utils/dates';
import {
  buildFitRequest,
  closedSentence,
  concernFromSaved,
  daysUntil,
  dueSentence,
  fitMatchesSaved,
  lineNeeds,
  lineWord,
  MANY_LINES_TO_CHECK,
  nextStepCopy,
  nextThing,
  NO_CONCERN,
  NO_CONCERN_RATIONALE,
  normalizeConcern,
  normalizeDecisions,
  partialFailureSentence,
  qualificationStep,
  qualificationTransition,
  acknowledgementFor,
  applyUnitToUnitless,
  carryChoices,
  decisionsByLineKey,
  decisionsFromLineKeys,
  keepTenantUnits,
  lineKeys,
  QUOTED_AS_READ_NOTE,
  receiptActor,
  receiptSentence,
  rfqRefOf,
  withAcknowledgement,
  type StepCopyContext,
} from './decideRules';

const line = (overrides: Partial<LeadDecisionLineDTO> & { id: number }): LeadDecisionLineDTO => ({
  revisionLineId: overrides.id * 10,
  lineItemNo: String(overrides.id).padStart(5, '0'),
  description: `Item ${overrides.id}`,
  quantity: 4,
  unitOfMeasure: 'EA',
  currency: 'SAR',
  verificationStatus: 'VERIFIED',
  ...overrides,
});

const workbench = (overrides: Partial<LeadDecisionWorkbenchDTO> = {}): LeadDecisionWorkbenchDTO => ({
  leadId: 407,
  leadRevisionId: 9001,
  leadRevisionNumber: 1,
  decisionVersion: 3,
  participationVersion: null,
  participationStatus: 'NONE',
  lifecycleStatusCode: 'RECEIVED',
  customerId: 30,
  customerName: 'Saudi Electricity Company',
  hasFrozenCommercialHeader: true,
  verificationStatus: 'VERIFIED',
  evidence: [],
  lines: [line({ id: 1 }), line({ id: 2, currency: null }), line({ id: 3 })],
  reasonCodes: [{ code: 'NO_STOCK', label: 'Item unavailable', appliesTo: ['NoBid'] }],
  unitOptions: [{ code: 'EA', label: 'Each' }],
  currencyOptions: [{ code: 'SAR', label: 'Saudi riyal' }, { code: 'USD', label: 'US dollar' }],
  fitAssessment: {
    version: 0,
    overallDecision: 'CONDITIONAL',
    rationale: '',
    criteria: ['ELIGIBILITY', 'CAPABILITY', 'DELIVERY', 'COMPLIANCE', 'COMMERCIAL']
      .map((code) => ({ code, label: code, decision: 'UNKNOWN' as const })),
  },
  promotion: null,
  blockers: [
    { code: 'FIT_REQUIRED', message: 'Save a human fit assessment for the current revision.' },
    { code: 'PARTICIPATION_REQUIRED', message: 'Save participation choices for every current revision line.' },
    { code: 'LEAD_NOT_ELIGIBLE', message: 'Only a qualified lead can be converted to an RFQ.', actionLabel: 'Open Lead lifecycle', actionPath: '/procurement/leads/view/407' },
  ],
  ...overrides,
});

const lifecycle = (overrides: Partial<LifecycleState> = {}): LifecycleState => ({
  aggregateId: 407,
  currentStatusCode: 'RECEIVED',
  version: 2,
  isTerminal: false,
  allowedTransitions: [{ statusId: 8, statusCode: 'QUALIFIED', label: 'Qualified', requiresReason: false }],
  ...overrides,
});

const quoteAll = (wb: LeadDecisionWorkbenchDTO, patch: Partial<DecisionMap[number]> = {}): DecisionMap =>
  Object.fromEntries(wb.lines.map((item) => [item.revisionLineId, {
    decision: 'Bid' as const,
    quantity: item.quantity ?? undefined,
    unitOfMeasure: item.unitOfMeasure ?? undefined,
    currency: item.currency ?? undefined,
    ...patch,
  }]));

describe('the single next thing', () => {
  it('asks for the customer before anything else', () => {
    const wb = workbench({ customerId: null, customerName: null });
    expect(nextThing({ workbench: wb, decisions: {}, concern: NO_CONCERN, leadId: 407 }))
      .toEqual({ kind: 'blocked', sentence: 'Choose the customer this request came from.' });
  });

  it('names the first undecided line, never a list', () => {
    const wb = workbench();
    const decisions: DecisionMap = { 10: { decision: 'Bid', quantity: 4, unitOfMeasure: 'EA', currency: 'SAR' } };
    const next = nextThing({ workbench: wb, decisions, concern: NO_CONCERN, leadId: 407 });
    expect(next).toMatchObject({ kind: 'blocked', sentence: 'Choose Quote or Skip for line 00002.' });
  });

  it('names the one field a quoted line is missing', () => {
    const wb = workbench();
    const next = nextThing({ workbench: wb, decisions: quoteAll(wb), concern: NO_CONCERN, leadId: 407 });
    expect(next).toMatchObject({ kind: 'blocked', sentence: 'Choose the currency for line 00002.' });
  });

  it('sends an unverified quoted line back to the document with a link', () => {
    const wb = workbench({ lines: [line({ id: 1, verificationStatus: 'NEEDS_CHECK' })] });
    const next = nextThing({ workbench: wb, decisions: quoteAll(wb), concern: NO_CONCERN, leadId: 407 });
    expect(next).toMatchObject({
      kind: 'blocked',
      sentence: 'Check what Nexora read for line 00001 against the document.',
      action: { path: '/procurement/extraction/review/407' },
    });
  });

  it('sends many unverified quoted lines to one approval instead of a check per line', () => {
    expect(MANY_LINES_TO_CHECK).toBeLessThanOrEqual(8);
    const many = workbench({
      lines: Array.from({ length: 8 }, (_item, index) => line({ id: index + 1, currency: 'SAR', verificationStatus: 'NEEDS_CHECK' })),
    });
    // Quoted as the page seeds them: the units the document gave are on the lines. (Lines with no
    // unit are asked for their unit first — see "asks for one unit for every unitless line".)
    const decisions = quoteAll(many);

    const next = nextThing({ workbench: many, decisions, concern: NO_CONCERN, lifecycle: undefined, leadId: 407 });

    expect(next).toMatchObject({
      kind: 'blocked',
      sentence: 'Nexora could not certify 8 of the lines marked to quote against the document. Approve the extraction once in Documents to check, then come back here.',
      action: { label: 'Approve the extraction', path: '/procurement/extraction/review/407' },
    });
  });

  it('asks why a line is skipped', () => {
    const wb = workbench({ lines: [line({ id: 1 })] });
    const next = nextThing({ workbench: wb, decisions: { 10: { decision: 'NoBid' } }, concern: NO_CONCERN, leadId: 407 });
    expect(next).toMatchObject({ kind: 'blocked', sentence: 'Say why line 00001 is skipped.' });
  });

  it('turns every-line-skipped into a decline, not a blocker', () => {
    const wb = workbench({ lines: [line({ id: 1 }), line({ id: 2 })] });
    const decisions: DecisionMap = { 10: { decision: 'NoBid', reasonCode: 'NO_STOCK' }, 20: { decision: 'NoBid', reasonCode: 'NO_STOCK' } };
    expect(nextThing({ workbench: wb, decisions, concern: NO_CONCERN, leadId: 407 })).toEqual({ kind: 'decline' });
  });

  it('is ready once every quoted line is complete, ignoring the blockers the button resolves itself', () => {
    const wb = workbench();
    const decisions = quoteAll(wb, {});
    decisions[20] = { ...decisions[20], currency: 'SAR' };
    expect(nextThing({ workbench: wb, decisions, concern: NO_CONCERN, lifecycle: lifecycle(), leadId: 407 })).toEqual({ kind: 'ready' });
  });

  it('asks which part concerns you, then what the concern is, then stops at the RFQ', () => {
    const wb = workbench();
    const decisions = quoteAll(wb);
    decisions[20] = { ...decisions[20], currency: 'SAR' };
    expect(nextThing({ workbench: wb, decisions, concern: { raised: true, codes: [], note: '' }, leadId: 407 }))
      .toMatchObject({ sentence: 'Tick which part concerns you.' });
    expect(nextThing({ workbench: wb, decisions, concern: { raised: true, codes: ['DELIVERY'], note: 'no' }, leadId: 407 }))
      .toMatchObject({ sentence: 'Say what the concern is, in a few words.' });
    expect(nextThing({ workbench: wb, decisions, concern: { raised: true, codes: ['DELIVERY'], note: 'Nine days is too tight.' }, leadId: 407 }))
      .toEqual({ kind: 'concern' });
  });

  it('reports a closed request by its status, and whether the server would reopen it', () => {
    const wb = workbench();
    const closed = lifecycle({ currentStatusCode: 'DISQUALIFIED', isTerminal: true, allowedTransitions: [] });
    expect(nextThing({ workbench: wb, decisions: quoteAll(wb), concern: NO_CONCERN, lifecycle: closed, leadId: 407 }))
      .toEqual({ kind: 'closed', statusCode: 'DISQUALIFIED', canReopen: false });
    expect(nextThing({ workbench: wb, decisions: quoteAll(wb), concern: NO_CONCERN, lifecycle: { ...closed, canReopen: true }, leadId: 407 }))
      .toEqual({ kind: 'closed', statusCode: 'DISQUALIFIED', canReopen: true });
  });

  it('says a closed request is closed before asking for a customer it can no longer use', () => {
    const wb = workbench({ customerId: null, customerName: null });
    const closed = lifecycle({ currentStatusCode: 'CANCELLED', isTerminal: true, allowedTransitions: [], canReopen: true });
    expect(nextThing({ workbench: wb, decisions: {}, concern: NO_CONCERN, lifecycle: closed, leadId: 407 }))
      .toEqual({ kind: 'closed', statusCode: 'CANCELLED', canReopen: true });
  });

  it('clears "not qualified" itself but says a duplicate flag in the server\'s words with the way out', () => {
    const wb = workbench();
    const decisions = quoteAll(wb);
    decisions[20] = { ...decisions[20], currency: 'SAR' };
    // The fixture's LEAD_NOT_ELIGIBLE is the not-yet-qualified refusal; the button qualifies.
    expect(nextThing({ workbench: wb, decisions, concern: NO_CONCERN, lifecycle: lifecycle(), leadId: 407 })).toEqual({ kind: 'ready' });
    const duplicate = workbench({ blockers: [
      { code: 'LEAD_NOT_ELIGIBLE', message: 'This lead is flagged as a possible duplicate of lead #9; resolve the duplicate flag first.', actionLabel: 'Open Lead lifecycle', actionPath: '/procurement/leads/view/407' },
    ] });
    expect(nextThing({ workbench: duplicate, decisions, concern: NO_CONCERN, lifecycle: lifecycle(), leadId: 407 })).toEqual({
      kind: 'blocked',
      sentence: 'This lead is flagged as a possible duplicate of lead #9; resolve the duplicate flag first.',
      action: { label: 'Open the lead', path: '/procurement/leads/view/407' },
    });
  });

  it('shows a server blocker it cannot resolve itself, in the server\'s words', () => {
    const wb = workbench({ blockers: [{ code: 'SOMETHING_NEW', message: 'A new rule applies.', actionLabel: 'Read it', actionPath: '/rules' }] });
    const decisions = quoteAll(wb);
    decisions[20] = { ...decisions[20], currency: 'SAR' };
    expect(nextThing({ workbench: wb, decisions, concern: NO_CONCERN, leadId: 407 }))
      .toEqual({ kind: 'blocked', sentence: 'A new rule applies.', action: { label: 'Read it', path: '/rules' } });
  });
});

describe('what one line needs', () => {
  it('lists the missing commercial values in fix order', () => {
    const needs = lineNeeds(line({ id: 1, needsAttention: true }), { decision: 'Bid' }, new Set(['EA']), new Set(['SAR']));
    expect(needs.map((need) => need.kind)).toEqual(['quantity', 'unit', 'currency', 'attention']);
  });

  it('says so when the tenant has no units or currencies to choose from, instead of letting the commit fail', () => {
    const needs = lineNeeds(line({ id: 1 }), { decision: 'Bid', quantity: 1, unitOfMeasure: 'PCS', currency: 'AED' }, new Set(), new Set());
    expect(needs.map((need) => need.kind)).toEqual(['unit-unconfigured', 'currency-unconfigured']);
    const wb = workbench({ unitOptions: [], lines: [line({ id: 1 })] });
    expect(nextThing({ workbench: wb, decisions: quoteAll(wb), concern: NO_CONCERN, leadId: 407 }))
      .toMatchObject({ kind: 'blocked', sentence: expect.stringMatching(/no units of measure set up/) });
  });
});

describe('the fit assessment behind one click', () => {
  it('records every governed criterion as passed and an honest rationale when no concern is raised', () => {
    const wb = workbench();
    const request = buildFitRequest(wb, NO_CONCERN, ['ELIGIBILITY', 'CAPABILITY', 'DELIVERY', 'COMPLIANCE', 'COMMERCIAL']);
    expect(request.overallDecision).toBe('FIT');
    expect(request.rationale).toBe(NO_CONCERN_RATIONALE);
    expect(request.expectedFitVersion).toBeUndefined();
    expect(request.criteria).toHaveLength(5);
    expect(request.criteria.every((criterion) => criterion.decision === 'PASS')).toBe(true);
  });

  it('records the concern against the ticked criteria only, with the note', () => {
    const wb = workbench();
    const request = buildFitRequest(wb, { raised: true, codes: ['DELIVERY'], note: 'Nine days is too tight.' }, ['ELIGIBILITY', 'DELIVERY']);
    expect(request.overallDecision).toBe('CONDITIONAL');
    expect(request.criteria).toEqual([
      { code: 'ELIGIBILITY', decision: 'PASS', note: undefined },
      { code: 'DELIVERY', decision: 'CONCERN', note: 'Nine days is too tight.' },
    ]);
  });

  it('does not re-save an assessment that already says the same thing', () => {
    const saved = {
      version: 2,
      overallDecision: 'FIT' as const,
      rationale: NO_CONCERN_RATIONALE,
      criteria: ['ELIGIBILITY', 'CAPABILITY'].map((code) => ({ code, label: code, decision: 'PASS' as const })),
    };
    expect(fitMatchesSaved(saved, NO_CONCERN, ['ELIGIBILITY', 'CAPABILITY'])).toBe(true);
    expect(fitMatchesSaved(saved, { raised: true, codes: ['DELIVERY'], note: 'x' }, ['ELIGIBILITY', 'CAPABILITY'])).toBe(false);
    expect(fitMatchesSaved({ ...saved, version: 0 }, NO_CONCERN, ['ELIGIBILITY', 'CAPABILITY'])).toBe(false);
    // A NOT_FIT verdict is non-actionable however its criteria read; "no concerns" must re-save it.
    expect(fitMatchesSaved({ ...saved, overallDecision: 'NOT_FIT' }, NO_CONCERN, ['ELIGIBILITY', 'CAPABILITY'])).toBe(false);
  });

  it('serialises the same choices the same way however they were built', () => {
    const clicked: DecisionMap = { 20: { decision: 'NoBid', quantity: 4, unitOfMeasure: 'EA', currency: 'SAR', reasonCode: 'NO_STOCK', note: 'x ' }, 10: { decision: 'Bid', quantity: 4, unitOfMeasure: 'EA', currency: 'SAR' } };
    const served: DecisionMap = { 10: { decision: 'Bid', quantity: 4, unitOfMeasure: 'EA', currency: 'SAR' }, 20: { decision: 'NoBid', reasonCode: 'NO_STOCK', note: 'x', quantity: 4, unitOfMeasure: 'EA', currency: 'SAR' } };
    expect(JSON.stringify(normalizeDecisions(clicked))).toBe(JSON.stringify(normalizeDecisions(served)));
    expect(JSON.stringify(normalizeConcern({ raised: true, codes: ['DELIVERY', 'COMMERCIAL'], note: 'tight ' })))
      .toBe(JSON.stringify(normalizeConcern({ raised: true, codes: ['COMMERCIAL', 'DELIVERY'], note: 'tight' })));
    expect(normalizeConcern({ raised: false, codes: ['DELIVERY'], note: 'stale' })).toEqual(NO_CONCERN);
  });

  it('rebuilds the concern controls from a saved assessment', () => {
    expect(concernFromSaved({
      version: 1,
      overallDecision: 'CONDITIONAL',
      rationale: 'Nine days is too tight.',
      criteria: [
        { code: 'ELIGIBILITY', label: 'Eligibility', decision: 'PASS' },
        { code: 'DELIVERY', label: 'Delivery', decision: 'CONCERN', note: 'Nine days is too tight.' },
      ],
    })).toEqual({ raised: true, codes: ['DELIVERY'], note: 'Nine days is too tight.' });
  });
});

describe('qualification and dates', () => {
  it('knows when the lead must be qualified first, and when it cannot be', () => {
    expect(qualificationStep(lifecycle())).toBe('transition');
    expect(qualificationStep(lifecycle({ currentStatusCode: 'QUALIFIED' }))).toBe('none');
    expect(qualificationStep(lifecycle({ currentStatusCode: 'LOST', allowedTransitions: [] }))).toBe('impossible');
    expect(qualificationStep(null)).toBe('none');
    expect(qualificationTransition(lifecycle())).toMatchObject({ statusCode: 'QUALIFIED' });
    expect(qualificationTransition(lifecycle({ currentStatusCode: 'QUALIFIED' }))).toBeNull();
  });

  it('says how long is left in words a rep would use', () => {
    const now = new Date('2026-09-09T08:00:00Z');
    expect(dueSentence(daysUntil('2026-09-18T00:00:00Z', now))).toBe('9 days left');
    expect(dueSentence(daysUntil('2026-09-09T20:00:00Z', now))).toBe('1 day left');
    expect(dueSentence(daysUntil('2026-09-01T00:00:00Z', now))).toBe('Closed 8 days ago');
    expect(dueSentence(daysUntil(null, now))).toBe('No deadline stated');
  });
});

describe('a line the customer gave no usable unit for', () => {
  it('says the request gave no unit, and the button goes to that line\'s picker', () => {
    const wb = workbench({ lines: [line({ id: 1, unitOfMeasure: null })] });
    expect(nextThing({ workbench: wb, decisions: quoteAll(wb), concern: NO_CONCERN, leadId: 407 })).toMatchObject({
      kind: 'blocked',
      sentence: 'The request gives no unit for line 00001. Choose it beside the quantity.',
      action: { label: 'Choose the unit', intent: 'choose-unit', lineId: 10 },
    });
  });

  it('names the word the customer wrote when the tenant does not quote in it', () => {
    const wb = workbench({ lines: [line({ id: 1, unitOfMeasure: 'Roll', normalizedUom: 'Roll' })] });
    expect(nextThing({ workbench: wb, decisions: quoteAll(wb), concern: NO_CONCERN, leadId: 407 }))
      .toMatchObject({ sentence: 'Line 00001 says "Roll". Choose the unit you\'ll quote it in.', action: { intent: 'choose-unit' } });
  });

  it('asks for one unit for every unitless line before sending the rep to certify them', () => {
    const many = workbench({
      lines: Array.from({ length: 8 }, (_item, index) => line({ id: index + 1, unitOfMeasure: null, verificationStatus: 'NEEDS_CHECK' })),
    });
    expect(nextThing({ workbench: many, decisions: quoteAll(many), concern: NO_CONCERN, leadId: 407 })).toMatchObject({
      kind: 'blocked',
      sentence: '8 lines marked to quote need a unit. Choose one for all 8 above the lines, or line by line.',
      action: { label: 'Choose the unit', intent: 'choose-unit' },
    });
  });

  it('takes units chosen on the screen into the check on the screen, not to an approval that would drop them', () => {
    const many = workbench({
      lines: Array.from({ length: 8 }, (_item, index) => line({ id: index + 1, unitOfMeasure: null, verificationStatus: 'NEEDS_CHECK' })),
    });
    expect(nextThing({ workbench: many, decisions: quoteAll(many, { unitOfMeasure: 'EA' }), concern: NO_CONCERN, leadId: 407 })).toMatchObject({
      kind: 'blocked',
      sentence: 'Check the 8 lines marked to quote against the document and confirm them. The units you chose go with them.',
      action: { label: 'Check the document', intent: 'check-document' },
    });
  });

  it('asks one line to be checked with the unit the rep chose', () => {
    const wb = workbench({ lines: [line({ id: 1, unitOfMeasure: null, verificationStatus: 'NEEDS_CHECK' })] });
    expect(nextThing({ workbench: wb, decisions: quoteAll(wb, { unitOfMeasure: 'EA' }), concern: NO_CONCERN, leadId: 407 })).toMatchObject({
      sentence: 'Check line 00001 against the document and confirm it. The unit you chose goes with it.',
      action: { intent: 'check-document' },
    });
  });

  it('says the tenant has no units before sending anyone to certify lines', () => {
    const many = workbench({
      unitOptions: [],
      lines: Array.from({ length: 8 }, (_item, index) => line({ id: index + 1, verificationStatus: 'NEEDS_CHECK' })),
    });
    expect(nextThing({ workbench: many, decisions: quoteAll(many), concern: NO_CONCERN, leadId: 407 }))
      .toMatchObject({ kind: 'blocked', sentence: expect.stringMatching(/no units of measure set up/) });
  });
});

describe('the acknowledgement written on a warned line', () => {
  it('keeps the catalogue sentence for a catalogue warning', () => {
    expect(acknowledgementFor({ attentionReason: 'No catalog match found' })).toBe(QUOTED_AS_READ_NOTE);
  });

  it('says what happened to the unit instead of claiming a catalogue miss', () => {
    const missing = { attentionReason: 'Unit of measure missing' };
    expect(acknowledgementFor(missing, { unitOfMeasure: 'EA' })).toBe('The request gave no unit; quoted in EA.');
    expect(acknowledgementFor(missing)).not.toMatch(/catalogue/);
    const pack = { attentionReason: 'Unit of measure "Pack" needs review — packaging unit — confirm how many items it contains before quoting' };
    expect(acknowledgementFor(pack, { unitOfMeasure: 'SET' })).toBe('The request said "Pack"; quoted in SET.');
    expect(acknowledgementFor({ attentionReason: 'No catalog match found; Unit of measure missing' }, { unitOfMeasure: 'M' }))
      .toBe(`${QUOTED_AS_READ_NOTE} The request gave no unit; quoted in M.`);
  });

  it('rewrites its own note as the unit is chosen, and never a note the rep typed', () => {
    const warned = { attentionReason: 'Unit of measure missing', needsAttention: true };
    const quoted = withAcknowledgement(warned, { decision: 'Pending' }, { decision: 'Bid' }, true);
    expect(quoted.note).toBe('The request gave no unit; the unit is chosen on the line.');
    expect(withAcknowledgement(warned, quoted, { ...quoted, unitOfMeasure: 'EA' }, false).note).toBe('The request gave no unit; quoted in EA.');
    const typed = { ...quoted, note: 'Customer confirmed by phone: each.' };
    expect(withAcknowledgement(warned, typed, { ...typed, unitOfMeasure: 'EA' }, false).note).toBe('Customer confirmed by phone: each.');
  });

  it('calls a unit-only warning a warning, not a catalogue warning', () => {
    const wb = workbench({ lines: [line({ id: 1, needsAttention: true, attentionReason: 'Unit of measure missing' })] });
    expect(nextThing({ workbench: wb, decisions: quoteAll(wb), concern: NO_CONCERN, leadId: 407 }))
      .toMatchObject({ sentence: 'Say how you handled the warning on line 00001.' });
  });
});

describe('choices that outlive a new revision', () => {
  it('carries a unit the rep chose onto a line the new revision still has none for, but a corrected unit wins', () => {
    const lines = [line({ id: 7, lineItemNo: '00001', unitOfMeasure: null }), line({ id: 8, lineItemNo: '00002', unitOfMeasure: 'SET' })];
    const initial: DecisionMap = {
      70: { decision: 'Pending', quantity: 4, currency: 'SAR' },
      80: { decision: 'Pending', quantity: 4, unitOfMeasure: 'SET', currency: 'SAR' },
    };
    const carried = new Map([
      ['00001', { decision: 'Bid' as const, unitOfMeasure: 'EA' }],
      ['00002', { decision: 'Bid' as const, unitOfMeasure: 'EA' }],
    ]);
    const result = carryChoices(lines, initial, carried, new Set(['EA', 'SET']), new Set(['SAR']));
    expect(result[70]).toMatchObject({ decision: 'Bid', unitOfMeasure: 'EA' });
    expect(result[80]).toMatchObject({ decision: 'Bid', unitOfMeasure: 'SET' });
  });

  it('keys repeated line numbers apart and round-trips choices by line number', () => {
    const lines = [line({ id: 1, lineItemNo: '10' }), line({ id: 2, lineItemNo: '10' }), line({ id: 3, lineItemNo: '20' })];
    expect([...lineKeys(lines).values()]).toEqual(['10', '10#2', '20']);
    const byKey = decisionsByLineKey(lines, { 10: { decision: 'Bid' }, 20: { decision: 'NoBid', reasonCode: 'NO_STOCK' } });
    const renumbered = [line({ id: 7, lineItemNo: '10' }), line({ id: 8, lineItemNo: '10' }), line({ id: 9, lineItemNo: '20' })];
    expect(decisionsFromLineKeys(renumbered, byKey, {})).toEqual({ 70: { decision: 'Bid' }, 80: { decision: 'NoBid', reasonCode: 'NO_STOCK' } });
  });

  it('pre-selects only units the tenant quotes in, spelled its way', () => {
    expect(keepTenantUnits(
      { 10: { decision: 'Pending', unitOfMeasure: 'Roll' }, 20: { decision: 'Pending', unitOfMeasure: 'ea' } },
      [{ code: 'EA', label: 'Each' }],
    )).toEqual({ 10: { decision: 'Pending' }, 20: { decision: 'Pending', unitOfMeasure: 'EA' } });
  });

  it('sets one unit on every quoted line without one, and leaves a line that has one', () => {
    const lines = [line({ id: 1 }), line({ id: 2 }), line({ id: 3 })];
    const result = applyUnitToUnitless(lines, {
      10: { decision: 'Bid' },
      20: { decision: 'Bid', unitOfMeasure: 'SET' },
      30: { decision: 'NoBid', reasonCode: 'NO_STOCK' },
    }, 'EA', new Set(['EA', 'SET']));
    expect(result[10].unitOfMeasure).toBe('EA');
    expect(result[20].unitOfMeasure).toBe('SET');
    expect(result[30].unitOfMeasure).toBeUndefined();
  });
});

const PROMOTION = {
  rfqId: 417,
  rfqNumber: 'RFQ-2026-0417',
  leadRevisionNumber: 1,
  participationVersion: 1,
  promotedLineCount: 3,
  promotedAtUtc: '2026-09-09T09:00:00Z',
  promotedBy: 'zack@kodekinetics.com',
};

describe('what the decision record says comes before the status read', () => {
  const closed = lifecycle({ currentStatusCode: 'DISQUALIFIED', isTerminal: true, allowedTransitions: [] });

  it('a request that became an RFQ is that, whatever the status read says or whether it came back', () => {
    const wb = workbench({ promotion: PROMOTION, blockers: [] });
    expect(nextThing({ workbench: wb, decisions: quoteAll(wb), concern: NO_CONCERN, lifecycle: closed, leadId: 407 }))
      .toEqual({ kind: 'rfq', rfqId: 417, rfqRef: 'RFQ-2026-0417', changed: false });
    expect(nextThing({ workbench: wb, decisions: quoteAll(wb), concern: NO_CONCERN, lifecycle: undefined, leadId: 407 }))
      .toEqual({ kind: 'rfq', rfqId: 417, rfqRef: 'RFQ-2026-0417', changed: false });
    const changed = workbench({ promotion: { ...PROMOTION, rfqNumber: null }, blockers: [{ code: 'RFQ_REVISION_REQUIRED', message: 'server words' }] });
    expect(nextThing({ workbench: changed, decisions: {}, concern: NO_CONCERN, leadId: 407 }))
      .toEqual({ kind: 'rfq', rfqId: 417, rfqRef: '#417', changed: true });
  });

  it('a legacy RFQ, then an inconsistent record, then a declined request', () => {
    const legacy = workbench({ blockers: [{ code: 'LEGACY_RFQ', message: 'server words', actionLabel: 'Open existing RFQ', actionPath: '/procurement/rfqs/view/88' }] });
    expect(nextThing({ workbench: legacy, decisions: {}, concern: NO_CONCERN, lifecycle: closed, leadId: 407 }))
      .toEqual({ kind: 'legacy', path: '/procurement/rfqs/view/88' });
    const broken = workbench({ blockers: [{ code: 'INCONSISTENT_CONVERTED_STATE', message: 'server words' }] });
    expect(nextThing({ workbench: broken, decisions: {}, concern: NO_CONCERN, lifecycle: closed, leadId: 407 }))
      .toEqual({ kind: 'inconsistent' });
    const declined = workbench({ participationStatus: 'COMMITTED', participationVersion: 2, blockers: [] });
    const skipped: DecisionMap = Object.fromEntries(declined.lines.map((item) => [item.revisionLineId, { decision: 'NoBid' as const, reasonCode: 'NO_STOCK' }]));
    expect(nextThing({ workbench: declined, decisions: skipped, concern: NO_CONCERN, lifecycle: undefined, leadId: 407 }))
      .toEqual({ kind: 'declined' });
    // The same skip-everything, not yet committed, is still an open decline.
    expect(nextThing({ workbench: { ...declined, participationStatus: 'DRAFT' }, decisions: skipped, concern: NO_CONCERN, leadId: 407 }))
      .toEqual({ kind: 'decline' });
  });

  it('names an RFQ by its number, or its id when it has none', () => {
    expect(rfqRefOf({ rfqId: 417, rfqNumber: 'RFQ-2026-0417' })).toBe('RFQ-2026-0417');
    expect(rfqRefOf({ rfqId: 417, rfqNumber: '  ' })).toBe('#417');
    expect(lineWord(1)).toBe('line');
    expect(lineWord(0)).toBe('lines');
  });
});

describe('the sentence for a closed request', () => {
  it('names the way back only where one exists for this viewer', () => {
    expect(closedSentence('DISQUALIFIED', { canReopen: true, mayReopen: true }))
      .toBe('This request was declined. To work on it again, reopen it on the lead page.');
    expect(closedSentence('DISQUALIFIED', { canReopen: true, mayReopen: false }))
      .toBe('This request was declined. Ask a manager to reopen it if the customer still wants a price.');
    expect(closedSentence('DISQUALIFIED', { canReopen: false, mayReopen: true }))
      .toBe('This request was declined. Nothing more can be decided here.');
    expect(closedSentence('cancelled', { canReopen: true, mayReopen: true }))
      .toBe('This request was cancelled. To work on it again, reopen it on the lead page.');
    expect(closedSentence('LOST', { canReopen: true, mayReopen: false }))
      .toBe('This request was lost. Ask a manager to reopen it if the customer still wants a price.');
  });

  it('never prints a raw status code or offers a reopen the server refuses', () => {
    const noReopen = { canReopen: false, mayReopen: true };
    expect(closedSentence('DUPLICATED', noReopen))
      .toBe('This request was marked as a duplicate of another request. Nothing can be decided here; work on the other one.');
    expect(closedSentence('COMPLETED', noReopen)).toBe('This request is completed. Nothing more can be decided here.');
    expect(closedSentence('QUOTED', noReopen)).toBe('This request has already moved on (Quote sent). Nothing more can be decided here.');
    expect(closedSentence('PARTIALLY_AWARDED', noReopen)).toBe('This request has already moved on (Partly won). Nothing more can be decided here.');
    expect(closedSentence('CONVERTED_TO_RFQ', noReopen)).toBe('This request already became an RFQ. Nothing more can be decided here.');
    // A tenant without an active QUALIFIED status leaves an ordinary status with no way to qualify.
    expect(closedSentence('RECEIVED', noReopen))
      .toBe("This request can't be marked qualified from its current status (New), so no RFQ can be created. Ask an administrator to check the lead statuses under Setup.");
    expect(closedSentence('ON_ICE', { ...noReopen, fallbackLabel: 'On ice' }))
      .toBe("This request can't be marked qualified from its current status (On ice), so no RFQ can be created. Ask an administrator to check the lead statuses under Setup.");
    expect(closedSentence('ON_ICE', noReopen)).toContain('(not recorded)');
    for (const code of ['DUPLICATED', 'QUOTED', 'CONVERTED_TO_RFQ', 'COMPLETED']) {
      expect(closedSentence(code, noReopen)).not.toMatch(/Reopen|reopen|_/);
    }
  });
});

describe('the next step for a finished or stopped request', () => {
  const ctx = (overrides: Partial<StepCopyContext> = {}): StepCopyContext => ({
    leadId: 407,
    canViewRfq: true,
    canReviewChange: true,
    mayReopen: true,
    currentRevisionNumber: 1,
    promotedRevisionNumber: 1,
    ...overrides,
  });
  const rfq = { kind: 'rfq' as const, rfqId: 417, rfqRef: 'RFQ-2026-0417', changed: false };

  it('says where the work carries on after the RFQ, with the one control to get there', () => {
    expect(nextStepCopy(rfq, ctx())).toEqual({
      tone: 'success',
      sentence: 'This request became RFQ RFQ-2026-0417. The work carries on from the RFQ.',
      action: { label: 'Open the RFQ', path: '/procurement/rfqs/view/417' },
    });
    expect(nextStepCopy(rfq, ctx({ currentRevisionNumber: 3, promotedRevisionNumber: 1 }))?.sentence)
      .toBe('This request became RFQ RFQ-2026-0417 from revision 1. The work carries on from the RFQ.');
    expect(nextStepCopy(rfq, ctx({ canViewRfq: false }))).toEqual({
      tone: 'success',
      sentence: "This request became RFQ RFQ-2026-0417. Your role can't open RFQs; ask a manager what happens next.",
    });
  });

  it('asks the one person who can review a customer change to do it, and tells everyone else who to ask', () => {
    const changed = { ...rfq, changed: true };
    expect(nextStepCopy(changed, ctx({ currentRevisionNumber: 2 }))).toEqual({
      tone: 'warning',
      sentence: 'The customer changed this request after it became RFQ RFQ-2026-0417. Compare revision 2 with the RFQ, then press Review the change to record what you did.',
      action: { label: 'Review the change', intent: 'review-change' },
    });
    expect(nextStepCopy(changed, ctx({ canReviewChange: false }))).toEqual({
      tone: 'warning',
      sentence: 'The customer changed this request after it became RFQ RFQ-2026-0417. Ask a manager to review the change.',
      action: { label: 'Open the RFQ', path: '/procurement/rfqs/view/417' },
    });
    expect(nextStepCopy(changed, ctx({ canReviewChange: false, canViewRfq: false }))?.action).toBeUndefined();
  });

  it('covers a legacy RFQ, a record that needs an administrator, and a declined request', () => {
    expect(nextStepCopy({ kind: 'legacy', path: '/procurement/rfqs/view/88' }, ctx())).toEqual({
      tone: 'info',
      sentence: 'This request already has an RFQ, created before decisions were recorded on this screen. The work carries on from the RFQ.',
      action: { label: 'Open the RFQ', path: '/procurement/rfqs/view/88' },
    });
    expect(nextStepCopy({ kind: 'legacy', path: '/procurement/rfqs/view/88' }, ctx({ canViewRfq: false }))).toEqual({
      tone: 'info',
      sentence: "This request already has an RFQ, created before decisions were recorded on this screen. Your role can't open RFQs; ask a manager what happens next.",
    });
    expect(nextStepCopy({ kind: 'inconsistent' }, ctx())).toEqual({
      tone: 'error',
      sentence: 'This request is marked as an RFQ, but no RFQ exists. Ask an administrator to repair it; nothing can be decided here.',
    });
    expect(nextStepCopy({ kind: 'declined' }, ctx())).toEqual({
      tone: 'info',
      sentence: 'Every line was skipped and the request was declined. No RFQ was created, and there is nothing more to do here.',
    });
  });

  it('offers Open the lead on a closed request only to someone who may reopen it', () => {
    const disqualified = { kind: 'closed' as const, statusCode: 'DISQUALIFIED', canReopen: true };
    expect(nextStepCopy(disqualified, ctx())).toEqual({
      tone: 'info',
      sentence: 'This request was declined. To work on it again, reopen it on the lead page.',
      action: { label: 'Open the lead', path: '/procurement/leads/view/407' },
    });
    expect(nextStepCopy(disqualified, ctx({ mayReopen: false }))).toEqual({
      tone: 'info',
      sentence: 'This request was declined. Ask a manager to reopen it if the customer still wants a price.',
    });
    expect(nextStepCopy({ kind: 'closed', statusCode: 'DUPLICATED', canReopen: true }, ctx())?.action).toBeUndefined();
  });

  it('leaves an open request to the page', () => {
    expect(nextStepCopy({ kind: 'ready' }, ctx())).toBeNull();
    expect(nextStepCopy({ kind: 'blocked', sentence: 'x' }, ctx())).toBeNull();
  });
});

describe('the receipt of an RFQ', () => {
  const current = { revisionNumber: 1, lineCount: 3 };
  // The date is written the way the page writes every date (the runtime's en-GB month names).
  const on = formatDateSafe(PROMOTION.promotedAtUtc);

  it('names the person, not the login, and counts against the revision the RFQ came from', () => {
    expect(receiptSentence({ ...PROMOTION, promotedByName: 'Zack Khan' }, current))
      .toBe(`Zack Khan created it on ${on}. 3 of 3 lines went into the RFQ.`);
    expect(receiptSentence(PROMOTION, current, 'ZACK@kodekinetics.com'))
      .toBe(`You created it on ${on}. 3 of 3 lines went into the RFQ.`);
    expect(receiptSentence(PROMOTION, current, 'someone@else.com'))
      .toBe(`zack@kodekinetics.com created it on ${on}. 3 of 3 lines went into the RFQ.`);
    // A newer revision's line total is never set beside the RFQ's count.
    expect(receiptSentence(PROMOTION, { revisionNumber: 2, lineCount: 5 }))
      .toBe(`zack@kodekinetics.com created it on ${on}. 3 lines went into the RFQ.`);
    expect(receiptSentence({ ...PROMOTION, promotedRevisionLineCount: 4 }, { revisionNumber: 2, lineCount: 5 }))
      .toBe(`zack@kodekinetics.com created it on ${on}. 3 of 4 lines went into the RFQ.`);
  });

  it('drops what it does not know instead of printing a dash', () => {
    expect(receiptSentence({ ...PROMOTION, promotedBy: null, promotedLineCount: 1 }, { revisionNumber: 1, lineCount: 1 }))
      .toBe(`Created on ${on}. 1 of 1 line went into the RFQ.`);
    expect(receiptSentence({ ...PROMOTION, promotedBy: null, promotedAtUtc: '0001-01-01T00:00:00' }, current))
      .toBe('Created. 3 of 3 lines went into the RFQ.');
    expect(receiptActor({ ...PROMOTION, promotedBy: '  ' })).toBeNull();
  });
});

describe('what a half-finished click says', () => {
  const none = { fitSaved: false, qualifiedNow: false, choicesSavedNow: false, choicesAlreadyCommitted: false, alreadyQualified: false };

  it('keeps "Nothing was changed" only for a click that wrote nothing', () => {
    expect(partialFailureSentence('rfq', none, 'Save for a manager')).toBeNull();
    expect(partialFailureSentence('rfq', { ...none, choicesAlreadyCommitted: true, alreadyQualified: true }, 'Save for a manager')).toBeNull();
  });

  it('says which steps of Create RFQ went through', () => {
    expect(partialFailureSentence('rfq', { ...none, fitSaved: true }, 'Save for a manager'))
      .toBe('The RFQ was not created. Your concern answer is saved, but the request is not marked qualified and your line choices are not saved. Press Create RFQ again.');
    expect(partialFailureSentence('rfq', { ...none, fitSaved: true, alreadyQualified: true }, 'Save for a manager'))
      .toBe('The RFQ was not created. Your concern answer is saved, but your line choices are not saved. Press Create RFQ again.');
    expect(partialFailureSentence('rfq', { ...none, fitSaved: true, qualifiedNow: true }, 'Save for a manager'))
      .toBe('The RFQ was not created. The request is marked qualified, but your line choices are not saved. Press Create RFQ again.');
    expect(partialFailureSentence('rfq', { ...none, qualifiedNow: true, choicesSavedNow: true }, 'Save for a manager'))
      .toBe('The RFQ was not created. Your choices are saved and the request is marked qualified. Press Create RFQ again.');
    expect(partialFailureSentence('rfq', { ...none, choicesSavedNow: true, alreadyQualified: true }, 'Save for a manager'))
      .toBe('The RFQ was not created. Your choices are saved and the request is marked qualified. Press Create RFQ again.');
    expect(partialFailureSentence('rfq', { ...none, choicesSavedNow: true }, 'Save for a manager'))
      .toBe('The RFQ was not created. Your choices are saved. Press Create RFQ again.');
  });

  it('says which steps of a save or a decline went through', () => {
    expect(partialFailureSentence('draft', { ...none, fitSaved: true }, 'Save for review'))
      .toBe('Your line choices were not saved. Your concern answer is saved. Press Save for review again.');
    expect(partialFailureSentence('draft', { ...none, fitSaved: true }, 'Save for a manager'))
      .toBe('Your line choices were not saved. Your concern answer is saved. Press Save for a manager again.');
    expect(partialFailureSentence('draft', { ...none, choicesSavedNow: true }, 'Save for a manager'))
      .toBe("Your choices are saved as a draft, but Nexora couldn't read the request back. Refresh the page to see them.");
    expect(partialFailureSentence('decline', { ...none, fitSaved: true }, 'Save for a manager'))
      .toBe('The request was not declined. Your concern answer is saved. Press Decline request again.');
    expect(partialFailureSentence('decline', { ...none, fitSaved: true, choicesSavedNow: true }, 'Save for a manager'))
      .toBe("The request is declined and recorded, but Nexora couldn't read it back. Refresh the page to see it.");
  });
});
