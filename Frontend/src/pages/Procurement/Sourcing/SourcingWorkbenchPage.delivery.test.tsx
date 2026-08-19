/**
 * Recording a delivery the buyer made themselves, seen the way a buyer sees it.
 *
 * A buyer who takes a price over the phone had no way to record it: response capture requires a
 * request that actually reached the supplier, and only a Nexora email could ever say so. These
 * tests hold the screen to the three things that makes it usable — the action is offered exactly
 * where the request has not been sent, the account of what happened is genuinely required, and a
 * delivery the buyer made is never dressed up as an email Nexora sent.
 */
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type {
  SourcingWorkbench,
  SupplierSolicitation,
} from '../../../api/services/procurementService';

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useParams: () => ({ rfqId: '5' }), useNavigate: () => vi.fn() };
});
vi.mock('../../../api/services/procurementService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/procurementService')>();
  return {
    ...actual,
    default: {
      ...actual.default,
      getWorkbench: vi.fn(),
      getQuoteComparison: vi.fn(),
      recordSolicitationDelivery: vi.fn(),
    },
  };
});
vi.mock('../../../api/services/currencyService', () => ({
  default: { getAll: vi.fn().mockResolvedValue({ items: [] }) },
}));
vi.mock('../../../api/services/warehouseService', () => ({
  default: { getAll: vi.fn().mockResolvedValue({ items: [] }) },
}));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 7 }, hasPermission: () => true }),
}));

const procurementService = (await import('../../../api/services/procurementService'))
  .default as unknown as {
    getWorkbench: ReturnType<typeof vi.fn>;
    getQuoteComparison: ReturnType<typeof vi.fn>;
    recordSolicitationDelivery: ReturnType<typeof vi.fn>;
  };
const SourcingWorkbenchPage = (await import('./SourcingWorkbenchPage')).default;

const solicitation = (
  overrides: Partial<SupplierSolicitation> = {},
): SupplierSolicitation => ({
  id: 41,
  rfqId: 5,
  supplierId: 3,
  supplierName: 'Gulf Instrumentation',
  supplierEmail: 'sales@gulf.test',
  status: 'PENDING_DISPATCH',
  channel: 'Email',
  attemptCount: 0,
  providerReference: null,
  lastErrorCode: null,
  sentOn: null,
  respondedOn: null,
  updatedOn: '2026-08-10T09:00:00Z',
  version: 2,
  requestedRfqItemIds: [10],
  recordedDelivery: null,
  ...overrides,
});

const workbench = (solicitations: SupplierSolicitation[]): SourcingWorkbench => ({
  rfqId: 5,
  rfqNumber: 'RFQ-5',
  lines: [{
    id: 10, rfqId: 5, description: 'Pressure transmitter', requestedQuantity: 5,
    availableQuantity: 0, reservedQuantity: 0, shortfallQuantity: 5, resolution: 'SHORTAGE',
  }],
  solicitations,
  offers: [],
  awards: [],
  purchaseOrders: [],
  customerQuoteDraft: null,
});

const renderPage = () =>
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter>
        <SourcingWorkbenchPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );

/** Opens the solicitations tab and returns the single supplier row. */
const solicitationRow = async () => {
  renderPage();
  fireEvent.click(await screen.findByRole('tab', { name: /Solicitations/ }));
  return (await screen.findByText('Gulf Instrumentation')).closest('tr')!;
};

beforeEach(() => {
  vi.clearAllMocks();
  procurementService.getQuoteComparison.mockResolvedValue({
    rfqItemId: 10, lines: [], recommendedSupplierQuotedItemId: null,
  });
});

describe('recording that the buyer reached the supplier themselves', () => {
  it('offers the action while the request has not reached the supplier, in plain language', async () => {
    procurementService.getWorkbench.mockResolvedValue(workbench([solicitation()]));
    const row = await solicitationRow();

    const action = within(row).getByRole('button', { name: /I contacted them myself/i });
    expect(action).toBeInTheDocument();
    // Capturing a price is still barred until the delivery is recorded, so the two buttons
    // together read as one order of work rather than two alternatives.
    expect(within(row).getByRole('button', { name: /Capture response/i })).toBeDisabled();
    expect(row.textContent).not.toMatch(/out of band/i);

    fireEvent.click(action);
    const dialog = await screen.findByRole('dialog');
    expect(dialog).toHaveTextContent('How did you reach Gulf Instrumentation?');
    expect(dialog.textContent).not.toMatch(/out of band/i);
    // The choices name what the buyer actually did.
    expect(within(dialog).getByText('Recorded by phone')).toBeInTheDocument();
  });

  it('will not save until the buyer says what happened, then sends the version it was shown', async () => {
    procurementService.getWorkbench.mockResolvedValue(workbench([solicitation()]));
    procurementService.recordSolicitationDelivery.mockResolvedValue({
      solicitationId: 41, status: 'Sent', channel: 'Phone', note: 'Spoke to Ahmed.',
      recordedBy: 'buyer@nexora.test', recordedOn: '2026-08-12T08:00:00Z',
      version: 3, replayed: false,
    });
    const row = await solicitationRow();
    fireEvent.click(within(row).getByRole('button', { name: /I contacted them myself/i }));
    const dialog = await screen.findByRole('dialog');
    const save = within(dialog).getByRole('button', { name: /Save and record their price/i });

    // A blank account of what happened is no evidence at all, so it cannot be saved.
    expect(save).toBeDisabled();
    fireEvent.change(within(dialog).getByLabelText(/What happened/i), {
      target: { value: '  Spoke to Ahmed.  ' },
    });
    expect(save).toBeEnabled();
    fireEvent.click(save);

    await waitFor(() =>
      expect(procurementService.recordSolicitationDelivery).toHaveBeenCalledWith(
        41,
        { deliveryChannel: 'Phone', note: 'Spoke to Ahmed.', expectedVersion: 2 },
        expect.stringContaining('solicitation-delivery:41'),
      ),
    );
  });

  it('shows a delivery the buyer made as what it was, never as a provider confirmation', async () => {
    procurementService.getWorkbench.mockResolvedValue(workbench([
      solicitation({
        status: 'SENT',
        channel: 'Phone',
        recordedDelivery: {
          channel: 'Phone',
          note: 'Called Ahmed; he quoted 12 per unit.',
          recordedBy: 'buyer@nexora.test',
          recordedOn: '2026-08-12T08:00:00Z',
        },
      }),
    ]));
    const row = await solicitationRow();

    expect(row).toHaveTextContent('Recorded by phone');
    expect(row).toHaveTextContent('buyer@nexora.test');
    expect(row).toHaveTextContent('Called Ahmed; he quoted 12 per unit.');
    // The email story must not be told about a phone call.
    expect(row).not.toHaveTextContent('Awaiting provider confirmation');
    // Nothing left to record; the price is what the buyer captures next.
    expect(within(row).queryByRole('button', { name: /I contacted them myself/i }))
      .not.toBeInTheDocument();
    expect(within(row).getByRole('button', { name: /Capture response/i })).toBeEnabled();
  });

  it('does not offer the action once the supplier has already responded', async () => {
    procurementService.getWorkbench.mockResolvedValue(workbench([
      solicitation({ status: 'RESPONDED', sentOn: '2026-08-11T09:00:00Z' }),
    ]));
    const row = await solicitationRow();

    expect(within(row).queryByRole('button', { name: /I contacted them myself/i }))
      .not.toBeInTheDocument();
  });
});
