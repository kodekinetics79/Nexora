import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * The part asked for is obsolete (the maker replaced it) or discontinued (an equivalent is
 * offered with specs). The customer reads one sentence; suppliers are asked for the real part.
 */

const mocks = vi.hoisted(() => ({ saveOfferedPart: vi.fn(), getAll: vi.fn() }));
vi.mock('../../../api/services/rfqService', () => ({ default: { saveOfferedPart: mocks.saveOfferedPart } }));
vi.mock('../../../api/services/productService', () => ({ default: { getAll: mocks.getAll } }));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { id: 3, businessUnitId: 7 }, hasPermission: () => true }),
}));

import { OfferedPartCell, offeredSentence } from './OfferedPartDialog';

const line = {
  id: 9, manufacturerName: 'Riyadh Cables', manufacturerPartNumber: 'RC-4C95-XLPE-SWA',
  productShortDescription: 'CABLE, LV, XLPE/PVC',
};

function renderCell(overrides = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <SnackbarProvider>
        <OfferedPartCell rfqId={2} line={{ ...line, ...overrides }} canEdit />
      </SnackbarProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  mocks.saveOfferedPart.mockResolvedValue({ offeredPartNumber: 'X' });
  mocks.getAll.mockResolvedValue({ items: [] });
});

describe('Offer a different part', () => {
  it('writes one sentence for a replacement and for an equivalent', () => {
    expect(offeredSentence({ ...line, offeredPartNumber: 'RC-N', offeredMakerName: 'Riyadh Cables', offeredKind: 'REPLACEMENT' }))
      .toBe('Offered: Riyadh Cables RC-N, replaces RC-4C95-XLPE-SWA');
    expect(offeredSentence({ ...line, offeredPartNumber: 'SEL-751A', offeredKind: 'EQUIVALENT', offeredNote: 'Discontinued' }))
      .toBe('Offered as an equivalent: SEL-751A, in place of RC-4C95-XLPE-SWA. Discontinued');
    expect(offeredSentence(line)).toBeNull();
  });

  it('records the maker\'s replacement part', async () => {
    renderCell();
    fireEvent.click(screen.getByRole('button', { name: 'Offer a different part' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Part number offered'), { target: { value: 'RC-4C95-XLPE-SWA-N' } });
    fireEvent.change(within(dialog).getByLabelText('Note for the customer'), { target: { value: 'Maker replaced the SWA series' } });
    expect(within(dialog).getByText(/Offered: Riyadh Cables RC-4C95-XLPE-SWA-N, replaces RC-4C95-XLPE-SWA/)).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(mocks.saveOfferedPart).toHaveBeenCalledWith(2, 9, expect.objectContaining({
      partNumber: 'RC-4C95-XLPE-SWA-N', kind: 'REPLACEMENT', note: 'Maker replaced the SWA series',
    })));
  });

  it('an equivalent cannot be saved without the specs the customer needs', async () => {
    renderCell();
    fireEvent.click(screen.getByRole('button', { name: 'Offer a different part' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByText('Different brand, similar part'));
    fireEvent.change(within(dialog).getByLabelText('Part number offered'), { target: { value: 'SEL-751A' } });
    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeDisabled();
    fireEvent.change(within(dialog).getByLabelText('Specs for the customer'), { target: { value: 'IEC 61850, 5A CT, 110V DC' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(mocks.saveOfferedPart).toHaveBeenCalledWith(2, 9, expect.objectContaining({
      kind: 'EQUIVALENT', specs: 'IEC 61850, 5A CT, 110V DC',
    })));
  });

  it('shows what is offered on the line and can go back to the part asked for', async () => {
    renderCell({ offeredPartNumber: 'SEL-751A', offeredMakerName: 'SEL', offeredKind: 'EQUIVALENT', offeredSpecs: 'IEC 61850' });
    expect(screen.getByText('SEL SEL-751A')).toBeInTheDocument();
    expect(screen.getByText('offered instead: similar part, customer to check')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Offer a different part' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Offer as asked' }));
    await waitFor(() => expect(mocks.saveOfferedPart).toHaveBeenCalledWith(2, 9, {}));
  });
});
