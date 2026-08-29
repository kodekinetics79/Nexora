import { render, screen } from '@testing-library/react';
import { Button } from '@mui/material';
import { describe, expect, it } from 'vitest';
import RoleGate from './RoleGate';

describe('RoleGate', () => {
  it('keeps a disabled action explanation keyboard-focusable', () => {
    render(
      <RoleGate allowed={false} requirement="A Platform Owner must perform this action.">
        {(disabled) => <Button disabled={disabled}>Delete tenant</Button>}
      </RoleGate>,
    );

    expect(screen.getByRole('button', { name: 'Delete tenant' })).toBeDisabled();
    const explanation = screen.getByRole('button', {
      name: 'Why this action is unavailable: A Platform Owner must perform this action.',
    });
    expect(explanation).toBeEnabled();
    expect(screen.getAllByRole('button')).toHaveLength(2);
  });

  it('does not add an extra focus stop when the action is allowed', () => {
    render(
      <RoleGate allowed requirement="A Platform Owner must perform this action.">
        {(disabled) => <Button disabled={disabled}>Delete tenant</Button>}
      </RoleGate>,
    );

    expect(screen.getByRole('button', { name: 'Delete tenant' })).toBeEnabled();
    expect(screen.queryByLabelText('A Platform Owner must perform this action.')).not.toBeInTheDocument();
  });
});
