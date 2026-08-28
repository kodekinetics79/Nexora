import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import RfqRevisionImpactResolutionDialog from './RfqRevisionImpactResolutionDialog';

describe('RfqRevisionImpactResolutionDialog', () => {
  it('requires a meaningful outcome and explicit historical-lineage confirmation', () => {
    render(
      <RfqRevisionImpactResolutionDialog
        open
        rfqLabel="RFQ-1042"
        leadRevisionNumber={3}
        saving={false}
        onCancel={vi.fn()}
        onConfirm={vi.fn()}
      />,
    );

    const submit = screen.getByRole('button', { name: 'Record review complete' });
    expect(submit).toBeDisabled();
    fireEvent.change(screen.getByRole('textbox', { name: /Reconciliation outcome/i }), {
      target: { value: 'Quantity updated.' },
    });
    expect(submit).toBeDisabled();
    fireEvent.click(screen.getByRole('checkbox'));
    expect(submit).toBeEnabled();
  });

  it('submits the trimmed reconciliation outcome without implying an RFQ rewrite', () => {
    const confirm = vi.fn();
    render(
      <RfqRevisionImpactResolutionDialog
        open
        rfqLabel="RFQ-1042"
        leadRevisionNumber={3}
        saving={false}
        onCancel={vi.fn()}
        onConfirm={confirm}
      />,
    );

    expect(screen.getByText(/does not rewrite the original RFQ/i)).toBeVisible();
    fireEvent.change(screen.getByRole('textbox', { name: /Reconciliation outcome/i }), {
      target: { value: '  Customer extended delivery; sourcing remains valid.  ' },
    });
    fireEvent.click(screen.getByRole('checkbox'));
    fireEvent.click(screen.getByRole('button', { name: 'Record review complete' }));

    expect(confirm).toHaveBeenCalledWith('Customer extended delivery; sourcing remains valid.');
  });

  it('clears a prior confirmation when the dialog is closed', () => {
    const props = {
      rfqLabel: 'RFQ-1042',
      leadRevisionNumber: 3,
      saving: false,
      onCancel: vi.fn(),
      onConfirm: vi.fn(),
    };
    const { rerender } = render(<RfqRevisionImpactResolutionDialog open {...props} />);

    fireEvent.change(screen.getByRole('textbox', { name: /Reconciliation outcome/i }), {
      target: { value: 'Customer extended delivery; sourcing remains valid.' },
    });
    fireEvent.click(screen.getByRole('checkbox'));
    expect(screen.getByRole('button', { name: 'Record review complete' })).toBeEnabled();

    rerender(<RfqRevisionImpactResolutionDialog open={false} {...props} />);
    rerender(<RfqRevisionImpactResolutionDialog open {...props} />);

    expect(screen.getByRole('textbox', { name: /Reconciliation outcome/i })).toHaveValue('');
    expect(screen.getByRole('checkbox')).not.toBeChecked();
    expect(screen.getByRole('button', { name: 'Record review complete' })).toBeDisabled();
  });
});
