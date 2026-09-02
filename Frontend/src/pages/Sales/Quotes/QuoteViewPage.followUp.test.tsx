import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * "Follow up on this quote".
 *
 * Quote delivery creates a follow-up on its own when a quote is sent; nothing let a rep set one
 * deliberately ("call Thursday about the price hold"), and the service method that could had no
 * endpoint and no button. This pins the button, the dialog, and the call it makes.
 */

const { getById, getPriceAttestation, createFollowUp } = vi.hoisted(() => ({
  getById: vi.fn(),
  getPriceAttestation: vi.fn(),
  createFollowUp: vi.fn(),
}));

vi.mock('../../../api/services/quoteService', () => ({
  default: {
    getById, getPriceAttestation, sendEmail: vi.fn(), transitionStatus: vi.fn(), exportPdf: vi.fn(),
    getRevisions: vi.fn().mockResolvedValue([]), getRevisionInfo: vi.fn().mockResolvedValue(null),
  },
}));
vi.mock('../../../api/services/commercialIntelligenceService', () => ({
  default: { createFollowUp: (body: unknown, key: string) => createFollowUp(body, key) },
}));
vi.mock('../../../api/services/procurementService', () => ({
  default: { getWorkbench: vi.fn().mockResolvedValue(null), getRfqIntelligence: vi.fn().mockResolvedValue(null) },
}));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 1 }, hasPermission: () => true }),
}));
vi.mock('react-hot-toast', () => ({
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
  default: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
}));
vi.mock('../../../components/common/CommercialLineIntelligence', () => ({ default: () => null }));
vi.mock('./QuoteOutcomeDialog', () => ({ default: () => null }));
vi.mock('./ExtendValidityDialog', () => ({ default: () => null }));
vi.mock('./PriceConfirmationDialog', () => ({ default: () => null }));
vi.mock('./customer-awards', () => ({ CustomerAwardDialog: () => null }));
vi.mock('../../../components/common/EmailPromptDialog', () => ({ default: () => null }));

import QuoteViewPage from './QuoteViewPage';

const sentQuote = {
  id: 9, quoteNo: 'QT-2026-0009', statusValue: 'Sent', statusCode: 'SENT', lifecycleVersion: 3, currencyId: 1,
  currencyCode: 'SAR', totalAmount: 115, quoteDate: '2026-08-01', validUntil: '2026-09-01', customerName: 'Aramco',
  quoteItems: [{ id: 1, productName: 'Cable tray', quantity: 1, unitPrice: 100, discount: 0, totalAmount: 100, taxAmount: 15, taxableBase: 100, taxRatePercentApplied: 15 }],
};

function renderQuote() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/sales/quotes/view/9']}>
        <Routes><Route path="/sales/quotes/view/:id" element={<QuoteViewPage />} /></Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getById.mockResolvedValue(sentQuote);
  getPriceAttestation.mockResolvedValue({ satisfied: true });
  createFollowUp.mockResolvedValue({ id: 501, quoteId: 9, dueAt: '2026-09-05T00:00:00Z', reason: 'Call about the price hold', status: 'Open', version: 1 });
});

describe('Follow up on this quote', () => {
  it('opens a small dialog and posts the due date and reason for this quote', async () => {
    renderQuote();
    fireEvent.click(await screen.findByRole('button', { name: /follow up on this quote/i }));

    const dialog = await screen.findByRole('dialog', { name: /follow up on qt-2026-0009/i });
    expect(dialog).toBeInTheDocument();
    const submit = screen.getByRole('button', { name: /set follow-up/i });
    // Nothing typed yet: disabled, and it says why.
    expect(submit).toBeDisabled();
    expect(screen.getByText(/say what the follow-up is for/i)).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText(/due on/i), { target: { value: '2026-09-05' } });
    fireEvent.change(screen.getByLabelText(/what for/i), { target: { value: 'Call about the price hold' } });
    expect(submit).toBeEnabled();
    fireEvent.click(submit);

    await waitFor(() => expect(createFollowUp).toHaveBeenCalledTimes(1));
    const [body, key] = createFollowUp.mock.calls[0] as [{ quoteId: number; dueAt: string; reason: string }, string];
    expect(body).toEqual({ quoteId: 9, dueAt: '2026-09-05T00:00:00.000Z', reason: 'Call about the price hold' });
    expect(key).toMatch(/^quote-follow-up:9:/);
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('refuses a reason longer than the 80 characters the Follow-ups list can show', async () => {
    renderQuote();
    fireEvent.click(await screen.findByRole('button', { name: /follow up on this quote/i }));
    await screen.findByRole('dialog');

    fireEvent.change(screen.getByLabelText(/what for/i), { target: { value: 'x'.repeat(81) } });
    expect(screen.getByRole('button', { name: /set follow-up/i })).toBeDisabled();
    expect(screen.getByText(/keep the reason to 80 characters/i)).toBeInTheDocument();
    expect(createFollowUp).not.toHaveBeenCalled();
  });
});
