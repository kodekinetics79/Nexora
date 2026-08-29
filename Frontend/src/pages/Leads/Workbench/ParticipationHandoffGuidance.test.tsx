import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import ParticipationHandoffGuidance from './ParticipationHandoffGuidance';

describe('ParticipationHandoffGuidance', () => {
  it('tells a representative exactly how a persisted draft reaches the assigned manager', () => {
    render(<ParticipationHandoffGuidance canEdit isManager={false} participationStatus="NONE" />);

    const guidance = screen.getByRole('alert');
    expect(guidance).toHaveTextContent('Prepare the participation scope for manager review');
    expect(guidance).toHaveTextContent('persisted draft will be available to your assigned manager in their managed scope');
    expect(guidance).not.toHaveTextContent(/notification|queue|SLA/i);
  });

  it('tells a manager to review the exact commercial scope of an existing draft', () => {
    render(<ParticipationHandoffGuidance canEdit isManager participationStatus="DRAFT" />);

    const guidance = screen.getByRole('alert');
    expect(guidance).toHaveTextContent('Participation draft requires manager review');
    expect(guidance).toHaveTextContent('source evidence');
    expect(guidance).toHaveTextContent('No-bid exclusion');
  });

  it('does not mislabel committed or read-only records as awaiting review', () => {
    const { rerender } = render(
      <ParticipationHandoffGuidance canEdit isManager participationStatus="COMMITTED" />,
    );
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();

    rerender(<ParticipationHandoffGuidance canEdit={false} isManager={false} participationStatus="DRAFT" />);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
