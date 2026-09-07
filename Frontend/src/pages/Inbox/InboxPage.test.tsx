import { render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * The landing screen has one job: answer "what do I do next" without the reader choosing a module.
 *
 * These tests assert what a PERSON SEES on it — the sentence at the top, the words on the buttons,
 * and above all that a failed queue never renders as a clear one. That last property is the reason
 * the screen exists in this shape: an empty list on an outage is how a rep concludes the pipeline
 * is dead and stops working it, and this page shows several queues at once, so one silent failure
 * is especially easy to miss.
 */

const auth = {
  userData: {
    id: 71,
    businessUnitId: 1,
    isManager: false,
    isSuperAdmin: false,
  },
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
    userData: auth.userData,
    hasPermission: (moduleName: string) => auth.modules.includes(moduleName),
  }),
}));

vi.mock('../../components/layout/ViewTabs', () => ({ default: () => null }));

const api = {
  stoppedMail: vi.fn(),
  needsReview: vi.fn(),
  outstandingLeads: vi.fn(),
  assignedLeads: vi.fn(),
  rfqs: vi.fn(),
  supplierInbox: vi.fn(),
  quotes: vi.fn(),
  clientPos: vi.fn(),
};

// The triage module is only partly mocked: `readTriagePage`, `isTriageUnavailable` and the state
// constant are the real ones, so a fixture below is read exactly as a server payload would be.
vi.mock('../../api/services/emailTriageService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/emailTriageService')>();
  return { ...actual, default: { listTriage: (...args: unknown[]) => api.stoppedMail(...args) } };
});
vi.mock('../../api/services/extractionReviewService', () => ({
  default: { getNeedsReview: (...args: unknown[]) => api.needsReview(...args) },
}));
vi.mock('../../api/services/leadService', () => ({
  default: {
    getOutstandingLeads: (...args: unknown[]) => api.outstandingLeads(...args),
    getAssignedLeads: (...args: unknown[]) => api.assignedLeads(...args),
  },
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
import { readTriagePage } from '../../api/services/emailTriageService';
import type { InboxItem, QueueKey } from './inboxQueues';

const empty = { items: [], totalCount: 0, pageNumber: 1, pageSize: 25 };

/**
 * The rows of a queue that DOES apply to this tenant. `loadQueue` answers null only for a channel
 * the tenant does not have at all, and a mapping test that quietly accepted that would assert
 * nothing — so it is a failure here, not an empty array.
 */
const rowsOf = async (key: QueueKey, context?: Parameters<typeof loadQueue>[1]): Promise<InboxItem[]> => {
  const items = await loadQueue(key, context);
  if (items === null) throw new Error(`Queue ${key} reported itself as not applicable to this tenant`);
  return items;
};

const allQueuesEmpty = () => {
  api.stoppedMail.mockResolvedValue(readTriagePage({ items: [], totalCount: 0, pageNumber: 1, pageSize: 25 }, 1));
  api.needsReview.mockResolvedValue(empty);
  api.outstandingLeads.mockResolvedValue(empty);
  api.assignedLeads.mockResolvedValue(empty);
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
  auth.userData = {
    id: 71,
    businessUnitId: 1,
    isManager: false,
    isSuperAdmin: false,
  };
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

    const items = await rowsOf('leads-to-own');

    expect(items).toHaveLength(1);
    expect(items[0]).toMatchObject({
      id: 412,
      reference: 'P34086',
      path: '/procurement/leads/view/412',
      actionLabel: 'Open it',
    });
    expect(items[0].path).not.toContain('undefined');
  });

  it('opens an assigned inquiry directly in the governed decision workbench', async () => {
    api.assignedLeads.mockResolvedValue({
      ...empty,
      items: [
        {
          id: 914,
          rfqno: 'RFQ-CUSTOMER-914',
          buyersName: 'Northstar Industries',
          customerName: 'Northstar Industries',
          acceptedDate: '2026-08-24T09:00:00Z',
          assignedOn: '2026-08-24T10:00:00Z',
          assignedToFullName: 'Amina Saleh',
        },
      ],
    });

    const items = await rowsOf('leads-to-decide', {
      businessUnitId: 1,
      userId: 71,
    });

    expect(items).toHaveLength(1);
    expect(items[0]).toMatchObject({
      id: 914,
      reference: 'RFQ-CUSTOMER-914',
      path: '/procurement/leads/914/workbench',
      actionLabel: 'Make decision',
    });
  });

  it('scopes assigned decisions to the rep but lets a manager read the team queue', async () => {
    await loadQueue('leads-to-decide', {
      businessUnitId: 1,
      userId: 71,
      teamScope: false,
    });
    expect(api.assignedLeads).toHaveBeenLastCalledWith(expect.objectContaining({
      businessUnitId: 1,
      assignedToId: 71,
    }));

    await loadQueue('leads-to-decide', {
      businessUnitId: 1,
      userId: 88,
      teamScope: true,
    });
    expect(api.assignedLeads).toHaveBeenLastCalledWith(expect.objectContaining({
      businessUnitId: 1,
      assignedToId: undefined,
    }));
  });

  it('fails closed rather than broadening a rep queue when user identity is unavailable', async () => {
    await expect(loadQueue('leads-to-decide', { businessUnitId: 1 })).rejects.toThrow(
      /identity is unavailable/i,
    );
    expect(api.assignedLeads).not.toHaveBeenCalled();
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

    const items = await rowsOf('client-pos');

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

  it('offers a next action on every queue', async () => {
    renderInbox();

    await screen.findByText('You are clear.');

    for (const heading of [
      /mail that needs a person/i,
      /documents to check/i,
      /enquiries without an owner/i,
      /my enquiries awaiting a decision/i,
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

  it('renders an assigned-decision outage as a failure, never an empty decision queue', async () => {
    api.assignedLeads.mockRejectedValue(outage());

    renderInbox();

    await screen.findByText(/assigned enquiries awaiting a decision could not be loaded/i);
    const decisions = section(/my enquiries awaiting a decision/i);
    expect(within(decisions).getByRole('button', { name: /try again/i })).toBeInTheDocument();
    expect(within(decisions).queryByText(/no assigned enquiry is waiting/i)).toBeNull();
  });
});

describe('permissions decide what is even asked for', () => {
  it('does not request a queue the user has no grant for', async () => {
    auth.modules = ['Leads'];

    renderInbox();

    await screen.findByRole('region', { name: /documents to check/i });
    expect(api.needsReview).toHaveBeenCalled();
    expect(api.assignedLeads).toHaveBeenCalledWith(expect.objectContaining({ assignedToId: 71 }));
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

/**
 * The population this screen used to be blind to.
 *
 * Every other queue reads a row that already became something — a document with a lead behind it,
 * an enquiry, an RFQ, a quote. A message that STOPPED became nothing at all: the backend filter
 * requires that no Lead points at it, so `documents-to-check` (GET /api/Lead/needs-review) cannot
 * carry it and no other queue here could either. The landing screen therefore said "You are
 * clear." over inbound mail that was going nowhere, and the one screen that would have shown it
 * is the one the reader had just been told there was no reason to open. On the live tenant that
 * was 80 of 332 messages.
 */
describe('inbound mail that stopped is work, and the landing screen must say so', () => {
  /** The server's own shape for a message held for a person: no lead, assembly at NeedsReview. */
  const stoppedPayload = {
    pageNumber: 1,
    pageSize: 25,
    totalCount: 1,
    items: [
      {
        id: 6120,
        receivedOn: '2026-08-30T05:12:00Z',
        from: 'tenders@dana-cont.qa',
        subject: 'RFQ 8891 — pipe supports',
        outcome: 'Uncertain',
        reasonCodes: ['no_signal'],
        hasAttachments: true,
        linkedBatchId: '3f0a5b6c-1d2e-4f70-8a91-0b2c3d4e5f60',
        parseStatus: 'Queued',
        attachmentCount: 1,
        attachmentNames: ['bid-list.doc'],
        bodySubmitted: true,
        skippedAttachments: [],
        assemblyState: 'NeedsReview',
        assemblyReason: 'A part could not be read with confidence.',
        expectedComponentCount: 2,
        completedComponentCount: 2,
        ingestedAtUtc: '2026-08-30T05:13:00Z',
        stoppedInProcessing: false,
      },
    ],
  };

  /** An entitlement refusal, exactly as RequiresEntitlementAttribute writes it. */
  const notEntitled = () =>
    Object.assign(new Error('Request failed with status code 403'), {
      isAxiosError: true,
      response: {
        status: 403,
        data: {
          type: 'https://nexora.invalid/problems/feature-not-entitled',
          title: 'Feature is not entitled',
          detail: 'Email intake is not included in this plan.',
          status: 403,
          entitlement: 'capability.email-intake',
        },
      },
      config: {},
    });

  it('refuses to say you are clear while a message is waiting on a person', async () => {
    api.stoppedMail.mockResolvedValue(readTriagePage(stoppedPayload, 1));

    renderInbox();

    expect(await screen.findByText('RFQ 8891 — pipe supports')).toBeInTheDocument();
    // The sentence that was false. Every other queue is empty in this test, which is exactly the
    // Monday morning the defect was found on.
    expect(screen.queryByText('You are clear.')).toBeNull();
    expect(screen.getByText(/1 thing needs you/i)).toBeInTheDocument();
    const mail = section(/mail that needs a person/i);
    expect(within(mail).getByText(/tenders@dana-cont.qa/)).toBeInTheDocument();
    expect(within(mail).getByRole('button', { name: 'Open it' })).toBeInTheDocument();
  });

  it('asks the state question, because no outcome filter can answer it', async () => {
    renderInbox();

    // `stopped` is about where the message IS. Every `outcome` value is about what the arrival
    // gate decided, and a message stops long after that — so a call without the state filter
    // would list every message ever received and this queue would be meaningless.
    await waitFor(() =>
      expect(api.stoppedMail).toHaveBeenCalledWith({ state: 'stopped', page: 1, pageSize: 25 }));
  });

  it('maps a stopped message onto a row that says why it stopped', async () => {
    api.stoppedMail.mockResolvedValue(readTriagePage(stoppedPayload, 1));

    const items = await rowsOf('mail-to-rescue');

    expect(items).toEqual([
      {
        id: 6120,
        reference: 'RFQ 8891 — pipe supports',
        party: 'tenders@dana-cont.qa',
        detail: 'Needs review',
        path: '/procurement/leads/inbound-mail',
        actionLabel: 'Open it',
        sortKey: '2026-08-30T05:12:00Z',
      },
    ]);
  });

  it('leaves the queue out for a tenant whose plan has no email intake, rather than banner it', async () => {
    api.stoppedMail.mockRejectedValue(notEntitled());

    renderInbox();

    // An entitlement is a plan-level switch, not a role, so this refusal is permanent: rendered
    // as an error it would put a red banner on the first screen after every sign-in, for ever.
    await screen.findByText('You are clear.');
    expect(screen.queryByRole('region', { name: /mail that needs a person/i })).toBeNull();
    expect(screen.queryByText(/inbound mail could not be loaded/i)).toBeNull();
  });

  it('still renders a real outage as a failure, never as an absent queue', async () => {
    api.stoppedMail.mockRejectedValue(outage());

    renderInbox();

    await screen.findByText(/inbound mail could not be loaded/i);
    const mail = section(/mail that needs a person/i);
    expect(within(mail).getByRole('button', { name: /try again/i })).toBeInTheDocument();
    expect(within(mail).queryByText(/no message is waiting on a person/i)).toBeNull();
    expect(screen.queryByText('You are clear.')).toBeNull();
  });
});
