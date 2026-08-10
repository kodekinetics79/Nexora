import React from 'react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { GridColDef } from '@mui/x-data-grid';
import useColumnPreferences, { readCustomFieldValue } from './useColumnPreferences';
import ColumnPreferences from '../components/common/ColumnPreferences';
import type { ListViewColumnsResponse } from '../api/services/listViewService';

vi.mock('../api/services/listViewService', () => ({
  default: {
    getColumns: vi.fn(),
    saveColumns: vi.fn(),
    resetColumns: vi.fn(),
  },
}));

const service = (await import('../api/services/listViewService')).default as any;

const response = (over: Partial<ListViewColumnsResponse> = {}): ListViewColumnsResponse => ({
  viewKey: 'customers.list',
  isCustomised: false,
  supportsCustomFields: true,
  columns: [
    { key: 'docId', label: 'Customer code', visible: true, locked: false, source: 'catalog' },
    { key: 'name', label: 'Name', visible: true, locked: false, source: 'catalog' },
    { key: 'createdOn', label: 'Created', visible: false, locked: false, source: 'catalog' },
    { key: 'actions', label: 'Actions', visible: true, locked: true, source: 'catalog' },
  ],
  ...over,
});

const wrapper = ({ children }: { children: React.ReactNode }) => (
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    {children}
  </QueryClientProvider>
);

/** Renders the hook through the real picker so behaviour is asserted end-to-end, not in isolation. */
const Harness: React.FC<{ defs: GridColDef[]; onColumns?: (c: GridColDef[]) => void }> = ({ defs, onColumns }) => {
  const preferences = useColumnPreferences('customers.list');
  const ordered = preferences.arrangeColumns(defs);
  onColumns?.(ordered);
  return (
    <>
      <ColumnPreferences preferences={preferences} />
      <div data-testid="order">{ordered.map((c) => c.field).join(',')}</div>
      <div data-testid="state">
        {preferences.isLoading ? 'loading' : preferences.isError ? 'error' : 'ready'}
      </div>
    </>
  );
};

const defs: GridColDef[] = [
  { field: 'docId', headerName: 'Customer code' },
  { field: 'name', headerName: 'Name' },
  { field: 'createdOn', headerName: 'Created' },
  { field: 'actions', headerName: 'Actions' },
];

describe('readCustomFieldValue', () => {
  it('reads a value out of the jsonb bag', () => {
    expect(readCustomFieldValue('{"vendor_code":"TC-9910"}', 'vendor_code')).toBe('TC-9910');
  });

  it('renders each JSON shape as something a person can read', () => {
    expect(readCustomFieldValue('{"is_strategic":true}', 'is_strategic')).toBe('Yes');
    expect(readCustomFieldValue('{"is_strategic":false}', 'is_strategic')).toBe('No');
    expect(readCustomFieldValue('{"credit_days":30}', 'credit_days')).toBe('30');
    expect(readCustomFieldValue('{"regions":["EP","WP"]}', 'regions')).toBe('EP, WP');
  });

  it('never throws on a malformed, absent or wrongly-shaped bag', () => {
    for (const bag of [undefined, null, '', '   ', 'not json', '[1,2,3]', '"a string"', '{"unclosed":', 42]) {
      expect(readCustomFieldValue(bag, 'vendor_code')).toBe('');
    }
    expect(readCustomFieldValue('{"other_key":"x"}', 'vendor_code')).toBe('');
    expect(readCustomFieldValue('{"vendor_code":null}', 'vendor_code')).toBe('');
  });
});

