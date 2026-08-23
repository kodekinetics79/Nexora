import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * A filtered list under a heading that claims to show everything.
 *
 * The four quote entries in the rail are filtered addresses — /sales/quotes?state=sent and
 * friends. The page read the `state` param and passed it into the query, but headed itself
 * "Quote Management / Manage sales quotations and customer offers" whichever door the rep came
 * through, with nothing lit in the rail (see Sidebar.filteredNav.test.tsx) and no filter stated.
 *
 * A rep clicked "Sent Quotes" at 4pm to chase offers, saw three rows, and reported to their
 * manager that the pipeline was nearly empty. This is a data-trust defect: the page has to name
 * the slice it is showing and offer the way back to all of it.
 */

const { getAll } = vi.hoisted(() => ({ getAll: vi.fn() }));

vi.mock('../../../api/services/quoteService', () => ({
  default: {
    getAll,
    revise: vi.fn(),
    sendEmail: vi.fn(),
    confirmPriceAttestation: vi.fn(),
    downloadPdf: vi.fn(),
  },
}));

vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { businessUnitId: 1 },
    hasPermission: () => true,
  }),
}));

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string, fallback?: string) => fallback ?? key }),
}));

// None of these is under test; they only add noise to the header assertions.
vi.mock('./QuoteOutcomeDialog', () => ({ default: () => null }));
vi.mock('./PriceConfirmationDialog', () => ({ default: () => null }));
vi.mock('../../../components/common/EmailPromptDialog', () => ({ default: () => null }));

import QuotesPage from './QuotesPage';

// The rep's three rows. Shaped from QuoteResponseDTO as the grid consumes it.
const sentQuotes = [
  {
    id: 11, quoteNo: 'QT-2026-0011', nexoraSerial: 'NX-Q-0011', customerName: 'Aramco',
    statusCode: 'SENT', statusValue: 'Sent', totalAmount: 1840000, currencyCode: 'SAR',
    quoteDate: '2026-08-01', validUntil: '2026-09-01', daysSinceSent: 3, isStale: false,
    itemCount: 2, rfqNo: 'RFQ-2026-0041',
  },
];

function renderQuotes(url: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const Address = () => <div data-testid="address">{useLocation().pathname + useLocation().search}</div>;
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[url]}>
        <Address />
        <Routes>
          <Route path="/sales/quotes" element={<QuotesPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getAll.mockResolvedValue({ items: sentQuotes, totalItems: sentQuotes.length });
});

describe('the quote list opened through a filtered rail entry', () => {
  it('states the active filter instead of claiming to show everything', async () => {
    renderQuotes('/sales/quotes?state=sent');

    expect(await screen.findByText('Filtered: Sent quotes')).toBeInTheDocument();
    expect(screen.getByText(/This is not the whole pipeline/i)).toBeInTheDocument();
    // The sentence the rep read as "the pipeline is nearly empty".
    expect(screen.queryByText('Manage sales quotations and customer offers')).not.toBeInTheDocument();
  });

  it('names the right slice for each state the server actually narrows on', async () => {
    renderQuotes('/sales/quotes?state=follow-up');
    expect(await screen.findByText('Filtered: Follow-up due')).toBeInTheDocument();
  });

  it('offers a way back to the unfiltered list', async () => {
    renderQuotes('/sales/quotes?state=sent');

    fireEvent.click(await screen.findByRole('button', { name: 'Show all quotes' }));

    await waitFor(() => expect(screen.getByTestId('address')).toHaveTextContent('/sales/quotes'));
    expect(screen.getByTestId('address').textContent).not.toContain('state=');
    expect(screen.getByText('Manage sales quotations and customer offers')).toBeInTheDocument();
    expect(screen.queryByText(/^Filtered:/)).not.toBeInTheDocument();
  });

  it('says so when the link asks for a filter the server does not apply', async () => {
    // QuoteRepository.GetAllAsync silently ignores an unknown state, so the grid really is
    // complete — claiming a filter here would be the same lie pointing the other way.
    renderQuotes('/sales/quotes?state=requires-sourcing');

    expect(await screen.findByText(/not a filter this list applies/i)).toBeInTheDocument();
    expect(screen.queryByText(/^Filtered:/)).not.toBeInTheDocument();
  });

  it('leaves the unfiltered page alone', async () => {
    // CONTROL: passes against the broken code too. It pins that the honest heading is still the
    // heading when there is no filter, so the fix cannot be "always shout FILTERED".
    renderQuotes('/sales/quotes');

    expect(await screen.findByText('Manage sales quotations and customer offers')).toBeInTheDocument();
    expect(screen.queryByText(/^Filtered:/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Show all quotes' })).not.toBeInTheDocument();
  });
});
