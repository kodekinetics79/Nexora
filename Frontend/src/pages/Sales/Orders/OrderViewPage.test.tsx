import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
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

describe('order fulfilment actions', () => {
  it('offers the next shipment when an earlier shipment only covered part of the order', async () => {
    get.mockImplementation((url: string) => {
      if (url === '/api/Order/900') return Promise.resolve({ data: ORDER });
      if (url === '/api/Shipment/order/900') return Promise.resolve({ data: [shipment(4)] });
      throw new Error(`unexpected GET ${url}`);
    });

    renderPage();

    expect(await screen.findByRole('button', { name: 'Create next shipment' })).toBeVisible();
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

  it('opens accepted-quantity invoicing and deep-links to the created document', async () => {
    get.mockImplementation((url: string) => {
      if (url === '/api/Order/900') return Promise.resolve({ data: ORDER });
      if (url === '/api/Shipment/order/900') return Promise.resolve({ data: [shipment(4)] });
      throw new Error(`unexpected GET ${url}`);
    });

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Invoice accepted delivery' }));
    expect(screen.getByRole('dialog', { name: 'Invoice accepted delivery' })).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Create synthetic invoice' }));
    expect(screen.getByLabelText('location')).toHaveTextContent('/sales/finance?documentId=4242');
  });
});
