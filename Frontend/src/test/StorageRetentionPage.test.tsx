import { describe, expect, it, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import StorageRetentionPage, {
  describeExclusionReason, formatBytes,
} from '../pages/PlatformGovernance/StorageRetentionPage';
import {
  readEvidenceRetentionRun, readEvidenceRetentionSummary,
} from '../api/services/platformGovernanceService';

/**
 * The behaviours locked down here are the ones that make an IRREVERSIBLE control safe:
 *  - the delete button cannot be reached without a dry run first;
 *  - the confirmation states the count, the byte total, and that lineage survives;
 *  - the screen never claims the purge erases personal data;
 *  - a field the backend omitted renders as "Not reported", never as 0.
 */

const getEvidenceRetention = vi.fn();
const updateEvidenceRetentionPolicy = vi.fn();
const runEvidenceRetentionPurge = vi.fn();

vi.mock('../api/services/platformGovernanceService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/services/platformGovernanceService')>();
  return {
    ...actual,
    platformGovernanceService: {
      getEvidenceRetention: () => getEvidenceRetention(),
      updateEvidenceRetentionPolicy: (command: unknown) => updateEvidenceRetentionPolicy(command),
      runEvidenceRetentionPurge: (command: unknown) => runEvidenceRetentionPurge(command),
    },
  };
});

const storageFigures = {
  usedBytes: 268_435_456,
  reclaimableBytes: 104_857_600,
  documentCount: 90,
  purgedCount: 0,
  reclaimableDocumentCount: 47,
};

/** A tenant that has opted in — the only state in which a real purge is permitted. */
const SUMMARY = readEvidenceRetentionSummary({
  policy: {
    retentionDays: 90, isEnabled: true, minimumRetentionDays: 30,
    maximumRetentionDays: 3650, version: 3,
  },
  storage: storageFigures,
});

/** The default state of a fresh tenant: irreversible deletion is never on out of the box. */
const SUMMARY_OPT_OUT = readEvidenceRetentionSummary({
  policy: {
    retentionDays: 90, isEnabled: false, minimumRetentionDays: 30,
    maximumRetentionDays: 3650, version: 1,
  },
  storage: storageFigures,
});

const DISCLOSURE = 'Dry run: 47 document(s) would be purged, freeing 1,932,735,283 bytes. '
  + 'Nothing was deleted. This does not erase personal data.';

const DRY_RUN = readEvidenceRetentionRun({
  dryRun: true,
  scanned: 90,
  eligible: 47,
  purged: 47,
  bytesReclaimed: 1_932_735_283,
  legacyCopiesDeleted: 0,
  legacyCopiesUnresolved: 0,
  disclosure: DISCLOSURE,
  idempotentReplay: false,
  skipped: [
    { documentId: 412, fileName: 'INV-2211.pdf', reason: 'STATUTORY_RETENTION' },
    { documentId: 415, fileName: 'RFQ-8841.pdf', reason: 'The document is under legal hold.' },
  ],
}, true);

const renderPage = () => {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <StorageRetentionPage />
    </QueryClientProvider>,
  );
};

/** Runs the preview and waits for its result to land. */
const runPreview = async () => {
  fireEvent.click(screen.getByRole('button', { name: /preview what would be deleted/i }));
  await screen.findByText(/preview — nothing has been deleted/i);
};

beforeEach(() => {
  vi.clearAllMocks();
  getEvidenceRetention.mockResolvedValue(SUMMARY);
  runEvidenceRetentionPurge.mockResolvedValue(DRY_RUN);
  updateEvidenceRetentionPolicy.mockResolvedValue(SUMMARY);
});

describe('formatBytes', () => {
  it('never renders an absent figure as zero', () => {
    expect(formatBytes(null)).toBe('Not reported');
    expect(formatBytes(undefined)).toBe('Not reported');
    expect(formatBytes(0)).toBe('0 B');
  });

  it('scales to human units', () => {
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(1024)).toBe('1.0 KB');
    expect(formatBytes(268_435_456)).toBe('256 MB');
    expect(formatBytes(1_932_735_283)).toBe('1.8 GB');
  });
});

