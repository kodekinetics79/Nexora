import { describe, expect, it } from 'vitest';
import type { LeadDecisionLineDTO, LeadDecisionWorkbenchDTO } from '../../../api/services/leadDecisionService';
import type { LifecycleState } from '../../../api/services/commercialLifecycleService';
import type { DecisionMap } from '../Workbench/workbenchRules';
import {
  buildFitRequest,
  concernFromSaved,
  daysUntil,
  dueSentence,
  fitMatchesSaved,
  lineNeeds,
  nextThing,
  NO_CONCERN,
  NO_CONCERN_RATIONALE,
  qualificationStep,
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

  it('says a closed request must be reopened from the lead page', () => {
    const wb = workbench();
    const closed = lifecycle({ currentStatusCode: 'DISQUALIFIED', isTerminal: true, allowedTransitions: [] });
    expect(nextThing({ workbench: wb, decisions: quoteAll(wb), concern: NO_CONCERN, lifecycle: closed, leadId: 407 }))
      .toEqual({ kind: 'closed', sentence: 'This request is disqualified. Reopen it from the lead page before deciding.' });
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

  it('accepts any unit or currency when the tenant configured none', () => {
    const needs = lineNeeds(line({ id: 1 }), { decision: 'Bid', quantity: 1, unitOfMeasure: 'PCS', currency: 'AED' }, new Set(), new Set());
    expect(needs).toEqual([]);
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
  });

  it('says how long is left in words a rep would use', () => {
    const now = new Date('2026-09-09T08:00:00Z');
    expect(dueSentence(daysUntil('2026-09-18T00:00:00Z', now))).toBe('9 days left');
    expect(dueSentence(daysUntil('2026-09-09T20:00:00Z', now))).toBe('1 day left');
    expect(dueSentence(daysUntil('2026-09-01T00:00:00Z', now))).toBe('Closed 8 days ago');
    expect(dueSentence(daysUntil(null, now))).toBe('No deadline stated');
  });
});
