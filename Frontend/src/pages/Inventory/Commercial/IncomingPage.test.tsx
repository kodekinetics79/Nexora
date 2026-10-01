import { fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import IncomingPage from './IncomingPage';

const mocks = vi.hoisted(() => ({ incoming: vi.fn(), lots: vi.fn() }));
vi.mock('../../../api/services/commercialIntelligenceService', () => ({ default: { getIncoming: mocks.incoming } }));
vi.mock('../../../api/services/materialTraceabilityService', () => ({ default: { searchLots: mocks.lots } }));

function renderPage() {
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter initialEntries={['/inventory/incoming']}><IncomingPage /></MemoryRouter>
  </QueryClientProvider>);
}

beforeEach(() => {
  mocks.incoming.mockReset().mockResolvedValue([{
    id: 1, purchaseOrderId: 8, purchaseOrderNumber: 'PO-008', sourceReference: '8:19',
    supplierName: 'Control Supply', supplierCity: 'Dammam', supplierCountry: 'Saudi Arabia',
    partNumber: 'VALVE-01', productName: 'Valve actuator', warehouseName: 'Main warehouse',
    orderedQuantity: 20, receivedQuantity: 5, expectedAt: '2026-10-07', status: 'PartiallyReceived',
  }]);
  mocks.lots.mockReset().mockResolvedValue([{
    id: 21, productName: 'Valve actuator', partNumber: 'VALVE-01', supplierName: 'Control Supply',
    warehouseName: 'Main warehouse', purchaseOrderNumber: 'PO-008', receiptNumber: 'GRN-009',
    lotNumber: 'BATCH-006', receivedOn: '2026-10-01T10:00:00Z', quantityReceived: 5, quantityRemaining: 3,
  }]);
});

describe('Incoming & receipts', () => {
  it('shows supplier supply and persisted receipt references in separate columns', async () => {
    renderPage();
    expect(await screen.findByText('GRN-009')).toBeInTheDocument();
    const incoming = within(screen.getByRole('region', { name: 'Incoming supply' }));
    expect(incoming.getByText('Dammam, Saudi Arabia')).toBeInTheDocument();
    expect(incoming.getByText('15')).toBeInTheDocument();
    expect(incoming.getByText('07 Oct 2026')).toBeInTheDocument();
    for (const label of ['Product', 'Part number', 'PO #', 'Supplier', 'Supplier invoice #', 'BL #']) {
      expect(incoming.getByRole('columnheader', { name: label })).toBeInTheDocument();
    }
    expect(screen.getByRole('link', { name: 'BATCH-006' })).toHaveAttribute('href', '/inventory/lots/21');
    expect(screen.getAllByText('Not recorded')).toHaveLength(5);
    expect(screen.getByRole('tab', { name: 'Incoming & receipts' })).toHaveAttribute('aria-selected', 'true');
  });

  it('searches receipt references without losing the distinction from incoming supply', async () => {
    renderPage();
    await screen.findByText('GRN-009');
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'GRN-009' } });
    expect(screen.getByText('No loaded incoming commitments match your search.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'BATCH-006' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Clear search' }));
    expect(screen.getByRole('region', { name: 'Incoming supply' })).toBeInTheDocument();
  });

  it('does not promote an internal source reference to a supplier purchase order', async () => {
    mocks.incoming.mockResolvedValue([{
      id: 2, sourceReference: '8:19', purchaseOrderNumber: null, supplierName: null,
      productName: 'Legacy actuator', partNumber: 'OLD-01', warehouseName: 'Main warehouse',
      orderedQuantity: 4, receivedQuantity: 0, status: 'Ordered',
    }]);
    renderPage();
    const product = await screen.findByText('Legacy actuator');
    const cells = within(product.closest('tr')!).getAllByRole('cell');
    expect(cells[5]).toHaveTextContent('Not recorded');
    expect(cells[13]).toHaveTextContent('8:19');
  });

  it('displays captured supplier documents without treating carrier tracking as a BL', async () => {
    mocks.incoming.mockResolvedValue([{
      id: 2, productName: 'Documented actuator', partNumber: 'DOC-01',
      orderedQuantity: 4, receivedQuantity: 1, status: 'PartiallyReceived',
      supplierInvoiceNumbers: ['SUP-INV-009'], billOfLadingNumbers: ['BL-007'], trackingReferences: ['AIR-005'],
    }]);
    mocks.lots.mockResolvedValue([{
      id: 22, productName: 'Documented actuator', partNumber: 'DOC-01', lotNumber: 'LOT-DOC',
      receiptNumber: 'GRN-DOC', supplierInvoiceNumber: 'SUP-INV-009', billOfLadingNumber: 'BL-007',
      quantityReceived: 1, quantityRemaining: 1,
    }]);
    renderPage();
    await screen.findByText('GRN-DOC');
    expect(screen.getAllByText('SUP-INV-009')).toHaveLength(2);
    expect(screen.getAllByText('BL-007')).toHaveLength(2);
    expect(screen.getByText('AIR-005')).toBeInTheDocument();
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'SUP-INV-009' } });
    expect(screen.getByRole('link', { name: 'LOT-DOC' })).toBeInTheDocument();
  });

  it('keeps receipt evidence visible when incoming supply fails to load', async () => {
    mocks.incoming.mockRejectedValue(new Error('Unavailable'));
    renderPage();
    expect(await screen.findByRole('alert')).toHaveTextContent('could not be loaded');
    expect(await screen.findByText('GRN-009')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
  });
});
