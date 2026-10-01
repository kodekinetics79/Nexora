import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AvailabilityDTO } from '../../api/services/commercialIntelligenceService';

const mocks = vi.hoisted(() => ({ getAvailability: vi.fn(), canEdit: true }));
vi.mock('../../api/services/commercialIntelligenceService', () => ({ default: { getAvailability: mocks.getAvailability } }));
vi.mock('../../context/AuthContext', () => ({ useAuth: () => ({ hasPermission: () => mocks.canEdit }) }));
vi.mock('./Commercial/StockActionsDialog', () => ({ default: ({ row, onClose }: { row: AvailabilityDTO; onClose: () => void }) =>
  <div role="dialog" aria-label="Stock editor">Editing {row.warehouseName}<button onClick={onClose}>Close stock editor</button></div> }));
import ProductStockDialog from './ProductStockDialog';

const stock: AvailabilityDTO = {
  inventoryId: 20, productId: 7, productName: 'Actuator', partNumber: 'PART-7',
  warehouseId: 3, warehouseName: 'Main warehouse', onHand: 40, available: 33, incoming: 4,
  reserved: 7, minimumLevel: null, maximumLevel: 0, safetyStock: 2,
};
function renderDialog(props: Partial<Parameters<typeof ProductStockDialog>[0]> = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}><ProductStockDialog productId={7} productName="Actuator" open onClose={vi.fn()} {...props} /></QueryClientProvider>);
}

beforeEach(() => {
  mocks.canEdit = true;
  mocks.getAvailability.mockReset().mockResolvedValue([stock]);
});

describe('Product stock details', () => {
  it('fetches the exact product and preserves the server availability without reservations', async () => {
    renderDialog();
    expect(await screen.findByText('Main warehouse')).toBeInTheDocument();
    expect(mocks.getAvailability).toHaveBeenCalledWith({ productId: 7 });
    expect(screen.getByText('40')).toBeInTheDocument();
    expect(screen.getByText('33')).toBeInTheDocument();
    expect(screen.getByText('Min Not set · Max 0 · Safety 2')).toBeInTheDocument();
    expect(screen.queryByText(/reserved/i)).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Stock actions for Main warehouse' }));
    expect(screen.getByText('Editing Main warehouse')).toBeInTheDocument();
    fireEvent.click(screen.getByText('Close stock editor'));
    expect(screen.queryByText('Editing Main warehouse')).not.toBeInTheDocument();
  });

  it('shows stock but no mutation action for read-only users', async () => {
    mocks.canEdit = false;
    renderDialog();
    await screen.findByText('Main warehouse');
    expect(screen.queryByRole('button', { name: /Stock actions/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('columnheader', { name: 'Action' })).not.toBeInTheDocument();
  });

  it('does not read the whole tenant when closed or no product is selected', () => {
    const rendered = renderDialog({ open: false });
    expect(mocks.getAvailability).not.toHaveBeenCalled();
    rendered.unmount();
    renderDialog({ productId: undefined });
    expect(mocks.getAvailability).not.toHaveBeenCalled();
  });

  it('distinguishes empty stock from a failed read and retries', async () => {
    mocks.getAvailability.mockRejectedValueOnce(new Error('Unavailable')).mockResolvedValueOnce([]);
    renderDialog();
    expect(await screen.findByRole('alert')).toHaveTextContent('could not be loaded');
    expect(screen.queryByText(/No stock recorded/)).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText(/No stock recorded for this product/)).toBeInTheDocument();
    await waitFor(() => expect(mocks.getAvailability).toHaveBeenCalledTimes(2));
  });
});
