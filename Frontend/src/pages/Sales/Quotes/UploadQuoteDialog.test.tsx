import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import dayjs from 'dayjs';

/**
 * Owner request 2026-09-28: a rep who quoted outside Nexora uploads that quote. One small window on
 * the Quotes list: the file, the RFQ it answers, the number on the file, the date sent, the amount.
 */

const { upload, getRfqs } = vi.hoisted(() => ({ upload: vi.fn(), getRfqs: vi.fn() }));

vi.mock('../../../api/services/quoteService', () => ({ default: { upload } }));
vi.mock('../../../api/services/rfqService', () => ({ default: { getAll: getRfqs } }));
vi.mock('../../../api/services/currencyService', () => ({
  default: {
    getAll: vi.fn().mockResolvedValue({
      items: [{ id: 5, code: 'SAR', symbol: null, isBaseCurrency: true }, { id: 6, code: 'USD', symbol: '$', isBaseCurrency: false }],
    }),
  },
}));
vi.mock('../../../api/services/commercialPolicyService', () => ({
  default: { getPolicy: vi.fn().mockResolvedValue({ outputTaxRatePercent: 15 }) },
}));

import UploadQuoteDialog from './UploadQuoteDialog';

const rfqs = [
  { id: 41, rfqno: 'NXR-RFQ-1-2026-00000007', customerName: 'Saudi Electricity Company', latestQuoteNo: null },
  { id: 42, rfqno: 'NXR-RFQ-1-2026-00000005', customerName: 'Khobar Power', latestQuoteNo: 'QT-0926-0006', latestQuoteSentOn: '2026-09-20T09:00:00' },
  { id: 43, rfqno: 'NXR-RFQ-1-2026-00000003', customerName: 'Saudi Electricity Company', latestQuoteNo: 'QT-0926-0005', latestQuoteSentOn: null },
];

function renderDialog(onSaved = vi.fn()) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <UploadQuoteDialog open businessUnitId={1} onClose={vi.fn()} onSaved={onSaved} />
    </QueryClientProvider>,
  );
  return onSaved;
}

const chooseFile = (name: string) => {
  const file = new File(['%PDF-1.4'], name, { type: 'application/pdf' });
  fireEvent.change(screen.getByTestId('upload-quote-file'), { target: { files: [file] } });
  return file;
};

const openRfqs = async () => {
  const input = screen.getByRole('combobox', { name: /RFQ/ });
  fireEvent.mouseDown(input);
  fireEvent.change(input, { target: { value: 'NXR' } });
  return screen.findByRole('listbox');
};

beforeEach(() => {
  vi.clearAllMocks();
  getRfqs.mockResolvedValue({ items: rfqs, totalItems: rfqs.length });
  upload.mockResolvedValue({ quoteId: 99, quoteNo: 'QT-0926-0007' });
});

describe('Upload a quote', () => {
  it('saves the file against the chosen RFQ as a sent quote', async () => {
    const onSaved = renderDialog();
    const file = chooseFile('QT-EXCEL-77.pdf');

    // The number on the quote is usually its file name: pre-filled, still editable.
    expect(screen.getByLabelText(/Quote no\. on the file/)).toHaveValue('QT-EXCEL-77');
    fireEvent.click(within(await openRfqs()).getByText('NXR-RFQ-1-2026-00000007'));
    fireEvent.change(screen.getByLabelText(/Amount before VAT/), { target: { value: '1000' } });

    expect(await screen.findByText('With VAT 15%: SAR 1,150.00')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save quote' })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: 'Save quote' }));

    await waitFor(() => expect(upload).toHaveBeenCalledWith({
      file,
      rfqId: 41,
      quoteNumber: 'QT-EXCEL-77',
      sentOn: dayjs().format('YYYY-MM-DD'),
      validUntil: null,
      currencyId: 5,
      amount: 1000,
    }));
    await waitFor(() => expect(onSaved).toHaveBeenCalledWith({ quoteId: 99, quoteNo: 'QT-0926-0007' }));
  });

  it('says which unsent draft it will replace', async () => {
    renderDialog();

    const listbox = await openRfqs();
    const withDraft = within(listbox).getByText('NXR-RFQ-1-2026-00000003').closest('li')!;
    expect(withDraft).not.toHaveAttribute('aria-disabled', 'true');
    expect(within(withDraft).getByText(/Replaces draft QT-0926-0005/)).toBeInTheDocument();

    fireEvent.click(within(listbox).getByText('NXR-RFQ-1-2026-00000003'));
    expect(await screen.findByText('Saudi Electricity Company · Replaces draft QT-0926-0005')).toBeInTheDocument();
  });

  it('will not attach to an RFQ whose quote reached the customer', async () => {
    renderDialog();

    const listbox = await openRfqs();
    const taken = within(listbox).getByText('NXR-RFQ-1-2026-00000005').closest('li')!;

    expect(taken).toHaveAttribute('aria-disabled', 'true');
    expect(within(taken).getByText(/Has QT-0926-0006/)).toBeInTheDocument();
  });

  it('keeps Save off until the file, RFQ, number and amount are there', async () => {
    renderDialog();

    expect(screen.getByRole('button', { name: 'Save quote' })).toBeDisabled();
    chooseFile('scan.pdf');
    expect(screen.getByRole('button', { name: 'Save quote' })).toBeDisabled();
  });

  it('shows the server\'s reason when the quote is refused', async () => {
    upload.mockRejectedValue({
      isAxiosError: true,
      response: { status: 409, data: { message: 'Quote number QT-EXCEL-77 is already in Nexora as QT-0926-0004.' } },
    });
    renderDialog();
    chooseFile('QT-EXCEL-77.pdf');
    fireEvent.click(within(await openRfqs()).getByText('NXR-RFQ-1-2026-00000007'));
    fireEvent.change(screen.getByLabelText(/Amount before VAT/), { target: { value: '10' } });
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save quote' })).toBeEnabled());

    fireEvent.click(screen.getByRole('button', { name: 'Save quote' }));

    expect(await screen.findByText('Quote number QT-EXCEL-77 is already in Nexora as QT-0926-0004.')).toBeInTheDocument();
  });
});
