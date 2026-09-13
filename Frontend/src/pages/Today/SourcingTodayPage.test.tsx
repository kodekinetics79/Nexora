import { act, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * A self-refreshing list must keep what it shows when one 60 s re-read fails. It used to swap the
 * whole table for "This persisted view could not be loaded" during every backend deploy.
 */

const getInbox = vi.fn();
vi.mock('../../api/services/supplierQuoteService', () => ({
  default: { getInbox: () => getInbox() },
}));

import SourcingTodayPage from './SourcingTodayPage';

const row = {
  supplierQuoteId: 31,
  supplierName: 'Gulf Cable Trading',
  supplierQuoteReference: 'GCT-Q-0442',
  nexoraSerial: 'NX-SQ-000031',
  inboxStatus: 'REVIEW_REQUIRED',
  reviewRequiredCount: 2,
  updatedOn: '2026-09-12T08:00:00Z',
};

const badGateway = {
  isAxiosError: true,
  message: 'Request failed with status code 502',
  response: { status: 502, data: {} },
};

const mount = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <SourcingTodayPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return client;
};

beforeEach(() => {
  getInbox.mockReset();
});

describe('SourcingTodayPage background refresh', () => {
  it('keeps the rows on screen when a re-read fails, and says how old they are', async () => {
    getInbox.mockResolvedValueOnce([row]).mockRejectedValue(badGateway);
    const client = mount();

    expect(await screen.findByText('Gulf Cable Trading')).toBeInTheDocument();

    await act(async () => {
      await client.refetchQueries({ queryKey: ['sourcing-today'] });
    });

    expect(getInbox).toHaveBeenCalledTimes(2);
    // TanStack notifies components on a timer after the refetch settles, so wait for the notice.
    expect(await screen.findByRole('status')).toHaveTextContent(/Couldn't refresh just now — showing what was loaded at \d{2}:\d{2}/);
    expect(screen.getByText('Gulf Cable Trading')).toBeInTheDocument();
    expect(screen.queryByText(/This persisted view could not be loaded/)).not.toBeInTheDocument();
  });

  it('still says the view could not be loaded when it never loaded (the control)', async () => {
    getInbox.mockRejectedValue(badGateway);
    mount();

    expect(await screen.findByText(/This persisted view could not be loaded/)).toBeInTheDocument();
    expect(screen.queryByText(/Couldn't refresh just now/)).not.toBeInTheDocument();
  });
});
