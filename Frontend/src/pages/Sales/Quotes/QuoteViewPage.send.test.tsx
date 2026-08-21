import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * The send control on a quote.
 *
 * A Draft quote used to carry a `variant="contained"` button with a send icon, labelled
 * "Ready to Send", wired to `statusMutation.mutate('Sent')` — a pure lifecycle write that raised
 * "Status updated successfully" and turned the status chip green. It emailed nobody. The control
 * that actually mails the customer was `variant="outlined"` and rendered ONLY once the status was
 * already 'Sent', so it did not exist at the moment a rep was trying to send.
 *
 * A rep beating a tender deadline clicked the prominent one, got a success toast and a green Sent
 * chip, and closed the tab. Nothing left the building. The quote's SentOn stayed null, so the
 * status was a claim the delivery record did not support.
 *
 * The server owns the real transition: FinalizeQuoteDeliveryAsync stamps SentOn, moves the
 * lifecycle to SENT and creates the follow-up task when the mail is delivered. These tests pin
 * that the UI no longer forges it.
 */

const { transitionStatus, sendEmail, getById, getPriceAttestation } = vi.hoisted(() => ({
  transitionStatus: vi.fn(),
  sendEmail: vi.fn(),
  getById: vi.fn(),
  getPriceAttestation: vi.fn(),
}));

vi.mock('../../../api/services/quoteService', () => ({
  default: {
    getById,
    sendEmail,
    transitionStatus,
    getPriceAttestation,
    exportPdf: vi.fn(),
    getRevisions: vi.fn().mockResolvedValue([]),
  },
}));

vi.mock('../../../api/services/procurementService', () => ({
  default: { getRfqIntelligence: vi.fn().mockResolvedValue(null) },
}));

vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { businessUnitId: 1 },
    hasPermission: () => true,
  }),
}));

vi.mock('react-hot-toast', () => ({
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
  default: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
}));

// The page renders these below the fold; none is under test here.
vi.mock('../../../components/common/CommercialLineIntelligence', () => ({ default: () => null }));
vi.mock('./QuoteOutcomeDialog', () => ({ default: () => null }));
vi.mock('./ExtendValidityDialog', () => ({ default: () => null }));
vi.mock('./PriceConfirmationDialog', () => ({ default: () => null }));
vi.mock('./customer-awards', () => ({ CustomerAwardDialog: () => null }));

// The recipient dialog is the first step of the real send chain. Rendering a marker proves the
// button opened the chain rather than writing a status.
vi.mock('../../../components/common/EmailPromptDialog', () => ({
  default: ({ open }: { open: boolean }) => (open ? <div>recipient dialog</div> : null),
}));

import QuoteViewPage from './QuoteViewPage';

const quoteFixture = (statusValue: string) => ({
  id: 9,
  quoteNo: 'QT-2026-0009',
  statusValue,
  statusCode: statusValue.toUpperCase(),
  lifecycleVersion: 3,
  currencyCode: 'SAR',
  totalAmount: 1840000,
  quoteDate: '2026-08-01',
  validUntil: '2026-09-01',
  // The page reduces over `quoteItems` (not `items`) for the subtotal — matching the real
  // QuoteResponseDTO rather than a guessed shape.
  quoteItems: [
    {
      id: 1, productName: 'Cisco Catalyst 9200', quantity: 2, unitPrice: 920000,
      discount: 0, totalAmount: 1840000, taxAmount: 0, taxRatePercentApplied: 15,
    },
  ],
  customerName: 'Aramco',
  rfqId: 41,
});

function renderQuote() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/sales/quotes/view/9']}>
        <Routes>
          <Route path="/sales/quotes/view/:id" element={<QuoteViewPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getPriceAttestation.mockResolvedValue({ satisfied: false });
  sendEmail.mockResolvedValue({ held: false });
  transitionStatus.mockResolvedValue({});
});

describe('sending a Draft quote', () => {
  it('offers one primary send control that opens the real send chain', async () => {
    getById.mockResolvedValue(quoteFixture('Draft'));
    renderQuote();

    const send = await screen.findByRole('button', { name: /send to customer/i });
    fireEvent.click(send);

    expect(await screen.findByText('recipient dialog')).toBeInTheDocument();
  });

  it('never transitions the quote to Sent from the UI', async () => {
    getById.mockResolvedValue(quoteFixture('Draft'));
    renderQuote();

    const send = await screen.findByRole('button', { name: /send to customer/i });
    fireEvent.click(send);

    // The regression: this used to be the ONLY thing the prominent button did.
    await waitFor(() => expect(screen.getByText('recipient dialog')).toBeInTheDocument());
    expect(transitionStatus).not.toHaveBeenCalled();
  });

  it('no longer renders a control that claims a send without performing one', async () => {
    getById.mockResolvedValue(quoteFixture('Draft'));
    renderQuote();

    await screen.findByRole('button', { name: /send to customer/i });
    expect(screen.queryByRole('button', { name: /ready to send/i })).not.toBeInTheDocument();
  });
});

describe('a quote already with the customer', () => {
  it('still offers a resend, and it uses the same chain', async () => {
    getById.mockResolvedValue(quoteFixture('Sent'));
    renderQuote();

    const again = await screen.findByRole('button', { name: /send again/i });
    fireEvent.click(again);

    expect(await screen.findByText('recipient dialog')).toBeInTheDocument();
    expect(transitionStatus).not.toHaveBeenCalled();
  });
});
