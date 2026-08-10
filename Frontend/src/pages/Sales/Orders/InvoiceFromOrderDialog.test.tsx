import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import InvoiceFromOrderDialog from './InvoiceFromOrderDialog';

/**
 * Gate 7 / FR-DLM-02. What is locked down here is the WIRING, not the values.
 *
 * The defect: the only invoice call in the product posted `lines: null`, which the server expands
 * to the full ORDERED quantity, so a short delivery — a customer signing for 7 of 10 — could never
 * be invoiced from the UI at all. It was a guaranteed 409 against the accepted-quantity ceiling,
 * with no screen able to ask for the smaller number.
 *
 * These tests assert against the HTTP layer (`axiosInstance`) rather than against the service
 * module, deliberately. That is what makes them proof: if anybody reverts
 * `commercialFinanceService.createInvoiceFromOrder` to `lines: null`, or restores the constant
 * `order-invoice-{id}-full` key, or stops the dialog reading the delivered-quantity endpoint, the
 * body and header asserted here change and these tests fail. A test that only checked the dialog's
 * own state would survive all three.
 *
 * All fixture data is obviously synthetic.
 */

const get = vi.fn();
const post = vi.fn();

vi.mock('../../../api/axiosInstance', () => ({
  default: {
    get: (url: string, config?: unknown) => get(url, config),
    post: (url: string, body?: unknown, config?: unknown) => post(url, body, config),
  },
}));

const ORDER = {
  id: 900,
  orderNo: 'SO-SYNTH-900',
  customerId: 77,
  status: 'CONFIRMED',
  items: [
    // Short delivery: 10 ordered, 10 despatched, 7 signed for, 3 refused.
    { id: 5001, productId: 1, productName: 'Gate valve', quantity: 10, unitPrice: 100, discount: 0, taxAmount: 0, totalAmount: 1000 },
    // Nothing accepted at all — a real state, not a blank row.
    { id: 5002, productId: 2, productName: 'Spiral gasket', quantity: 4, unitPrice: 25, discount: 0, taxAmount: 0, totalAmount: 100 },
  ],
};

/** The shape `GET /api/delivery/orders/{id}/delivered-quantities` returns. */
const DELIVERED = [
  {
    orderItemId: 5001,
    awardedQuantity: 10,
    despatchedQuantity: 10,
    acceptedQuantity: 7,
    awaitingConfirmationQuantity: 0,
    refusedQuantity: 3,
    outstandingQuantity: 3,
    isFullyDelivered: false,
  },
  {
    orderItemId: 5002,
    awardedQuantity: 4,
    despatchedQuantity: 4,
    acceptedQuantity: 0,
    awaitingConfirmationQuantity: 4,
    refusedQuantity: 0,
    outstandingQuantity: 0,
    isFullyDelivered: false,
  },
];

const renderDialog = (onCreated = vi.fn()) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <InvoiceFromOrderDialog
        orderId={900}
        orderNo="SO-SYNTH-900"
        businessUnitId={1}
        onClose={vi.fn()}
        onCreated={onCreated}
      />
    </QueryClientProvider>,
  );
  return onCreated;
};

interface PostCall {
  url: string;
  body: { lines?: { orderItemId: number; quantity: number }[] };
  config: { headers: Record<string, string> };
}

/** Every POST this dialog made to the invoice endpoint, as the wire saw it. */
const invoicePosts = (): PostCall[] => post.mock.calls
  .filter((call) => String(call[0]).includes('/invoices'))
  .map((call) => ({ url: call[0], body: call[1], config: call[2] }) as PostCall);

beforeEach(() => {
  get.mockReset();
  post.mockReset();
  get.mockImplementation((url: string) => {
    if (url.includes('/delivered-quantities')) return Promise.resolve({ data: DELIVERED });
    if (url.startsWith('/api/Order/')) return Promise.resolve({ data: ORDER });
    if (url.includes('/commercial-finance/documents')) return Promise.resolve({ data: [] });
    throw new Error(`unexpected GET ${url}`);
  });
  post.mockResolvedValue({ data: { id: 4242, documentType: 'Invoice', status: 'Draft' } });
});

