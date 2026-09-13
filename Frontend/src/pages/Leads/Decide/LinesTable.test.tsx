import { fireEvent, render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { LeadDecisionLineDTO } from '../../../api/services/leadDecisionService';
import type { DecisionMap } from '../Workbench/workbenchRules';
import LinesTable, { type LinesTableProps } from './LinesTable';

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

describe('what the buyer wrote about a line', () => {
  it('folds the specification and the buyer\'s own columns behind one Details link on the line', () => {
    render(
      <LinesTable
        leadId={407}
        lines={[{
          ...line(9),
          itemMaterialCode: '000000002000008965',
          specification: 'MODULE; 16 CHANNEL FAILSAFE RELAY OUTPUT MODULE TYPE, WITH CSA/NRTL/C (CLASS I DIV.2) APPROVAL',
          extras: { 'Approved manufacturers': 'BENTLY-NEVADA LLC (US): P/N 3500/33-02-02, model 3500/33; GE OIL AND GAS THE NETHERLANDS B (NL): P/N 3500/33-02-02', 'Material Type': '9CAT' },
        }]}
        decisions={{}}
        unitOptions={[{ code: 'EA', label: 'Each' }]}
        currencyOptions={[{ code: 'SAR', label: 'Saudi riyal' }]}
        reasonCodes={[]}
        readOnly={false}
        onChange={vi.fn()}
        onOpenDocument={vi.fn()}
      />,
    );

    expect(screen.getByText(/Material 000000002000008965/)).toBeInTheDocument();
    expect(screen.queryByText(/CSA\/NRTL\/C/)).not.toBeInTheDocument();          // folded: the list stays a list
    fireEvent.click(screen.getByRole('button', { name: /Show details for line 00009/ }));
    expect(screen.getByText(/CSA\/NRTL\/C \(CLASS I DIV.2\) APPROVAL/)).toBeInTheDocument();
    expect(screen.getByText(/BENTLY-NEVADA LLC \(US\): P\/N 3500\/33-02-02/)).toBeInTheDocument();
    expect(screen.getByText('Approved manufacturers:')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /Hide details for line 00009/ }));
    expect(screen.queryByText(/CSA\/NRTL\/C/)).not.toBeInTheDocument();
  });
});

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

describe('the unit of measure on a line', () => {
  const unitOptions = [{ code: 'EA', label: 'Each' }, { code: 'SET', label: 'Set' }];
  const table = (lines: LeadDecisionLineDTO[], decisions: DecisionMap, extra: Partial<LinesTableProps> = {}) => (
    <LinesTable
      leadId={407}
      lines={lines}
      decisions={decisions}
      unitOptions={unitOptions}
      currencyOptions={[{ code: 'SAR', label: 'Saudi riyal' }]}
      reasonCodes={[]}
      readOnly={false}
      onChange={vi.fn()}
      onOpenDocument={vi.fn()}
      {...extra}
    />
  );

  it('pre-selects a mapped spelling and names the word the customer wrote', () => {
    render(table(
      [{ ...line(1), normalizedUom: 'EA', sourceFields: [{ field: 'UnitOfMeasure', rawValue: 'nos' }] }],
      { 10: { decision: 'Bid', quantity: 4, unitOfMeasure: 'EA', currency: 'SAR' } },
    ));
    expect(screen.getByRole('combobox', { name: 'Unit for line 00001' })).toHaveTextContent('EA');
    expect(screen.getByText('as written: nos (read as EA)')).toBeInTheDocument();
  });

  it('never shows a blank picker holding a word: the picker asks and the word is said underneath', () => {
    render(table(
      [{ ...line(1), unitOfMeasure: 'Roll', normalizedUom: 'Roll' }],
      { 10: { decision: 'Bid', quantity: 4, unitOfMeasure: 'Roll', currency: 'SAR' } },
    ));
    expect(screen.getByRole('combobox', { name: 'Unit for line 00001' })).toHaveTextContent('Unit');
    expect(screen.getByText('as written: Roll — choose the unit you quote in')).toBeInTheDocument();
  });

  it('says a line has no unit before it is even quoted', () => {
    render(table([{ ...line(1), unitOfMeasure: null }], {}));
    expect(screen.getByRole('group', { name: 'Quote or skip line 00001' })).toBeInTheDocument();
    expect(screen.getByText('not stated in the request')).toBeInTheDocument();
  });

  it('offers one picker for every quoted line without a unit, only when there are several', () => {
    const onBulkUnit = vi.fn();
    const lines = [{ ...line(1), unitOfMeasure: null }, { ...line(2), unitOfMeasure: null }, line(3)];
    const { rerender } = render(table(lines, { 10: { decision: 'Bid' }, 20: { decision: 'Bid' }, 30: { decision: 'Bid', unitOfMeasure: 'EA' } }, { onBulkUnit }));
    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Unit for the 2 quoted lines without one' }));
    fireEvent.click(within(screen.getByRole('listbox')).getByRole('option', { name: 'SET · Set' }));
    expect(onBulkUnit).toHaveBeenCalledWith('SET');

    rerender(table(lines, { 10: { decision: 'Bid' }, 20: { decision: 'Bid', unitOfMeasure: 'SET' }, 30: { decision: 'Bid', unitOfMeasure: 'EA' } }, { onBulkUnit }));
    expect(screen.getByRole('combobox', { name: 'Unit for line 00001' })).toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: /quoted lines without one/ })).toBeNull();
  });

  it('turns to the line\'s page and focuses its unit picker when the next step asks for it', () => {
    const lines = Array.from({ length: 5 }, (_item, index) => ({ ...line(index + 1), unitOfMeasure: null }));
    const decisions: DecisionMap = Object.fromEntries(lines.map((item) => [item.revisionLineId, { decision: 'Bid' as const, quantity: 4, currency: 'SAR' }]));
    const { rerender } = render(table(lines, decisions, { linesPerPage: 2 }));
    expect(screen.getByRole('combobox', { name: 'Unit for line 00001' })).toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: 'Unit for line 00005' })).toBeNull();

    rerender(table(lines, decisions, { linesPerPage: 2, focusUnit: { lineId: 50, nonce: 1 } }));
    expect(screen.getByRole('combobox', { name: 'Unit for line 00005' })).toHaveFocus();
  });
});
