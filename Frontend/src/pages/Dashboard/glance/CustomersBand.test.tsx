import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import CustomersBand from './CustomersBand';
import type { CustomerCommercialMemory } from '../../../api/services/commercialLearningService';

const getCustomers = vi.fn();
vi.mock('../../../api/services/commercialLearningService', () => ({
  default: { getCustomers: (...args: unknown[]) => getCustomers(...args) },
}));

const getColumns = vi.fn();
const saveColumns = vi.fn();
vi.mock('../../../api/services/listViewService', () => ({
  default: {
    getColumns: (...args: unknown[]) => getColumns(...args),
    saveColumns: (...args: unknown[]) => saveColumns(...args),
  },
}));

const wrapper = ({ children }: { children: ReactNode }) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <QueryClientProvider client={client}>
      <MemoryRouter>{children}</MemoryRouter>
    </QueryClientProvider>
  );
};

const customer = (
  customerId: number, customerName: string, quoteCount: number, wonCount: number, lostCount: number,
  pendingCount: number, inquiryCount = quoteCount + 2,
): CustomerCommercialMemory => ({
  customerId, customerName, inquiryCount, quoteCount, decidedCount: wonCount + lostCount, wonCount, lostCount,
  pendingCount, conversionRatePercent: null, wonValues: [], lossReasons: [], evidence: [],
});

const SEVEN = [
  customer(30, 'Saudi Aramco', 14, 5, 6, 3),
  customer(31, 'SEC', 10, 1, 2, 7),
  customer(32, 'Marafiq', 8, 4, 1, 3),
  customer(33, 'SABIC', 6, 0, 3, 3),
  customer(34, 'Ma\'aden', 4, 2, 1, 1),
  customer(35, 'NEOM', 2, 0, 0, 2),
  customer(36, 'SWCC', 1, 1, 0, 0),
];

beforeEach(() => {
  vi.clearAllMocks();
  getCustomers.mockResolvedValue(SEVEN);
  getColumns.mockResolvedValue({ viewKey: 'dashboard.charts', columns: [], isCustomised: false, supportsCustomFields: false });
  saveColumns.mockImplementation((viewKey: string, columns: { key: string; visible: boolean }[]) => Promise.resolve({
    viewKey, isCustomised: true, supportsCustomFields: false,
    columns: columns.map((c) => ({ ...c, label: c.key, locked: false, source: 'catalog' })),
  }));
});

describe('CustomersBand', () => {
  it('rings the customers by quotes, all time and company-wide, never governed by the period', async () => {
    render(<CustomersBand />, { wrapper });

    expect(await screen.findByText('Saudi Aramco')).toBeInTheDocument();
    expect(getCustomers).toHaveBeenCalledWith(50);
    // Aramco is 14 of 45 quotes.
    expect(screen.getByRole('button', { name: /Saudi Aramco\s*14\s*31%/ })).toBeInTheDocument();
    expect(screen.getByText('45')).toBeInTheDocument();
    expect(screen.getByText('quotes')).toBeInTheDocument();
    const seal = screen.getByTestId('band-seal');
    expect(seal).toHaveTextContent('Company-wide · All time');
    expect(seal).toHaveAttribute('data-governed', 'false');
  });

  it('folds everything past the top five into Other', async () => {
    render(<CustomersBand />, { wrapper });

    expect(await screen.findByText('Other (2)')).toBeInTheDocument();
    expect(screen.queryByText('SWCC')).toBeNull();
  });

  it('opens one sentence and a way to the customer for a picked slice', async () => {
    render(<CustomersBand />, { wrapper });

    const aramco = await screen.findByRole('button', { name: /^Saudi Aramco/ });
    fireEvent.click(aramco);
    expect(screen.getByTestId('customers-drill')).toHaveTextContent('Saudi Aramco: 14 quotes, 5 won, 6 lost, 3 open.');
    expect(screen.getByRole('link', { name: 'Open customer' })).toHaveAttribute('href', '/customers/30');
    fireEvent.click(aramco);
    expect(screen.queryByTestId('customers-drill')).toBeNull();
  });

  it('switches to Won and saves the choice', async () => {
    render(<CustomersBand />, { wrapper });

    fireEvent.click(await screen.findByRole('button', { name: /Ring shows: Quotes/ }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Won' }));

    await waitFor(() => expect(saveColumns).toHaveBeenCalledWith('dashboard.charts', expect.arrayContaining([
      { key: 'customers.won', visible: true },
      { key: 'customers.quotes', visible: false },
      { key: 'customers.inquiries', visible: false },
    ])));
    // 13 wins in all; the customers with none drop out of the ring.
    await waitFor(() => expect(screen.getByText('13')).toBeInTheDocument());
    expect(screen.getByText('won')).toBeInTheDocument();
    expect(screen.queryByText('SABIC')).toBeNull();
  });

  it('keeps the frame and says so when nothing has been quoted', async () => {
    getCustomers.mockResolvedValue([]);
    render(<CustomersBand />, { wrapper });

    expect(await screen.findByTestId('customers-empty')).toHaveTextContent('No quote has gone to a customer yet.');
  });

  it('tells a reader without the permission, calmly, with no Retry', async () => {
    getCustomers.mockRejectedValue({ isAxiosError: true, response: { status: 403, data: '' } });
    render(<CustomersBand />, { wrapper });

    expect(await screen.findByText(/can see both customers and quotes/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
  });
});
