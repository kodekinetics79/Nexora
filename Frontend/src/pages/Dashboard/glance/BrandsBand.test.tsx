import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import BrandsBand from './BrandsBand';
import type { BrandDemandDTO, BrandDemandRowDTO } from '../../../api/services/dashboardService';

const get = vi.fn();
vi.mock('../../../api/axiosInstance', () => ({
  default: { get: (...args: unknown[]) => get(...args) },
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

const FROM = '2026-08-08';
const TO = '2026-09-06';

const row = (manufacturer: string, lines: number, documents: number): BrandDemandRowDTO => ({
  manufacturer, normalizedKey: manufacturer.toUpperCase(), variants: 1, lines, documents,
  totalQuantity: null, lineSharePercent: 0,
});

const demand = (rows: BrandDemandRowDTO[], without = 0): BrandDemandDTO => {
  const named = rows.reduce((sum, r) => sum + r.lines, 0);
  return {
    generatedAt: '2026-09-06T09:15:00Z', from: FROM, to: TO,
    totalLines: named + without, linesWithManufacturer: named, linesWithoutManufacturer: without,
    distinctManufacturers: rows.length, distinctRawSpellings: rows.length, topFiveLineSharePercent: 0,
    rows, quantityCaveat: 'Indicative only.',
  };
};

const EIGHT = [
  row('Siemens', 120, 14), row('ABB', 200, 20), row('Eaton', 100, 9), row('Schneider', 80, 7),
  row('Rockwell', 60, 5), row('Honeywell', 40, 4), row('Emerson', 30, 3), row('Yokogawa', 30, 2),
];

beforeEach(() => {
  vi.clearAllMocks();
  get.mockResolvedValue({ status: 200, data: demand(EIGHT, 45) });
  getColumns.mockResolvedValue({ viewKey: 'dashboard.charts', columns: [], isCustomised: false, supportsCustomFields: false });
  saveColumns.mockResolvedValue({ viewKey: 'dashboard.charts', columns: [], isCustomised: true, supportsCustomFields: false });
});

describe('BrandsBand', () => {
  it('asks for the window with its last day included, and names the top makers with their share', async () => {
    render(<BrandsBand from={FROM} to={TO} />, { wrapper });

    expect(await screen.findByText('ABB')).toBeInTheDocument();
    expect(get).toHaveBeenCalledWith('/api/brand-demand', {
      params: { from: FROM, to: `${TO}T23:59:59`, topN: 200 },
    });
    // ABB is 200 of the 660 lines that name a maker.
    expect(screen.getByRole('button', { name: /ABB\s*200\s*30%/ })).toBeInTheDocument();
    expect(screen.getByText('660')).toBeInTheDocument();
    expect(screen.getByText('lines')).toBeInTheDocument();
    expect(screen.getByTestId('brands-unnamed')).toHaveTextContent('45 lines named no maker.');
    const seal = screen.getByTestId('band-seal');
    expect(seal).toHaveTextContent('Company-wide · 8 Aug – 6 Sep');
    expect(seal).toHaveAttribute('data-governed', 'true');
  });

  it('folds everything past the top five into Other', async () => {
    render(<BrandsBand from={FROM} to={TO} />, { wrapper });

    expect(await screen.findByText('Other (3)')).toBeInTheDocument();
    expect(screen.queryByText('Emerson')).toBeNull();
    expect(screen.queryByText('Yokogawa')).toBeNull();
  });

  it('opens one sentence for a picked maker and closes it on a second press', async () => {
    render(<BrandsBand from={FROM} to={TO} />, { wrapper });

    const siemens = await screen.findByRole('button', { name: /^Siemens/ });
    fireEvent.click(siemens);
    expect(screen.getByTestId('brands-drill'))
      .toHaveTextContent('Siemens: 120 lines across 14 documents (18% of lines that name a maker).');
    fireEvent.click(siemens);
    expect(screen.queryByTestId('brands-drill')).toBeNull();
  });

  it('keeps the frame and says so when no line names a maker', async () => {
    get.mockResolvedValue({ status: 200, data: demand([], 12) });
    render(<BrandsBand from={FROM} to={TO} />, { wrapper });

    expect(await screen.findByTestId('brands-empty')).toHaveTextContent('names a maker yet');
    expect(screen.getByTestId('brands-unnamed')).toHaveTextContent('12 lines named no maker.');
  });

  it('tells a reader without the role, calmly, with no Retry', async () => {
    get.mockRejectedValue({ isAxiosError: true, response: { status: 403, data: '' } });
    render(<BrandsBand from={FROM} to={TO} />, { wrapper });

    expect(await screen.findByText(/managers and admins only/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
  });

  it('links to the full brand demand page', async () => {
    render(<BrandsBand from={FROM} to={TO} />, { wrapper });
    await waitFor(() => expect(screen.getByRole('link', { name: /details/ })).toHaveAttribute('href', '/analytics/brand-demand'));
  });
});
