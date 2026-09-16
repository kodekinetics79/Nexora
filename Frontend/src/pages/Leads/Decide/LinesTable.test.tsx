import { fireEvent, render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { LeadDecisionLineDTO } from '../../../api/services/leadDecisionService';
import type { DecisionMap } from '../Workbench/workbenchRules';
import LinesTable, { readOnlyLineChip, type LinesTableProps } from './LinesTable';

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

describe('why a line is skipped', () => {
  it('offers only reasons a line is not quoted, never how a quote ended', () => {
    render(
      <LinesTable
        leadId={407}
        lines={[line(1)]}
        decisions={{ 10: { decision: 'NoBid' } }}
        unitOptions={[{ code: 'EA', label: 'Each' }]}
        currencyOptions={[{ code: 'SAR', label: 'Saudi riyal' }]}
        reasonCodes={[
          { code: 'OUT_OF_SCOPE', label: 'Outside approved product scope', appliesTo: ['NoBid'] },
          { code: 'PRICE', label: 'Price too high', appliesTo: ['NoBid', 'Decline'] },
          { code: 'LOST_COMPETITOR', label: 'Lost to competitor', appliesTo: ['Decline'] },
          { code: 'AUTO_EXPIRED', label: 'Expired automatically', appliesTo: ['Decline'] },
        ]}
        readOnly={false}
        onChange={vi.fn()}
        onOpenDocument={vi.fn()}
      />,
    );

    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Why skip line 00001' }));
    const options = within(screen.getByRole('listbox')).getAllByRole('option').map((option) => option.textContent);
    expect(options).toEqual(['Outside approved product scope', 'Price too high']);
  });
});

describe('a line a person has not yet checked', () => {
  const table = (lines: LeadDecisionLineDTO[]) => (
    <LinesTable
      leadId={407}
      lines={lines}
      decisions={{ 10: { decision: 'Bid', quantity: 4, unitOfMeasure: 'EA', currency: 'SAR' } }}
      unitOptions={[{ code: 'EA', label: 'Each' }]}
      currencyOptions={[{ code: 'SAR', label: 'Saudi riyal' }]}
      reasonCodes={[]}
      readOnly={false}
      onChange={vi.fn()}
      onOpenDocument={vi.fn()}
    />
  );

  it('says a line read from the document cells was read, not that Nexora is unsure of it', () => {
    // A native spreadsheet parse: item, quantity and unit are each an exact cell.
    render(table([{ ...line(1), verificationStatus: 'NEEDS_CHECK', sourceEvidenceComplete: true }]));
    expect(screen.getByText(/Read from the document; not yet checked by a person\./)).toBeInTheDocument();
    expect(screen.queryByText(/not sure it read this line/)).toBeNull();
    // The check stays one click away; it is offered, not demanded as doubt.
    expect(screen.getByRole('button', { name: 'Check line 00001' })).toBeInTheDocument();
  });

  it('only doubts a line the retained evidence does not cover', () => {
    render(table([{ ...line(1), verificationStatus: 'NEEDS_CHECK', sourceEvidenceComplete: false }]));
    expect(screen.getByText(/Nexora is not sure it read this line correctly\./)).toBeInTheDocument();
    expect(screen.queryByText(/Read from the document/)).toBeNull();
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
    expect(screen.getByText('2 lines marked to quote need a unit.')).toBeInTheDocument();
    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Unit for the 2 lines marked to quote without one' }));
    fireEvent.click(within(screen.getByRole('listbox')).getByRole('option', { name: 'SET · Set' }));
    expect(onBulkUnit).toHaveBeenCalledWith('SET');

    rerender(table(lines, { 10: { decision: 'Bid' }, 20: { decision: 'Bid', unitOfMeasure: 'SET' }, 30: { decision: 'Bid', unitOfMeasure: 'EA' } }, { onBulkUnit }));
    expect(screen.getByRole('combobox', { name: 'Unit for line 00001' })).toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: /lines marked to quote without one/ })).toBeNull();
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

/**
 * A locked or view-only record reported "Quoted" on every line marked Bid, on requests that had not
 * even become an RFQ. The chip now reports what is true for the record: a choice, whether the line
 * went into the RFQ, or that the lines arrived after it.
 */
describe('a read-only line says what happened to it, in job words', () => {
  const reasonCodes: LinesTableProps['reasonCodes'] = [{ code: 'NO_STOCK', label: 'Item unavailable', appliesTo: ['NoBid'] }];
  const mixed: DecisionMap = {
    10: { decision: 'Bid', quantity: 4, unitOfMeasure: 'EA', currency: 'SAR' },
    20: { decision: 'NoBid', reasonCode: 'NO_STOCK' },
    30: { decision: 'Pending' },
    40: { decision: 'Clarify' },
  };
  /** The lines as the server sends them: what was saved for each line is on the line itself. */
  const recorded = (saved: DecisionMap): LeadDecisionLineDTO[] => [line(1), line(2), line(3), line(4)].map((item) => {
    const entry = saved[item.revisionLineId];
    return entry ? { ...item, participation: { decision: entry.decision, reasonCode: entry.reasonCode ?? null } } : item;
  });
  const renderReadOnly = (decisions: DecisionMap, extra: Partial<LinesTableProps> = {}, lines: LeadDecisionLineDTO[] = recorded(decisions)) => render(
    <LinesTable
      leadId={407}
      lines={lines}
      decisions={decisions}
      unitOptions={[{ code: 'EA', label: 'Each' }]}
      currencyOptions={[{ code: 'SAR', label: 'Saudi riyal' }]}
      reasonCodes={reasonCodes}
      readOnly
      onChange={vi.fn()}
      onOpenDocument={vi.fn()}
      {...extra}
    />,
  );
  const decisionHeader = () =>
    within(screen.getByRole('table', { name: 'Lines the customer asked for' })).getAllByRole('columnheader').at(-1);
  const chipRoot = (label: HTMLElement) => label.closest('.MuiChip-root');

  it('reports the choice on a record nobody can change here, and never says "Quoted"', () => {
    renderReadOnly(mixed);

    expect(screen.getByText('Marked to quote')).toBeInTheDocument();
    expect(screen.getByText('Skipped · Item unavailable')).toBeInTheDocument();
    expect(screen.getAllByText('Not chosen yet')).toHaveLength(2);        // Pending and Clarify
    expect(screen.queryByText('Quoted')).toBeNull();
    expect(screen.queryByText('Undecided')).toBeNull();
    expect(screen.queryByText(/NO_STOCK/)).toBeNull();
    expect(decisionHeader()).toHaveTextContent('Choice');
    expect(screen.queryByRole('group', { name: 'Quote or skip line 00001' })).toBeNull();
  });

  // The choice on screen and the saved record must be able to disagree in a test. When every
  // fixture built one from the other, a chip reading the wrong source in either mode still passed.
  it('follows the choice on screen before an RFQ, and the saved record once one exists', () => {
    const onScreen: DecisionMap = { 10: { decision: 'Bid' } };
    const saved = recorded({ 10: { decision: 'NoBid' } });

    const first = renderReadOnly(onScreen, {}, saved);
    expect(screen.getByText('Marked to quote')).toBeInTheDocument();
    expect(screen.queryByText(/^Skipped/)).toBeNull();
    first.unmount();

    renderReadOnly(onScreen, { chipMode: 'rfq', rfqRef: 'RFQ-2026-0417', currentRevisionNumber: 2, promotedRevisionNumber: 2 }, saved);
    expect(screen.queryByText('Went into the RFQ')).toBeNull();
    expect(screen.getAllByText(/^Left out/).length).toBeGreaterThan(0);
  });

  it('says "Skipped" alone, never the raw code, when the reason has no words', () => {
    renderReadOnly({ 10: { decision: 'NoBid', reasonCode: 'RETIRED_REASON' } });
    expect(screen.getByText('Skipped')).toBeInTheDocument();
    expect(screen.queryByText(/RETIRED_REASON/)).toBeNull();
  });

  it('on a request that became an RFQ, says which lines went into it and which were left out', () => {
    renderReadOnly(mixed, { chipMode: 'rfq', rfqRef: 'RFQ-2026-0417', currentRevisionNumber: 2, promotedRevisionNumber: 2 });

    expect(screen.getByText('Went into the RFQ')).toBeInTheDocument();
    expect(screen.getByText('Left out · Item unavailable')).toBeInTheDocument();
    expect(screen.getAllByText('Left out')).toHaveLength(2);
    expect(screen.queryByText('Marked to quote')).toBeNull();
    expect(screen.queryByText('Quoted')).toBeNull();
    expect(decisionHeader()).toHaveTextContent('RFQ');
  });

  it('on a revision that arrived after the RFQ, says so on every line and explains on hover', async () => {
    renderReadOnly(mixed, { chipMode: 'newer-revision', rfqRef: 'RFQ-2026-0417', currentRevisionNumber: 3, promotedRevisionNumber: 2 });

    const explanation = 'Revision 3 arrived after RFQ RFQ-2026-0417 was created from revision 2. Its lines are not decided one by one.';
    const chips = screen.getAllByText('Newer revision');
    expect(chips).toHaveLength(4);
    expect(screen.queryByText('Went into the RFQ')).toBeNull();
    expect(screen.queryByText('Marked to quote')).toBeNull();
    expect(decisionHeader()).toHaveTextContent('RFQ');
    const first = chipRoot(chips[0]);
    expect(first).toHaveAttribute('title', explanation);
    fireEvent.mouseOver(first!);
    expect(await screen.findByRole('tooltip')).toHaveTextContent(explanation);
  });

  it('on an RFQ made before choices were recorded, keeps any recorded choice and says where none was', () => {
    renderReadOnly(mixed, { chipMode: 'legacy' });

    expect(screen.getByText('Marked to quote')).toBeInTheDocument();
    expect(screen.getByText('Skipped · Item unavailable')).toBeInTheDocument();
    expect(screen.getAllByText('Not recorded here')).toHaveLength(2);
    expect(chipRoot(screen.getAllByText('Not recorded here')[0])).toHaveAttribute(
      'title',
      'This request became an RFQ before this screen recorded decisions. The RFQ shows which lines it holds.',
    );
    expect(decisionHeader()).toHaveTextContent('Choice');
  });

  // The page fills its choices after the first paint. A request that became an RFQ painted "Left
  // out" on every line in that frame, then "Went into the RFQ": a false claim, however brief.
  it('on a request that became an RFQ, reads the saved record from the first paint, not the choices on screen', () => {
    const saved = recorded({ 10: { decision: 'Bid' }, 20: { decision: 'Bid' }, 30: { decision: 'NoBid', reasonCode: 'NO_STOCK' }, 40: { decision: 'Bid' } });
    renderReadOnly({}, { chipMode: 'rfq', rfqRef: 'RFQ-2026-0417', currentRevisionNumber: 1, promotedRevisionNumber: 1 }, saved);

    expect(screen.getAllByText('Went into the RFQ')).toHaveLength(3);
    expect(screen.getByText('Left out · Item unavailable')).toBeInTheDocument();
    expect(screen.queryByText('Left out')).toBeNull();
  });

  it('on an RFQ made before choices were recorded, reads the saved record from the first paint', () => {
    renderReadOnly({}, { chipMode: 'legacy' }, recorded(mixed));

    expect(screen.getByText('Marked to quote')).toBeInTheDocument();
    expect(screen.getByText('Skipped · Item unavailable')).toBeInTheDocument();
    expect(screen.getAllByText('Not recorded here')).toHaveLength(2);
  });

  it('keeps the question as the header while the lines can still be changed', () => {
    renderTable(2);
    expect(within(screen.getByRole('table', { name: 'Lines the customer asked for' })).getAllByRole('columnheader').at(-1))
      .toHaveTextContent('Quote it?');
  });

  it('colours only what is true: filled for a line in the RFQ, outlined for a line marked to quote', () => {
    expect(readOnlyLineChip('rfq', 'Bid', null)).toEqual({ label: 'Went into the RFQ', color: 'primary', variant: 'filled' });
    expect(readOnlyLineChip('choice', 'Bid', null)).toEqual({ label: 'Marked to quote', color: 'primary', variant: 'outlined' });
    expect(readOnlyLineChip('choice', 'NoBid', 'Item unavailable')).toEqual({ label: 'Skipped · Item unavailable', color: 'default', variant: 'outlined' });
    expect(readOnlyLineChip('rfq', 'NoBid', null)).toEqual({ label: 'Left out', color: 'default', variant: 'outlined' });
  });

  it('still explains a newer revision when the numbers are not known', () => {
    expect(readOnlyLineChip('newer-revision', 'Bid', null).tooltip)
      .toBe('A newer revision arrived after the RFQ was created. Its lines are not decided one by one.');
  });
});

describe('the per-line document check', () => {
  it('is named for its line, so the page keeps one "Check the document"', () => {
    const onOpenDocument = vi.fn();
    const unsure = { ...line(1), verificationStatus: 'NEEDS_CHECK' };
    render(
      <LinesTable
        leadId={407}
        lines={[unsure]}
        decisions={{ 10: { decision: 'Bid', quantity: 4, unitOfMeasure: 'EA', currency: 'SAR' } }}
        unitOptions={[{ code: 'EA', label: 'Each' }]}
        currencyOptions={[{ code: 'SAR', label: 'Saudi riyal' }]}
        reasonCodes={[]}
        readOnly={false}
        onChange={vi.fn()}
        onOpenDocument={onOpenDocument}
      />,
    );

    expect(screen.queryByRole('button', { name: 'Check the document' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Check line 00001' }));
    expect(onOpenDocument).toHaveBeenCalledWith(unsure);
  });
});
