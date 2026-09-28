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
  getLatestQuote: vi.fn(), prepareQuoteDraft: vi.fn(), saveQuoteTerms: vi.fn(), saveLinePricing: vi.fn(), stockGet: vi.fn(),
  getById: vi.fn(), getSendReadiness: vi.fn(), getPriceAttestation: vi.fn(), getEmailDraft: vi.fn(),
  confirmPriceAttestation: vi.fn(), sendEmail: vi.fn(), downloadPdf: vi.fn(), recordPortalSubmission: vi.fn(),
}));

vi.mock('../../../api/services/rfqService', () => ({
  default: { getLatestQuote: mocks.getLatestQuote, prepareQuoteDraft: mocks.prepareQuoteDraft, saveQuoteTerms: mocks.saveQuoteTerms, saveLinePricing: mocks.saveLinePricing },
}));
vi.mock('../../../api/services/quoteService', () => ({
  default: {
    getById: mocks.getById, getSendReadiness: mocks.getSendReadiness, getPriceAttestation: mocks.getPriceAttestation,
    getEmailDraft: mocks.getEmailDraft, confirmPriceAttestation: mocks.confirmPriceAttestation, sendEmail: mocks.sendEmail,
    downloadPdf: mocks.downloadPdf, recordPortalSubmission: mocks.recordPortalSubmission,
  },
}));
vi.mock('../../../api/services/stockPriceService', () => ({ default: { get: mocks.stockGet } }));
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

