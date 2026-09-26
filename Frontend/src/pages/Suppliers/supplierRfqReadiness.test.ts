import { describe, expect, it } from 'vitest';
import {
  approveForRfqs, approveForRfqsReason, blockerWords, presetClearsAllBlockers, rfqReadinessGaps,
} from './supplierRfqReadiness';

/**
 * The governance dialog offered five selects (8 x 5 x 6 x 5 x 4 values) and never said which
 * combination lets a supplier be asked. This module is the rule in one place; the server's READY
 * check (SupplierGovernanceService) refuses UNKNOWN risk, so the preset has to fill risk in.
 */
describe('approveForRfqs — the one-click preset', () => {
  it('turns a fresh supplier into the combination the server accepts as askable', () => {
    const decision = approveForRfqs({
      governanceStatus: 'UNVERIFIED', verificationStatus: 'UNKNOWN', complianceStatus: 'UNKNOWN',
      riskStatus: 'UNKNOWN', readinessStatus: 'REVIEW_REQUIRED',
    });
    expect(decision).toEqual({
      governanceStatus: 'APPROVED', verificationStatus: 'VERIFIED', complianceStatus: 'CLEARED',
      riskStatus: 'LOW', readinessStatus: 'READY',
    });
    expect(rfqReadinessGaps(decision!)).toEqual([]);
  });

  it('keeps a Preferred verdict and a Medium risk a person already recorded', () => {
    const decision = approveForRfqs({ governanceStatus: 'PREFERRED', riskStatus: 'MEDIUM' });
    expect(decision?.governanceStatus).toBe('PREFERRED');
    expect(decision?.riskStatus).toBe('MEDIUM');
    expect(rfqReadinessGaps(decision!)).toEqual([]);
  });

  it('refuses to lower a High or Blocked risk verdict', () => {
    expect(approveForRfqs({ riskStatus: 'HIGH' })).toBeNull();
    expect(approveForRfqs({ riskStatus: 'BLOCKED' })).toBeNull();
  });
});

describe('rfqReadinessGaps — the live sentence under Advanced', () => {
  it('names every verdict still in the way, in plain words', () => {
    expect(rfqReadinessGaps({
      governanceStatus: 'REVIEW_REQUIRED', verificationStatus: 'PENDING', complianceStatus: 'PENDING',
      riskStatus: 'UNKNOWN', readinessStatus: 'REVIEW_REQUIRED',
    })).toEqual(['not approved', 'not verified', 'compliance not cleared', 'risk not assessed', 'not marked ready for RFQs']);
    expect(rfqReadinessGaps({
      governanceStatus: 'PROVISIONAL', verificationStatus: 'VERIFIED', complianceStatus: 'CLEARED',
      riskStatus: 'HIGH', readinessStatus: 'READY',
    })).toEqual(['risk too high']);
  });
});

describe('the audit reason and the blockers in words', () => {
  it('records who approved and from where', () => {
    expect(approveForRfqsReason('Rana')).toBe('Approved for RFQs by Rana');
    expect(approveForRfqsReason('Rana', 'from the sourcing case')).toBe('Approved for RFQs by Rana from the sourcing case');
    expect(approveForRfqsReason('  ')).toBe('Approved for RFQs by a manager');
  });

  it('translates each server blocker and passes an unknown one through', () => {
    expect(blockerWords('Supplier approval or explicit provisional approval is required')).toBe('Not approved');
    expect(blockerWords('Supplier verification status must be VERIFIED')).toBe('Not verified');
    expect(blockerWords('Supplier outreach readiness must be READY')).toBe('Not marked ready for RFQs');
    expect(blockerWords('Supplier compliance status must be CLEARED')).toBe('Compliance not cleared');
    expect(blockerWords('Supplier risk status blocks outreach')).toBe('Risk is High or Blocked');
    expect(blockerWords('A verified dispatch contact is required')).toBe('No contact email');
    expect(blockerWords('Something new')).toBe('Something new');
  });

  it('offers the preset only when approval alone would make the supplier askable', () => {
    const governanceOnly = [
      'Supplier approval or explicit provisional approval is required',
      'Supplier verification status must be VERIFIED',
      'Supplier outreach readiness must be READY',
      'Supplier compliance status must be CLEARED',
    ];
    expect(presetClearsAllBlockers(governanceOnly)).toBe(true);
    expect(presetClearsAllBlockers([...governanceOnly, 'A verified dispatch contact is required'])).toBe(false);
    expect(presetClearsAllBlockers([...governanceOnly, 'Supplier risk status blocks outreach'])).toBe(false);
    expect(presetClearsAllBlockers([])).toBe(false);
    expect(presetClearsAllBlockers(undefined)).toBe(false);
  });
});
