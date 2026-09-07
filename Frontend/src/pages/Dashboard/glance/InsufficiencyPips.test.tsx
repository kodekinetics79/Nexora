import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import InsufficiencyPips from './InsufficiencyPips';

describe('InsufficiencyPips', () => {
  it('shows how far off the threshold is, in a sentence and in pips', () => {
    render(<InsufficiencyPips have={3} need={5} label="A win rate" />);

    expect(screen.getByText('A win rate is published once 5 quotes have been decided. You have 3.')).toBeInTheDocument();
    expect(screen.getAllByTestId('pip-filled')).toHaveLength(3);
    expect(screen.getAllByTestId('pip-empty')).toHaveLength(2);
    expect(screen.getByRole('img', { name: '3 of 5' })).toBeInTheDocument();
  });

  // The empty case is the pre-launch default, and it is the one the old grey dash got wrong: with
  // nothing decided the reader still has to see the threshold and the sentence.
  it('renders the full threshold, and never a dash, with nothing counted yet', () => {
    const { container } = render(<InsufficiencyPips have={0} need={5} label="A win rate" />);

    expect(screen.getByText(/You have 0\./)).toBeInTheDocument();
    expect(screen.queryAllByTestId('pip-filled')).toHaveLength(0);
    expect(screen.getAllByTestId('pip-empty')).toHaveLength(5);
    expect(container.textContent).not.toMatch(/[—–-]\s*$/);
  });

  it('does not overfill when the count has passed the threshold', () => {
    render(<InsufficiencyPips have={9} need={5} label="A win rate" />);

    expect(screen.getAllByTestId('pip-filled')).toHaveLength(5);
    expect(screen.queryAllByTestId('pip-empty')).toHaveLength(0);
    expect(screen.getByText(/You have 9\./)).toBeInTheDocument();
  });

  it('takes the counted noun from the caller', () => {
    render(<InsufficiencyPips have={1} need={4} label="A median response time" unitPhrase="suppliers have replied" />);

    expect(screen.getByText('A median response time is published once 4 suppliers have replied. You have 1.')).toBeInTheDocument();
  });
});
