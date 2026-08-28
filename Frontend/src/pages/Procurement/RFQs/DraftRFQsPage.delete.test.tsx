import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const testState = vi.hoisted(() => ({
  grants: new Set<string>(),
  deleteRfq: vi.fn(),
}));

vi.mock('react-i18next', () => ({ useTranslation: () => ({ t: (key: string) => key }) }));
vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: vi.fn() }) }));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { businessUnitId: 7, userName: 'qa' },
    hasPermission: (moduleName: string, action = 'view') => testState.grants.has(`${moduleName}:${action}`),
  }),
}));
vi.mock('../../../api/services/rfqService', () => ({
  default: {
    getAll: vi.fn().mockResolvedValue({
      items: [{ id: 7, rfqno: 'RFQ-MANUAL-7', leadId: null }],
      totalItems: 1,
    }),
    approve: vi.fn(),
    delete: (...args: unknown[]) => testState.deleteRfq(...args),
  },
}));
vi.mock('../../../components/common/SearchField', () => ({ default: () => null }));
vi.mock('../../../components/layout/ViewTabs', () => ({ default: () => null }));
vi.mock('../../../components/common/EmailPromptDialog', () => ({ default: () => null }));
vi.mock('../../../components/common/ApiErrorNotice', () => ({
  default: ({ fallbackMessage }: { fallbackMessage: string }) => <div role="alert">{fallbackMessage}</div>,
}));
vi.mock('@mui/x-data-grid', () => ({
  DataGrid: ({ rows, columns }: {
    rows: Array<Record<string, unknown>>;
    columns: Array<{ field: string; renderCell?: (params: { row: Record<string, unknown> }) => ReactNode }>;
  }) => {
    const actions = columns.find((column) => column.field === 'actions');
    return (
      <div>
        {rows.map((row) => (
          <div key={String(row.id)}>
            <span>{String(row.rfqno)}</span>
            {actions?.renderCell?.({ row })}
          </div>
        ))}
      </div>
    );
  },
}));

import DraftRFQsPage from './DraftRFQsPage';

const renderPage = () => {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <MemoryRouter>
      <QueryClientProvider client={client}>
        <DraftRFQsPage />
      </QueryClientProvider>
    </MemoryRouter>,
  );
};

const deferred = <T,>() => {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
};

beforeEach(() => {
  vi.clearAllMocks();
  testState.grants.clear();
  testState.grants.add('RFQ Management:view');
});

describe('Draft RFQ destructive action', () => {
  it('does not advertise deletion without RFQ delete permission', async () => {
    renderPage();

    await screen.findByText('RFQ-MANUAL-7');
    expect(screen.queryByRole('button', { name: 'Delete draft RFQ RFQ-MANUAL-7' })).not.toBeInTheDocument();
  });

  it('requires a named confirmation and locks it while deletion is pending', async () => {
    testState.grants.add('RFQ Management:delete');
    const pending = deferred<unknown>();
    testState.deleteRfq.mockReturnValue(pending.promise);
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Delete draft RFQ RFQ-MANUAL-7' }));
    const dialog = screen.getByRole('dialog', { name: 'Delete draft RFQ RFQ-MANUAL-7?' });
    expect(dialog).toHaveTextContent(/cannot be undone/i);
    expect(testState.deleteRfq).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Delete draft permanently' }));
    await waitFor(() => expect(testState.deleteRfq).toHaveBeenCalledWith(7, 7));
    expect(await screen.findByRole('button', { name: 'Deleting…' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled();

    pending.resolve({});
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('keeps the confirmation open and explains a failed deletion', async () => {
    testState.grants.add('RFQ Management:delete');
    testState.deleteRfq.mockRejectedValue(new Error('service unavailable'));
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Delete draft RFQ RFQ-MANUAL-7' }));
    fireEvent.click(screen.getByRole('button', { name: 'Delete draft permanently' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/was not deleted. Nothing changed/i);
    expect(screen.getByRole('dialog', { name: 'Delete draft RFQ RFQ-MANUAL-7?' })).toBeInTheDocument();
  });

  it('re-checks current delete authority at confirm and closes a revoked destructive action', async () => {
    testState.grants.add('RFQ Management:delete');
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Delete draft RFQ RFQ-MANUAL-7' }));
    expect(screen.getByRole('dialog', { name: 'Delete draft RFQ RFQ-MANUAL-7?' })).toBeVisible();

    // Models a permission refresh/revocation after the dialog opened but before confirmation.
    testState.grants.delete('RFQ Management:delete');
    fireEvent.click(screen.getByRole('button', { name: 'Delete draft permanently' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(testState.deleteRfq).not.toHaveBeenCalled();
  });
});
