import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/** A rep's own message and signature for supplier RFQ emails; empty means the company's wording. */

const mocks = vi.hoisted(() => ({ getMine: vi.fn(), saveMine: vi.fn(), onClose: vi.fn() }));

vi.mock('../../api/services/supplierEmailSettingsService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/supplierEmailSettingsService')>();
  return { ...actual, default: { getMine: mocks.getMine, saveMine: mocks.saveMine } };
});

import MySupplierEmailDialog from './MySupplierEmailDialog';

const COMPANY = { defaultMessage: 'Please send your best price.', signOff: 'Kind regards,\nNoor And Sons' };

function renderDialog() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <SnackbarProvider>
        <MySupplierEmailDialog open onClose={mocks.onClose} />
      </SnackbarProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  mocks.saveMine.mockImplementation(async (body) => ({ ...body, company: COMPANY }));
});

describe('My supplier email', () => {
  it("shows the company's wording when the rep has not set their own", async () => {
    mocks.getMine.mockResolvedValue({ defaultMessage: null, signOff: null, company: COMPANY });
    renderDialog();

    expect(await screen.findByLabelText('My default message')).toHaveValue('');
    expect(screen.getByText("Your company's: Please send your best price.")).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'How your email ends' })).toHaveTextContent('Noor And Sons');
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('saves what the rep typed', async () => {
    mocks.getMine.mockResolvedValue({ defaultMessage: null, signOff: null, company: COMPANY });
    renderDialog();

    fireEvent.change(await screen.findByLabelText('My default message'), { target: { value: 'Need this by Sunday.' } });
    fireEvent.change(screen.getByLabelText('My signature'), { target: { value: 'Aisha Noor\n+966 50 000 0000' } });
    expect(screen.getByRole('region', { name: 'How your email ends' })).toHaveTextContent('Need this by Sunday.');
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(mocks.saveMine).toHaveBeenCalled());
    expect(mocks.saveMine.mock.calls[0][0]).toEqual({
      defaultMessage: 'Need this by Sunday.',
      signOff: 'Aisha Noor\n+966 50 000 0000',
    });
    await waitFor(() => expect(mocks.onClose).toHaveBeenCalled());
  });

  it("'Use company's' goes back to the company wording by saving null", async () => {
    mocks.getMine.mockResolvedValue({ defaultMessage: 'Mine', signOff: 'Aisha', company: COMPANY });
    renderDialog();

    expect(await screen.findByLabelText('My signature')).toHaveValue('Aisha');
    const useCompany = screen.getAllByRole('button', { name: "Use company's" });
    fireEvent.click(useCompany[1]);
    expect(screen.getByText(/Your company's: Kind regards/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(mocks.saveMine).toHaveBeenCalledWith({ defaultMessage: 'Mine', signOff: null }));
  });
});
