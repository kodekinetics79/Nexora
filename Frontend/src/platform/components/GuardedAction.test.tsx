import { describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import GuardedAction from './GuardedAction';

/**
 * The point of this component is that "switched off" and "the operator has been told why" cannot
 * come apart. These tests hold that line at runtime; the type has no `disabled` prop, which holds
 * it at compile time.
 */
describe('GuardedAction', () => {
  it('is actionable when nothing blocks it', () => {
    const onClick = vi.fn();
    render(<GuardedAction label="Compute statement" onClick={onClick} />);
    fireEvent.click(screen.getByRole('button', { name: 'Compute statement' }));
    expect(onClick).toHaveBeenCalledOnce();
  });

  it('prints the reason IN THE PAGE, not in a tooltip, when blocked', () => {
    render(
      <GuardedAction
        label="Compute statement"
        onClick={vi.fn()}
        blockers={[{ reason: 'Choose which customer this statement is for.' }]}
      />,
    );
    expect(screen.getByRole('button', { name: 'Compute statement' })).toBeDisabled();
    // Present without any hover, which is the whole point: a tooltip is invisible to a keyboard
    // user, to a touch screen, and to anybody who does not think to hover a dead control.
    expect(screen.getByText('Choose which customer this statement is for.')).toBeVisible();
  });

  it('lists every reason when more than one thing is outstanding', () => {
    render(
      <GuardedAction
        label="Compute statement"
        onClick={vi.fn()}
        blockers={[
          { reason: 'Choose which customer this statement is for.' },
          { reason: 'The period must be a month, written as YYYY-MM.' },
        ]}
      />,
    );
    expect(screen.getByText('2 things to do first')).toBeVisible();
    expect(screen.getByText('The period must be a month, written as YYYY-MM.')).toBeVisible();
  });

  it('offers the control that clears a blocker, turning a dead end into a next step', () => {
    const fix = vi.fn();
    render(
      <GuardedAction
        label="Activate tenant"
        onClick={vi.fn()}
        blockers={[{ reason: 'No rate card is pinned.', fix: { label: 'Pin a rate card', onClick: fix } }]}
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Pin a rate card' }));
    expect(fix).toHaveBeenCalledOnce();
  });

  it('ignores a blank reason rather than going silently dead', () => {
    // A caller that computes reasons from data can produce an empty string. Disabling on that
    // would reintroduce exactly the defect this component exists to remove, so it does not count.
    render(<GuardedAction label="Save" onClick={vi.fn()} blockers={[{ reason: '   ' }]} />);
    expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled();
  });
});
