import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { FitCriterionDTO } from '../../../api/services/leadDecisionService';
import FitAssessmentPanel from './FitAssessmentPanel';

vi.mock('@mui/icons-material', () => ({ SaveOutlined: () => null }));

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
  it('does not anchor a new assessment to a default overall conclusion', () => {
    renderPanel([criterion('ELIGIBILITY', 'PASS')]);

    expect(screen.getByRole('combobox', { name: 'Overall decision' })).toHaveTextContent('Select an overall decision');
    expect(screen.getByText('No overall decision is assumed.')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Save fit assessment' })).toBeDisabled();
  });

  it('gives criterion controls unique names, grouping, and requirement descriptions', () => {
    renderPanel([{ ...criterion('ELIGIBILITY', 'CONCERN'), label: 'Eligibility', description: 'Customer and geography fit.' }]);

    expect(screen.getByRole('group', { name: 'Eligibility' })).toHaveAccessibleDescription('Customer and geography fit.');
    expect(screen.getByRole('combobox', { name: /Eligibility Assessment/ })).toHaveAccessibleDescription(
      'Choose Pass, Concern, or Not applicable before saving.',
    );
    const note = screen.getByRole('textbox', { name: 'Eligibility evidence or note' });
    expect(note).toHaveAccessibleDescription('Required for Concern; enter at least 5 characters.');
    expect(note).toHaveAttribute('aria-invalid', 'true');
  });

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
