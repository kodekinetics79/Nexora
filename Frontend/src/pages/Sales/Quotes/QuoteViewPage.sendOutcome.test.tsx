import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * "Queued" is not "emailed" — on the screen the rep is actually looking at.
 *
 * The send chain ends in `POST /api/Quote/{id}/email`, which answers 202 with
 * `{ queuedForDelivery, delivered }`. In the normal case nothing has reached the customer: a
 * delivery row was written and `QuoteDeliveryWorker` sends it later, or refuses it later with
 * nobody watching — and the fixed delivery key then makes the quote number permanently
 * unsendable. This screen used to answer every accepted send with a green "Quote emailed to
 * the customer". These tests drive the real chain (recipient → price confirmation → send) and
 * pin the words at the end of it to the server's own distinction.
 */

const { getById, getPriceAttestation, getSendReadiness, sendEmail, confirmPriceAttestation } = vi.hoisted(() => ({
  getById: vi.fn(),
  getPriceAttestation: vi.fn(),
  getSendReadiness: vi.fn(),
  sendEmail: vi.fn(),
  confirmPriceAttestation: vi.fn(),
}));

const toastMock = vi.hoisted(() => Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }));

vi.mock('../../../api/services/quoteService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/quoteService')>();
  return {
    ...actual,
    default: {
      getById,
      getPriceAttestation,
      getSendReadiness,
      sendEmail,
      confirmPriceAttestation,
      transitionStatus: vi.fn(),
      exportPdf: vi.fn(),
      getRevisions: vi.fn().mockResolvedValue([]),
      getRevisionInfo: vi.fn().mockResolvedValue(null),
    },
  };
});

vi.mock('../../../api/services/procurementService', () => ({
  default: { getWorkbench: vi.fn().mockResolvedValue(null), getRfqIntelligence: vi.fn().mockResolvedValue(null) },
}));

vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 7 }, hasPermission: () => true }),
}));

vi.mock('react-hot-toast', () => ({
  toast: toastMock,
  default: toastMock,
}));

vi.mock('../../../components/common/CommercialLineIntelligence', () => ({ default: () => null }));
vi.mock('./QuoteOutcomeDialog', () => ({ default: () => null }));
vi.mock('./ExtendValidityDialog', () => ({ default: () => null }));
vi.mock('./customer-awards', () => ({ CustomerAwardDialog: () => null }));

// The two dialogs of the real chain, reduced to the one callback each contributes.
vi.mock('../../../components/common/EmailPromptDialog', () => ({
  default: ({ open, onConfirm }: { open: boolean; onConfirm: (email: string) => void }) =>
    open ? <button onClick={() => onConfirm('buyer@customer.test')}>choose recipient</button> : null,
}));
vi.mock('./PriceConfirmationDialog', () => ({
  default: ({ open, onConfirm }: { open: boolean; onConfirm: (source: string, reference: string) => void }) =>
    open ? <button onClick={() => onConfirm('SUPPLIER_QUOTE', 'SQ-1')}>confirm prices</button> : null,
}));

import QuoteViewPage from './QuoteViewPage';

const quoteFixture = () => ({
  id: 66,
  quoteNo: 'QT-0826-0002',
  statusValue: 'Draft',
  statusCode: 'DRAFT',
  lifecycleVersion: 1,
  currencyId: 10,
  currencyCode: 'SAR',
  totalAmount: 460460,
  quoteDate: '2026-08-26',
  validUntil: '2026-09-25',
  quoteItems: [
    { id: 2021, productName: 'Pump', quantity: 4, unitPrice: 18500, discount: 0, totalAmount: 85100, taxAmount: 11100, taxRatePercentApplied: 15 },
  ],
  customerName: 'Naspak',
  customerEmail: 'zahid@naspakinc.com',
  rfqId: 58,
});

function renderQuote() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/sales/quotes/view/66']}>
        <Routes>
          <Route path="/sales/quotes/view/:id" element={<QuoteViewPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

async function walkTheSendChain() {
  fireEvent.click(await screen.findByRole('button', { name: /send to customer/i }));
  fireEvent.click(await screen.findByRole('button', { name: 'choose recipient' }));
  fireEvent.click(await screen.findByRole('button', { name: 'confirm prices' }));
  await waitFor(() => expect(sendEmail).toHaveBeenCalledWith(66, 'buyer@customer.test'));
}

beforeEach(() => {
  vi.clearAllMocks();
  getById.mockResolvedValue(quoteFixture());
  getPriceAttestation.mockResolvedValue({ satisfied: false });
  getSendReadiness.mockResolvedValue({ quoteId: 66, canSend: true, blockers: [] });
  confirmPriceAttestation.mockResolvedValue({});
});

describe('what the rep is told after the server accepts a send', () => {
  it('does not claim the quote was emailed when it was only queued', async () => {
    sendEmail.mockResolvedValue({ held: false, queuedForDelivery: true, delivered: false });
    renderQuote();

    await walkTheSendChain();

    await waitFor(() => expect(toastMock).toHaveBeenCalledWith(expect.stringMatching(/queued/i), expect.anything()));
    expect(toastMock.success).not.toHaveBeenCalledWith(expect.stringMatching(/emailed/i));
  });

  it('says emailed only when the server confirmed delivery', async () => {
    sendEmail.mockResolvedValue({ held: false, queuedForDelivery: false, delivered: true });
    renderQuote();

    await walkTheSendChain();

    await waitFor(() => expect(toastMock.success).toHaveBeenCalledWith(expect.stringMatching(/emailed to the customer/i)));
  });
});
