import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * Edit Quote's line grid says what the customer's PDF says (pilot audit UX-04, UX-03, CB-03).
 *
 * The Total column is the ex-VAT figure the PDF prints; the buyer's material / maker / part number
 * sits under the description; the notes field says it reaches the customer and does not open with
 * the internal "Commercial Review Required" marker. And after a line is deleted, every remaining
 * row shows its OWN total: the grid filtered deleted rows before numbering them, so each row read
 * its neighbour's figures (and an edit landed on the wrong line).
 */

const { getById, update, getAll, productGetAll, customerGetAll, policyGet } = vi.hoisted(() => ({
  getById: vi.fn(),
  update: vi.fn(),
  getAll: vi.fn(),
  productGetAll: vi.fn(),
  customerGetAll: vi.fn(),
  policyGet: vi.fn(),
}));

vi.mock('../../../api/services/quoteService', () => ({ default: { getById, update } }));
vi.mock('../../../api/services/setupService', () => ({ default: { getAll } }));
vi.mock('../../../api/services/productService', () => ({ default: { getAll: productGetAll } }));
vi.mock('../../../api/services/customerService', () => ({ default: { getAll: customerGetAll } }));
vi.mock('../../../api/services/commercialPolicyService', () => ({
  default: { get: policyGet, getPolicy: policyGet },
}));
vi.mock('./CustomerContextPanel', () => ({ default: () => null }));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 1 }, hasPermission: () => true }),
}));
vi.mock('react-hot-toast', () => ({
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
  default: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
}));

import EditQuotePage from './EditQuotePage';

const draft = {
  id: 9,
  quoteNo: 'QT-2026-0009',
  statusValue: 'Draft',
  statusCode: 'DRAFT',
  statusId: 1,
  currencyCode: 'SAR',
  customerId: 3,
  quoteDate: '2026-09-28',
  validUntil: '2026-12-27',
  headerRemarks: 'Commercial Review Required: pricing, inventory, lead time, tax, freight and validity remain pending.',
  totalAmount: 356.5,
  quoteItems: [
    {
      id: 1, productId: null, productName: '', itemDescription: 'RTD PT100 3-wire', quantity: 1, unitPrice: 100,
      discount: 0, totalAmount: 115, taxAmount: 15, taxableBase: 100, taxRatePercentApplied: 15, taxCategory: 'STANDARD',
      customerMaterialCode: '902507285', manufacturerName: 'ABB', manufacturerPartNumber: 'PT100',
    },
    {
      id: 2, productId: null, productName: '', itemDescription: 'Gas detector', quantity: 3, unitPrice: 70,
      discount: 0, totalAmount: 241.5, taxAmount: 31.5, taxableBase: 210, taxRatePercentApplied: 15, taxCategory: 'STANDARD',
    },
  ],
};

function renderEdit() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/sales/quotes/edit/9']}>
        <Routes>
          <Route path="/sales/quotes/edit/:id" element={<EditQuotePage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const rowOf = (description: string) =>
  screen.getByDisplayValue(description).closest('tr') as HTMLElement;

beforeEach(() => {
  vi.clearAllMocks();
  getById.mockResolvedValue(draft);
  getAll.mockResolvedValue({ items: [] });
  productGetAll.mockResolvedValue({ items: [] });
  customerGetAll.mockResolvedValue({ items: [] });
  policyGet.mockResolvedValue({ outputTaxRatePercent: 15 });
  update.mockResolvedValue({});
});

describe('the Edit Quote line grid', () => {
  it('prints the ex-VAT line total and the buyer material, maker and part number', async () => {
    renderEdit();
    await screen.findByDisplayValue('RTD PT100 3-wire');

    expect(screen.getByRole('columnheader', { name: 'Total excl. VAT' })).toBeInTheDocument();
    expect(rowOf('RTD PT100 3-wire')).toHaveTextContent('100.00');
    expect(rowOf('RTD PT100 3-wire')).not.toHaveTextContent('115.00');
    expect(screen.getByText('Material: 902507285 · Make: ABB · Part no.: PT100')).toBeInTheDocument();
  });

  it('keeps each row on its own figures after a line is deleted', async () => {
    renderEdit();
    await screen.findByDisplayValue('RTD PT100 3-wire');

    fireEvent.click(screen.getAllByTestId('DeleteIcon')[0].closest('button') as HTMLElement);

    await waitFor(() => expect(screen.queryByDisplayValue('RTD PT100 3-wire')).not.toBeInTheDocument());
    expect(rowOf('Gas detector')).toHaveTextContent('210.00');
  });

  it('opens the customer notes empty instead of with the internal draft marker, and says the customer sees them', async () => {
    renderEdit();
    await screen.findByDisplayValue('RTD PT100 3-wire');

    const notes = screen.getByLabelText('Notes to customer') as HTMLInputElement;
    expect(notes.value).toBe('');
    expect(screen.getByText('Printed on the quote and in the email')).toBeInTheDocument();
  });
});
