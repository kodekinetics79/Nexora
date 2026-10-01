import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import OrderViewPage from './OrderViewPage';

const get = vi.fn();

vi.mock('../../../api/axiosInstance', () => ({
  default: {
    get: (url: string, config?: unknown) => get(url, config),
  },
}));

vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { businessUnitId: 1 },
    hasPermission: () => true,
  }),
}));

vi.mock('./InvoiceFromOrderDialog', () => ({
  default: ({ onCreated }: { onCreated: (document: { id: number }) => void }) => (
    <div role="dialog" aria-label="Invoice accepted delivery">
      <button onClick={() => onCreated({ id: 4242 })}>Create synthetic invoice</button>
    </div>
  ),
}));

const ORDER = {
  id: 900,
  orderNo: 'SO-SYNTH-900',
  customerId: 77,
  customerName: 'Synthetic Trading Co',
  status: 'CONFIRMED',
  paymentStatus: 'UNPAID',
  orderDate: '2026-08-01T00:00:00Z',
  totalAmount: 1000,
  subTotal: 1000,
  taxAmount: 0,
  discountAmount: 0,
  paidAmount: 0,
  balanceAmount: 1000,
  hasShipments: true,
  items: [{
    id: 5001, productId: 1, productName: 'Gate valve', quantity: 10,
    unitPrice: 100, discount: 0, taxAmount: 0, totalAmount: 1000,
  }],
};

const shipment = (quantity: number) => ({
  id: 300,
  orderId: 900,
  shipmentNo: 'SHP-SYNTH-300',
  orderNo: 'SO-SYNTH-900',
  statusId: 1,
  status: 'Shipped',
  shipmentDate: '2026-08-02T00:00:00Z',
  deliveryStatus: 'DISPATCHED',
  items: [{ id: 301, orderItemId: 5001, productName: 'Gate valve', quantity }],
});

function LocationProbe() {
  return <output aria-label="location">{useLocation().pathname}{useLocation().search}</output>;
}

const renderPage = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/sales/orders/900']}>
        <LocationProbe />
        <Routes>
          <Route path="/sales/orders/:id" element={<OrderViewPage />} />
          <Route path="*" element={null} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
};

beforeEach(() => {
  get.mockReset();
});

describe('deferred order modules', () => {
  it('does not offer Fulfilment or Receivables actions in this release', async () => {
    get.mockImplementation((url: string) => {
      if (url === '/api/Order/900') return Promise.resolve({ data: ORDER });
      if (url === '/api/Shipment/order/900') return Promise.resolve({ data: [shipment(4)] });
      throw new Error(`unexpected GET ${url}`);
    });

    renderPage();

    await screen.findByText('Order #SO-SYNTH-900');
    expect(screen.queryByRole('button', { name: /shipment/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /invoice accepted delivery/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /accounts receivable/i })).not.toBeInTheDocument();
    expect(get).not.toHaveBeenCalledWith('/api/Shipment/order/900', expect.anything());
  });

  it('does not offer another shipment after every ordered unit has despatched', async () => {
    get.mockImplementation((url: string) => {
      if (url === '/api/Order/900') return Promise.resolve({ data: ORDER });
      if (url === '/api/Shipment/order/900') return Promise.resolve({ data: [shipment(10)] });
      throw new Error(`unexpected GET ${url}`);
    });

    renderPage();

    await screen.findByText('Order #SO-SYNTH-900');
    expect(screen.queryByRole('button', { name: /Create.*shipment/i })).not.toBeInTheDocument();
  });

});
