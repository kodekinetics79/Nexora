import { describe, expect, it } from 'vitest';
import {
  conversionBlockers,
  formatLeadStatus,
  isConvertibleLeadStatus,
} from './leadConversionGate';

describe('isConvertibleLeadStatus', () => {
  it('accepts the canonical qualified state', () => {
    expect(isConvertibleLeadStatus('QUALIFIED')).toBe(true);
  });

  it('accepts the legacy synonyms the server canonicalizes to QUALIFIED', () => {
    expect(isConvertibleLeadStatus('ACCEPTED')).toBe(true);
    expect(isConvertibleLeadStatus('LEAD_ACCEPTED')).toBe(true);
  });

  it('rejects every other lifecycle state', () => {
    for (const code of ['RECEIVED', 'UNASSIGNED', 'ASSIGNED', 'UNDER_REVIEW', 'DISQUALIFIED', 'CONVERTED_TO_RFQ', 'CANCELLED']) {
      expect(isConvertibleLeadStatus(code)).toBe(false);
    }
  });

  it('treats an absent status as not convertible', () => {
    expect(isConvertibleLeadStatus(null)).toBe(false);
    expect(isConvertibleLeadStatus(undefined)).toBe(false);
    expect(isConvertibleLeadStatus('  ')).toBe(false);
  });
});

describe('formatLeadStatus', () => {
  it('turns a status code into a readable phrase', () => {
    expect(formatLeadStatus('UNDER_REVIEW')).toBe('Under review');
    expect(formatLeadStatus('CONVERTED_TO_RFQ')).toBe('Converted to rfq');
  });

  it('never renders an empty label', () => {
    expect(formatLeadStatus(null)).toBe('Unknown');
  });
});

describe('conversionBlockers', () => {
  it('returns nothing for a qualified, clean lead', () => {
    expect(conversionBlockers({
      lifecycleStatusCode: 'QUALIFIED',
      duplicateStatus: 'not_duplicate',
      requiresCommercialReview: true,
      commercialFactsVerified: true,
    })).toEqual([]);
  });

  it('blocks an unqualified lead and names its current status', () => {
    const [blocker, ...rest] = conversionBlockers({ lifecycleStatusCode: 'UNDER_REVIEW' });
    expect(rest).toHaveLength(0);
    expect(blocker.key).toBe('lifecycle');
    expect(blocker.action).toBe('qualify');
    expect(blocker.detail).toContain('Under review');
    expect(blocker.shortReason).toContain('Under review');
  });

  it('does not block while the lifecycle state is unknown', () => {
    expect(conversionBlockers({ lifecycleStatusCode: undefined })).toEqual([]);
    expect(conversionBlockers({ lifecycleStatusCode: null })).toEqual([]);
  });

  it('blocks a suspected duplicate and points at the original lead', () => {
    const [blocker] = conversionBlockers({
      lifecycleStatusCode: 'QUALIFIED',
      duplicateStatus: 'suspected',
      duplicateOfLeadId: 4211,
    });
    expect(blocker.key).toBe('duplicate');
    expect(blocker.severity).toBe('warning');
    expect(blocker.title).toContain('Lead #4211');
    expect(blocker.action).toBe('open-lead');
  });

  it('treats a confirmed duplicate as an error and a resolved one as clear', () => {
    expect(conversionBlockers({ lifecycleStatusCode: 'QUALIFIED', duplicateStatus: 'confirmed' })[0].severity)
      .toBe('error');
    expect(conversionBlockers({ lifecycleStatusCode: 'QUALIFIED', duplicateStatus: 'not_duplicate' })).toEqual([]);
    expect(conversionBlockers({ lifecycleStatusCode: 'QUALIFIED', duplicateStatus: null })).toEqual([]);
  });

  it('blocks only when commercial review is required and unverified', () => {
    const [blocker] = conversionBlockers({
      lifecycleStatusCode: 'QUALIFIED',
      requiresCommercialReview: true,
      commercialFactsVerified: false,
    });
    expect(blocker.key).toBe('commercial-review');
    expect(blocker.action).toBe('open-extraction-review');

    expect(conversionBlockers({
      lifecycleStatusCode: 'QUALIFIED',
      requiresCommercialReview: false,
      commercialFactsVerified: false,
    })).toEqual([]);
  });

  it('reports every blocker at once, in the order the server applies them', () => {
    const keys = conversionBlockers({
      lifecycleStatusCode: 'ASSIGNED',
      duplicateStatus: 'suspected',
      duplicateOfLeadId: 7,
      requiresCommercialReview: true,
      commercialFactsVerified: false,
    }).map((blocker) => blocker.key);
    expect(keys).toEqual(['lifecycle', 'duplicate', 'commercial-review']);
  });
});
