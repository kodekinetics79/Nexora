import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import DestructiveConfirmDialog from './DestructiveConfirmDialog';

const renderDialog = (blocked: boolean) => {
  const onConfirm = vi.fn();
  render(
    <DestructiveConfirmDialog
      open
      title="Permanently delete Acme"
      description="This cannot be undone."
      confirmationRequired="Acme"
      confirmLabel="Confirm permanent deletion"
      blocked={blocked}
      onClose={vi.fn()}
      onConfirm={onConfirm}
    />,
  );
  fireEvent.change(screen.getByLabelText(/Why is this being done\?/), {
    target: { value: 'Contract ended after governed customer offboarding.' },
  });
  fireEvent.change(screen.getByLabelText(/Type Acme to confirm/), {
    target: { value: 'Acme' },
  });
  return onConfirm;
};

describe('DestructiveConfirmDialog external prerequisite', () => {
  it('cannot confirm while the server blast-radius preview is unavailable', () => {
    const onConfirm = renderDialog(true);
    const confirm = screen.getByRole('button', { name: 'Confirm permanent deletion' });

    expect(confirm).toBeDisabled();
    fireEvent.click(confirm);
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it('enables only after the external prerequisite and local confirmations are satisfied', () => {
    const onConfirm = renderDialog(false);
    const confirm = screen.getByRole('button', { name: 'Confirm permanent deletion' });

    expect(confirm).toBeEnabled();
    fireEvent.click(confirm);
    expect(onConfirm).toHaveBeenCalledWith({
      reason: 'Contract ended after governed customer offboarding.',
      confirmation: 'Acme',
    });
  });
});
