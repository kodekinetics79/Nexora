import { act, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { BatchReconciliationDTO, BatchReconciliationItemDTO } from '../../api/services/leadService';

/**
 * The upload results page re-reads every 2 s while documents are read. Two things made it look as
 * if it kept reloading itself: the Refresh button spun and greyed out on every automatic poll, and
 * one failed poll during a backend deploy swapped the whole page for an error and back.
 */

const getIngestionBatch = vi.fn();
vi.mock('../../api/services/leadService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/leadService')>();
  return {
    ...actual,
    default: {
      ...actual.default,
      getIngestionBatch: (id: string) => getIngestionBatch(id),
      retryBlockedFiles: vi.fn(),
    },
  };
});

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));

import LeadIngestionBatchPage from './LeadIngestionBatchPage';

const item = (overrides: Partial<BatchReconciliationItemDTO>): BatchReconciliationItemDTO => ({
  occurrenceId: 1, leadId: null, classification: 'Pending', fileName: 'SE RFP-C001835789.doc', ingestedAtUtc: '2026-09-12T10:00:00Z',
  processingPath: 'Deterministic', externalAiUsed: false, confidence: 0, reasons: [], matchCandidates: [],
  customerResolutionStatus: 'Awaiting customer resolution', ...overrides,
});
const batch = (items: BatchReconciliationItemDTO[]): BatchReconciliationDTO => ({
  batchId: 'b1', filesReceived: items.length, logicalInquiries: 0, newLeads: 0, exactDuplicates: 0, revisions: 0, possibleMatches: 0, rejected: 0,
  externalOccurrences: 0, awaitingSecurityScan: 0, localFirstOccurrences: 0, items,
});

const reading = batch([item({ securityStatus: 'Cleared', extractionStatus: 'Extracting' })]);
const settled = batch([item({ securityStatus: 'Cleared', extractionStatus: 'Succeeded', classification: 'New', leadId: 42, customerResolutionStatus: 'AUTO_MATCHED' })]);

const mount = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/procurement/leads/ingestion/b1']}>
        <Routes>
          <Route path="/procurement/leads/ingestion/:batchId" element={<LeadIngestionBatchPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return client;
};

describe('LeadIngestionBatchPage background refresh', () => {
  beforeEach(() => {
    getIngestionBatch.mockReset();
    vi.useFakeTimers({ shouldAdvanceTime: true });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('does not show the Refresh button as busy during an automatic poll', async () => {
    getIngestionBatch.mockResolvedValueOnce(reading).mockReturnValue(new Promise(() => {}));
    mount();

    expect(await screen.findByRole('heading', { name: 'Batch reconciliation' })).toBeInTheDocument();
    await act(async () => { await vi.advanceTimersByTimeAsync(2_100); });

    // The 2 s poll is in flight right now.
    expect(getIngestionBatch).toHaveBeenCalledTimes(2);
    const refresh = screen.getByRole('button', { name: 'Refresh' });
    expect(refresh).toBeEnabled();
    expect(within(refresh).queryByRole('progressbar')).not.toBeInTheDocument();
  });

  it('keeps the documents on screen when a poll fails, and says so', async () => {
    getIngestionBatch.mockResolvedValueOnce(settled).mockRejectedValue({
      isAxiosError: true,
      message: 'Request failed with status code 502',
      response: { status: 502, data: {} },
    });
    const client = mount();

    expect(await screen.findByRole('heading', { name: 'Batch reconciliation' })).toBeInTheDocument();

    await act(async () => {
      const refetch = client.refetchQueries({ queryKey: ['lead-ingestion-batch', 'b1'] });
      // The page retries a 5xx twice (1 s, then 2 s) before it reports the failure.
      await vi.advanceTimersByTimeAsync(4_000);
      await refetch;
    });

    expect(getIngestionBatch).toHaveBeenCalledTimes(4);
    expect(screen.getByRole('heading', { name: 'Batch reconciliation' })).toBeInTheDocument();
    expect(screen.getByText(/Couldn't refresh just now — showing what was loaded at \d{2}:\d{2}/)).toBeInTheDocument();
    expect(screen.queryByText(/reconciliation service is not answering/i)).not.toBeInTheDocument();
  });
});
