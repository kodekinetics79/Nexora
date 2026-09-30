import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const snack = vi.fn();
vi.mock('notistack', () => ({ useSnackbar: () => ({ enqueueSnackbar: snack }) }));

const api = { getById: vi.fn(), confirmClosingDate: vi.fn() };
vi.mock('../../../api/services/leadService', () => ({
  default: {
    getById: (...args: unknown[]) => api.getById(...args),
    confirmClosingDate: (...args: unknown[]) => api.confirmClosingDate(...args),
  },
}));
// The question reads Decide's small owner read, not the full lead record (PERF-02).
vi.mock('../../../api/services/leadDecisionService', () => ({
  default: { getOwner: (...args: unknown[]) => api.getById(...args) },
}));

import ClosingDateQuestion from './ClosingDateQuestion';

/**
 * Pilot audit CP-02: "the ambiguity flag never reaches the rep". Decide showed "09 Aug 2026 · Closed
 * 50 days ago" for an SEC tender that closed on 8 September; the guess was written only to a
 * remarks column the rep never sees.
 */
const asking = {
  id: 17,
  bidClosingDate: '2026-08-09T17:00:00',
  closingDateQuestion: { documentText: '9/8/2026 5:00 PM', currentReading: '2026-08-09T17:00:00', otherReading: '2026-09-08T17:00:00' },
};

const renderQuestion = (canEdit = true) => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>
    <ClosingDateQuestion leadId={17} canEdit={canEdit} />
  </QueryClientProvider>,
);

describe('ClosingDateQuestion', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    api.getById.mockResolvedValue(asking);
    const answered = { ...asking, bidClosingDate: '2026-09-08T17:00:00', closingDateQuestion: null };
    api.confirmClosingDate.mockImplementation(async () => {
      api.getById.mockResolvedValue(answered);
      return answered;
    });
  });

  it('asks "Closes 9 Aug or 8 Sep?" with the document\'s own words, and one press answers it', async () => {
    renderQuestion();
    const region = await screen.findByRole('region', { name: 'Closing date question' });
    expect(within(region).getByText('Closes 9 Aug or 8 Sep?')).toBeInTheDocument();
    expect(within(region).getByText('The document says 9/8/2026 5:00 PM.')).toBeInTheDocument();

    fireEvent.click(within(region).getByRole('button', { name: '8 Sep' }));
    await waitFor(() => expect(api.confirmClosingDate).toHaveBeenCalledWith(17, '2026-09-08T17:00:00'));
    expect(snack).toHaveBeenCalledWith('Closing date set: 8 Sep 2026, 5:00 PM', { variant: 'success' });
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Closing date question' })).not.toBeInTheDocument());
  });

  it('shows nothing when the document left no doubt', async () => {
    api.getById.mockResolvedValue({ id: 17, bidClosingDate: '2026-09-06T17:00:00', closingDateQuestion: null });
    const { container } = renderQuestion();
    await waitFor(() => expect(api.getById).toHaveBeenCalled());
    expect(container).toBeEmptyDOMElement();
  });

  it('shows the question without answers to someone who cannot edit the lead', async () => {
    renderQuestion(false);
    const region = await screen.findByRole('region', { name: 'Closing date question' });
    expect(within(region).getByRole('button', { name: '8 Sep' })).toBeDisabled();
  });
});