function renderDialog(onSent = vi.fn(), deadline: string | null = null) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <SnackbarProvider>
        <MemoryRouter>
          <SendQuoteDialog open rfqId={6} onClose={vi.fn()} onSent={onSent} deadline={deadline} />
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
    expect(within(dialog).getByRole('button', { name: 'Send by email' })).toBeDisabled();

    fireEvent.change(within(dialog).getByLabelText('Customer email'), { target: { value: 'buyer@sec.example' } });
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Send by email' })).toBeEnabled());
    fireEvent.click(within(dialog).getByRole('button', { name: 'Send by email' }));

    await waitFor(() => expect(mocks.sendEmail).toHaveBeenCalledWith(7, 'buyer@sec.example', undefined));
    expect(mocks.saveQuoteTerms).toHaveBeenCalledWith(7, expect.objectContaining({ currencyId: 1, validUntil: expect.stringMatching(/^\d{4}-\d{2}-\d{2}$/) }));
    expect(mocks.confirmPriceAttestation).toHaveBeenCalledWith(7, 'SALES_MANAGER', 'Golden Manager');
    await waitFor(() => expect(onSent).toHaveBeenCalled());
  });

  it('after the customer deadline, Send asks first and sends only on Send anyway', async () => {
    renderDialog(vi.fn(), '2026-01-15T00:00:00');
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(await within(dialog).findByLabelText('Customer email'), { target: { value: 'buyer@sec.example' } });
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Send by email' })).toBeEnabled());

    fireEvent.click(within(dialog).getByRole('button', { name: 'Send by email' }));
    expect(await screen.findByText('The deadline has passed')).toBeInTheDocument();
    expect(screen.getByText(/Do you really want to send the quote\?/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel', hidden: false }));
    await waitFor(() => expect(screen.queryByText('The deadline has passed')).not.toBeInTheDocument());
    expect(mocks.sendEmail).not.toHaveBeenCalled();

    fireEvent.click(within(dialog).getByRole('button', { name: 'Send by email' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Send anyway' }));
    await waitFor(() => expect(mocks.sendEmail).toHaveBeenCalledWith(7, 'buyer@sec.example', undefined));
  });

  it('a portal customer: Download PDF needs no email, then Mark as submitted records it as sent', async () => {
    mocks.downloadPdf.mockResolvedValue(new Blob(['%PDF'], { type: 'application/pdf' }));
    mocks.recordPortalSubmission.mockResolvedValue({ quoteNo: 'QT-0926-0004', submitted: true, alreadySent: false });
    URL.createObjectURL = vi.fn(() => 'blob:quote');
    URL.revokeObjectURL = vi.fn();
    const onSent = renderDialog();
    const dialog = await screen.findByRole('dialog');
    await within(dialog).findByText('BATTERY, DRY CELL, 3.6VDC');

    // No email typed: emailing is not possible yet, downloading for the portal is.
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Download PDF' })).toBeEnabled());
    expect(within(dialog).getByRole('button', { name: 'Send by email' })).toBeDisabled();

    fireEvent.click(within(dialog).getByRole('button', { name: 'Download PDF' }));
    await waitFor(() => expect(mocks.downloadPdf).toHaveBeenCalledWith(7));
    expect(mocks.confirmPriceAttestation).toHaveBeenCalled();
    expect(await screen.findByText("Submit it on the customer's portal")).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Portal reference (optional)'), { target: { value: 'BID-2291' } });
    fireEvent.click(screen.getByRole('button', { name: 'Mark as submitted' }));
    await waitFor(() => expect(mocks.recordPortalSubmission).toHaveBeenCalledWith(7, 'BID-2291'));
    await waitFor(() => expect(onSent).toHaveBeenCalled());
    expect(mocks.sendEmail).not.toHaveBeenCalled();
  });

  it('a portal submission after the deadline asks first, like the email', async () => {
    mocks.downloadPdf.mockResolvedValue(new Blob(['%PDF']));
    mocks.recordPortalSubmission.mockResolvedValue({ quoteNo: 'QT-0926-0004', submitted: true, alreadySent: false });
    URL.createObjectURL = vi.fn(() => 'blob:quote');
    URL.revokeObjectURL = vi.fn();
    renderDialog(vi.fn(), '2026-01-15T00:00:00');
    const dialog = await screen.findByRole('dialog');
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Download PDF' })).toBeEnabled());
    fireEvent.click(within(dialog).getByRole('button', { name: 'Download PDF' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Mark as submitted' }));

    expect(await screen.findByText(/Do you really want to record the quote as submitted\?/)).toBeInTheDocument();
    expect(mocks.recordPortalSubmission).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Record anyway' }));
    await waitFor(() => expect(mocks.recordPortalSubmission).toHaveBeenCalled());
  });

  it('an unpriced line and a Setup gap stop the send and say what to do', async () => {
    mocks.getById.mockResolvedValue(quote({ customerEmail: 'buyer@sec.example', quoteItems: [line(), line({ id: 2, unitPrice: 0, taxableBase: 0, taxAmount: null })] }));
    mocks.getSendReadiness.mockResolvedValue({ quoteId: 7, canSend: false, blockers: [
      { code: 'ISSUER_IDENTITY_INCOMPLETE', message: 'Add your company address to quotes.', setupLabel: 'Open Quote Format', setupPath: '/setup/quote-format' },
      { code: 'OUTPUT_TAX_NOT_DERIVED', message: 'Line 2 has no derived tax.' },
    ] });
    renderDialog();
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText(/1 line has no price yet. Price it on the RFQ, or send it as Price to follow, an Estimate, or Not quoting/)).toBeInTheDocument();
    expect(within(dialog).getByText('Add your company address to quotes.')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Open Quote Format' })).toBeInTheDocument();
    // Tax cannot be worked out on an unpriced line, so that message is not repeated.
    expect(within(dialog).queryByText('Line 2 has no derived tax.')).not.toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Send by email' })).toBeDisabled();
  });

  it('a quote already sent is not sent again from here', async () => {
    mocks.getLatestQuote.mockResolvedValue({ quoteId: 7, quoteNo: 'QT-0926-0004', state: 'SENT' });
    renderDialog();
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText(/QT-0926-0004 was already sent/)).toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: 'Send by email' })).not.toBeInTheDocument();
  });

  it('a line with no price can go as Price to follow, an estimate from the record, or not quoted with a reason', async () => {
    mocks.saveLinePricing.mockResolvedValue({ quoteId: 7 });
    mocks.stockGet.mockResolvedValue({ trackRecord: { lastQuoted: { unitPrice: 51 }, lastWon: null, timesQuoted: 1, timesWon: 0 }, supplierPrices: [], price: { unitPrice: null } });
    mocks.getById.mockResolvedValue(quote({ customerEmail: 'buyer@sec.example', currencyId: 1, currencyCode: 'SAR', quoteItems: [
      line(),
      line({ id: 2, rfqItemId: 22, itemDescription: 'RELAY', unitPrice: 0, taxableBase: 0, taxAmount: null }),
      line({ id: 3, rfqItemId: 23, itemDescription: 'TYRE', unitPrice: 0, taxableBase: 0, taxAmount: null }),
      line({ id: 4, rfqItemId: 24, itemDescription: 'CABLE', unitPrice: 0, taxableBase: 0, taxAmount: null }),
    ] }));
    renderDialog();
    const dialog = await screen.findByRole('dialog');
    expect((await within(dialog).findAllByText(/3 lines have no price yet/)).length).toBeGreaterThan(0);
    const rowOf = (name: string) => within(dialog).getByText(name).closest('tr')! as HTMLElement;

    fireEvent.click(within(rowOf('RELAY')).getByText('Price to follow'));
    await waitFor(() => expect(mocks.saveLinePricing).toHaveBeenCalledWith(7, 2, { status: 'TO_FOLLOW' }));
    // The email preview follows the new total when the rep has not edited it.
    await waitFor(() => expect(mocks.getEmailDraft.mock.calls.length).toBeGreaterThan(1));

    fireEvent.click(within(rowOf('TYRE')).getByText('Estimate'));
    await waitFor(() => expect(within(rowOf('TYRE')).getByLabelText('Estimated price')).toHaveValue(51));
    fireEvent.click(within(rowOf('TYRE')).getByRole('button', { name: 'Use estimate' }));
    await waitFor(() => expect(mocks.saveLinePricing).toHaveBeenCalledWith(7, 3, { status: 'ESTIMATE', unitPrice: 51 }));

    fireEvent.click(within(rowOf('CABLE')).getByText('Not quoting'));
    fireEvent.click(within(rowOf('CABLE')).getByText('Discontinued by manufacturer'));
    fireEvent.click(within(rowOf('CABLE')).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(mocks.saveLinePricing).toHaveBeenCalledWith(7, 4, { status: 'NOT_QUOTED', note: 'Discontinued by manufacturer' }));
  });

  it("Not quoting offers Other, which opens a box for the rep's own reason", async () => {
    mocks.saveLinePricing.mockResolvedValue({ quoteId: 7 });
    mocks.getById.mockResolvedValue(quote({ customerEmail: 'buyer@sec.example', currencyId: 1, currencyCode: 'SAR', quoteItems: [
      line(),
      line({ id: 4, rfqItemId: 24, itemDescription: 'CABLE', unitPrice: 0, taxableBase: 0, taxAmount: null }),
    ] }));
    renderDialog();
    const dialog = await screen.findByRole('dialog');
    const row = await within(dialog).findByText('CABLE').then((el) => el.closest('tr')! as HTMLElement);

    fireEvent.click(within(row).getByText('Not quoting'));
    // No typing box until Other is chosen; the quick reasons are one click.
    expect(within(row).queryByLabelText('Reason not quoted')).not.toBeInTheDocument();
    expect(within(row).getByRole('button', { name: 'Save' })).toBeDisabled();

    fireEvent.click(within(row).getByText('Other'));
    fireEvent.change(within(row).getByLabelText('Reason not quoted'), { target: { value: "Customer's drawing revision not received" } });
    fireEvent.click(within(row).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(mocks.saveLinePricing).toHaveBeenCalledWith(7, 4, { status: 'NOT_QUOTED', note: "Customer's drawing revision not received" }));
  });

  it('lines already set to follow or not quoted do not block the send', async () => {
    mocks.getById.mockResolvedValue(quote({ customerEmail: 'buyer@sec.example', currencyId: 1, currencyCode: 'SAR', validUntil: '2099-01-01T00:00:00', quoteItems: [
      line(),
      line({ id: 2, itemDescription: 'RELAY', unitPrice: 0, taxableBase: 0, pricingStatus: 'TO_FOLLOW' }),
      line({ id: 3, itemDescription: 'CABLE', unitPrice: 0, taxableBase: 0, pricingStatus: 'NOT_QUOTED', pricingNote: 'Discontinued by manufacturer' }),
    ] }));
    renderDialog();
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText('To follow')).toBeInTheDocument();
    expect(within(dialog).getByText('Not quoted: Discontinued by manufacturer')).toBeInTheDocument();
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Send by email' })).toBeEnabled());
  });
});
