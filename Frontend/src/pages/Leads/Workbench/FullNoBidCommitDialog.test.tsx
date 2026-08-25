import { fireEvent, render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import FullNoBidCommitDialog from './FullNoBidCommitDialog';

describe('FullNoBidCommitDialog', () => {
  it('requires a configured governed header reason before commit', () => {
    render(<FullNoBidCommitDialog open lineCount={3} reasonCodes={[]} onCancel={vi.fn()} onConfirm={vi.fn()} />);

    expect(screen.getByRole('button', { name: 'Commit full no-bid' })).toBeDisabled();
    expect(screen.getByText(/cannot be committed with free text alone/i)).toBeVisible();
  });

  it('returns the governed header reason and note', () => {
    const confirm = vi.fn();
    render(
      <FullNoBidCommitDialog
        open
        lineCount={3}
        reasonCodes={[{ code: 'COMMERCIAL_NO_FIT', label: 'Commercially not viable', appliesTo: ['NoBid'] }]}
        onCancel={vi.fn()}
        onConfirm={confirm}
      />,
    );

    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Full no-bid reason' }));
    fireEvent.click(within(screen.getByRole('listbox')).getByText('Commercially not viable'));
    fireEvent.change(screen.getByLabelText('Decision note (optional)'), { target: { value: 'Margin and delivery constraints.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Commit full no-bid' }));

    expect(confirm).toHaveBeenCalledWith('COMMERCIAL_NO_FIT', 'Margin and delivery constraints.');
  });

  it('shows every line reason and note before closing the lead', () => {
    render(
      <FullNoBidCommitDialog
        open
        lineCount={2}
        reasonCodes={[{ code: 'OUT_OF_SCOPE', label: 'Outside capability', appliesTo: ['NoBid'] }]}
        lines={[
          { id: 1, revisionLineId: 101, lineItemNo: '10', verificationStatus: 'VERIFIED' },
          { id: 2, revisionLineId: 102, lineItemNo: '20', verificationStatus: 'VERIFIED' },
        ]}
        decisions={{
          101: { decision: 'NoBid', reasonCode: 'OUT_OF_SCOPE', note: 'Unsupported material.' },
          102: { decision: 'NoBid', reasonCode: 'OUT_OF_SCOPE', note: 'Unsupported size.' },
        }}
        onCancel={vi.fn()}
        onConfirm={vi.fn()}
      />,
    );

    expect(screen.getByRole('region', { name: 'Full no-bid line scope' })).toHaveTextContent('Line 10 · No-bid');
    expect(screen.getByText('Unsupported material.')).toBeVisible();
    expect(screen.getByText('Unsupported size.')).toBeVisible();
  });

  it('paginates large line scopes instead of rendering every confirmation card', () => {
    const lines = Array.from({ length: 30 }, (_, index) => ({
      id: index + 1,
      revisionLineId: index + 101,
      lineItemNo: String(index + 1),
      verificationStatus: 'VERIFIED',
    }));
    const decisions = Object.fromEntries(lines.map((line) => [line.revisionLineId, {
      decision: 'NoBid' as const,
      reasonCode: 'OUT_OF_SCOPE',
    }]));
    render(
      <FullNoBidCommitDialog
        open
        lineCount={30}
        reasonCodes={[{ code: 'OUT_OF_SCOPE', label: 'Outside capability', appliesTo: ['NoBid'] }]}
        lines={lines}
        decisions={decisions}
        onCancel={vi.fn()}
        onConfirm={vi.fn()}
      />,
    );

    const scope = screen.getByRole('region', { name: 'Full no-bid line scope' });
    expect(within(scope).getAllByText(/· No-bid$/)).toHaveLength(25);
    expect(within(scope).queryByText('Line 30 · No-bid')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Go to next page' }));
    expect(within(scope).getByText('Line 30 · No-bid')).toBeVisible();
  });
});
