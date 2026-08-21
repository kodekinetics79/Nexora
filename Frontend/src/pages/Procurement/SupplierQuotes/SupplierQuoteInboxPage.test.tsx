import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * The supplier quote inbox must not offer a door nobody can walk through.
 *
 * "Capture Supplier Quote" opened a dialog asking a buyer to type six database row identifiers as
 * free-text numbers — Supplier ID, Supplier RFQ ID, Sourcing Case ID, Currency ID, RFQ line ID,
 * Demand line ID — with no picker and no lookup, behind helper text telling them to read those
 * numbers off a different screen first. Thirteen of twenty-three fields were mandatory and the
 * disabled submit gave no indication which was missing. The payload hardcoded `lineNumber: 1`, so
 * an eighteen-line supplier quote could not be captured at all.
 *
 * The commercial consequence is not the dialog: it is that supplier quotes then get captured
 * nowhere, and the sourcing comparison behind every margin decision has no input.
 *
 * Upload is the working door and stays.
 */

const { getInbox } = vi.hoisted(() => ({ getInbox: vi.fn() }));

// Spread the real module so constants like INCOTERMS_2020 survive; override only the network.
vi.mock('../../../api/services/procurementService', async () => {
  const actual = await vi.importActual<Record<string, unknown>>(
    '../../../api/services/procurementService',
  );
  return {
    ...actual,
    default: {
      ...(actual.default as Record<string, unknown>),
      getSupplierQuoteInbox: getInbox,
      captureSupplierQuote: vi.fn(),
      uploadSupplierQuote: vi.fn(),
    },
    getSupplierQuoteInbox: getInbox,
  };
});

vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { businessUnitId: 1 },
    hasPermission: () => true,
  }),
}));

import SupplierQuoteInboxPage from './SupplierQuoteInboxPage';

function renderInbox() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <SupplierQuoteInboxPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getInbox.mockResolvedValue({ items: [], totalItems: 0 });
});

describe('supplier quote inbox', () => {
  it('does not offer hand-keyed capture', async () => {
    renderInbox();
    await screen.findByRole('button', { name: /Refresh/i });

    expect(screen.queryByRole('button', { name: /Capture Supplier Quote/i })).not.toBeInTheDocument();
  });

  it('still offers upload, which is the door that works', async () => {
    renderInbox();

    expect(await screen.findByRole('button', { name: /Upload Supplier Quote/i })).toBeInTheDocument();
  });
});
