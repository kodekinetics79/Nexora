import { fireEvent, render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { LeadDecisionLineDTO } from '../../../api/services/leadDecisionService';
import LinesTable from './LinesTable';

const line = (id: number): LeadDecisionLineDTO => ({
  id,
  revisionLineId: id * 10,
  lineItemNo: String(id).padStart(5, '0'),
  description: `Item ${id}`,
  quantity: 4,
  unitOfMeasure: 'EA',
  currency: 'SAR',
  verificationStatus: 'VERIFIED',
});

const renderTable = (count: number, decisions: Record<number, { decision: 'Bid' | 'NoBid' | 'Pending' }> = {}) =>
  render(
    <LinesTable
      leadId={407}
      lines={Array.from({ length: count }, (_item, index) => line(index + 1))}
      decisions={decisions}
      unitOptions={[{ code: 'EA', label: 'Each' }]}
      currencyOptions={[{ code: 'SAR', label: 'Saudi riyal' }]}
      reasonCodes={[{ code: 'NO_STOCK', label: 'Item unavailable', appliesTo: ['NoBid'] }]}
      readOnly={false}
      onChange={vi.fn()}
      onOpenDocument={vi.fn()}
      linesPerPage={2}
    />,
  );

describe('a long bid list', () => {
  it('draws a page of lines at a time and says where you are', () => {
    renderTable(5);

    expect(screen.getByText('Lines 1–2 of 5')).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'Quote or skip line 00001' })).toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'Quote or skip line 00003' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'next page of lines' }));
    expect(screen.getByText('Lines 3–4 of 5')).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'Quote or skip line 00003' })).toBeInTheDocument();
  });

  it('shows the decision already made on a line that was not drawn until now', () => {
    renderTable(5, { 30: { decision: 'Bid' }, 40: { decision: 'NoBid' } });
    fireEvent.click(screen.getByRole('button', { name: 'next page of lines' }));

    expect(within(screen.getByRole('group', { name: 'Quote or skip line 00003' })).getByRole('button', { name: 'Quote' })).toHaveAttribute('aria-pressed', 'true');
    expect(within(screen.getByRole('group', { name: 'Quote or skip line 00004' })).getByRole('button', { name: 'Skip' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('does not page a short list', () => {
    renderTable(2);
    expect(screen.queryByText(/Lines 1–2 of/)).toBeNull();
  });
});
