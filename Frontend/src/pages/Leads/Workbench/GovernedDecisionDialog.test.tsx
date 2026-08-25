import { fireEvent, render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import GovernedDecisionDialog from './GovernedDecisionDialog';

describe('GovernedDecisionDialog', () => {
  it('cannot apply a no-bid with free text but no governed reason', () => {
    const confirm = vi.fn();
    render(
      <GovernedDecisionDialog
        open
        decision="NoBid"
        lineCount={4}
        reasonCodes={[]}
        onCancel={vi.fn()}
        onConfirm={confirm}
      />,
    );

    fireEvent.change(screen.getByLabelText('Decision note (optional)'), { target: { value: 'No supplier source' } });
    expect(screen.getByRole('button', { name: 'Apply decision' })).toBeDisabled();
    expect(screen.getByText(/No governed reason is configured/)).toBeVisible();
  });

  it('records the selected governed reason and optional note', () => {
    const confirm = vi.fn();
    render(
      <GovernedDecisionDialog
        open
        decision="Clarify"
        lineCount={1}
        reasonCodes={[{ code: 'SPEC_MISSING', label: 'Specification missing', appliesTo: ['Clarify'] }]}
        onCancel={vi.fn()}
        onConfirm={confirm}
      />,
    );

    fireEvent.mouseDown(screen.getByRole('combobox', { name: /Governed reason/ }));
    fireEvent.click(within(screen.getByRole('listbox')).getByText('Specification missing'));
    fireEvent.change(screen.getByLabelText('Decision note (optional)'), { target: { value: 'Need the buyer drawing.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Apply decision' }));

    expect(confirm).toHaveBeenCalledWith('SPEC_MISSING', 'Need the buyer drawing.');
  });
});
