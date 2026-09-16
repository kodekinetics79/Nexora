import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * The words the rep reviewed in the send dialog are the words that are sent.
 *
 * Owner ask 2026-09-15 (D26): the "Email quote" dialog showed only a recipient. The server now
 * hands the default subject and body over when the dialog opens, and the edited pair travels
 * through the price confirmation to the send — this pins the hand-off on the screen.
 */

const { getById, getPriceAttestation, getSendReadiness, sendEmail, confirmPriceAttestation, getEmailDraft } = vi.hoisted(() => ({
  getById: vi.fn(),
  getPriceAttestation: vi.fn(),
  getSendReadiness: vi.fn(),
  sendEmail: vi.fn(),
  confirmPriceAttestation: vi.fn(),
  getEmailDraft: vi.fn(),
}));

const toastMock = vi.hoisted(() => Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }));

vi.mock('../../../api/services/quoteService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/quoteService')>();
  return {
    ...actual,
    default: {
      getById, getPriceAttestation, getSendReadiness, sendEmail, confirmPriceAttestation, getEmailDraft,
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
vi.mock('react-hot-toast', () => ({ toast: toastMock, default: toastMock }));
vi.mock('../../../components/common/CommercialLineIntelligence', () => ({ default: () => null }));
vi.mock('./QuoteOutcomeDialog', () => ({ default: () => null }));
vi.mock('./ExtendValidityDialog', () => ({ default: () => null }));
vi.mock('./customer-awards', () => ({ CustomerAwardDialog: () => null }));

// The dialog reduced to what it hands back: the recipient plus the words the rep edited. It also
// echoes the draft it was given, so the test can see the server's default reached the dialog.
vi.mock('../../../components/common/EmailPromptDialog', () => ({
  default: ({ open, onConfirm, initialSubject, initialBody, attachmentName }: {
    open: boolean; onConfirm: (email: string, subject?: string, body?: string) => void;
    initialSubject?: string; initialBody?: string; attachmentName?: string;
  }) => open ? (
    <div>
      <div data-testid="draft-subject">{initialSubject}</div>
      <div data-testid="draft-body">{initialBody}</div>
      <div data-testid="draft-attachment">{attachmentName}</div>
      <button onClick={() => onConfirm('buyer@customer.test', 'Re: tender 77', 'Dear Ahmed,\nEdited.')}>send edited</button>
    </div>
  ) : null,
}));
vi.mock('./PriceConfirmationDialog', () => ({
  default: ({ open, onConfirm }: { open: boolean; onConfirm: (source: string, reference: string) => void }) =>
    open ? <button onClick={() => onConfirm('SUPPLIER_QUOTE', 'SQ-1')}>confirm prices</button> : null,
}));

import QuoteViewPage from './QuoteViewPage';

const quote = {
  id: 66,
  quoteNo: 'QT-0926-0001',
  statusValue: 'Draft',
  statusCode: 'DRAFT',
  lifecycleVersion: 1,
  currencyId: 3,
  currencyCode: 'SAR',
  totalAmount: 4205,
  quoteDate: '2026-09-15',
  validUntil: '2026-10-15',
  customerName: 'Marafiq',
  customerEmail: 'buyer@customer.test',
  quoteItems: [
    { id: 1, productName: 'Gasket', quantity: 20, unitPrice: 10, discount: 0, totalAmount: 230, taxAmount: 30, taxableBase: 200, taxRatePercentApplied: 15 },
  ],
};

function renderQuote() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/sales/quotes/view/66']}>
        <Routes><Route path="/sales/quotes/view/:id" element={<QuoteViewPage />} /></Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getById.mockResolvedValue(quote);
  getPriceAttestation.mockResolvedValue({ satisfied: false, currentLines: [], attestedLines: [] });
  getSendReadiness.mockResolvedValue({ quoteId: 66, canSend: true, blockers: [] });
  confirmPriceAttestation.mockResolvedValue({});
  sendEmail.mockResolvedValue({ held: false, queuedForDelivery: true, delivered: false });
  getEmailDraft.mockResolvedValue({
    quoteId: 66, quoteNo: 'QT-0926-0001', recipientEmail: 'buyer@customer.test',
    subject: 'Quote #QT-0926-0001 from Noor and Sons',
    body: 'Dear Marafiq,\n\nPlease find attached our quotation #QT-0926-0001.',
    attachmentFileName: 'Quote_QT-0926-0001.pdf',
  });
});

describe('QuoteViewPage — the reviewed e-mail is the e-mail that is sent', () => {
  it('fetches the server default when the dialog opens and hands it to the dialog', async () => {
    renderQuote();
    fireEvent.click(await screen.findByRole('button', { name: /send to customer/i }));

    await waitFor(() => expect(getEmailDraft).toHaveBeenCalledWith(66));
    expect(await screen.findByTestId('draft-subject')).toHaveTextContent('Quote #QT-0926-0001 from Noor and Sons');
    expect(screen.getByTestId('draft-body')).toHaveTextContent(/Please find attached our quotation/);
    expect(screen.getByTestId('draft-attachment')).toHaveTextContent('Quote_QT-0926-0001.pdf');
  });

  it('sends the edited subject and body, through the price confirmation, to the send', async () => {
    renderQuote();
    fireEvent.click(await screen.findByRole('button', { name: /send to customer/i }));
    fireEvent.click(await screen.findByText('send edited'));
    fireEvent.click(await screen.findByText('confirm prices'));

    await waitFor(() => expect(sendEmail).toHaveBeenCalledWith(66, 'buyer@customer.test', {
      subject: 'Re: tender 77',
      body: 'Dear Ahmed,\nEdited.',
    }));
  });
});
