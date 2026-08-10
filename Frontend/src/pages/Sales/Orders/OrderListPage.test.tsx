import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
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

describe('the invoice action on the order list', () => {
  it('opens the line-level screen instead of posting the whole ordered quantity', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter><OrderListPage /></MemoryRouter>
      </QueryClientProvider>,
    );

    fireEvent.click(await screen.findByLabelText('Invoice order SO-SYNTH-900'));

    // The screen appears, pre-filled with what the customer accepted...
    const field = await screen.findByLabelText('Invoice quantity for Gate valve') as HTMLInputElement;
    expect(field.value).toBe('7');
    // ...and nothing has been billed by the click itself.
    await waitFor(() => expect(get).toHaveBeenCalledWith(
      '/api/delivery/orders/900/delivered-quantities', undefined));
    expect(post).not.toHaveBeenCalled();
  });
});
