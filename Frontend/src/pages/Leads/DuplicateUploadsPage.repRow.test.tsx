import { fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { DuplicateUploadDTO } from '../../api/services/leadService';

/**
 * Duplicate Uploads was an engineering console: "Occurrence #2", "Batch 9c884301-…", "Hash 0 ms",
 * "Physical 0.0 KB | Logical 6.8 KB", "Actual 0.000000 | External 0.000000", "Parser yes | OCR yes",
 * and a timestamp shaped "9/15/2026, 11:19:22 PM" beside lists that say "15 Sep 2026". A rep needs
 * one sentence — which inquiry this copies and who has it — and one button that opens it.
 */

const getDuplicateUploads = vi.fn();
const getById = vi.fn();
const navigate = vi.fn();

vi.mock('../../api/services/leadService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/leadService')>();
  return {
    ...actual,
    default: {
      ...actual.default,
      getDuplicateUploads: () => getDuplicateUploads(),
      getById: (id: number) => getById(id),
      retryBlockedFiles: vi.fn(),
    },
  };
});
vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => navigate };
});

import DuplicateUploadsPage, { sameAsSentence } from './DuplicateUploadsPage';

const copyOfLead12: DuplicateUploadDTO = {
  occurrenceId: 2,
  fileName: 'Aramco RFQ 7781.pdf',
  uploadBatch: '9c884301-0000-4000-8000-000000000000',
  ingestedAt: '2026-09-15T20:19:22Z',
  uploadedBy: 'zahid',
  source: 'Manual upload',
  duplicateType: 'EXACT_DUPLICATE_CONFIRMED',
  originalOccurrenceId: 1,
  canonicalLeadId: 12,
  nexoraSerial: 'NOOR-SONS-LLC-2026-000012',
  securityStatus: 'Cleared',
  processingReused: true,
  resources: {
    bytesUploaded: 6963,
    hashingDurationMs: 4,
    storagePhysicalBytes: 0,
    storageLogicalBytes: 6963,
    malwareScanReused: true,
    malwareScanRerun: false,
    parserReused: true,
    ocrReused: true,
    localModelReused: true,
    externalModelReused: true,
    localComputeCost: 0,
    externalCost: 0,
    totalActualCost: 0,
    estimatedProcessingAvoided: 0,
    costStatus: 'LOCAL_COMPUTE_UNPRICED',
  },
  actions: [],
};

const stillHeld: DuplicateUploadDTO = {
  ...copyOfLead12,
  occurrenceId: 3,
  fileName: 'Marafiq bid.xlsx',
  duplicateType: 'EXACT_DUPLICATE_PENDING_SECURITY',
  canonicalLeadId: null,
  nexoraSerial: null,
  securityStatus: 'Pending',
  processingReused: false,
};

const mount = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <DuplicateUploadsPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
};

beforeEach(() => {
  vi.clearAllMocks();
  getDuplicateUploads.mockResolvedValue([copyOfLead12, stillHeld]);
  getById.mockResolvedValue({ id: 12, rfqno: 'RFQ-7781', customerName: 'Saudi Aramco', assignedToFullName: 'Sara Bin Ali' });
});

describe('Duplicate Uploads — a row a rep can read', () => {
  it('says which inquiry the file copies and who owns it, with one button that opens it', async () => {
    mount();
    const row = (await screen.findByText('Same as RFQ-7781 · Saudi Aramco · owned by Sara Bin Ali')).closest('tr')!;
    expect(getById).toHaveBeenCalledWith(12);

    fireEvent.click(within(row).getByRole('button', { name: 'Open the original' }));
    expect(navigate).toHaveBeenCalledWith('/procurement/leads/view/12');
  });

  it('prints the upload time the way every other list prints a date', async () => {
    mount();
    await screen.findByText('Same as RFQ-7781 · Saudi Aramco · owned by Sara Bin Ali');
    // en-GB abbreviates September as "Sept", exactly as formatDateSafe does on every other list.
    expect(screen.getAllByText(/^\d{2} Sept? 2026, \d{2}:\d{2}$/).length).toBeGreaterThan(0);
    expect(screen.queryByText(/\d\/\d{1,2}\/2026/)).not.toBeInTheDocument();
    expect(screen.queryByText(/PM|AM/)).not.toBeInTheDocument();
  });

  it('keeps the engineering figures, but behind Details', async () => {
    mount();
    const row = (await screen.findByText('Same as RFQ-7781 · Saudi Aramco · owned by Sara Bin Ali')).closest('tr')!;

    // Not on the row a rep scans…
    expect(screen.queryByText(/Occurrence/)).not.toBeInTheDocument();
    expect(screen.queryByText(/hash 4 ms/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/9c884301/)).not.toBeInTheDocument();
    expect(screen.queryByText(/0\.000000/)).not.toBeInTheDocument();
    expect(screen.queryByText(/parser yes/i)).not.toBeInTheDocument();

    // …but one click away, verbatim, for the person who does read them.
    fireEvent.click(within(row).getByRole('button', { name: /details for Aramco RFQ 7781\.pdf/i }));
    expect(await screen.findByText('#2')).toBeInTheDocument();
    expect(screen.getByText(/hash 4 ms/i)).toBeInTheDocument();
    expect(screen.getByText(/9c884301/)).toBeInTheDocument();
    expect(screen.getByText(/Physical 0\.0 KB · logical 6\.8 KB/)).toBeInTheDocument();
    expect(screen.getByText(/Actual 0\.000000 · external 0\.000000/)).toBeInTheDocument();
    expect(screen.getByText(/parser yes · OCR yes · local model yes · external yes/i)).toBeInTheDocument();
  });

  it('says plainly when the original is still being processed and offers the scan retry instead', async () => {
    getDuplicateUploads.mockResolvedValue([{ ...stillHeld, actions: ['Retry security scan'] }]);
    mount();
    expect(await screen.findByText('Original still being processed')).toBeInTheDocument();
    expect(screen.getByText('Same file, scan still running')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Open the original' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Retry scan' })).toBeEnabled();
    expect(getById).not.toHaveBeenCalled();
  });
});

describe('sameAsSentence', () => {
  it('names only what is known while the original is still loading', () => {
    expect(sameAsSentence({ canonicalLeadId: 12, nexoraSerial: 'NOOR-2026-000012' }, undefined, false))
      .toBe('Same as NOOR-2026-000012');
  });

  it('states the gaps as gaps once the original has been read', () => {
    expect(sameAsSentence({ canonicalLeadId: 12, nexoraSerial: 'NOOR-2026-000012' }, { id: 12, rfqno: '', customerMatchStatus: 'UNRESOLVED' } as never, true))
      .toBe('Same as NOOR-2026-000012 · customer not yet known · owned by nobody yet');
  });
});
