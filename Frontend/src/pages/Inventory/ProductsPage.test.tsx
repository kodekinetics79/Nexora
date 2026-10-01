import { fireEvent, render, screen, within, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({ getAll: vi.fn(), getCurrencies: vi.fn(), getWarehouses: vi.fn(), savePrices: vi.fn(), canEdit: true }));
vi.mock('../../api/services/productService', () => ({ default: { getAll: mocks.getAll, getCurrencies: mocks.getCurrencies, getWarehouses: mocks.getWarehouses } }));
vi.mock('../../api/services/pricingSheetService', () => ({ default: { save: mocks.savePrices } }));
vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: vi.fn() }) }));
vi.mock('../../context/AuthContext', () => ({ useAuth: () => ({ hasPermission: (_module: string, action: string) => action === 'edit' && mocks.canEdit }) }));
vi.mock('../../components/common/UploadExportToolbar', () => ({ default: () => null }));
vi.mock('./Commercial/OpeningStockDialog', () => ({ default: () => null }));
vi.mock('./ProductStockDialog', () => ({ default: ({ open, productId }: { open: boolean; productId?: number }) => open ? <div role="dialog">Stock details {productId}</div> : null }));
vi.mock('./ProductFormDialog', () => ({ default: ({ open, productId }: { open: boolean; productId?: number }) => open ? <div role="dialog">Edit product {productId}</div> : null }));
import ProductsPage from './ProductsPage';

const product = {
  id: 7, partNo: 'PART-007', productName: 'Valve actuator', qtyOnHand: 40,
  reservedQuantity: 4, availableQuantity: 33, incomingQuantity: 22,
  nextIncomingOn: '2026-10-07', incomingCommitmentCount: 1, reorderPoint: 8,
  unitCost: 100, sellingPrice: 135, priceCurrencyCode: null, isActive: true,
  preferredSupplierName: 'Precision Controls', preferredSupplierTier: 'TIER_1_PARTNER',
  stockLocationSummary: 'Primary warehouse', materialLotCount: 0,
};
function LocationProbe() {
  const location = useLocation();
  return <><output aria-label="Current query">{location.search}</output><output aria-label="Current path">{location.pathname}</output></>;
}
function renderProducts(path = '/inventory/products') {
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><MemoryRouter initialEntries={[path]}><ProductsPage /><LocationProbe /></MemoryRouter></QueryClientProvider>);
}

beforeEach(() => {
  sessionStorage.clear();
  mocks.canEdit = true;
  mocks.getAll.mockReset().mockResolvedValue({ items: [product], totalItems: 1 });
  mocks.getCurrencies.mockReset().mockResolvedValue([{ id: 1, code: 'SAR' }]);
  mocks.getWarehouses.mockReset().mockResolvedValue([{ id: 2, warehouseName: 'Primary' }]);
  mocks.savePrices.mockReset().mockResolvedValue({ saved: 1 });
});

afterEach(() => vi.restoreAllMocks());

