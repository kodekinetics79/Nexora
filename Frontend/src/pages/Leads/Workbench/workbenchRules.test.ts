import { describe, expect, it } from 'vitest';
import type { LeadDecisionWorkbenchDTO } from '../../../api/services/leadDecisionService';
import {
  blockerAction,
  countDecisions,
  decisionRecordIsLocked,
  fitAssessmentDraftComplete,
  initializeDecisionMap,
  promotionBlockers,
  validGovernedDecision,
} from './workbenchRules';

const workbench = (over: Partial<LeadDecisionWorkbenchDTO> = {}): LeadDecisionWorkbenchDTO => ({
  leadId: 42,
  leadRevisionId: 4201,
  leadRevisionNumber: 1,
  decisionVersion: 3,
  participationVersion: null,
  participationStatus: 'NONE',
  lifecycleStatusCode: 'UNDER_REVIEW',
  customerId: 7,
  customerName: 'Client Co',
  verificationStatus: 'VERIFIED',
  evidence: [],
  reasonCodes: [],
  fitAssessment: null,
  promotion: null,
  blockers: [],
  lines: [
    { id: 1, revisionLineId: 101, verificationStatus: 'VERIFIED' },
    { id: 2, revisionLineId: 102, verificationStatus: 'VERIFIED' },
    { id: 3, revisionLineId: 103, verificationStatus: 'VERIFIED' },
  ],
  ...over,
});

