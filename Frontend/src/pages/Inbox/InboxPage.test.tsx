import { render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * The landing screen has one job: answer "what do I do next" without the reader choosing a module.
 *
 * These tests assert what a PERSON SEES on it — the sentence at the top, the words on the buttons,
 * and above all that a failed queue never renders as a clear one. That last property is the reason
 * the screen exists in this shape: an empty list on an outage is how a rep concludes the pipeline
 * is dead and stops working it, and this page shows six queues at once, so one silent failure would
 * be six times as easy to miss.
 */

const auth = {
  modules: [
    'Leads',
    'RFQ Management',
    'Supplier History',
    'Quotations',
    'Customer Awards',
  ] as string[],
};

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { businessUnitId: 1, isManager: false },
    hasPermission: (moduleName: string) => auth.modules.includes(moduleName),
  }),
}));

vi.mock('../../components/layout/ViewTabs', () => ({ default: () => null }));

const api = {
  needsReview: vi.fn(),
  outstandingLeads: vi.fn(),
  rfqs: vi.fn(),
  supplierInbox: vi.fn(),
  quotes: vi.fn(),
  clientPos: vi.fn(),
};

vi.mock('../../api/services/extractionReviewService', () => ({
  default: { getNeedsReview: (...args: unknown[]) => api.needsReview(...args) },
}));
vi.mock('../../api/services/leadService', () => ({
  default: { getOutstandingLeads: (...args: unknown[]) => api.outstandingLeads(...args) },
}));
vi.mock('../../api/services/rfqService', () => ({
  default: { getAll: (...args: unknown[]) => api.rfqs(...args) },
}));
vi.mock('../../api/services/supplierQuoteService', () => ({
  default: { getInbox: (...args: unknown[]) => api.supplierInbox(...args) },
}));
vi.mock('../../api/services/quoteService', () => ({
  default: { getAll: (...args: unknown[]) => api.quotes(...args) },
}));
vi.mock('../../api/services/customerAwardService', () => ({
  default: { searchPurchaseOrders: (...args: unknown[]) => api.clientPos(...args) },
}));

import InboxPage, { loadQueue } from './InboxPage';

const empty = { items: [], totalCount: 0, pageNumber: 1, pageSize: 25 };

const allQueuesEmpty = () => {
  api.needsReview.mockResolvedValue(empty);
  api.outstandingLeads.mockResolvedValue(empty);
  api.rfqs.mockResolvedValue({ items: [], totalItems: 0, pageNumber: 1, pageSize: 25, totalPages: 0 });
  api.supplierInbox.mockResolvedValue([]);
  api.quotes.mockResolvedValue({ items: [], totalItems: 0 });
  api.clientPos.mockResolvedValue([]);
};

const renderInbox = () => {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/inbox']}>
        <InboxPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
};

const section = (heading: string | RegExp) =>
  screen.getByRole('region', { name: heading });

beforeEach(() => {
  auth.modules = ['Leads', 'RFQ Management', 'Supplier History', 'Quotations', 'Customer Awards'];
  allQueuesEmpty();
});

afterEach(() => {
  vi.clearAllMocks();
});

describe('what is waiting on you', () => {
  it('opens an unassigned inquiry with the authoritative Lead id returned by the API', async () => {
    api.outstandingLeads.mockResolvedValue({
      ...empty,
      items: [
        {
          id: 412,
          rfqno: 'P34086',
          buyersName: 'Zahid Khan',
          acceptedDate: '2026-08-23T12:00:00Z',
          unassignedHours: 48,
        },
      ],
    });

    const items = await loadQueue('leads-to-own');

    expect(items).toHaveLength(1);
    expect(items[0]).toMatchObject({
      id: 412,
      reference: 'P34086',
      path: '/procurement/leads/view/412',
      actionLabel: 'Open it',
    });
    expect(items[0].path).not.toContain('undefined');
  });

  it('keeps open customer POs urgent and excludes only terminal lifecycle records', async () => {
    const purchaseOrder = (id: number, status: string, receivedOn: string) => ({
      id,
      internalNumber: `PO-${id}`,
      externalPoNumber: `CUSTOMER-${id}`,
      customerName: 'Northstar Industries',
      nexoraSerial: `NX-${id}`,
      receivedOn,
      status,
      matchOutcome: 'POSSIBLE_MATCH_REVIEW',
      discrepancyCount: 0,
    });
    api.clientPos.mockResolvedValue([
      purchaseOrder(1, 'DRAFT', '2026-08-20T00:00:00Z'),
      purchaseOrder(2, 'PARTIALLY_AWARDED', '2026-08-21T00:00:00Z'),
      purchaseOrder(3, 'FULLY_AWARDED', '2026-08-22T00:00:00Z'),
      purchaseOrder(4, 'CLOSED', '2026-08-23T00:00:00Z'),
      purchaseOrder(5, 'CANCELLED', '2026-08-24T00:00:00Z'),
    ]);

    const items = await loadQueue('client-pos');

    expect(items.map((item) => item.id)).toEqual([1, 2]);
  });

  it('counts the work and says so in a sentence, not in a chart', async () => {
    api.needsReview.mockResolvedValue({
      ...empty,
      items: [
        { id: 11, rfqno: 'RFQ-500', buyersName: 'Aramco', recDate: '2026-08-20', bidClosingDate: null, leadSource: 'Email', aiconfidence: null, itemCount: 4, reviewReason: null, receivedOn: null, reviewVersion: 1 },
      ],
    });
    api.quotes.mockResolvedValue({
      items: [{ id: 7, quoteNo: 'QT-0826-0002', customerName: 'Noor & Sons', quoteDate: '2026-08-21', itemCount: 3 }],
      totalItems: 1,
    });

    renderInbox();

    expect(await screen.findByText(/2 things need you/i)).toBeInTheDocument();
  });

  it('gives every row a verb that goes where the work is', async () => {
    api.needsReview.mockResolvedValue({
      ...empty,
      items: [
        { id: 11, rfqno: 'RFQ-500', buyersName: 'Aramco', recDate: '2026-08-20', bidClosingDate: null, leadSource: 'Email', aiconfidence: null, itemCount: 4, reviewReason: null, receivedOn: null, reviewVersion: 1 },
      ],
    });

    renderInbox();

    // Wait on the CONTENT, not on the section: the section renders immediately with a spinner in
    // it, so awaiting the region alone would assert against the loading state.
    await screen.findByText('RFQ-500');
    const documents = screen.getByRole('region', { name: /documents to check/i });
    expect(within(documents).getByRole('button', { name: 'Check it' })).toBeInTheDocument();
  });

  it('says you are clear when every queue really is empty', async () => {
    renderInbox();

    expect(await screen.findByText('You are clear.')).toBeInTheDocument();
    expect(screen.getByText(/nothing is waiting on you right now/i)).toBeInTheDocument();
  });
});

