import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { FitCriterionDTO } from '../../../api/services/leadDecisionService';
import FitAssessmentPanel from './FitAssessmentPanel';

const criterion = (code: string, decision: FitCriterionDTO['decision']): FitCriterionDTO => ({
  code,
  label: code,
  decision,
});

const renderPanel = (criteria: FitCriterionDTO[]) => render(
  <FitAssessmentPanel
    assessment={{ version: 0, overallDecision: 'CONDITIONAL', rationale: '', criteria }}
    leadRevisionId={1}
    decisionVersion={0}
    saving={false}
    onSave={vi.fn()}
  />,
);

describe('FitAssessmentPanel', () => {
  it('uses the singular criterion wording', () => {
    renderPanel([criterion('ELIGIBILITY', 'UNKNOWN')]);
    expect(screen.getByText(/1 criterion remains unknown/)).toBeVisible();
  });

  it('uses the plural criteria wording', () => {
    renderPanel([
      criterion('ELIGIBILITY', 'UNKNOWN'),
      criterion('CAPABILITY', 'UNKNOWN'),
      criterion('DELIVERY', 'UNKNOWN'),
      criterion('COMPLIANCE', 'UNKNOWN'),
      criterion('COMMERCIALS', 'UNKNOWN'),
    ]);
    expect(screen.getByText(/5 criteria remain unknown/)).toBeVisible();
  });

  it('omits unknown wording when every criterion is assessed', () => {
    renderPanel([criterion('ELIGIBILITY', 'PASS')]);
    expect(screen.queryByText(/remain(?:s)? unknown/)).not.toBeInTheDocument();
  });
});