describe('invoicing a short delivery', () => {
  it('reads the delivered-quantity endpoint and pre-fills the accepted quantity', async () => {
    renderDialog();

    const field = await screen.findByLabelText('Invoice quantity for Gate valve') as HTMLInputElement;
    // 7 accepted, nothing invoiced yet. Not 10, which is what the old `lines: null` call sent.
    expect(field.value).toBe('7');
    expect(get).toHaveBeenCalledWith('/api/delivery/orders/900/delivered-quantities', undefined);
  });

  it('posts the per-line quantities, so a short delivery is invoiceable at all', async () => {
    renderDialog();
    await screen.findByLabelText('Invoice quantity for Gate valve');

    fireEvent.click(screen.getByRole('button', { name: 'Create invoice draft' }));

    await waitFor(() => expect(invoicePosts()).toHaveLength(1));
    const { url, body } = invoicePosts()[0];
    expect(url).toBe('/api/commercial-finance/orders/900/invoices');
    // THE assertion. `lines: null` — the defect — fails here, and so does any expansion to the
    // ordered quantity of 10.
    expect(body).toMatchObject({ lines: [{ orderItemId: 5001, quantity: 7 }] });
    expect(body.lines).toHaveLength(1);
  });

  it('shows the cap as a number and refuses a quantity above it without rewriting it', async () => {
    renderDialog();
    const field = await screen.findByLabelText('Invoice quantity for Gate valve') as HTMLInputElement;

    // The ceiling is on the page, not only in an input `max`.
    expect(screen.getAllByText('7').length).toBeGreaterThan(0);

    fireEvent.change(field, { target: { value: '9' } });

    // Never silently clamped: the operator's 9 is still their 9.
    expect(field.value).toBe('9');
    expect(await screen.findByText(/Above the 7 the customer has accepted/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create invoice draft' })).toBeDisabled();
    expect(invoicePosts()).toHaveLength(0);
  });

  it('renders a line with nothing accepted as a named state, not a blank', async () => {
    renderDialog();
    await screen.findByLabelText('Invoice quantity for Gate valve');

    expect(screen.getByText('Spiral gasket')).toBeInTheDocument();
    expect(screen.getByText('Nothing accepted yet')).toBeInTheDocument();
    // And it is not editable, because there is nothing to invoice on it.
    expect(screen.queryByLabelText('Invoice quantity for Spiral gasket')).not.toBeInTheDocument();
  });
});

describe('the idempotency key', () => {
  it('changes when the quantities change, so a corrected attempt is not swallowed as a replay', async () => {
    renderDialog();
    const field = await screen.findByLabelText('Invoice quantity for Gate valve') as HTMLInputElement;

    fireEvent.click(screen.getByRole('button', { name: 'Create invoice draft' }));
    await waitFor(() => expect(invoicePosts()).toHaveLength(1));

    fireEvent.change(field, { target: { value: '5' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create invoice draft' }));
    await waitFor(() => expect(invoicePosts()).toHaveLength(2));

    const keyOf = (index: number) => invoicePosts()[index].config.headers['Idempotency-Key'];
    expect(keyOf(0)).not.toBe(keyOf(1));
    // And it is not the old constant, which was the same string for every request forever.
    expect(keyOf(0)).not.toBe('order-invoice-900-full');
  });

  it('is stable for the identical submission, so a double click cannot raise two drafts', async () => {
    renderDialog();
    await screen.findByLabelText('Invoice quantity for Gate valve');

    fireEvent.click(screen.getByRole('button', { name: 'Create invoice draft' }));
    await waitFor(() => expect(invoicePosts()).toHaveLength(1));
    fireEvent.click(screen.getByRole('button', { name: 'Create invoice draft' }));
    await waitFor(() => expect(invoicePosts()).toHaveLength(2));

    const keyOf = (index: number) => invoicePosts()[index].config.headers['Idempotency-Key'];
    expect(keyOf(0)).toBe(keyOf(1));
  });
});

describe('the 409 from the accepted-quantity ceiling', () => {
  it('is shown verbatim, with the product name behind the order line id the server quoted', async () => {
    const detail = 'Invoice quantity exceeds the quantity the customer has accepted for order '
      + 'line 5001: 10 despatched, 7 accepted, 0 already invoiced, 9 requested. Confirm the '
      + 'delivery before invoicing it.';
    post.mockRejectedValue({
      response: { status: 409, data: { status: 409, title: 'Commercial finance conflict', detail } },
      config: { method: 'post', url: '/api/commercial-finance/orders/900/invoices' },
      request: {},
    });

    renderDialog();
    await screen.findByLabelText('Invoice quantity for Gate valve');
    fireEvent.click(screen.getByRole('button', { name: 'Create invoice draft' }));

    // The server's own sentence, unedited.
    expect(await screen.findByText(new RegExp('10 despatched, 7 accepted'))).toBeInTheDocument();
    // Plus the one thing the server could not say: which product line 5001 is.
    expect(screen.getByText(/Gate valve \(order line 5001\)/)).toBeInTheDocument();
    expect(screen.getByText(/Reduce this line to 7/)).toBeInTheDocument();
  });
});

describe('lines already on an issued invoice', () => {
  it('reduces the ceiling by what has already been billed', async () => {
    get.mockImplementation((url: string) => {
      if (url.includes('/delivered-quantities')) return Promise.resolve({ data: DELIVERED });
      if (url.startsWith('/api/Order/')) return Promise.resolve({ data: ORDER });
      if (url.includes('/commercial-finance/documents')) {
        return Promise.resolve({
          data: [{
            id: 11, orderId: 900, customerId: 77, documentType: 'Invoice', status: 'Issued',
            lines: [{ id: 1, orderItemId: 5001, quantity: 4 }],
          }],
        });
      }
      throw new Error(`unexpected GET ${url}`);
    });

    renderDialog();

    // 7 accepted less 4 already issued. The server's ceiling, mirrored.
    const field = await screen.findByLabelText('Invoice quantity for Gate valve') as HTMLInputElement;
    await waitFor(() => expect(field.value).toBe('3'));
  });
});
