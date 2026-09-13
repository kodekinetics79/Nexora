import { act, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { readTriagePage } from '../../api/services/emailTriageService';

/**
 * Inbound mail re-reads itself every 15 s while a message is being assembled. Refresh and the pager
 * followed `isFetching`, so they greyed out on every automatic poll and a click that landed then did
 * nothing; a failed poll replaced the rows with an error; and turning a page swapped the table for a
 * loading panel.
 */

const listTriage = vi.fn();
vi.mock('../../api/services/emailTriageService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/emailTriageService')>();
  return {
    ...actual,
    default: {
      listTriage: (params: unknown) => listTriage(params),
      reprocess: vi.fn(),
      pollMailboxes: vi.fn(),
      getMessage: vi.fn(),
    },
  };
});

vi.mock('../../api/services/mailboxService', () => ({
  default: { getAll: () => Promise.resolve([]) },
}));

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => vi.fn() };
});

import InboundMailTriagePage from './InboundMailTriagePage';

const PAGE_ONE = readTriagePage(
  {
    items: [{
      id: 41,
      receivedOn: '2026-08-03T06:12:00Z',
      from: 'ops@alfuttaim-contracting.ae',
      subject: 'Automatic reply: Cable tray enquiry',
      outcome: 'Noise',
      reasonCodes: ['auto_submitted_header'],
      hasAttachments: false,
      linkedBatchId: null,
      bodyPreview: 'I am out of the office until 12 August.',
      bodySubmitted: false,
      attachmentCount: 0,
      extractedItemCount: 0,
    }],
    totalCount: 60,
    pageNumber: 1,
    pageSize: 25,
  },
  1,
);

/**
 * TanStack Query tells components about a fetch starting or settling on a timer. Without this flush
 * an assertion about the in-flight state runs before the page has seen it, and passes either way.
 */
const flushQueryNotifications = () => act(async () => {
  await new Promise((resolve) => setTimeout(resolve, 20));
});

const mount = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  render(
    <MemoryRouter>
      <QueryClientProvider client={client}>
        <InboundMailTriagePage />
      </QueryClientProvider>
    </MemoryRouter>,
  );
  return client;
};

describe('InboundMailTriagePage background refresh', () => {
  beforeEach(() => {
    listTriage.mockReset();
  });

  it('leaves Refresh and Next usable while an automatic re-read is in flight', async () => {
    listTriage.mockResolvedValue(PAGE_ONE);
    const client = mount();
    expect(await screen.findByText('Automatic reply: Cable tray enquiry')).toBeInTheDocument();

    listTriage.mockReturnValue(new Promise(() => {}));
    act(() => {
      void client.refetchQueries({ queryKey: ['email-triage'] });
    });
    await flushQueryNotifications();
    expect(client.isFetching({ queryKey: ['email-triage'] })).toBeGreaterThan(0);

    expect(screen.getByRole('button', { name: 'Next' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeEnabled();
    expect(screen.getByText('Automatic reply: Cable tray enquiry')).toBeInTheDocument();
  });

  it('keeps the rows when a re-read fails, and says so', async () => {
    listTriage.mockResolvedValue(PAGE_ONE);
    const client = mount();
    expect(await screen.findByText('Automatic reply: Cable tray enquiry')).toBeInTheDocument();

    listTriage.mockRejectedValue({ isAxiosError: true, message: 'Request failed with status code 502', response: { status: 502, data: {} } });
    await act(async () => {
      await client.refetchQueries({ queryKey: ['email-triage'] });
    });

    expect(await screen.findByText(/Couldn't refresh just now/)).toBeInTheDocument();
    expect(screen.getByText('Automatic reply: Cable tray enquiry')).toBeInTheDocument();
    expect(screen.queryByText(/Inbound mail decisions could not be loaded/)).not.toBeInTheDocument();
  });

  it('keeps the current rows on screen while the next page loads, instead of a loading panel', async () => {
    listTriage.mockResolvedValue(PAGE_ONE);
    mount();
    expect(await screen.findByText('Automatic reply: Cable tray enquiry')).toBeInTheDocument();

    listTriage.mockReturnValue(new Promise(() => {}));
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    expect(screen.getByText('Automatic reply: Cable tray enquiry')).toBeInTheDocument();
    expect(screen.queryByText('Loading inbound mail decisions…')).not.toBeInTheDocument();
    // Until page 2 arrives, the pager cannot skip past it.
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
  });
});
