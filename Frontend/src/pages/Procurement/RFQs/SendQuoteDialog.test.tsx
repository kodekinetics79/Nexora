import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * Send quote from the RFQ: lines and total, currency and validity, where the prices came from,
 * and the customer email, in one window; Setup gaps named in plain words.
 */

const mocks = vi.hoisted(() => ({
  getLatestQuote: vi.fn(), prepareQuoteDraft: vi.fn(), saveQuoteTerms: vi.fn(),
  getById: vi.fn(), getSendReadiness: vi.fn(), getPriceAttestation: vi.fn(), getEmailDraft: vi.fn(),
  confirmPriceAttestation: vi.fn(), sendEmail: vi.fn(),
}));

vi.mock('../../../api/services/rfqService', () => ({
  default: { getLatestQuote: mocks.getLatestQuote, prepareQuoteDraft: mocks.prepareQuoteDraft, saveQuoteTerms: mocks.saveQuoteTerms },
}));
vi.mock('../../../api/services/quoteService', () => ({
  default: {
    getById: mocks.getById, getSendReadiness: mocks.getSendReadiness, getPriceAttestation: mocks.getPriceAttestation,
    getEmailDraft: mocks.getEmailDraft, confirmPriceAttestation: mocks.confirmPriceAttestation, sendEmail: mocks.sendEmail,
  },
}));
vi.mock('../../../api/services/currencyService', () => ({
  default: { getAll: vi.fn().mockResolvedValue({ items: [{ id: 1, code: 'SAR', isBaseCurrency: true }, { id: 2, code: 'USD' }] }) },
}));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { id: 3, businessUnitId: 7, userName: 'Golden Manager' }, hasPermission: () => true }),
}));

import SendQuoteDialog from './SendQuoteDialog';

const line = (overrides = {}) => ({
  id: 1, quoteId: 7, itemDescription: 'BATTERY, DRY CELL, 3.6VDC', quantity: 20, unitOfMeasure: 'EA', customerLineRef: '10',
  unitPrice: 45, taxableBase: 900, taxAmount: 135, totalAmount: 1035, deliveryLeadTime: 21, ...overrides,
});
const quote = (overrides = {}) => ({
  id: 7, quoteNo: 'QT-0926-0004', customerName: 'Saudi Electricity Company', customerEmail: '', currencyId: null, currencyCode: null,
  validUntil: null, quoteItems: [line()], ...overrides,
});

function renderDialog(onSent = vi.fn()) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <SnackbarProvider>
        <MemoryRouter>
          <SendQuoteDialog open rfqId={6} onClose={vi.fn()} onSent={onSent} />
        </MemoryRouter>
      </SnackbarProvider>
    </QueryClientProvider>,
  );
  return onSent;
}

beforeEach(() => {
  vi.clearAllMocks();
  mocks.getLatestQuote.mockResolvedValue({ quoteId: 7, quoteNo: 'QT-0926-0004', state: 'DRAFT' });
  mocks.getById.mockResolvedValue(quote());
  mocks.getSendReadiness.mockResolvedValue({ quoteId: 7, canSend: false, blockers: [{ code: 'QUOTE_INCOMPLETE', message: 'no currency' }, { code: 'PRICE_ATTESTATION_REQUIRED', message: 'confirm' }] });
  mocks.getPriceAttestation.mockResolvedValue({ quoteId: 7, satisfied: false });
  mocks.getEmailDraft.mockResolvedValue({ quoteId: 7, quoteNo: 'QT-0926-0004', recipientEmail: null, subject: 'Quote #QT-0926-0004', body: 'Dear customer', attachmentFileName: 'Quote_QT-0926-0004.pdf' });
  mocks.saveQuoteTerms.mockResolvedValue({ quoteId: 7 });
  mocks.confirmPriceAttestation.mockResolvedValue({ quoteId: 7, satisfied: true });
  mocks.sendEmail.mockResolvedValue({ held: false, delivered: false, queuedForDelivery: true });
});

describe('Send quote', () => {
  it('sets currency and 30-day validity, confirms the price source and sends to the typed email', async () => {
    const onSent = renderDialog();
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText('BATTERY, DRY CELL, 3.6VDC')).toBeInTheDocument();
    expect(within(dialog).getByText('Delivery 3 weeks')).toBeInTheDocument();
    expect(await within(dialog).findByText(/Enter the customer's email/)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Send quote' })).toBeDisabled();

    fireEvent.change(within(dialog).getByLabelText('Customer email'), { target: { value: 'buyer@sec.example' } });
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Send quote' })).toBeEnabled());
    fireEvent.click(within(dialog).getByRole('button', { name: 'Send quote' }));

    await waitFor(() => expect(mocks.sendEmail).toHaveBeenCalledWith(7, 'buyer@sec.example', undefined));
    expect(mocks.saveQuoteTerms).toHaveBeenCalledWith(7, expect.objectContaining({ currencyId: 1, validUntil: expect.stringMatching(/^\d{4}-\d{2}-\d{2}$/) }));
    expect(mocks.confirmPriceAttestation).toHaveBeenCalledWith(7, 'SALES_MANAGER', 'Golden Manager');
    await waitFor(() => expect(onSent).toHaveBeenCalled());
  });

  it('an unpriced line and a Setup gap stop the send and say what to do', async () => {
    mocks.getById.mockResolvedValue(quote({ customerEmail: 'buyer@sec.example', quoteItems: [line(), line({ id: 2, unitPrice: 0, taxableBase: 0, taxAmount: null })] }));
    mocks.getSendReadiness.mockResolvedValue({ quoteId: 7, canSend: false, blockers: [
      { code: 'ISSUER_IDENTITY_INCOMPLETE', message: 'Add your company address to quotes.', setupLabel: 'Open Quote Format', setupPath: '/setup/quote-format' },
      { code: 'OUTPUT_TAX_NOT_DERIVED', message: 'Line 2 has no derived tax.' },
    ] });
    renderDialog();
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText(/1 line has no price yet. Close this window and use Price it/)).toBeInTheDocument();
    expect(within(dialog).getByText('Add your company address to quotes.')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Open Quote Format' })).toBeInTheDocument();
    // Tax cannot be worked out on an unpriced line, so that message is not repeated.
    expect(within(dialog).queryByText('Line 2 has no derived tax.')).not.toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Send quote' })).toBeDisabled();
  });

  it('a quote already sent is not sent again from here', async () => {
    mocks.getLatestQuote.mockResolvedValue({ quoteId: 7, quoteNo: 'QT-0926-0004', state: 'SENT' });
    renderDialog();
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText(/QT-0926-0004 was already sent/)).toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: 'Send quote' })).not.toBeInTheDocument();
  });
});
