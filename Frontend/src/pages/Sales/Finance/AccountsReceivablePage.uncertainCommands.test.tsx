import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { SnackbarProvider } from 'notistack';
import AccountsReceivablePage from './AccountsReceivablePage';

const getOpenItems = vi.fn();
const getDocuments = vi.fn();
const getPayments = vi.fn();
const getWriteOffs = vi.fn();
const getRefunds = vi.fn();
const getBankAccounts = vi.fn();
const getWriteOffEligibility = vi.fn();
const getRefundEligibility = vi.fn();
const createAdjustment = vi.fn();
const createWriteOff = vi.fn();
const createRefund = vi.fn();

vi.mock('../../../api/services/commercialFinanceService', async importOriginal => {
  const actual = await importOriginal<typeof import('../../../api/services/commercialFinanceService')>();
  return {
    ...actual,
    default: {
      ...actual.default,
      getOpenItems: () => getOpenItems(),
      getDocuments: () => getDocuments(),
      getPayments: () => getPayments(),
      getWriteOffs: () => getWriteOffs(),
      getRefunds: () => getRefunds(),
      getBankAccounts: () => getBankAccounts(),
      getWriteOffEligibility: (documentId: number) => getWriteOffEligibility(documentId),
      getRefundEligibility: (paymentId: number) => getRefundEligibility(paymentId),
      createAdjustment: (invoiceId: number, command: unknown, key: string) => createAdjustment(invoiceId, command, key),
      createWriteOff: (command: unknown, key: string) => createWriteOff(command, key),
      createRefund: (command: unknown, key: string) => createRefund(command, key),
    },
  };
});

vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));

const OPEN_ITEM = {
  documentId: 101,
  documentNumber: 'INV-SYNTH-101',
  documentType: 'Invoice',
  customerId: 71,
  commercialCaseId: 91,
  currencyId: 4,
  currencyCode: 'USD',
  documentDate: '2026-08-20T00:00:00Z',
  dueDate: '2026-09-20T00:00:00Z',
  originalAmount: 500,
  outstandingAmount: 500,
  daysPastDue: 0,
  agingBucket: 'Current',
};

const INVOICE = {
  id: 101,
  commercialCaseId: 91,
  customerId: 71,
  currencyId: 4,
  currencyCode: 'USD',
  documentType: 'Invoice',
  status: 'Issued',
  documentNumber: 'INV-SYNTH-101',
  documentDate: '2026-08-20T00:00:00Z',
  dueDate: '2026-09-20T00:00:00Z',
  issuedOn: '2026-08-20T00:00:00Z',
  subTotal: 500,
  discountAmount: 0,
  taxAmount: 0,
  totalAmount: 500,
  allocatedAmount: 0,
  outstandingAmount: 500,
  version: 1,
  lines: [{ id: 1001, description: 'Synthetic line', quantity: 5, unitPrice: 100, discountAmount: 0, taxAmount: 0, lineTotal: 500 }],
};

const PAYMENT = {
  id: 301,
  customerId: 71,
  commercialCaseId: 91,
  currencyId: 4,
  currencyCode: 'USD',
  receiptNumber: 'RCPT-SYNTH-301',
  status: 'Posted',
  paymentDate: '2026-08-21T00:00:00Z',
  amount: 175,
  allocatedAmount: 100,
  unappliedAmount: 75,
  version: 1,
};

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <SnackbarProvider>
        <MemoryRouter><AccountsReceivablePage /></MemoryRouter>
      </SnackbarProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getOpenItems.mockResolvedValue([OPEN_ITEM]);
  getDocuments.mockResolvedValue([INVOICE]);
  getPayments.mockResolvedValue([PAYMENT]);
  getWriteOffs.mockResolvedValue([]);
  getRefunds.mockResolvedValue([]);
  getBankAccounts.mockResolvedValue([]);
  getWriteOffEligibility.mockResolvedValue({ receivableDocumentId: 101, currentBalance: 500, pendingAmount: 0, availableAmount: 500 });
  getRefundEligibility.mockResolvedValue({ sourcePaymentId: 301, paymentAmount: 175, allocatedAmount: 100, reservedAmount: 0, releasedAmount: 0, availableAmount: 75 });
});

describe('uncertain finance command recovery', () => {
  it('keeps an uncertain receivable adjustment open and replays the identical command and key', async () => {
    createAdjustment.mockRejectedValueOnce(new Error('connection dropped')).mockResolvedValueOnce({ id: 202, documentType: 'CreditNote' });
    renderPage();

    fireEvent.click(await screen.findByRole('tab', { name: /Documents/ }));
    fireEvent.click(await screen.findByRole('button', { name: 'Adjust invoice INV-SYNTH-101' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Create credit note' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Reason' }), { target: { value: 'Customer-approved price correction' } });
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Adjustment quantity for Synthetic line' }), { target: { value: '1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create draft' }));

    const retry = await screen.findByRole('button', { name: 'Retry safely' });
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled();
    expect(screen.getByRole('textbox', { name: 'Reason' })).toBeDisabled();
    fireEvent.click(retry);

    await waitFor(() => expect(createAdjustment).toHaveBeenCalledTimes(2));
    expect(createAdjustment.mock.calls[1]).toEqual(createAdjustment.mock.calls[0]);
  });

  it('keeps an uncertain write-off open and replays the identical command and key', async () => {
    createWriteOff.mockRejectedValueOnce(new Error('connection dropped')).mockResolvedValueOnce({ id: 401 });
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Create write-off for INV-SYNTH-101' }));
    await waitFor(() => expect(getWriteOffEligibility).toHaveBeenCalledWith(101));
    fireEvent.change(screen.getByRole('textbox', { name: 'Business reason' }), { target: { value: 'Customer insolvency confirmed by finance' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create draft' }));

    const retry = await screen.findByRole('button', { name: 'Retry safely' });
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled();
    expect(screen.getByRole('textbox', { name: 'Business reason' })).toBeDisabled();
    fireEvent.click(retry);

    await waitFor(() => expect(createWriteOff).toHaveBeenCalledTimes(2));
    expect(createWriteOff.mock.calls[1]).toEqual(createWriteOff.mock.calls[0]);
  });

  it('keeps an uncertain refund open and replays the identical command and key', async () => {
    createRefund.mockRejectedValueOnce(new Error('connection dropped')).mockResolvedValueOnce({ id: 501 });
    renderPage();

    fireEvent.click(await screen.findByRole('tab', { name: /Payments/ }));
    fireEvent.click(await screen.findByRole('button', { name: 'Create customer refund from RCPT-SYNTH-301' }));
    await waitFor(() => expect(getRefundEligibility).toHaveBeenCalledWith(301));
    fireEvent.change(screen.getByRole('textbox', { name: 'Provider destination token' }), { target: { value: 'token:approved_12345' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Business reason' }), { target: { value: 'Verified customer overpayment return' } });
    fireEvent.click(screen.getByRole('checkbox', { name: /verified the destination/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Create draft' }));

    const retry = await screen.findByRole('button', { name: 'Retry safely' });
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled();
    expect(screen.getByRole('textbox', { name: 'Provider destination token' })).toBeDisabled();
    fireEvent.click(retry);

    await waitFor(() => expect(createRefund).toHaveBeenCalledTimes(2));
    expect(createRefund.mock.calls[1]).toEqual(createRefund.mock.calls[0]);
  });
});
