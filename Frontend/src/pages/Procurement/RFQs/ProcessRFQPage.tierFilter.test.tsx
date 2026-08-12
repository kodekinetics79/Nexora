/**
 * FR-QTM-01 in the browser: a buyer picking suppliers for an RFQ can see each candidate's tier and
 * narrow the list by it, with Tier 1 and Tier 2 already selected and Tier 3 one click away.
 *
 * The tier is never a gate. These tests hold that line: a hidden supplier is always counted on
 * screen and always one visible click from coming back, and suppliers nobody has classified stay
 * in the list throughout — they are every supplier the customer has today.
 */
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';

vi.mock('../../../api/services/supplierService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/supplierService')>();
  return {
    ...actual,
    default: { ...actual.default, searchSuppliers: vi.fn(), searchWebSuppliers: vi.fn() },
  };
});
vi.mock('../../../api/services/productService', () => ({
  default: { matchProduct: vi.fn() },
}));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 7 } }),
}));

const supplierService = (await import('../../../api/services/supplierService')).default as unknown as {
  searchSuppliers: ReturnType<typeof vi.fn>;
  searchWebSuppliers: ReturnType<typeof vi.fn>;
};
const productService = (await import('../../../api/services/productService')).default as unknown as {
  matchProduct: ReturnType<typeof vi.fn>;
};
const { ItemDetailsDialog } = await import('./ProcessRFQPage');

const item = {
  id: 1,
  productShortName: 'Pressure transmitter',
  productShortDescription: 'Pressure transmitter, 0-10 bar',
  manufacturerName: 'Rosemount',
  manufacturerPartNumber: '3051S',
  quantity: 4,
  selectionSource: 'product',
  productId: null,
  supplierQuotedItemId: null,
  matchStatus: 'pending',
} as any;

const candidates = [
  { id: 11, name: 'Gulf Instrument Partners', contactEmail: 'a@x.com', tier: 'TIER_1_PARTNER' },
  { id: 12, name: 'Delta Spot Trading', contactEmail: 'b@x.com', tier: 'TIER_3_OUT_OF_NETWORK' },
  { id: 13, name: 'Older Supplier Co', contactEmail: 'c@x.com' },
];

/** Opens the supplier picker the way a buyer does: no product match, then the search field. */
const openSupplierPicker = async () => {
  render(<ItemDetailsDialog item={item} open onClose={() => {}} rfqNo="RFQ-1001" />);
  const searchField = await screen.findByPlaceholderText('Search Internet');
  fireEvent.click(searchField);
  await screen.findByText('Gulf Instrument Partners');
};

beforeEach(() => {
  vi.clearAllMocks();
  productService.matchProduct.mockResolvedValue({ hasExactMatch: false });
  supplierService.searchSuppliers.mockResolvedValue(candidates);
  supplierService.searchWebSuppliers.mockResolvedValue([]);
});

describe('supplier RFQ dispatch — filtering candidates by tier', () => {
  it('shows every candidate’s tier, including the ones nobody has classified', async () => {
    await openSupplierPicker();
    const partnerRow = screen.getByText('Gulf Instrument Partners').closest('li');
    expect(partnerRow).toHaveTextContent('Tier 1 — Partner');
    // Absence of a tier is a real answer and says so on the row; it is not shown as Tier 3.
    const untieredRow = screen.getByText('Older Supplier Co').closest('li');
    expect(untieredRow).toHaveTextContent('Not classified');
  });

  it('starts on Tier 1, Tier 2 and untiered suppliers, holding Tier 3 back', async () => {
    await openSupplierPicker();
    expect(screen.getByText('Gulf Instrument Partners')).toBeInTheDocument();
    expect(screen.getByText('Older Supplier Co')).toBeInTheDocument();
    expect(screen.queryByText('Delta Spot Trading')).not.toBeInTheDocument();
  });

  it('says how many suppliers the filter is holding back rather than losing them silently', async () => {
    await openSupplierPicker();
    expect(
      screen.getByText('1 supplier matches this search but is in a tier you have turned off.'),
    ).toBeInTheDocument();
  });

  it('brings the Tier 3 spot suppliers in on one click — the trader’s obsolete-part call', async () => {
    await openSupplierPicker();
    fireEvent.click(screen.getByRole('button', { name: 'Tier 3' }));
    await waitFor(() =>
      expect(screen.getByText('Delta Spot Trading')).toBeInTheDocument(),
    );
    // And nothing else was dropped to make room for it.
    expect(screen.getByText('Gulf Instrument Partners')).toBeInTheDocument();
    expect(screen.getByText('Older Supplier Co')).toBeInTheDocument();
  });

  it('offers one control that puts every tier back', async () => {
    await openSupplierPicker();
    fireEvent.click(screen.getByRole('button', { name: 'Show every tier' }));
    await waitFor(() =>
      expect(screen.getByText('Delta Spot Trading')).toBeInTheDocument(),
    );
  });

  it('never asks the server to narrow while untiered suppliers are wanted', async () => {
    await openSupplierPicker();
    expect(supplierService.searchSuppliers).toHaveBeenCalledWith(
      'Rosemount', '', 7, undefined,
    );
  });

  it('asks the server to narrow only once untiered suppliers are excluded', async () => {
    await openSupplierPicker();
    fireEvent.click(screen.getByRole('button', { name: 'Not classified' }));
    await waitFor(() =>
      expect(supplierService.searchSuppliers).toHaveBeenLastCalledWith(
        'Rosemount', '', 7, ['TIER_1_PARTNER', 'TIER_2_EXTENDED'],
      ),
    );
  });

  it('shows every supplier again when the buyer turns all the tiers off', async () => {
    await openSupplierPicker();
    ['Tier 1', 'Tier 2', 'Not classified'].forEach((label) =>
      fireEvent.click(screen.getByRole('button', { name: label })),
    );
    await waitFor(() =>
      expect(screen.getByText('Delta Spot Trading')).toBeInTheDocument(),
    );
    expect(
      screen.getByText('Showing every supplier — no tier is being left out.'),
    ).toBeInTheDocument();
  });
});

describe('what the picker says it is showing', () => {
  it('names the tiers currently on screen rather than describing the defaults', async () => {
    await openSupplierPicker();
    expect(
      screen.getByText(/^Showing Tier 1, Tier 2, Not classified\./),
    ).toBeInTheDocument();
  });

  it('says plainly that a tier does not stop anyone being sent an RFQ', async () => {
    await openSupplierPicker();
    expect(
      screen.getByText(/never stops you sending them an RFQ/),
    ).toBeInTheDocument();
  });
});
