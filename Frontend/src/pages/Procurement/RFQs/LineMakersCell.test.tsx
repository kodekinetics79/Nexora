import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import { describe, expect, it, vi } from 'vitest';

/**
 * Owner 2026-09-27: "Makers" was too vague and "Approved brands" begged "approved by whom?". The
 * line says who decides, the customer, and the list is edited as tags: remove with ×, add with Enter.
 */

const mocks = vi.hoisted(() => ({ save: vi.fn() }));
vi.mock('../../../api/services/rfqService', () => ({ default: { saveAcceptedMakers: mocks.save } }));

import { LineMakersCell } from './LineMakers';

const item = {
  id: 7,
  manufacturerName: null,
  manufacturerPartNumber: '300012346',
  productShortDescription: 'CONTACTOR, 3P, 95A',
  extraFields: JSON.stringify({ 'Approved manufacturers': 'ABB AF96-30-00-13; SIEMENS 3RT2046-1AN20; Eaton' }),
};

function renderCell() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <SnackbarProvider>
        <LineMakersCell rfqId={2} item={item} canEdit />
      </SnackbarProvider>
    </QueryClientProvider>,
  );
}

describe('Approved brands on an RFQ line', () => {
  it('says the customer accepts any of the brands, and opens a tag list the rep edits without separators', async () => {
    mocks.save.mockResolvedValue({ acceptedMakers: ['ABB AF96-30-00-13', 'Eaton', 'Schneider LC1D95M7'] });
    renderCell();
    expect(screen.getByText('Customer accepts any of')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Edit accepted brands' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('Brands the customer accepts for this part')).toBeInTheDocument();
    expect(within(dialog).queryByText(/Separate makers with/)).not.toBeInTheDocument();

    fireEvent.click(within(dialog).getByLabelText(/Remove SIEMENS/));
    fireEvent.change(within(dialog).getByLabelText('Add a brand'), { target: { value: 'Schneider LC1D95M7' } });
    fireEvent.keyDown(within(dialog).getByLabelText('Add a brand'), { key: 'Enter' });
    expect(within(dialog).getByLabelText('Add a brand')).toHaveValue('');

    fireEvent.click(within(dialog).getByRole('button', { name: 'Save brands' }));
    await waitFor(() => expect(mocks.save).toHaveBeenCalledWith(2, 7, 'ABB AF96-30-00-13; Eaton; Schneider LC1D95M7'));
  });
});