describe('describeExclusionReason', () => {
  it('translates known eligibility codes into tenant language', () => {
    expect(describeExclusionReason('LEGAL_HOLD')).toMatch(/legal hold/i);
    expect(describeExclusionReason('STATUTORY_RETENTION')).toMatch(/invoice/i);
  });

  it('makes an unknown code readable rather than dropping it', () => {
    expect(describeExclusionReason('SOME_NEW_RULE')).toBe('Some new rule.');
  });

  it('suppresses operator diagnostics instead of printing them', () => {
    expect(describeExclusionReason('Npgsql.PostgresException at Foo.Bar')).toMatch(/cannot be shown/i);
  });

  it('says so when no reason was supplied at all', () => {
    expect(describeExclusionReason(null)).toMatch(/did not name/i);
  });
});

describe('StorageRetentionPage', () => {
  it('shows storage in plain language', async () => {
    renderPage();
    expect(await screen.findByText('256 MB')).toBeInTheDocument();
    expect(screen.getByText('100 MB')).toBeInTheDocument();
    expect(screen.getByText('Stored files')).toBeInTheDocument();
    expect(screen.getByText('Reclaimable now')).toBeInTheDocument();
  });

  it('states what is kept and refuses to claim personal data is erased', async () => {
    renderPage();
    await screen.findByText('256 MB');
    expect(screen.getAllByText(/this does not erase personal data/i).length).toBeGreaterThan(0);
    expect(screen.getByText(/SHA-256 fingerprint/i)).toBeInTheDocument();
    expect(screen.getByText(/Nexora keeps no backup of these files/i)).toBeInTheDocument();
  });

  it('defaults the policy to 90 days and keeps automatic deletion opt-in', async () => {
    getEvidenceRetention.mockResolvedValue(SUMMARY_OPT_OUT);
    renderPage();
    const days = await screen.findByLabelText(/keep original files for/i);
    expect(days).toHaveValue(90);
    expect(screen.getByRole('checkbox', { name: /delete stored files automatically/i })).not.toBeChecked();
  });

  it('refuses permanent deletion until the tenant has opted in', async () => {
    getEvidenceRetention.mockResolvedValue(SUMMARY_OPT_OUT);
    renderPage();
    await screen.findByRole('button', { name: /preview what would be deleted/i });
    fireEvent.change(screen.getByLabelText(/reason for reclaiming space/i), {
      target: { value: 'Quarterly storage reclaim' },
    });
    await runPreview();

    // A preview is always allowed; deleting is not, until the saved policy says so.
    expect(screen.getByRole('button', { name: /delete stored files permanently/i })).toBeDisabled();
    expect(screen.getByText(/permanent deletion is opt-in/i)).toBeInTheDocument();
  });

  it('renders the server disclosure verbatim rather than restating it', async () => {
    renderPage();
    await screen.findByRole('button', { name: /preview what would be deleted/i });
    await runPreview();
    expect(screen.getByText(DISCLOSURE)).toBeInTheDocument();
  });

  it('reports legacy copies it could not safely match instead of hiding them', async () => {
    renderPage();
    await screen.findByRole('button', { name: /preview what would be deleted/i });
    fireEvent.change(screen.getByLabelText(/reason for reclaiming space/i), {
      target: { value: 'Quarterly storage reclaim' },
    });
    await runPreview();
    fireEvent.click(screen.getByRole('button', { name: /delete stored files permanently/i }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText(/type delete to confirm/i), { target: { value: 'DELETE' } });

    runEvidenceRetentionPurge.mockResolvedValueOnce(readEvidenceRetentionRun({
      dryRun: false, purged: 47, bytesReclaimed: 1_932_735_283,
      legacyCopiesDeleted: 12, legacyCopiesUnresolved: 3, skipped: [],
    }, false));
    fireEvent.click(within(dialog).getByRole('button', { name: /delete 47 documents/i }));

    expect(await screen.findByText(/could not be matched with certainty/i)).toBeInTheDocument();
    expect(screen.getByText(/12 older duplicate copies also removed/i)).toBeInTheDocument();
  });

  it('says nothing further was deleted when the server replays an earlier run', async () => {
    renderPage();
    await screen.findByRole('button', { name: /preview what would be deleted/i });
    fireEvent.change(screen.getByLabelText(/reason for reclaiming space/i), {
      target: { value: 'Quarterly storage reclaim' },
    });
    await runPreview();
    fireEvent.click(screen.getByRole('button', { name: /delete stored files permanently/i }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText(/type delete to confirm/i), { target: { value: 'DELETE' } });

    runEvidenceRetentionPurge.mockResolvedValueOnce(readEvidenceRetentionRun({
      dryRun: false, purged: 47, bytesReclaimed: 1_932_735_283, idempotentReplay: true, skipped: [],
    }, false));
    fireEvent.click(within(dialog).getByRole('button', { name: /delete 47 documents/i }));

    expect(await screen.findByText(/already been carried out, so nothing further was deleted/i)).toBeInTheDocument();
  });

  it('rejects a retention period below the 30-day floor', async () => {
    renderPage();
    const days = await screen.findByLabelText(/keep original files for/i);
    fireEvent.change(days, { target: { value: '5' } });
    fireEvent.change(screen.getByLabelText(/reason for this change/i), { target: { value: 'Shorter window' } });
    expect(await screen.findByText(/between 30 and 3650/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /save retention policy/i })).toBeDisabled();
  });

  it('keeps permanent deletion disabled until a preview has been run', async () => {
    renderPage();
    const deleteButton = await screen.findByRole('button', { name: /delete stored files permanently/i });
    expect(deleteButton).toBeDisabled();

    fireEvent.change(screen.getByLabelText(/reason for reclaiming space/i), {
      target: { value: 'Quarterly storage reclaim' },
    });
    // A reason alone is not enough — the estimate must exist first.
    expect(deleteButton).toBeDisabled();

    await runPreview();
    expect(deleteButton).toBeEnabled();
    expect(runEvidenceRetentionPurge).toHaveBeenCalledWith(expect.objectContaining({ dryRun: true }));
  });

  it('shows the excluded documents and why each is held back', async () => {
    renderPage();
    await screen.findByRole('button', { name: /preview what would be deleted/i });
    await runPreview();

    expect(screen.getByText(/47 documents would be deleted/i)).toBeInTheDocument();
    expect(screen.getByText(/1.8 GB would be freed/i)).toBeInTheDocument();
    const table = screen.getByRole('table', { name: /excluded from this purge/i });
    expect(within(table).getByText('INV-2211.pdf')).toBeInTheDocument();
    expect(within(table).getByText(/invoice, purchase order or contract/i)).toBeInTheDocument();
    expect(within(table).getByText(/under legal hold/i)).toBeInTheDocument();
  });

  it('requires the typed confirmation phrase before deleting', async () => {
    renderPage();
    await screen.findByRole('button', { name: /preview what would be deleted/i });
    fireEvent.change(screen.getByLabelText(/reason for reclaiming space/i), {
      target: { value: 'Quarterly storage reclaim' },
    });
    await runPreview();
    fireEvent.click(screen.getByRole('button', { name: /delete stored files permanently/i }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/this cannot be undone/i)).toBeInTheDocument();
    expect(within(dialog).getByText(/what is kept forever/i)).toBeInTheDocument();
    expect(within(dialog).getByText(/this does not erase personal data/i)).toBeInTheDocument();

    const confirmButton = within(dialog).getByRole('button', { name: /delete 47 documents/i });
    expect(confirmButton).toBeDisabled();

    fireEvent.change(within(dialog).getByLabelText(/type delete to confirm/i), { target: { value: 'delete' } });
    expect(confirmButton).toBeDisabled();

    fireEvent.change(within(dialog).getByLabelText(/type delete to confirm/i), { target: { value: 'DELETE' } });
    expect(confirmButton).toBeEnabled();

    runEvidenceRetentionPurge.mockResolvedValueOnce(readEvidenceRetentionRun({
      dryRun: false, scanned: 90, eligible: 47, purged: 47, bytesReclaimed: 1_932_735_283, skipped: [],
    }, false));
    fireEvent.click(confirmButton);

    await waitFor(() => expect(runEvidenceRetentionPurge).toHaveBeenLastCalledWith(
      expect.objectContaining({ dryRun: false, reason: 'Quarterly storage reclaim' }),
    ));
    expect(await screen.findByText(/stored files deleted/i)).toBeInTheDocument();
  });

  it('reuses one idempotency key across retries of the same confirmed purge', async () => {
    renderPage();
    await screen.findByRole('button', { name: /preview what would be deleted/i });
    fireEvent.change(screen.getByLabelText(/reason for reclaiming space/i), {
      target: { value: 'Quarterly storage reclaim' },
    });
    await runPreview();
    fireEvent.click(screen.getByRole('button', { name: /delete stored files permanently/i }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText(/type delete to confirm/i), { target: { value: 'DELETE' } });

    runEvidenceRetentionPurge.mockRejectedValueOnce({ response: { status: 502 } });
    fireEvent.click(within(dialog).getByRole('button', { name: /delete 47 documents/i }));
    await within(dialog).findByText(/temporarily unavailable|did not complete/i);
    const firstKey = runEvidenceRetentionPurge.mock.calls.at(-1)?.[0].idempotencyKey;

    runEvidenceRetentionPurge.mockResolvedValueOnce(readEvidenceRetentionRun({
      dryRun: false, purged: 47, bytesReclaimed: 1_932_735_283, skipped: [],
    }, false));
    fireEvent.click(within(dialog).getByRole('button', { name: /delete 47 documents/i }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    expect(runEvidenceRetentionPurge.mock.calls.at(-1)?.[0].idempotencyKey).toBe(firstKey);
  });

  it('discards a stale preview when the policy is saved', async () => {
    renderPage();
    await screen.findByRole('button', { name: /preview what would be deleted/i });
    fireEvent.change(screen.getByLabelText(/reason for reclaiming space/i), {
      target: { value: 'Quarterly storage reclaim' },
    });
    await runPreview();
    expect(screen.getByRole('button', { name: /delete stored files permanently/i })).toBeEnabled();

    fireEvent.change(screen.getByLabelText(/reason for this change/i), { target: { value: 'Tighten to 60 days' } });
    fireEvent.click(screen.getByRole('button', { name: /save retention policy/i }));

    await waitFor(() => expect(screen.getByRole('button', { name: /delete stored files permanently/i })).toBeDisabled());
    expect(screen.queryByText(/preview — nothing has been deleted/i)).not.toBeInTheDocument();
  });

  it('says a figure is unknown rather than reporting it as zero', async () => {
    getEvidenceRetention.mockResolvedValue(readEvidenceRetentionSummary({
      policy: { retentionDays: 90, isEnabled: false },
      storage: { documentCount: 90 },
    }));
    renderPage();
    expect(await screen.findAllByText('Not reported')).not.toHaveLength(0);
    expect(screen.getByText(/they are not zero — they are unknown/i)).toBeInTheDocument();
  });

  it('does not claim zero exclusions when the deployment reported none', async () => {
    runEvidenceRetentionPurge.mockResolvedValue(readEvidenceRetentionRun({
      dryRun: true, scanned: 90, eligible: 47, bytesReclaimed: 1024,
    }, true));
    renderPage();
    await screen.findByRole('button', { name: /preview what would be deleted/i });
    await runPreview();
    expect(screen.getByText(/exclusions not reported/i)).toBeInTheDocument();
    expect(screen.getByText(/did not report which documents were excluded/i)).toBeInTheDocument();
  });

  it('degrades to an explanation when the endpoint is not deployed', async () => {
    getEvidenceRetention.mockRejectedValue({ response: { status: 404 } });
    renderPage();
    expect(await screen.findByText(/not available on this deployment yet/i)).toBeInTheDocument();
    expect(screen.getByText(/nothing is being deleted, and nothing is at risk/i)).toBeInTheDocument();
  });

  it('offers a retry on a genuine load failure', async () => {
    getEvidenceRetention.mockRejectedValue({ response: { status: 503 } });
    renderPage();
    expect(await screen.findByRole('button', { name: /retry/i })).toBeInTheDocument();
  });
});
