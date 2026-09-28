import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * Pilot audit UX-23: the "This customer" panel printed a literal "$" on a SAR tenant ("Typical
 * quote size $12,025", "Last sold at $…"). Figures now carry the currency the history names, else
 * the quote's own, else none.
 */

const { getCustomerContext } = vi.hoisted(() => ({ getCustomerContext: vi.fn() }));
vi.mock('../../../api/services/intelligenceService', () => ({ default: { getCustomerContext } }));

import CustomerContextPanel from './CustomerContextPanel';

const context = {
  customerId: 3, customerName: 'SEC', totalQuotes: 4, wonQuotes: 1, lostQuotes: 1, winRatePct: 50,
  leadStageLosses: 0, inquiryWinRatePct: 50, recentLeadLosses: [], ordersLast24Months: 1,
  orderValueLast24Months: 40000, orderValueStatus: 'OK', orderValueByCurrency: [],
  avgQuoteTotal: 12025, avgQuoteTotalStatus: 'OK', quoteValueByCurrency: [],
  avgMarginPct: null, lastQuoteDate: null, recentQuotes: [], recentRfqs: [], recentOrders: [], demandProfile: [],
  recentItemPrices: [{ productId: 1, description: 'Battery', unitPrice: 45, quoteDate: null, monthsAgo: 2 }],
  completeness: {}, generatedAt: '2026-09-28',
};

function renderPanel(currencyCode?: string | null) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <CustomerContextPanel customerId={3} currencyCode={currencyCode} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getCustomerContext.mockResolvedValue(context);
});

describe('the This customer panel on a SAR quote', () => {
  it('never prints a dollar sign and states the quote currency', async () => {
    const { container } = renderPanel('SAR');
    await screen.findByText('Typical quote size');

    expect(container.textContent).not.toContain('$');
    expect(container.textContent).toMatch(/SAR|﷼/);
  });

  it('prints bare numbers rather than inventing a currency when none is known', async () => {
    const { container } = renderPanel(null);
    await screen.findByText('Typical quote size');

    expect(container.textContent).not.toContain('$');
    expect(container.textContent).toContain('12,025');
  });
});
