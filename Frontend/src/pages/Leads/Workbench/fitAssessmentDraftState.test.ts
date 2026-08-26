import { describe, expect, it } from 'vitest';
import type { FitAssessmentDTO } from '../../../api/services/leadDecisionService';
import { fitAssessmentFormComplete, initialOverallFitDecision } from './fitAssessmentDraftState';

const unsavedAssessment: FitAssessmentDTO = {
  version: 0,
  overallDecision: 'CONDITIONAL',
  rationale: '',
  criteria: [{ code: 'ELIGIBILITY', label: 'Eligibility', decision: 'UNKNOWN' }],
};

describe('fit assessment draft state', () => {
  it('does not treat a version-zero transport default as a human overall decision', () => {
    expect(initialOverallFitDecision(unsavedAssessment)).toBe('');
    expect(initialOverallFitDecision({ ...unsavedAssessment, version: 2, overallDecision: 'FIT' })).toBe('FIT');
  });

  it('requires a deliberate overall decision in addition to complete criteria and rationale', () => {
    const criteria = [{ code: 'ELIGIBILITY', label: 'Eligibility', decision: 'PASS' as const }];
    expect(fitAssessmentFormComplete('', criteria, 'Reviewed by the bid manager.')).toBe(false);
    expect(fitAssessmentFormComplete('FIT', criteria, 'Reviewed by the bid manager.')).toBe(true);
  });
});
