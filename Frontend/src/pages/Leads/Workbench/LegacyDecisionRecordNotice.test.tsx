import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import LegacyDecisionRecordNotice from './LegacyDecisionRecordNotice';

describe('LegacyDecisionRecordNotice', () => {
  it('explains the read-only compatibility record and opens the existing RFQ', () => {
    const openRfq = vi.fn();
    render(
      <LegacyDecisionRecordNotice
        message="This historical decision record is read-only; no lineage was fabricated."
        actionLabel="Open existing RFQ"
        onOpenRfq={openRfq}
      />,
    );

    expect(screen.getByText('Historical RFQ decision record')).toBeInTheDocument();
    expect(screen.getByText(/read-only/i)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Open existing RFQ' }));
    expect(openRfq).toHaveBeenCalledOnce();
  });

  it('does not offer an RFQ action when the viewer lacks permission', () => {
    render(<LegacyDecisionRecordNotice message="This historical decision record is read-only." />);

    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });
});