describe('Products list', () => {
  it('keeps recorded amounts visible without inventing a currency', async () => {
    renderProducts();
    expect(await screen.findByText('135.00')).toBeInTheDocument();
    expect(screen.getByText('100.00')).toBeInTheDocument();
    expect(screen.getByText('Not set')).toBeInTheDocument();
    expect(screen.queryByText('Needs price')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Edit PART-007' }));
    expect(screen.getByRole('dialog')).toHaveTextContent('Edit product 7');
  });

  it('uses separate commercial columns and moves supply details off the master sheet', async () => {
    renderProducts();
    const link = await screen.findByRole('link', { name: 'Valve actuator' });
    expect(link).toHaveAttribute('href', '/inventory/products/7');
    for (const name of ['Part number', 'Unit', 'Currency', 'Purchase cost', 'Landed cost', 'Selling price', 'In stock']) {
      expect(screen.getByRole('columnheader', { name })).toBeInTheDocument();
    }
    const row = link.closest('[role="row"]') as HTMLElement;
    expect(within(row).getByText('40')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Columns' })).not.toBeInTheDocument();
    for (const name of ['Reserved', 'On hand', 'Incoming', 'Supplier & location', 'Traceability']) {
      expect(screen.queryByRole('columnheader', { name })).not.toBeInTheDocument();
    }
  });

  it('shows zero prices as valid and does not expose editing to read-only users', async () => {
    mocks.canEdit = false;
    mocks.getAll.mockResolvedValue({ items: [{ ...product, unitCost: 0, sellingPrice: 0, priceCurrencyCode: 'SAR' }], totalItems: 1 });
    renderProducts();
    await screen.findByRole('link', { name: 'Valve actuator' });
    expect(screen.queryByText('Currency not set')).not.toBeInTheDocument();
    expect(screen.getAllByText('0.00')).toHaveLength(2);
    expect(screen.queryByRole('button', { name: /Edit PART/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Edit prices' })).not.toBeInTheDocument();
  });

  it('keeps purchase currency distinct and never substitutes landed cost for missing purchase evidence', async () => {
    mocks.getAll.mockResolvedValue({ items: [{ ...product, priceCurrencyCode: 'SAR', lastPurchaseCost: 26.5, lastPurchaseCurrencyCode: 'USD' }], totalItems: 1 });
    renderProducts();
    expect(await screen.findByText('26.50 USD')).toBeInTheDocument();
    expect(screen.getByText('SAR')).toBeInTheDocument();
    expect(screen.getByText('100.00')).toBeInTheDocument();
  });

  it('keeps a retry path when the list cannot load', async () => {
    mocks.getAll.mockRejectedValue(new Error('Unavailable'));
    renderProducts();
    expect(await screen.findByRole('alert')).toHaveTextContent('Inventory could not be loaded.');
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
  });

  it('shows availability without replacing physical stock and opens warehouse detail in place', async () => {
    renderProducts();
    expect(await screen.findByRole('columnheader', { name: 'Available' })).toBeInTheDocument();
    expect(await screen.findByText('33')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Stock details for PART-007' }));
    expect(screen.getByRole('dialog')).toHaveTextContent('Stock details 7');
    expect(screen.queryByRole('tab', { name: 'Pricing sheet' })).not.toBeInTheDocument();
  });

  it('edits and saves pricing on the same product row without altering stock or purchase cost', async () => {
    mocks.getAll.mockResolvedValue({ items: [{ ...product, priceCurrencyId: 1, priceCurrencyCode: 'SAR', lastPurchaseCost: 25, lastPurchaseCurrencyCode: 'USD' }], totalItems: 1 });
    renderProducts();
    await screen.findByText('25.00 USD');
    fireEvent.click(screen.getByRole('button', { name: 'Edit prices' }));
    fireEvent.change(await screen.findByLabelText('Selling price for PART-007'), { target: { value: '150' } });
    expect(screen.getByText('25.00 USD')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Edit PART-007' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Save prices' }));
    await waitFor(() => expect(mocks.savePrices).toHaveBeenCalledWith([{ productId: 7, landedCost: 100, salePrice: 150, currencyId: 1 }]));
    expect(await screen.findByRole('button', { name: 'Edit prices' })).toBeInTheDocument();
  });

  it('retains an immediately changed price when product-name navigation is refused', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    mocks.getAll.mockResolvedValue({ items: [{ ...product, priceCurrencyId: 1, priceCurrencyCode: 'SAR' }], totalItems: 1 });
    renderProducts();
    const productLink = await screen.findByRole('link', { name: 'Valve actuator' });
    fireEvent.click(screen.getByRole('button', { name: 'Edit prices' }));
    const salePrice = await screen.findByLabelText('Selling price for PART-007');

    // No debounce wait: the in-memory guard must protect work before storage catches up.
    fireEvent.change(salePrice, { target: { value: '175' } });
    fireEvent.click(productLink);

    expect(confirm).toHaveBeenCalledWith('Leave Products without saving your price changes?');
    expect(screen.getByLabelText('Current path')).toHaveTextContent(/^\/inventory\/products$/);
    expect(screen.getByLabelText('Selling price for PART-007')).toHaveValue(175);
    expect(screen.getByRole('button', { name: 'Save prices' })).toBeEnabled();
    expect(mocks.savePrices).not.toHaveBeenCalled();
    expect(sessionStorage.getItem('nexora.products.pricing-sheet')).toBeNull();
  });

  it('restores URL filters, pagination and sort into the server query', async () => {
    mocks.getAll.mockResolvedValue({ items: [product], totalItems: 250 });
    renderProducts('/inventory/products?search=valve&active=false&stock=low-stock&warehouse=2&page=2&size=10&sort=sellingPrice&direction=desc');

    await screen.findByRole('link', { name: 'Valve actuator' });
    expect(mocks.getAll).toHaveBeenLastCalledWith({
      pageNumber: 3, pageSize: 10, search: 'valve', isActive: false,
      stock: 'low-stock', warehouseId: 2, sortBy: 'salePrice', sortDirection: 'desc',
    });
    expect(screen.getByRole('textbox', { name: 'Search product or part number…' })).toHaveValue('valve');
    expect(screen.getByRole('combobox', { name: 'Stock filter' })).toHaveTextContent('Stock: Low stock');
    expect(screen.getByRole('combobox', { name: 'Warehouse filter' })).toHaveTextContent('Warehouse: Primary');
    expect(screen.getByRole('columnheader', { name: /^Selling price(?: Sort)?$/ })).toHaveAttribute('aria-sort', 'descending');
  });

  it('sends Stock and Warehouse changes to the server instead of filtering only visible rows', async () => {
    renderProducts();
    await screen.findByRole('link', { name: 'Valve actuator' });

    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Stock filter' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Out of stock' }));
    await waitFor(() => expect(mocks.getAll).toHaveBeenLastCalledWith(expect.objectContaining({ stock: 'out-of-stock', warehouseId: undefined, pageNumber: 1 })));

    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Warehouse filter' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Primary' }));
    await waitFor(() => expect(mocks.getAll).toHaveBeenLastCalledWith(expect.objectContaining({ stock: 'out-of-stock', warehouseId: 2, pageNumber: 1 })));
    expect(screen.getByLabelText('Current query')).toHaveTextContent('stock=out-of-stock&warehouse=2');
  });

  it('sorts through the server and resets pagination when the sort changes', async () => {
    mocks.getAll.mockResolvedValue({ items: [product], totalItems: 250 });
    renderProducts('/inventory/products?page=2');
    await screen.findByRole('link', { name: 'Valve actuator' });

    fireEvent.click(screen.getByRole('columnheader', { name: /^Selling price(?: Sort)?$/ }));
    await waitFor(() => expect(mocks.getAll).toHaveBeenLastCalledWith(expect.objectContaining({ sortBy: 'salePrice', sortDirection: 'asc', pageNumber: 1 })));
    fireEvent.click(screen.getByRole('columnheader', { name: /^Selling price(?: Sort)?$/ }));
    await waitFor(() => expect(mocks.getAll).toHaveBeenLastCalledWith(expect.objectContaining({ sortBy: 'salePrice', sortDirection: 'desc', pageNumber: 1 })));
    expect(screen.getByLabelText('Current query')).not.toHaveTextContent('page=');
  });

  it('limits price editing and the saved payload to a selected row among two products', async () => {
    mocks.getAll.mockResolvedValue({ items: [
      { ...product, priceCurrencyId: 1, priceCurrencyCode: 'SAR' },
      { ...product, id: 8, partNo: 'PART-008', productName: 'Pressure regulator', priceCurrencyId: 1, priceCurrencyCode: 'SAR' },
    ], totalItems: 2 });
    renderProducts();
    const selectedRow = (await screen.findByRole('link', { name: 'Valve actuator' })).closest('[role="row"]') as HTMLElement;
    fireEvent.click(within(selectedRow).getByRole('checkbox'));
    fireEvent.click(screen.getByRole('button', { name: 'Edit prices (1)' }));

    expect(await screen.findByLabelText('Selling price for PART-007')).toBeInTheDocument();
    expect(screen.queryByLabelText('Selling price for PART-008')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Landed cost for PART-008')).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Selling price for PART-007'), { target: { value: '155' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save prices' }));

    await waitFor(() => expect(mocks.savePrices).toHaveBeenCalledWith([
      { productId: 7, landedCost: 100, salePrice: 155, currencyId: 1 },
    ]));
  });

  it('clears all filters and resets to the first page while preserving sort and page size', async () => {
    mocks.getAll.mockResolvedValue({ items: [product], totalItems: 250 });
    renderProducts('/inventory/products?search=valve&active=false&stock=low-stock&warehouse=2&page=2&size=10&sort=sellingPrice&direction=desc');
    await screen.findByRole('link', { name: 'Valve actuator' });
    fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }));

    await waitFor(() => expect(mocks.getAll).toHaveBeenLastCalledWith({
      pageNumber: 1, pageSize: 10, search: undefined, isActive: undefined,
      stock: 'all', warehouseId: undefined, sortBy: 'salePrice', sortDirection: 'desc',
    }));
    expect(screen.getByLabelText('Current query')).toHaveTextContent('?size=10&sort=sellingPrice&direction=desc');
    expect(screen.getByRole('textbox', { name: 'Search product or part number…' })).toHaveValue('');
  });

  it('searches on the server and resets the page without dropping the stock filter', async () => {
    mocks.getAll.mockResolvedValue({ items: [product], totalItems: 250 });
    renderProducts('/inventory/products?stock=in-stock&page=2');
    await screen.findByRole('link', { name: 'Valve actuator' });
    fireEvent.change(screen.getByRole('textbox', { name: 'Search product or part number…' }), { target: { value: 'PART-007' } });

    await waitFor(() => expect(mocks.getAll).toHaveBeenLastCalledWith(expect.objectContaining({ pageNumber: 1, search: 'PART-007', stock: 'in-stock' })));
    expect(screen.getByLabelText('Current query')).not.toHaveTextContent('page=');
  });

  it('keeps products usable when warehouse filters fail and retries the warehouse lookup', async () => {
    mocks.getWarehouses.mockRejectedValueOnce(new Error('Warehouse lookup unavailable'));
    renderProducts();

    expect(await screen.findByRole('alert')).toHaveTextContent('Warehouse filters could not be loaded. You can still use the product list.');
    expect(await screen.findByRole('link', { name: 'Valve actuator' })).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'Warehouse filter' })).toHaveAttribute('aria-disabled', 'true');
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));

    await waitFor(() => expect(mocks.getWarehouses).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
    expect(screen.getByRole('combobox', { name: 'Warehouse filter' })).not.toHaveAttribute('aria-disabled', 'true');
  });
});
