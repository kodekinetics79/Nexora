import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import CustomisableGrid from './CustomisableGrid';

const mocks = vi.hoisted(() => ({ getColumns: vi.fn(), saveColumns: vi.fn() }));
vi.mock('../../../api/services/listViewService', () => ({
  default: { getColumns: mocks.getColumns, saveColumns: mocks.saveColumns },
}));

const bands = {
  verdict: <section>Verdict body</section>,
  outstanding: <section>Outstanding body</section>,
  losses: <section>Losses body</section>,
  closing: <section>Closing body</section>,
  today: <section>Today body</section>,
  sixmonths: <section>Six months body</section>,
  brands: <section>Brands body</section>,
  customers: <section>Customers body</section>,
};

const empty = (viewKey: string) => ({ viewKey, columns: [], isCustomised: false, supportsCustomFields: false });

const renderGrid = (editing = true) => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <CustomisableGrid bands={bands} editing={editing} />
  </QueryClientProvider>,
);

const bandOrder = () => [...document.querySelectorAll('[data-band]')].map(el => el.getAttribute('data-band'));

describe('CustomisableGrid', () => {
  beforeEach(() => {
    mocks.getColumns.mockReset().mockImplementation(async (viewKey: string) => empty(viewKey));
    mocks.saveColumns.mockReset().mockImplementation(async (viewKey: string, columns: { key: string; visible: boolean }[]) => ({
      ...empty(viewKey), isCustomised: true,
      columns: columns.map(c => ({ ...c, label: c.key, locked: false, source: 'catalog' })),
    }));
  });

  it('shows every band in the default order with no arranging bar outside Edit layout', () => {
    renderGrid(false);
    expect(bandOrder()).toEqual(['verdict', 'outstanding', 'losses', 'closing', 'today', 'sixmonths', 'brands', 'customers']);
    expect(screen.queryByRole('toolbar')).not.toBeInTheDocument();
  });

  it('opens the reader saved order and hidden bands', async () => {
    mocks.getColumns.mockImplementation(async (viewKey: string) => viewKey !== 'dashboard.layout' ? empty(viewKey) : {
      ...empty(viewKey), isCustomised: true,
      columns: ['sixmonths', 'verdict', 'outstanding', 'losses', 'closing', 'today']
        .map(key => ({ key, label: key, visible: key !== 'losses', locked: false, source: 'catalog' })),
    });
    renderGrid(false);
    await waitFor(() => expect(bandOrder()[0]).toBe('sixmonths'));
    expect(screen.queryByText('Losses body')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Show Why we lost' })).toBeInTheDocument();
  });

  it('moves a band later and saves the new order', async () => {
    renderGrid();
    fireEvent.click(screen.getByRole('button', { name: 'Move Did we win what we decided? later' }));
    await waitFor(() => expect(bandOrder().slice(0, 2)).toEqual(['outstanding', 'verdict']));
    await waitFor(() => expect(mocks.saveColumns).toHaveBeenCalledWith('dashboard.layout', expect.any(Array)));
  });

  it('hides a band and brings it back from the hidden row', async () => {
    renderGrid();
    fireEvent.click(screen.getByRole('button', { name: 'Hide Why we lost' }));
    await waitFor(() => expect(screen.queryByText('Losses body')).not.toBeInTheDocument());
    fireEvent.click(await screen.findByRole('button', { name: 'Show Why we lost' }));
    expect(await screen.findByText('Losses body')).toBeInTheDocument();
  });

  it('drops a dragged band before the band it lands on', async () => {
    renderGrid();
    const from = document.querySelector('[data-band="today"]')!;
    const onto = document.querySelector('[data-band="verdict"]')!;
    const dataTransfer = { setData: vi.fn(), effectAllowed: '' };
    fireEvent.dragStart(from, { dataTransfer });
    fireEvent.dragOver(onto, { dataTransfer });
    fireEvent.drop(onto, { dataTransfer });
    await waitFor(() => expect(bandOrder()[0]).toBe('today'));
  });

  it('makes a band wide and saves the size', async () => {
    renderGrid();
    fireEvent.click(screen.getByRole('button', { name: 'Make What needs you today wide' }));
    expect(await screen.findByRole('button', { name: 'Make What needs you today normal width' })).toBeInTheDocument();
    await waitFor(() => expect(mocks.saveColumns).toHaveBeenCalledWith('dashboard.charts', expect.arrayContaining([{ key: 'size.today.wide', visible: true }])));
  });
});
