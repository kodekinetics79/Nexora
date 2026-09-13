import { act, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { DuplicateUploadDTO } from '../../api/services/leadService';

/**
 * Duplicate uploads re-reads every 5 s while a copy waits for its security scan. Refresh greyed out
 * on every one of those polls, and a single failed poll replaced the whole table with an error.
 */

const getDuplicateUploads = vi.fn();
vi.mock('../../api/services/leadService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/leadService')>();
  return {
    ...actual,
    default: {
      ...actual.default,
      getDuplicateUploads: () => getDuplicateUploads(),
      retryBlockedFiles: vi.fn(),
    },
  };
});

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));

import DuplicateUploadsPage from './DuplicateUploadsPage';

const row: DuplicateUploadDTO = {
  occurrenceId: 88,
  fileName: 'Aramco RFQ 7781.pdf',
  uploadBatch: 'batch-88',
  ingestedAt: '2026-09-12T09:00:00Z',
  uploadedBy: 'zahid',
  source: 'Manual upload',
  duplicateType: 'EXACT_DUPLICATE_PENDING_SECURITY',
  originalOccurrenceId: 12,
  canonicalLeadId: null,
  nexoraSerial: null,
  securityStatus: 'Pending',
  processingReused: false,
  resources: {
    bytesUploaded: 2048,
    hashingDurationMs: 4,
    storagePhysicalBytes: 0,
    storageLogicalBytes: 2048,
    malwareScanReused: false,
    malwareScanRerun: false,
    parserReused: false,
    ocrReused: false,
    localModelReused: false,
    externalModelReused: false,
    localComputeCost: 0,
    externalCost: 0,
    totalActualCost: 0,
    estimatedProcessingAvoided: 0,
    costStatus: 'LOCAL_COMPUTE_UNPRICED',
  },
  actions: [],
};

/**
 * TanStack Query tells components about a fetch starting or settling on a timer. Without this flush
 * an assertion about the in-flight state runs before the page has seen it, and passes either way.
 */
const flushQueryNotifications = () => act(async () => {
  await new Promise((resolve) => setTimeout(resolve, 20));
});

const mount = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <DuplicateUploadsPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return client;
};

describe('DuplicateUploadsPage background refresh', () => {
  beforeEach(() => {
    getDuplicateUploads.mockReset();
  });

  it('leaves Refresh usable while an automatic re-read is in flight', async () => {
    getDuplicateUploads.mockResolvedValueOnce([row]).mockReturnValue(new Promise(() => {}));
    const client = mount();
    expect(await screen.findByText('Aramco RFQ 7781.pdf')).toBeInTheDocument();

    act(() => {
      void client.refetchQueries({ queryKey: ['duplicate-uploads'] });
    });
    await flushQueryNotifications();
    expect(client.isFetching({ queryKey: ['duplicate-uploads'] })).toBeGreaterThan(0);

    expect(getDuplicateUploads).toHaveBeenCalledTimes(2);
    const refresh = screen.getByRole('button', { name: 'Refresh' });
    expect(refresh).toBeEnabled();
    expect(within(refresh).queryByRole('progressbar')).not.toBeInTheDocument();
  });

  it('keeps the table when a re-read fails, and says so', async () => {
    getDuplicateUploads.mockResolvedValueOnce([row]).mockRejectedValue({
      isAxiosError: true,
      message: 'Request failed with status code 502',
      response: { status: 502, data: {} },
    });
    const client = mount();
    expect(await screen.findByText('Aramco RFQ 7781.pdf')).toBeInTheDocument();

    await act(async () => {
      await client.refetchQueries({ queryKey: ['duplicate-uploads'] });
    });

    expect(await screen.findByText(/Couldn't refresh just now/)).toBeInTheDocument();
    expect(screen.getByText('Aramco RFQ 7781.pdf')).toBeInTheDocument();
    expect(screen.queryByText(/Duplicate uploads could not be loaded/)).not.toBeInTheDocument();
  });
});
