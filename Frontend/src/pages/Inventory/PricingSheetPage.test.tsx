import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * The Pricing sheet (owner ruling 2026-09-27): one landed cost and one sale price per part, in a
 * currency the keeper chooses; margin is on cost. Only someone who can edit products changes it.
 */

const mocks = vi.hoisted(() => ({ get: vi.fn(), save: vi.fn(), canEdit: true }));

vi.mock('../../api/services/pricingSheetService', () => ({ default: { get: mocks.get, save: mocks.save } }));
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: (module: string, action?: string) => module !== 'Products' || action !== 'edit' || mocks.canEdit }),
}));

import PricingSheetPage from './PricingSheetPage';

const sheet = (rows: object[]) => ({
  rows, total: rows.length, page: 1, pageSize: 50, missingCount: 1,
  currencies: [{ id: 1, code: 'SAR', isBase: true }, { id: 2, code: 'USD', isBase: false }],
});
const unpriced = { productId: 3, partNo: 'GOLD-QUOTE-0004', name: 'Test catalogue item', unit: 'EA', currencyId: null, currencyCode: null, landedCost: null, salePrice: null, lastPurchasePrice: 95, onHand: 40, changedOn: null, changedBy: null };

function renderSheet() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <SnackbarProvider>
        <MemoryRouter>
          <PricingSheetPage />
        </MemoryRouter>
      </SnackbarProvider>
    </QueryClientProvider>,
  );
}

describe('Pricing sheet', () => {
  beforeEach(() => {
    mocks.get.mockReset();
    mocks.save.mockReset();
    mocks.canEdit = true;
  });

  it('opens on the parts missing a price and says how many there are', async () => {
    mocks.get.mockResolvedValue(sheet([unpriced]));
    renderSheet();
    expect(await screen.findByText('GOLD-QUOTE-0004')).toBeInTheDocument();
    expect(screen.getByText('1 part needs a price')).toBeInTheDocument();
    expect(mocks.get).toHaveBeenCalledWith(expect.objectContaining({ missingOnly: true }));
    expect(screen.getByLabelText('Only parts missing a price')).toBeChecked();
  });

  it('a margin on cost fills the sale price, and one save sends the part in the chosen currency', async () => {
    mocks.get.mockResolvedValue(sheet([unpriced]));
    mocks.save.mockResolvedValue({ saved: 1 });
    renderSheet();
    fireEvent.change(await screen.findByLabelText('Landed cost for GOLD-QUOTE-0004'), { target: { value: '100' } });
    fireEvent.change(screen.getByLabelText('Margin on cost for GOLD-QUOTE-0004'), { target: { value: '20' } });
    fireEvent.mouseDown(screen.getByLabelText('Currency for GOLD-QUOTE-0004'));
    fireEvent.click(await screen.findByRole('option', { name: 'SAR' }));
    expect(screen.getByLabelText('Sale price for GOLD-QUOTE-0004')).toHaveValue(120);
    fireEvent.click(screen.getByRole('button', { name: 'Save 1 change' }));
    await waitFor(() => expect(mocks.save).toHaveBeenCalledWith([{ productId: 3, landedCost: 100, salePrice: 120, currencyId: 1 }]));
  });

  it('does not pretend a missing currency is the tenant base currency', async () => {
    mocks.get.mockResolvedValue(sheet([{ ...unpriced, landedCost: 100, salePrice: 120 }]));
    mocks.save.mockResolvedValue({ saved: 1 });
    renderSheet();

    await screen.findByText('GOLD-QUOTE-0004');
    expect(screen.getByLabelText('Currency for GOLD-QUOTE-0004')).toHaveTextContent('Not set');
    expect(screen.queryByText('SAR')).not.toBeInTheDocument();

    fireEvent.mouseDown(screen.getByLabelText('Currency for GOLD-QUOTE-0004'));
    fireEvent.click(await screen.findByRole('option', { name: 'SAR' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save 1 change' }));

    await waitFor(() => expect(mocks.save).toHaveBeenCalledWith([
      { productId: 3, landedCost: 100, salePrice: 120, currencyId: 1 },
    ]));
  });

  it('does not relabel the purchase currency when catalogue currency changes', async () => {
    mocks.get.mockResolvedValue(sheet([{ ...unpriced, currencyId: 1, currencyCode: 'SAR', landedCost: 100, salePrice: 120, lastPurchasePrice: 25.5, lastPurchaseCurrencyCode: 'EUR' }]));
    renderSheet();
    await screen.findByText('GOLD-QUOTE-0004');
    expect(screen.getByText('25.50')).toBeInTheDocument();
    expect(screen.getByText('EUR')).toBeInTheDocument();
    fireEvent.mouseDown(screen.getByLabelText('Currency for GOLD-QUOTE-0004'));
    fireEvent.click(await screen.findByRole('option', { name: 'USD' }));
    expect(screen.getByText('EUR')).toBeInTheDocument();
    expect(screen.getByText('25.50')).toBeInTheDocument();
  });

  it('typing a sale price works the margin out, and a price under cost is called out', async () => {
    mocks.get.mockResolvedValue(sheet([{ ...unpriced, currencyId: 1, currencyCode: 'SAR', landedCost: 100 }]));
    renderSheet();
    fireEvent.change(await screen.findByLabelText('Sale price for GOLD-QUOTE-0004'), { target: { value: '125' } });
    expect(screen.getByLabelText('Margin on cost for GOLD-QUOTE-0004')).toHaveValue(25);
    fireEvent.change(screen.getByLabelText('Sale price for GOLD-QUOTE-0004'), { target: { value: '90' } });
    expect(screen.getByText('Below cost')).toBeInTheDocument();
  });

  it('someone who cannot edit products reads the sheet and cannot change it', async () => {
    mocks.canEdit = false;
    mocks.get.mockResolvedValue(sheet([{ ...unpriced, currencyId: 1, currencyCode: 'SAR', landedCost: 100, salePrice: 120 }]));
    renderSheet();
    expect(await screen.findByText(/Prices are changed by a manager/)).toBeInTheDocument();
    expect(screen.queryByLabelText('Landed cost for GOLD-QUOTE-0004')).not.toBeInTheDocument();
    await screen.findByText('GOLD-QUOTE-0004');
    expect(screen.getByText('120.00')).toBeInTheDocument();
    expect(screen.getByText('SAR')).toBeInTheDocument();
  });
});
