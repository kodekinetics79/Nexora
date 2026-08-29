import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { LeadDecisionLineDTO } from '../../../api/services/leadDecisionService';
import LeadValidationGrid from './LeadValidationGrid';

vi.mock('@mui/x-data-grid', () => ({
  DataGrid: ({ columns, checkboxSelection }: {
    columns: Array<{ field: string; headerName?: string }>;
    checkboxSelection: boolean;
  }) => (
    <div data-testid="grid" data-checkbox-selection={String(checkboxSelection)}>
      {columns.map((column) => <span key={column.field}>{column.headerName}</span>)}
    </div>
  ),
}));

const line: LeadDecisionLineDTO = {
  id: 1,
  revisionLineId: 101,
  lineItemNo: '10',
  sourceText: '10 anchor bolts',
  productName: 'Anchor bolt',
  manufacturerPartNumber: 'AB-10',
  quantity: 10,
  unitOfMeasure: 'EA',
  currency: 'USD',
  verificationStatus: 'VERIFIED',
};

const renderGrid = (mode: 'validation' | 'participation') => render(
  <LeadValidationGrid
    mode={mode}
    lines={[line]}
    decisions={{ 101: { decision: 'Pending' } }}
    reasonCodes={[]}
    unitOptions={[]}
    currencyOptions={[]}
    readOnly={mode === 'validation'}
    onDecisionsChange={vi.fn()}
  />,
);

describe('LeadValidationGrid stage scope', () => {
  it('keeps transformation review limited to source-versus-canonical validation', () => {
    renderGrid('validation');

    expect(screen.getByText('Customer request / source')).toBeInTheDocument();
    expect(screen.getByText('Normalized item')).toBeInTheDocument();
    expect(screen.getByText('Validation')).toBeInTheDocument();
    expect(screen.queryByText('Participation')).not.toBeInTheDocument();
    expect(screen.queryByText('Quote values')).not.toBeInTheDocument();
    expect(screen.getByTestId('grid')).toHaveAttribute('data-checkbox-selection', 'false');
  });

  it('shows participation inputs only in the fit and participation grid', () => {
    renderGrid('participation');

    expect(screen.getByText('Participation')).toBeInTheDocument();
    expect(screen.getByText('Quote values')).toBeInTheDocument();
    expect(screen.getByText('Decision record')).toBeInTheDocument();
    expect(screen.queryByText('Customer request / source')).not.toBeInTheDocument();
    expect(screen.getByTestId('grid')).toHaveAttribute('data-checkbox-selection', 'true');
  });
});
