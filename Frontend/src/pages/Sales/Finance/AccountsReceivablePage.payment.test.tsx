import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { SnackbarProvider } from 'notistack';
import dayjs from 'dayjs';
import AccountsReceivablePage from './AccountsReceivablePage';

const getOpenItems = vi.fn();
const getDocuments = vi.fn();
const getPayments = vi.fn();
const getWriteOffs = vi.fn();
const getRefunds = vi.fn();
const getBankAccounts = vi.fn();
const postPayment = vi.fn();

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
      postPayment: (command: unknown, key: string) => postPayment(command, key),
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

const BANK_ACCOUNTS = [
  { id: 21, name: 'Operating USD', institutionName: 'Synthetic Bank', maskedAccountNumber: '****1001', currencyId: 4, ledgerAccountId: 301, status: 'Active', openingDate: '2026-01-01', version: 1 },
  { id: 22, name: 'Receipts USD', institutionName: 'Synthetic Bank', maskedAccountNumber: '****2002', currencyId: 4, ledgerAccountId: 302, status: 'Active', openingDate: '2026-01-01', version: 1 },
  { id: 23, name: 'Closed USD', institutionName: 'Synthetic Bank', maskedAccountNumber: '****3003', currencyId: 4, ledgerAccountId: 303, status: 'Closed', openingDate: '2026-01-01', version: 2 },
  { id: 24, name: 'Operating EUR', institutionName: 'Synthetic Bank', maskedAccountNumber: '****4004', currencyId: 5, ledgerAccountId: 304, status: 'Active', openingDate: '2026-01-01', version: 1 },
];

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

async function chooseBankAccount(name: RegExp) {
  fireEvent.mouseDown(screen.getByRole('combobox', { name: /deposit bank account/i }));
  fireEvent.click(await screen.findByRole('option', { name }));
}

beforeEach(() => {
  vi.clearAllMocks();
  getOpenItems.mockResolvedValue([OPEN_ITEM]);
  getDocuments.mockResolvedValue([]);
  getPayments.mockResolvedValue([]);
  getWriteOffs.mockResolvedValue([]);
  getRefunds.mockResolvedValue([]);
  getBankAccounts.mockResolvedValue(BANK_ACCOUNTS);
});

describe('governed payment capture', () => {
  it('requires a compatible active authorized bank account and sends its id', async () => {
    postPayment.mockResolvedValue({ id: 501, receiptNumber: 'RCPT-501' });
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Record payment for INV-SYNTH-101' }));
    await chooseBankAccount(/Receipts USD/);
    const paymentDate = screen.getByLabelText('Payment date');
    expect(paymentDate).toBeRequired();
    fireEvent.change(paymentDate, { target: { value: '2026-08-15' } });

    expect(screen.queryByRole('option', { name: /Closed USD/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('option', { name: /Operating EUR/ })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Post payment' }));

    await waitFor(() => expect(postPayment).toHaveBeenCalledTimes(1));
    expect(postPayment.mock.calls[0][0]).toMatchObject({
      customerId: 71,
      commercialCaseId: 91,
      currencyId: 4,
      bankAccountId: 22,
      paymentDate: dayjs('2026-08-15').startOf('day').toISOString(),
      amount: 500,
      allocations: [{ receivableDocumentId: 101, amount: 500 }],
    });
    expect(postPayment.mock.calls[0][1]).toEqual(expect.any(String));
  });

  it('retries an uncertain payment with the identical command and idempotency key', async () => {
    postPayment.mockRejectedValueOnce(new Error('connection dropped')).mockResolvedValueOnce({ id: 501 });
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Record payment for INV-SYNTH-101' }));
    await chooseBankAccount(/Operating USD/);
    fireEvent.change(screen.getByLabelText('Payment date'), { target: { value: '2026-08-14' } });
    fireEvent.change(screen.getByLabelText('Bank reference'), { target: { value: 'WIRE-SYNTH-1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Post payment' }));

    const retry = await screen.findByRole('button', { name: 'Retry safely' });
    expect(screen.getByLabelText('Amount')).toBeDisabled();
    expect(screen.getByLabelText('Payment date')).toBeDisabled();
    expect(screen.getByRole('combobox', { name: /deposit bank account/i })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled();
    fireEvent.click(retry);

    await waitFor(() => expect(postPayment).toHaveBeenCalledTimes(2));
    expect(postPayment.mock.calls[1]).toEqual(postPayment.mock.calls[0]);
  });
});