describe('Lead Decision Workbench rules', () => {
  it('defaults every line to Pending instead of implicitly approving it', () => {
    const decisions = initializeDecisionMap(workbench());

    expect(Object.values(decisions)).toEqual([
      { decision: 'Pending' },
      { decision: 'Pending' },
      { decision: 'Pending' },
    ]);
    expect(countDecisions(decisions)).toEqual({ total: 3, bid: 0, noBid: 0, clarify: 0, pending: 3 });
  });

  it('restores only persisted Lead-revision participation decisions', () => {
    const record = workbench({
      lines: [
        { id: 1, revisionLineId: 101, verificationStatus: 'VERIFIED', participation: { decision: 'Bid' } },
        { id: 2, revisionLineId: 102, verificationStatus: 'VERIFIED', participation: { decision: 'NoBid', reasonCode: 'NO_SOURCE', note: 'Obsolete' } },
      ],
    });

    expect(initializeDecisionMap(record)).toEqual({
      101: { decision: 'Bid' },
      102: { decision: 'NoBid', reasonCode: 'NO_SOURCE', note: 'Obsolete' },
    });
  });

  it('requires governed reasons for NoBid and Clarify', () => {
    expect(validGovernedDecision({ decision: 'Bid' })).toBe(true);
    expect(validGovernedDecision({ decision: 'NoBid' })).toBe(false);
    expect(validGovernedDecision({ decision: 'Clarify', note: 'Need drawing' })).toBe(false);
    expect(validGovernedDecision({ decision: 'Clarify', reasonCode: 'SPEC_MISSING' })).toBe(true);
  });

  it('requires a deliberate decision for every governed fit criterion', () => {
    const defaults = [{ code: 'CAPABILITY', label: 'Capability', decision: 'UNKNOWN' as const }];
    expect(fitAssessmentDraftComplete(defaults, 'Reviewed by the bid manager.')).toBe(false);
    expect(fitAssessmentDraftComplete([{ ...defaults[0], decision: 'PASS' }], '')).toBe(false);
    expect(fitAssessmentDraftComplete([{ ...defaults[0], decision: 'PASS' }], 'Reviewed by the bid manager.')).toBe(true);
  });

  it('routes qualification blockers to the actual governed Lead detail route', () => {
    expect(blockerAction({
      code: 'LEAD_NOT_ELIGIBLE',
      message: 'Lead must be qualified.',
      actionLabel: 'Open Lead lifecycle',
      actionPath: '/procurement/leads/42',
    }, 42)).toEqual({ label: 'Open Lead lifecycle', path: '/procurement/leads/view/42' });
  });

  it('blocks promotion until validation, fit, complete participation and commit are all true', () => {
    const record = workbench({ customerId: null, verificationStatus: 'NEEDS_REVIEW' });
    const decisions = initializeDecisionMap(record);
    const blockers = promotionBlockers({
      workbench: record,
      decisions,
      fitAssessment: null,
      dirty: true,
      participationStatus: 'DRAFT',
      participationVersion: null,
    });

    expect(blockers).toContain('Resolve the customer before promoting an RFQ.');
    expect(blockers).toContain('Complete source validation before promoting an RFQ.');
    expect(blockers).toContain('Save the fit assessment before deciding participation.');
    expect(blockers).toContain('Decide the remaining 3 lines.');
    expect(blockers).toContain('Commit the participation decision before promotion.');
  });

  it('treats governed version-zero criteria as an unsaved first assessment', () => {
    const record = workbench({ participationStatus: 'COMMITTED', participationVersion: 9 });
    const decisions = {
      101: { decision: 'Bid' as const, quantity: 10, unitOfMeasure: 'EA', currency: 'USD' },
      102: { decision: 'NoBid' as const, reasonCode: 'OUT_OF_SCOPE' },
      103: { decision: 'NoBid' as const, reasonCode: 'OUT_OF_SCOPE' },
    };
    const governedDefaults = {
      version: 0,
      overallDecision: 'CONDITIONAL' as const,
      rationale: '',
      criteria: [{ code: 'ELIGIBILITY', label: 'Eligibility', decision: 'UNKNOWN' as const }],
    };

    expect(promotionBlockers({ workbench: record, decisions, fitAssessment: governedDefaults, dirty: false, participationStatus: 'COMMITTED', participationVersion: 9 }))
      .toContain('Save the fit assessment before deciding participation.');
  });

  it('allows a committed partial bid and refuses a second promotion', () => {
    const record = workbench({ participationStatus: 'COMMITTED', participationVersion: 9 });
    const decisions = {
      101: { decision: 'Bid' as const, quantity: 10, unitOfMeasure: 'EA', currency: 'USD' },
      102: { decision: 'NoBid' as const, reasonCode: 'OUT_OF_SCOPE' },
      103: { decision: 'NoBid' as const, reasonCode: 'NO_SOURCE' },
    };
    const fit = { version: 1, overallDecision: 'CONDITIONAL' as const, rationale: 'Bid supported lines only.', criteria: [] };

    expect(promotionBlockers({ workbench: record, decisions, fitAssessment: fit, dirty: false, participationStatus: 'COMMITTED', participationVersion: 9 })).toEqual([]);

    const promoted = workbench({
      participationStatus: 'COMMITTED',
      participationVersion: 9,
      promotion: { rfqId: 77, rfqNumber: 'RFQ-77', leadRevisionNumber: 1, participationVersion: 9, promotedLineCount: 1, promotedAtUtc: '2026-08-24T00:00:00Z' },
    });
    expect(promotionBlockers({ workbench: promoted, decisions, fitAssessment: fit, dirty: false, participationStatus: 'COMMITTED', participationVersion: 9 })[0]).toContain('already promoted');
  });

  it('keeps Clarify as a draft state that blocks promotion', () => {
    const record = workbench({ participationStatus: 'COMMITTED', participationVersion: 9 });
    const decisions = {
      101: { decision: 'Bid' as const },
      102: { decision: 'NoBid' as const, reasonCode: 'OUT_OF_SCOPE' },
      103: { decision: 'Clarify' as const, reasonCode: 'SPEC_MISSING' },
    };
    const fit = { version: 1, overallDecision: 'CONDITIONAL' as const, rationale: 'Await buyer clarification.', criteria: [] };

    expect(promotionBlockers({ workbench: record, decisions, fitAssessment: fit, dirty: false, participationStatus: 'COMMITTED', participationVersion: 9 }))
      .toContain('Resolve the 1 line awaiting clarification before promotion.');
  });

  it('locks a committed full no-bid and any already-promoted decision record', () => {
    const noBid = {
      101: { decision: 'NoBid' as const, reasonCode: 'OUT_OF_SCOPE' },
      102: { decision: 'NoBid' as const, reasonCode: 'OUT_OF_SCOPE' },
      103: { decision: 'NoBid' as const, reasonCode: 'OUT_OF_SCOPE' },
    };
    expect(decisionRecordIsLocked(workbench({ participationStatus: 'COMMITTED' }), noBid)).toBe(true);
    expect(decisionRecordIsLocked(workbench({ participationStatus: 'DRAFT' }), noBid)).toBe(false);
    expect(decisionRecordIsLocked(workbench({ promotion: {
      rfqId: 77,
      leadRevisionNumber: 1,
      participationVersion: 1,
      promotedLineCount: 1,
      promotedAtUtc: '2026-08-25T12:00:00Z',
    } }), initializeDecisionMap(workbench()))).toBe(true);
  });
});