describe('an empty queue is never a dead end', () => {
  it('says what happened and offers the next action as a button', async () => {
    renderInbox();

    await screen.findByText('Every document has been checked');
    const documents = screen.getByRole('region', { name: /documents to check/i });
    expect(
      within(documents).getByText(/new documents land here automatically/i),
    ).toBeInTheDocument();
    expect(within(documents).getByRole('button', { name: 'Upload a document' })).toBeInTheDocument();
  });

  it('offers a next action on every one of the six queues', async () => {
    renderInbox();

    await screen.findByText('You are clear.');

    for (const heading of [
      /documents to check/i,
      /enquiries without an owner/i,
      /rfqs still in draft/i,
      /supplier replies to read/i,
      /quotes not yet sent/i,
      /customer orders to confirm/i,
    ]) {
      const region = section(heading);
      // Exactly one call to action per empty queue — a stated reason plus one button.
      expect(within(region).getAllByRole('button')).toHaveLength(1);
    }
  });
});

/** An axios-shaped 503 — a real outage, not a bare `Error`, so the error mapper takes its real path. */
const outage = () =>
  Object.assign(new Error('Request failed with status code 503'), {
    isAxiosError: true,
    response: { status: 503, data: {} },
    config: {},
  });

describe('a queue that failed is never shown as a queue that is clear', () => {
  it('renders the failure in place, with a retry, instead of an empty section', async () => {
    api.needsReview.mockRejectedValue(outage());

    renderInbox();

    await screen.findByText(/the review queue could not be loaded/i);
    const documents = screen.getByRole('region', { name: /documents to check/i });
    expect(within(documents).getByRole('button', { name: /try again/i })).toBeInTheDocument();
    // The words that would be a lie here.
    expect(within(documents).queryByText('Every document has been checked')).toBeNull();
  });

  it('refuses to claim you are clear while any queue is unread', async () => {
    api.needsReview.mockRejectedValue(outage());

    renderInbox();

    await screen.findByText(/could not be read/i);
    expect(screen.queryByText('You are clear.')).toBeNull();
  });
});

describe('permissions decide what is even asked for', () => {
  it('does not request a queue the user has no grant for', async () => {
    auth.modules = ['Leads'];

    renderInbox();

    await screen.findByRole('region', { name: /documents to check/i });
    expect(api.needsReview).toHaveBeenCalled();
    // Asking for a queue the server will refuse turns a permission boundary into an error banner
    // on the first screen after sign-in.
    expect(api.quotes).not.toHaveBeenCalled();
    expect(api.clientPos).not.toHaveBeenCalled();
    expect(screen.queryByRole('region', { name: /quotes not yet sent/i })).toBeNull();
  });

  it('explains itself rather than showing a blank page when the user holds nothing', async () => {
    auth.modules = [];

    renderInbox();

    expect(
      await screen.findByText('Your role has no Inbox work queues.'),
    ).toBeInTheDocument();
    expect(screen.getByText('Your role does not have any Inbox work queues.')).toBeInTheDocument();
    expect(screen.queryByText(/nothing is waiting on you/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /roles & permissions/i })).not.toBeInTheDocument();
    expect(screen.getByText(/ask your Nexora administrator/i)).toBeInTheDocument();
  });

  it('offers the governed Roles & Permissions door only to a user who can manage it', async () => {
    auth.modules = ['Roles & Permissions'];

    renderInbox();

    expect(await screen.findByRole('button', { name: 'Open Roles & Permissions' })).toBeInTheDocument();
    expect(screen.getByText(/grant the modules it needs/i)).toBeInTheDocument();
  });
});
