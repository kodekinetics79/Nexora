import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * The currency on the screen where the price is decided.
 *
 * This page carried fourteen hardcoded `$` literals — the per-line total, the tax preview, the
 * subtotal, the header discount, and the GRAND TOTAL a rep reads back to a customer over the
 * phone. On a Saudi riyal tender it printed US dollars. `utils/currency.ts` was written to stop
 * exactly this and says so in its own header: "inventing a symbol is precisely how the
 * hardcoded-`$` defect began." It is imported by ten files; the two quote-authoring screens were
 * not among them.
 *
 * The exported PDF is not affected BY THAT DEFECT — GenerateQuotePdfAsync renders
 * `quote.Currency?.Code` — so the hardcoded-`$` problem was misread-on-screen only.
 *
 * CORRECTION, found later by auditing the 94 commits that shipped unreviewed: the PDF *is*
 * reachable by a DIFFERENT mechanism. This screen's payload never carried `currencyId`, and
 * `QuoteService.UpdateQuoteAsync` assigned it unconditionally, so every save from here ERASED the
 * quote's currency. A draft then fails PDF export on a field this screen does not show; a
 * non-draft prints under the `?? "USD"` fallback. Both silent. Pinned below.
 */

const { getById, update, getAll, productGetAll, customerGetAll, policyGet } = vi.hoisted(() => ({
  getById: vi.fn(),
  update: vi.fn(),
  getAll: vi.fn(),
  productGetAll: vi.fn(),
  customerGetAll: vi.fn(),
  policyGet: vi.fn(),
}));

vi.mock('../../../api/services/quoteService', () => ({
  default: { getById, update },
}));
vi.mock('../../../api/services/setupService', () => ({ default: { getAll } }));
vi.mock('../../../api/services/productService', () => ({ default: { getAll: productGetAll } }));
vi.mock('../../../api/services/customerService', () => ({ default: { getAll: customerGetAll } }));
vi.mock('../../../api/services/commercialPolicyService', () => ({
  default: { get: policyGet, getPolicy: policyGet },
}));
vi.mock('./CustomerContextPanel', () => ({ default: () => null }));
vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({ userData: { businessUnitId: 1 }, hasPermission: () => true }),
}));
vi.mock('react-hot-toast', () => ({
  toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
  default: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn() }),
}));

import EditQuotePage from './EditQuotePage';

const sarQuote = {
  id: 9,
  quoteNo: 'QT-2026-0009',
  statusValue: 'Draft',
  statusCode: 'DRAFT',
  statusId: 1,
  currencyCode: 'SAR',
  customerId: 3,
  quoteDate: '2026-08-01',
  validUntil: '2026-09-01',
  headerRemarks: '',
  totalAmount: 1840000,
  quoteItems: [
    {
      id: 1, productId: 11, productName: 'Cisco Catalyst 9200',
      description: 'Switch', quantity: 2, unitPrice: 920000, discount: 0,
      totalAmount: 1840000, taxAmount: 276000, taxRatePercentApplied: 15,
      taxCategory: 'STANDARD',
    },
  ],
};

function renderEdit() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/sales/quotes/edit/9']}>
        <Routes>
          <Route path="/sales/quotes/edit/:id" element={<EditQuotePage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getById.mockResolvedValue(sarQuote);
  getAll.mockResolvedValue({ items: [] });
  productGetAll.mockResolvedValue({ items: [] });
  customerGetAll.mockResolvedValue({ items: [] });
  policyGet.mockResolvedValue({ outputTaxRatePercent: 15 });
  update.mockResolvedValue({});
});

describe('a quote priced in Saudi riyals', () => {
  it('never renders a dollar sign anywhere on the page', async () => {
    const { container } = renderEdit();
    await waitFor(() => expect(getById).toHaveBeenCalled());
    await screen.findByText(/Revised Summary/i);

    // The regression, stated the way a rep would notice it.
    expect(container.textContent).not.toContain('$');
  });

  it('states the actual currency on the total the rep reads to the customer', async () => {
    renderEdit();
    await screen.findByText(/Revised Summary/i);

    await waitFor(() => {
      // Intl renders SAR as "SAR" or "﷼" depending on the ICU build; either states the unit.
      expect(container_text()).toMatch(/SAR|﷼/);
    });
  });
});

/** The whole rendered document, so the assertion is about what a person sees. */
function container_text(): string {
  return document.body.textContent ?? '';
}

describe('a quote whose validity date is not set', () => {
  /**
   * Found on the live tenant, not in review. Quote QT-0826-0002 carries validUntil = null; the
   * form loads that as '' and sent it straight back, and '' is not a DateTime?. ASP.NET failed to
   * bind the entire request, so the 400 read "The request field is required" and the quote could
   * not be saved AT ALL from this screen — permanently, by any user.
   */
  it('sends an unset date as null rather than an empty string', async () => {
    getById.mockResolvedValue({ ...sarQuote, validUntil: null, quoteDate: null });
    renderEdit();
    await screen.findByText(/Revised Summary/i);

    fireEvent.click(screen.getByRole('button', { name: /update quote/i }));

    await waitFor(() => expect(update).toHaveBeenCalled());
    const payload = update.mock.calls[0][1];
    expect(payload.validUntil).toBeNull();
    expect(payload.validUntil).not.toBe('');
  });

  it('round-trips the currency instead of silently dropping it', async () => {
    // The omission that erased it: an absent key binds to null on the server, and the assignment
    // there was unconditional. Saving a SAR quote made it currency-less, and the customer's PDF
    // then printed USD.
    getById.mockResolvedValue({ ...sarQuote, currencyId: 4, currencyCode: 'SAR' });
    renderEdit();
    await screen.findByText(/Revised Summary/i);

    fireEvent.click(screen.getByRole('button', { name: /update quote/i }));

    await waitFor(() => expect(update).toHaveBeenCalled());
    expect(update.mock.calls[0][1].currencyId).toBe(4);
  });
});