describe('useColumnPreferences', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('orders the page columns to the layout the server returned', async () => {
    service.getColumns.mockResolvedValue(response({
      isCustomised: true,
      columns: [
        { key: 'name', label: 'Name', visible: true, locked: false, source: 'catalog' },
        { key: 'docId', label: 'Customer code', visible: false, locked: false, source: 'catalog' },
        { key: 'createdOn', label: 'Created', visible: true, locked: false, source: 'catalog' },
        { key: 'actions', label: 'Actions', visible: true, locked: true, source: 'catalog' },
      ],
    }));

    render(<Harness defs={defs} />, { wrapper });

    await waitFor(() =>
      expect(screen.getByTestId('order').textContent).toBe('name,docId,createdOn,actions'));
  });

  it('materialises a tenant-defined custom field as a real, selectable column', async () => {
    service.getColumns.mockResolvedValue(response({
      columns: [
        ...response().columns,
        {
          key: 'cf:vendor_code', label: 'Our vendor code', visible: true, locked: false,
          source: 'customField', dataType: 'Text', stableKey: 'vendor_code',
        },
      ],
    }));

    let captured: GridColDef[] = [];
    render(<Harness defs={defs} onColumns={(c) => { captured = c; }} />, { wrapper });

    await waitFor(() => expect(screen.getByTestId('order').textContent).toContain('cf:vendor_code'));

    const custom = captured.find((c) => c.field === 'cf:vendor_code');
    expect(custom?.headerName).toBe('Our vendor code');
    // And it reads its value out of the row's jsonb bag.
    const value = custom?.valueGetter?.(
      undefined as never,
      { id: 1, customFields: '{"vendor_code":"TC-9910"}' } as never,
      custom as never,
      undefined as never,
    );
    expect(value).toBe('TC-9910');

    // It is offered in the picker, badged as tenant-defined.
    fireEvent.click(screen.getByRole('button', { name: 'Choose columns' }));
    expect(screen.getByLabelText('Show Our vendor code')).toBeInTheDocument();
    expect(screen.getByText('Custom')).toBeInTheDocument();
  });

  it('ignores a server column this page no longer renders instead of emptying the grid', async () => {
    service.getColumns.mockResolvedValue(response({
      columns: [
        { key: 'a_column_that_no_longer_exists', label: 'Ghost', visible: true, locked: false, source: 'catalog' },
        ...response().columns,
      ],
    }));

    render(<Harness defs={defs} />, { wrapper });

    await waitFor(() =>
      expect(screen.getByTestId('order').textContent).toBe('docId,name,createdOn,actions'));
  });

  it('keeps a page column the server catalog does not list rather than dropping it', async () => {
    service.getColumns.mockResolvedValue(response());

    render(
      <Harness defs={[...defs, { field: 'brandNewColumn', headerName: 'Brand new' }]} />,
      { wrapper },
    );

    await waitFor(() =>
      expect(screen.getByTestId('order').textContent).toBe('docId,name,createdOn,actions,brandNewColumn'));
  });

  it('falls back to the page defaults when preferences cannot be loaded', async () => {
    service.getColumns.mockRejectedValue(new Error('offline'));

    render(<Harness defs={defs} />, { wrapper });

    await waitFor(() => expect(screen.getByTestId('state').textContent).toBe('error'));
    expect(screen.getByTestId('order').textContent).toBe('docId,name,createdOn,actions');

    fireEvent.click(screen.getByRole('button', { name: 'Choose columns' }));
    expect(screen.getByText(/standard\s+columns/)).toBeInTheDocument();
  });

  it('saves the whole ordered layout when a column is ticked', async () => {
    service.getColumns.mockResolvedValue(response());
    service.saveColumns.mockResolvedValue(response({ isCustomised: true }));

    render(<Harness defs={defs} />, { wrapper });
    await waitFor(() => expect(screen.getByTestId('state').textContent).toBe('ready'));

    fireEvent.click(screen.getByRole('button', { name: 'Choose columns' }));
    fireEvent.click(screen.getByLabelText('Show Created'));

    await waitFor(() => expect(service.saveColumns).toHaveBeenCalledWith('customers.list', [
      { key: 'docId', visible: true },
      { key: 'name', visible: true },
      { key: 'createdOn', visible: true },
      { key: 'actions', visible: true },
    ]));
  });

  it('shuffles a column and persists the new order', async () => {
    service.getColumns.mockResolvedValue(response());
    // The server is authoritative on the response: echo the saved order back, as it does.
    service.saveColumns.mockImplementation((_viewKey: string, saved: { key: string; visible: boolean }[]) =>
      Promise.resolve(response({
        isCustomised: true,
        columns: saved.map((c) => ({
          ...response().columns.find((x) => x.key === c.key)!,
          visible: c.visible,
        })),
      })));

    render(<Harness defs={defs} />, { wrapper });
    await waitFor(() => expect(screen.getByTestId('state').textContent).toBe('ready'));

    fireEvent.click(screen.getByRole('button', { name: 'Choose columns' }));
    fireEvent.click(screen.getByRole('button', { name: 'Move Name up' }));

    await waitFor(() => expect(service.saveColumns).toHaveBeenCalledWith('customers.list', [
      { key: 'name', visible: true },
      { key: 'docId', visible: true },
      { key: 'createdOn', visible: false },
      { key: 'actions', visible: true },
    ]));
    // Optimistic: the grid reflects the new order before the server answers.
    await waitFor(() => expect(screen.getByTestId('order').textContent).toBe('name,docId,createdOn,actions'));
  });

  it('never lets a locked column be unticked', async () => {
    service.getColumns.mockResolvedValue(response());

    render(<Harness defs={defs} />, { wrapper });
    await waitFor(() => expect(screen.getByTestId('state').textContent).toBe('ready'));

    fireEvent.click(screen.getByRole('button', { name: 'Choose columns' }));
    expect(screen.getByLabelText('Show Actions')).toBeDisabled();
    expect(screen.getByLabelText('Show Actions')).toBeChecked();
  });

  it('offers Reset only once the user has a saved layout, and reset restores the default', async () => {
    service.getColumns.mockResolvedValue(response({ isCustomised: false }));
    service.saveColumns.mockResolvedValue(response({ isCustomised: true }));
    service.resetColumns.mockResolvedValue(response({ isCustomised: false }));

    render(<Harness defs={defs} />, { wrapper });
    await waitFor(() => expect(screen.getByTestId('state').textContent).toBe('ready'));

    fireEvent.click(screen.getByRole('button', { name: 'Choose columns' }));
    expect(screen.getByRole('button', { name: /Reset to default/ })).toBeDisabled();

    // Customise, and Reset becomes available.
    fireEvent.click(screen.getByRole('button', { name: 'Move Name up' }));
    await waitFor(() =>
      expect(screen.getByRole('button', { name: /Reset to default/ })).toBeEnabled());

    fireEvent.click(screen.getByRole('button', { name: /Reset to default/ }));
    await waitFor(() => expect(service.resetColumns).toHaveBeenCalledWith('customers.list'));
    await waitFor(() =>
      expect(screen.getByTestId('order').textContent).toBe('docId,name,createdOn,actions'));
  });
});
