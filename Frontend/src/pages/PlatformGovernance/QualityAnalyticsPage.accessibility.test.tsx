import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { QualityMetric } from '../../api/services/platformGovernanceService';
import { QualityMetricCard, QualityRecommendationButton } from './QualityAnalyticsActions';

const metric: QualityMetric = {
  key: 'source-coverage',
  label: 'Source coverage',
  value: 98,
  unit: '%',
  numerator: 98,
  denominator: 100,
  definition: 'Records with exact source evidence.',
  evidenceStatus: 'Measured',
  drilldownKey: 'source-coverage',
};

describe('Quality Analytics interactive surfaces', () => {
  it('exposes a named metric toggle with native keyboard activation', () => {
    const onSelect = vi.fn();
    render(<QualityMetricCard metric={metric} selected={false} onSelect={onSelect} />);

    const trigger = screen.getByRole('button', { name: 'View evidence for Source coverage' });
    expect(trigger.tagName).toBe('BUTTON');
    expect(trigger).toHaveAttribute('type', 'button');
    expect(trigger).toHaveAttribute('aria-pressed', 'false');
    trigger.focus();
    expect(trigger).toHaveFocus();

    fireEvent.click(trigger, { detail: 0 });
    expect(onSelect).toHaveBeenCalledTimes(1);
  });

  it('gives each recommendation a specific accessible action name', () => {
    const onSelect = vi.fn();
    render(
      <QualityRecommendationButton
        title="Review unmatched lines"
        priority="High"
        recommendation="Confirm the customer request before promotion."
        evidence="Three lines have no exact source span."
        onSelect={onSelect}
      />,
    );

    const trigger = screen.getByRole('button', { name: 'Review recommendation: Review unmatched lines' });
    expect(trigger.tagName).toBe('BUTTON');
    fireEvent.click(trigger, { detail: 0 });
    expect(onSelect).toHaveBeenCalledTimes(1);
  });
});
