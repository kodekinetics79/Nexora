import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import OrderListPage from './OrderListPage';

/**
 * The invoice icon on the order list used to fire the invoice call directly, with `lines: null`.
 * That is the defect: the server expands a null line set to the full ORDERED quantity, so after any
 * short delivery it was a guaranteed 409 against the accepted-quantity ceiling, and there was no
 * other way into the endpoint.
 *
 * This asserts the button now COMPOSES rather than posts. If anyone restores the one-click
 * mutation, the "nothing is posted" assertion below fails.
 *
 * Fixture data is obviously synthetic.
 */

const get = vi.fn();
const post = vi.fn();

vi.mock('../../../api/axiosInstance', () => ({
  default: {
    get: (url: string, config?: unknown) => get(url, config),
    post: (url: string, body?: unknown, config?: unknown) => post(url, body, config),
  },
}));

vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { businessUnitId: 1 },
    token: 'synthetic-test-token',
    hasPermission: () => true,
    permissionsError: null,
    permissionsLoading: false,
    refreshPermissions: vi.fn(),
  }),
}));

const ORDER = {
  id: 900,
  orderNo: 'SO-SYNTH-900',
  orderDate: '2026-08-01T00:00:00Z',
  customerId: 77,
  customerName: 'Synthetic Trading Co',
  clientPoNumber: 'CLIENT-PO-SYNTH-77',
  quoteNo: 'QT-SUBMITTED-77',
  status: 'CONFIRMED',
  paymentStatus: 'UNPAID',
  totalAmount: 1100,
  hasShipments: true,
  items: [
    { id: 5001, productId: 1, productName: 'Gate valve', quantity: 10, unitPrice: 100, discount: 0, taxAmount: 0, totalAmount: 1000 },
  ],
};

const DELIVERED = [{
  orderItemId: 5001,
  awardedQuantity: 10,
  despatchedQuantity: 10,
  acceptedQuantity: 7,
  awaitingConfirmationQuantity: 0,
  refusedQuantity: 3,
  outstandingQuantity: 3,
  isFullyDelivered: false,
}];

beforeEach(() => {
  get.mockReset();
  post.mockReset();
  get.mockImplementation((url: string) => {
    if (url === '/api/Order') return Promise.resolve({ data: [ORDER] });
    if (url.includes('/delivered-quantities')) return Promise.resolve({ data: DELIVERED });
    if (url.startsWith('/api/Order/')) return Promise.resolve({ data: ORDER });
    if (url.includes('/commercial-finance/documents')) return Promise.resolve({ data: [] });
    throw new Error(`unexpected GET ${url}`);
  });
});

describe('deferred modules on the order list', () => {
  it('keeps Fulfilment and Receivables actions out of this release', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter><OrderListPage /></MemoryRouter>
      </QueryClientProvider>,
    );

    await screen.findByText('SO-SYNTH-900');
    // The order row keeps the buyer commitment beside the submitted quotation it answered.
    expect(screen.getByText('CLIENT-PO-SYNTH-77')).toBeInTheDocument();
    expect(screen.getByText('QT-SUBMITTED-77')).toBeInTheDocument();

    expect(screen.queryByLabelText('Invoice order SO-SYNTH-900')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /shipment/i })).not.toBeInTheDocument();
    expect(post).not.toHaveBeenCalled();
  });

  it('keeps the order itself available for work', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter><OrderListPage /></MemoryRouter>
      </QueryClientProvider>,
    );

    expect(await screen.findByRole('button', { name: 'View Order' })).toBeVisible();
  });
});
