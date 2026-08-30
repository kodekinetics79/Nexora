import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { ThemeProvider, createTheme } from '@mui/material/styles';
import type { ReactNode } from 'react';
import ShipmentListPage from './ShipmentListPage';

const getAll = vi.fn();

vi.mock('../../../api/services/shipmentService', () => ({
  default: { getAll: (params: unknown) => getAll(params) },
}));

vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 7 } }),
}));

vi.mock('../../../components/common/PermissionGuard', () => ({
  default: ({ children }: { children: ReactNode }) => children,
}));

vi.mock('@mui/x-data-grid', () => ({
  DataGrid: ({ rows, columns }: { rows: Array<Record<string, any>>; columns: Array<Record<string, any>> }) => (
    <div>
      {rows.map(row => (
        <div key={row.id} data-testid={`shipment-row-${row.id}`}>
          {columns.filter(column => ['shippingCost', 'deliveryStatus'].includes(column.field)).map(column => (
            <div key={column.field}>
              {column.renderCell
                ? column.renderCell({ value: row[column.field], row })
                : String(row[column.field] ?? '')}
            </div>
          ))}
        </div>
      ))}
    </div>
  ),
}));

const SHIPMENTS = [
  {
    id: 1,
    shipmentNo: 'SHP-001',
    orderId: 11,
    orderNo: 'SO-001',
    statusId: 1,
    // Deliberately contradictory legacy label: it must never drive the canonical queue.
    status: 'Delivered',
    deliveryStatus: 'DISPATCHED',
    shipmentDate: '2026-08-20T00:00:00Z',
    estimatedDeliveryDate: '2026-08-31T00:00:00Z',
    carrier: 'North Carrier',
    serviceLevel: 'Ground',
    trackingNumber: 'TRACK-001',
    shippingCost: 12.5,
    currencyCode: 'CAD',
    items: [],
  },
  {
    id: 2,
    shipmentNo: 'SHP-002',
    orderId: 12,
    orderNo: 'SO-002',
    statusId: 2,
    // Deliberately contradictory in the other direction.
    status: 'In Transit',
    deliveryStatus: 'DELIVERED',
    shipmentDate: '2026-08-21T00:00:00Z',
    estimatedDeliveryDate: '2026-08-29T00:00:00Z',
    carrier: 'South Carrier',
    serviceLevel: 'Express',
    trackingNumber: 'TRACK-002',
    shippingCost: 20,
    items: [],
  },
];

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <ThemeProvider theme={createTheme()}>
      <QueryClientProvider client={client}>
        <MemoryRouter><ShipmentListPage /></MemoryRouter>
      </QueryClientProvider>
    </ThemeProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getAll.mockResolvedValue(SHIPMENTS);
});

describe('governed shipment queue', () => {
  it('renders canonical delivery labels, keeps tenant status secondary, and never guesses a currency symbol', async () => {
    renderPage();

    const dispatched = await screen.findByTestId('shipment-row-1');
    expect(within(dispatched).getByText('Dispatched')).toBeInTheDocument();
    expect(within(dispatched).getByText('Tenant status: Delivered')).toBeInTheDocument();
    expect(within(dispatched).getByText('CAD 12.50')).toBeInTheDocument();

    const delivered = screen.getByTestId('shipment-row-2');
    expect(within(delivered).getByText('Delivered')).toBeInTheDocument();
    expect(within(delivered).getByText('Tenant status: In Transit')).toBeInTheDocument();
    expect(within(delivered).getByText('20.00')).toBeInTheDocument();
    expect(within(delivered).getByText('Currency not supplied')).toBeInTheDocument();
    expect(screen.queryByText(/\$/)).not.toBeInTheDocument();
  });

  it('filters the queue by governed delivery status even when legacy labels contradict it', async () => {
    renderPage();
    await screen.findByTestId('shipment-row-1');

    fireEvent.click(screen.getByRole('button', { name: 'In Transit' }));
    expect(screen.getByTestId('shipment-row-1')).toBeInTheDocument();
    expect(screen.queryByTestId('shipment-row-2')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Delivered' }));
    expect(screen.queryByTestId('shipment-row-1')).not.toBeInTheDocument();
    expect(screen.getByTestId('shipment-row-2')).toBeInTheDocument();
  });
});
