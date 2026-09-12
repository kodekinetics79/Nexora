import { describe, expect, it, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import StorageRetentionPage, {
  describeExclusionReason, formatBytes,
} from '../pages/PlatformGovernance/StorageRetentionPage';
import {
  TENANT_DATA_BUCKETS,
  readEvidenceRetentionRun, readEvidenceRetentionSummary,
  readTenantDataCleanup, readTenantDataControl,
} from '../api/services/platformGovernanceService';

/**
 * The behaviours locked down here are the ones that make an IRREVERSIBLE control safe:
 *  - the delete button cannot be reached without a dry run first;
 *  - the confirmation states the count, the byte total, and that lineage survives;
 *  - a SECOND confirmation asks for the number being deleted and sends it to the server;
 *  - no standing "policy switch" has to be on first — the tenant is not made to opt into
 *    automatic deletion in order to delete what he has just previewed;
 *  - the screen says the next step itself, and says "unknown" rather than zero;
 *  - the screen never claims the purge erases personal data;
 *  - a field the backend omitted renders as "Not reported", never as 0.
 */

const getEvidenceRetention = vi.fn();
const updateEvidenceRetentionPolicy = vi.fn();
const runEvidenceRetentionPurge = vi.fn();
const getTenantDataControl = vi.fn();
const runTenantDataCleanup = vi.fn();
const authUser: { isSuperAdmin: boolean } = { isSuperAdmin: true };

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({ userData: authUser }),
}));

vi.mock('../api/services/platformGovernanceService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/services/platformGovernanceService')>();
  return {
    ...actual,
    platformGovernanceService: {
      getEvidenceRetention: () => getEvidenceRetention(),
      updateEvidenceRetentionPolicy: (command: unknown) => updateEvidenceRetentionPolicy(command),
      runEvidenceRetentionPurge: (command: unknown) => runEvidenceRetentionPurge(command),
      getTenantDataControl: () => getTenantDataControl(),
      runTenantDataCleanup: (command: unknown) => runTenantDataCleanup(command),
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

const SUMMARY = readEvidenceRetentionSummary({
  policy: {
    retentionDays: 90, isEnabled: true, minimumRetentionDays: 1,
    maximumRetentionDays: 3650, version: 3,
  },
  storage: storageFigures,
});

/** The default state of a fresh tenant: no standing rule saved. Deleting must still work. */
const SUMMARY_OPT_OUT = readEvidenceRetentionSummary({
  policy: {
    retentionDays: 90, isEnabled: false, minimumRetentionDays: 1,
    maximumRetentionDays: 3650, version: 1,
  },
  storage: storageFigures,
});

/** Nothing reclaimable by age, so the next step depends on the clear-out buckets alone. */
const SUMMARY_NOTHING_BY_AGE = readEvidenceRetentionSummary({
  policy: {
    retentionDays: 90, isEnabled: false, minimumRetentionDays: 1,
    maximumRetentionDays: 3650, version: 1,
  },
  storage: { ...storageFigures, reclaimableBytes: 0, reclaimableDocumentCount: 0 },
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
  previewToken: 'server-signed-preview-token',
  previewExpiresOn: '2026-08-26T23:59:00Z',
  skipped: [
    { documentId: 412, fileName: 'INV-2211.pdf', reason: 'STATUTORY_RETENTION' },
    { documentId: 415, fileName: 'RFQ-8841.pdf', reason: 'The document is under legal hold.' },
  ],
}, true);


/* ── "Clear out what produced nothing" ─────────────────────────────────────────────────────────
 * The server sends finished copy, so these fixtures carry the sentences a business owner reads.
 * Every assertion below is about what he sees, never about a code.
 * ─────────────────────────────────────────────────────────────────────────────────────────── */

const TENANT_DATA = readTenantDataControl({
  buckets: [
    {
      code: TENANT_DATA_BUCKETS.mailProducedNothing,
      title: 'Mail that never became an inquiry',
      detail: 'Messages we received, read and filed — and nothing came of them.',
      count: 48, bytes: 3_355_443, canClear: true, blockedReason: null,
    },
    {
      code: TENANT_DATA_BUCKETS.mailNoise,
      title: 'Mail we identified as not being business',
      detail: 'Out-of-office replies, mailing lists and no-reply senders.',
      count: 28, bytes: 1_048_576, canClear: true, blockedReason: null,
    },
    {
      code: TENANT_DATA_BUCKETS.orphanedFiles,
      title: 'Stored files nothing points to any more',
      detail: 'Leftover copies in your storage that no record refers to.',
      count: 170, bytes: 11_744_051, canClear: true, blockedReason: null,
    },
  ],
  kept: [
    {
      title: 'Invoices, purchase orders, customer orders, delivery notes and supplier confirmations',
      detail: 'Tax and commercial law require these to be kept for years.',
      count: 12,
    },
    { title: 'Anything you have put on legal hold', detail: 'Release the hold first.', count: 3 },
    {
      title: 'Invoices you have already issued to a customer',
      detail: 'Once an invoice is issued it is fixed.', count: 5,
    },
    {
      title: 'Anything already posted to your accounts',
      detail: 'Payments that reached your books are permanent.', count: 2,
    },
    { title: 'Files quarantined by the virus scanner', detail: 'The copy IS the evidence.', count: 0 },
  ],
  keptSummary: '22 document(s) are protected and will not be deleted by anything on this page.',
});

/** Every bucket empty, and each one says so in words rather than offering a dead button. */
const TENANT_DATA_EMPTY = readTenantDataControl({
  buckets: [
    {
      code: TENANT_DATA_BUCKETS.mailProducedNothing,
      title: 'Mail that never became an inquiry',
      detail: 'Messages we received, read and filed — and nothing came of them.',
      count: 0, bytes: 0, canClear: false, blockedReason: 'There is nothing here to clear.',
    },
    {
      code: TENANT_DATA_BUCKETS.orphanedFiles,
      title: 'Stored files nothing points to any more',
      detail: 'Leftover copies in your storage that no record refers to.',
      count: 0, bytes: 0, canClear: false,
      blockedReason: 'Your storage provider cannot list what it holds, so we cannot prove which '
        + 'files are unused. Nothing will be deleted from storage.',
    },
  ],
  kept: [],
  keptSummary: null,
});

const CLEANUP_PREVIEW = readTenantDataCleanup({
  dryRun: true,
  messagesCleared: 48,
  filesDeleted: 170,
  bytesReclaimed: 15_099_494,
  refused: [],
  summary: 'Preview only — nothing has been deleted.',
  idempotentReplay: false,
}, true);

const CLEANUP_RECEIPT = readTenantDataCleanup({
  dryRun: false,
  messagesCleared: 48,
  filesDeleted: 170,
  bytesReclaimed: 15_099_494,
  refused: [
    {
      what: 'One leftover file',
      why: 'We do not recognise this file\'s name, so we cannot prove nothing is using it.',
      bytes: 4096,
    },
  ],
  summary: '48 stored message(s) and 170 leftover file(s) removed.',
  idempotentReplay: false,
}, false);

/** Ticks a bucket by its human label — the only handle a real user has. */
const tickBucket = async (label: RegExp) => {
  fireEvent.click(await screen.findByRole('checkbox', { name: label }));
};

const previewCleanup = async () => {
  fireEvent.click(screen.getByRole('button', { name: /preview what would be removed/i }));
  await screen.findByText(/preview — nothing has been deleted/i);
};

/**
 * The two confirmations, in order: the fixed word in the first dialog, then the number of items in
 * the second. Returns the second dialog so a test can press its button or read its error.
 */
const confirmTwice = async (phraseDialog: HTMLElement, continueLabel: RegExp, count: number) => {
  fireEvent.change(within(phraseDialog).getByLabelText(/type delete to confirm/i), { target: { value: 'DELETE' } });
  fireEvent.click(within(phraseDialog).getByRole('button', { name: continueLabel }));
  const finalCheck = await screen.findByRole('dialog', { name: /last check before/i });
  fireEvent.change(within(finalCheck).getByLabelText(new RegExp(`type ${count} to confirm`, 'i')), {
    target: { value: String(count) },
  });
  return finalCheck;
};

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
  authUser.isSuperAdmin = true;
  getEvidenceRetention.mockResolvedValue(SUMMARY);
  runEvidenceRetentionPurge.mockResolvedValue(DRY_RUN);
  updateEvidenceRetentionPolicy.mockResolvedValue(SUMMARY);
  getTenantDataControl.mockResolvedValue(TENANT_DATA);
  runTenantDataCleanup.mockResolvedValue(CLEANUP_PREVIEW);
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

  it('defaults the period to 90 days, offers no opt-in switch, and says nothing runs on a schedule', async () => {
    getEvidenceRetention.mockResolvedValue(SUMMARY_OPT_OUT);
    renderPage();
    const days = await screen.findByLabelText(/keep original files for/i);
    expect(days).toHaveValue(90);
    expect(screen.queryByRole('checkbox', { name: /allow permanent deletion/i })).not.toBeInTheDocument();
    expect(screen.getByText(/nothing is deleted on a schedule in this build/i)).toBeInTheDocument();
    expect(screen.getByText(/beyond that the period is yours to set/i)).toBeInTheDocument();
  });

  it('says the next step itself, with the figure it can free and where to start', async () => {
    renderPage();
    const panel = await screen.findByTestId('storage-next-step');
    // Clearable buckets: 3,355,443 + 1,048,576 + 11,744,051 bytes = 15.4 MB.
    expect(await within(panel).findByText(/15\.4 MB can be freed now from mail and files that never became anything/i)).toBeInTheDocument();
    expect(within(panel).getByText(/nothing is deleted until you confirm twice/i)).toBeInTheDocument();
    expect(within(panel).getByRole('button', { name: /take me there/i })).toBeInTheDocument();
  });

  it('points at the age-based deletion when only that can free space', async () => {
    getTenantDataControl.mockResolvedValue(TENANT_DATA_EMPTY);
    renderPage();
    const panel = await screen.findByTestId('storage-next-step');
    expect(await within(panel).findByText(/100 MB can be freed now from 47 documents older than your retention period/i)).toBeInTheDocument();
  });

  it('says nothing can be freed, rather than zero, when every file is in use or protected', async () => {
    getEvidenceRetention.mockResolvedValue(SUMMARY_NOTHING_BY_AGE);
    getTenantDataControl.mockResolvedValue(TENANT_DATA_EMPTY);
    renderPage();
    const panel = await screen.findByTestId('storage-next-step');
    expect(await within(panel).findByText(/nothing can be freed right now/i)).toBeInTheDocument();
  });

  it('says how much can be freed is unknown when the figures failed to load', async () => {
    getEvidenceRetention.mockResolvedValue(readEvidenceRetentionSummary({ policy: { retentionDays: 90 }, storage: {} }));
    getTenantDataControl.mockRejectedValue({ response: { status: 500 } });
    renderPage();
    const panel = await screen.findByTestId('storage-next-step');
    expect(await within(panel).findByText(/unknown right now\. nothing has been deleted/i)).toBeInTheDocument();
  });

  it('is visibly read-only and offers no callable mutations to a non-super-admin', async () => {
    authUser.isSuperAdmin = false;
    renderPage();

    expect(await screen.findByText(/read-only storage view/i)).toBeInTheDocument();
    expect(screen.getByText(/only a tenant super administrator/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /save retention policy/i })).toBeDisabled();
    expect(screen.getByRole('button', { name: /preview what would be deleted/i })).toBeDisabled();
    expect(screen.getByRole('button', { name: /delete stored files permanently/i })).toBeDisabled();

    const cleanup = screen.getByRole('region', { name: /clear out what produced nothing/i });
    for (const checkbox of within(cleanup).getAllByRole('checkbox')) expect(checkbox).toBeDisabled();
    expect(within(cleanup).getByRole('button', { name: /preview what would be removed/i })).toBeDisabled();
    expect(within(cleanup).getByRole('button', { name: /remove them permanently/i })).toBeDisabled();

    expect(updateEvidenceRetentionPolicy).not.toHaveBeenCalled();
    expect(runEvidenceRetentionPurge).not.toHaveBeenCalled();
    expect(runTenantDataCleanup).not.toHaveBeenCalled();
  });

  it('does not make the tenant switch on a standing policy before a confirmed deletion', async () => {
    getEvidenceRetention.mockResolvedValue(SUMMARY_OPT_OUT);
    renderPage();
    await screen.findByRole('button', { name: /preview what would be deleted/i });
    fireEvent.change(screen.getByLabelText(/reason for reclaiming space/i), {
      target: { value: 'Quarterly storage reclaim' },
    });
    await runPreview();

    expect(screen.getByRole('button', { name: /delete stored files permanently/i })).toBeEnabled();
    expect(screen.queryByText(/permanent deletion is opt-in/i)).not.toBeInTheDocument();
    // Saving the period carries the server's own flag over untouched; the screen never sets it.
    fireEvent.change(screen.getByLabelText(/reason for this change/i), { target: { value: 'Keep a fortnight' } });
    fireEvent.change(screen.getByLabelText(/keep original files for/i), { target: { value: '14' } });
    fireEvent.click(screen.getByRole('button', { name: /save retention policy/i }));
    await waitFor(() => expect(updateEvidenceRetentionPolicy).toHaveBeenCalledWith(
      expect.objectContaining({ retentionDays: 14, isEnabled: false }),
    ));
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
    const finalCheck = await confirmTwice(dialog, /delete 47 documents/i, 47);

    runEvidenceRetentionPurge.mockResolvedValueOnce(readEvidenceRetentionRun({
      dryRun: false, purged: 47, bytesReclaimed: 1_932_735_283,
      legacyCopiesDeleted: 12, legacyCopiesUnresolved: 3, skipped: [],
    }, false));
    fireEvent.click(within(finalCheck).getByRole('button', { name: /delete 47 documents for good/i }));

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
    const finalCheck = await confirmTwice(dialog, /delete 47 documents/i, 47);

    runEvidenceRetentionPurge.mockResolvedValueOnce(readEvidenceRetentionRun({
      dryRun: false, purged: 47, bytesReclaimed: 1_932_735_283, idempotentReplay: true, skipped: [],
    }, false));
    fireEvent.click(within(finalCheck).getByRole('button', { name: /delete 47 documents for good/i }));

    expect(await screen.findByText(/already been carried out, so nothing further was deleted/i)).toBeInTheDocument();
  });

  it('lets the tenant keep originals for as little as one day, and refuses only zero', async () => {
    renderPage();
    const days = await screen.findByLabelText(/keep original files for/i);
    fireEvent.change(screen.getByLabelText(/reason for this change/i), { target: { value: 'Shorter window' } });
    fireEvent.change(days, { target: { value: '5' } });
    expect(screen.queryByText(/between 1 and 3650/i)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /save retention policy/i })).toBeEnabled();

    fireEvent.change(days, { target: { value: '0' } });
    expect(await screen.findByText(/between 1 and 3650/i)).toBeInTheDocument();
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

    // The first confirmation does not delete. It opens the second one.
    fireEvent.click(confirmButton);
    expect(runEvidenceRetentionPurge).toHaveBeenCalledTimes(1); // the preview only
    const finalCheck = await screen.findByRole('dialog', { name: /last check before 47 documents go/i });
    const finalButton = within(finalCheck).getByRole('button', { name: /delete 47 documents for good/i });
    expect(finalButton).toBeDisabled();

    // The wrong number is refused in the browser, in words.
    fireEvent.change(within(finalCheck).getByLabelText(/type 47 to confirm/i), { target: { value: '74' } });
    expect(finalButton).toBeDisabled();
    expect(within(finalCheck).getByText(/that is not the number shown above/i)).toBeInTheDocument();

    fireEvent.change(within(finalCheck).getByLabelText(/type 47 to confirm/i), { target: { value: '47' } });
    expect(finalButton).toBeEnabled();

    runEvidenceRetentionPurge.mockResolvedValueOnce(readEvidenceRetentionRun({
      dryRun: false, scanned: 90, eligible: 47, purged: 47, bytesReclaimed: 1_932_735_283, skipped: [],
      administratorsNotified: 2,
    }, false));
    fireEvent.click(finalButton);

    // The typed number travels to the server, which verifies it against its own count.
    await waitFor(() => expect(runEvidenceRetentionPurge).toHaveBeenLastCalledWith(
      expect.objectContaining({
        dryRun: false,
        reason: 'Quarterly storage reclaim',
        previewToken: 'server-signed-preview-token',
        confirmedCount: 47,
      }),
    ));
    expect(await screen.findByText(/stored files deleted/i)).toBeInTheDocument();
    expect(screen.getByText(/a receipt was emailed to 2 administrators/i)).toBeInTheDocument();
  });

  it('shows the server refusal inside the final check and keeps the files', async () => {
    renderPage();
    await screen.findByRole('button', { name: /preview what would be deleted/i });
    fireEvent.change(screen.getByLabelText(/reason for reclaiming space/i), {
      target: { value: 'Quarterly storage reclaim' },
    });
    await runPreview();
    fireEvent.click(screen.getByRole('button', { name: /delete stored files permanently/i }));
    const dialog = await screen.findByRole('dialog');
    const finalCheck = await confirmTwice(dialog, /delete 47 documents/i, 47);

    runEvidenceRetentionPurge.mockRejectedValueOnce({
      response: { status: 400, data: { detail: 'Type the number of documents being deleted — 46 — to confirm. The number did not match, so nothing was deleted.' } },
    });
    fireEvent.click(within(finalCheck).getByRole('button', { name: /delete 47 documents for good/i }));
    expect(await within(finalCheck).findByText(/nothing was deleted/i)).toBeInTheDocument();
    expect(screen.queryByText(/stored files deleted/i)).not.toBeInTheDocument();
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
    const finalCheck = await confirmTwice(dialog, /delete 47 documents/i, 47);

    runEvidenceRetentionPurge.mockRejectedValueOnce({ response: { status: 502 } });
    fireEvent.click(within(finalCheck).getByRole('button', { name: /delete 47 documents for good/i }));
    await within(finalCheck).findByText(/temporarily unavailable|did not complete/i);
    const firstKey = runEvidenceRetentionPurge.mock.calls.at(-1)?.[0].idempotencyKey;

    runEvidenceRetentionPurge.mockResolvedValueOnce(readEvidenceRetentionRun({
      dryRun: false, purged: 47, bytesReclaimed: 1_932_735_283, skipped: [],
    }, false));
    fireEvent.click(within(finalCheck).getByRole('button', { name: /delete 47 documents for good/i }));
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

/**
 * The selection surface the product owner asked for: three rows, a count, a size — read by a
 * business owner with no training. These tests are written the way he would use the screen: they
 * find controls by the words on them, and they fail if an internal name ever appears.
 */
describe('StorageRetentionPage — clear out what produced nothing', () => {
  it('offers exactly three rows, each with a count and a size, in plain words', async () => {
    renderPage();
    await screen.findByText(/mail that never became an inquiry/i);

    expect(screen.getByText(/mail we identified as not being business/i)).toBeInTheDocument();
    expect(screen.getByText(/stored files nothing points to any more/i)).toBeInTheDocument();
    expect(screen.getByText('48 · 3.2 MB')).toBeInTheDocument();
    expect(screen.getByText('28 · 1.0 MB')).toBeInTheDocument();
    expect(screen.getByText('170 · 11.2 MB')).toBeInTheDocument();

    // Three rows and no fourth control: no date picker, no filter, no per-record ticking.
    const section = screen.getByRole('region', { name: /clear out what produced nothing/i });
    expect(within(section).getAllByRole('checkbox')).toHaveLength(3);
    expect(within(section).queryByRole('spinbutton')).not.toBeInTheDocument();
  });

  it('never shows an internal name, code or table to the person reading it', async () => {
    renderPage();
    await screen.findByText(/mail that never became an inquiry/i);
    const words = screen.getByRole('region', { name: /clear out what produced nothing/i }).textContent ?? '';

    for (const jargon of ['MAIL_PRODUCED_NOTHING', 'MAIL_TRIAGED_AS_NOISE', 'ORPHANED_STORED_FILES',
      'assembly', 'EmailIngest', 'source_document', 'SourceDocument', 'occurrence', 'enum']) {
      expect(words).not.toContain(jargon);
    }
  });

  it('reads as reassurance: says what it will never touch, with counts', async () => {
    renderPage();
    await screen.findByText(/never deleted, whatever you choose/i);

    expect(screen.getByText(/22 document\(s\) are protected/i)).toBeInTheDocument();
    expect(screen.getByText(/invoices you have already issued to a customer/i)).toBeInTheDocument();
    expect(screen.getByText(/anything already posted to your accounts/i)).toBeInTheDocument();
    expect(screen.getByText(/anything you have put on legal hold/i)).toBeInTheDocument();
    // A reason with nothing behind it is not listed — the panel is reassurance, not an inventory.
    expect(screen.queryByText(/files quarantined by the virus scanner/i)).not.toBeInTheDocument();
  });

  it('keeps removal switched off until something is ticked, previewed and explained', async () => {
    renderPage();
    await screen.findByText(/mail that never became an inquiry/i);
    const remove = () => screen.getByRole('button', { name: /remove them permanently/i });

    expect(remove()).toBeDisabled();
    expect(screen.getByText(/tick at least one group above to preview it/i)).toBeInTheDocument();

    await tickBucket(/mail that never became an inquiry/i);
    expect(remove()).toBeDisabled();
    expect(screen.getByText(/removing stays switched off until you have previewed it/i)).toBeInTheDocument();

    await previewCleanup();
    // Previewed, but no reason written yet.
    expect(remove()).toBeDisabled();
    expect(screen.getByText(/enter a reason to switch on permanent removal/i)).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText(/reason for clearing this/i), {
      target: { value: 'Removing our own test mail.' },
    });
    await waitFor(() => expect(remove()).toBeEnabled());
  });

  it('shows the preview numbers first and never claims it deleted anything', async () => {
    renderPage();
    await screen.findByText(/mail that never became an inquiry/i);
    await tickBucket(/mail that never became an inquiry/i);
    await previewCleanup();

    expect(screen.getByText('48 messages would be cleared')).toBeInTheDocument();
    expect(screen.getByText('170 leftover files would be deleted')).toBeInTheDocument();
    expect(screen.getByText('14.4 MB would be freed')).toBeInTheDocument();
    expect(runTenantDataCleanup).toHaveBeenCalledWith(
      expect.objectContaining({ dryRun: true, buckets: [TENANT_DATA_BUCKETS.mailProducedNothing] }),
    );
  });

  it('makes the second confirmation state what goes, what stays, and the space freed', async () => {
    renderPage();
    await screen.findByText(/mail that never became an inquiry/i);
    await tickBucket(/mail that never became an inquiry/i);
    await previewCleanup();
    fireEvent.change(screen.getByLabelText(/reason for clearing this/i), {
      target: { value: 'Removing our own test mail.' },
    });
    fireEvent.click(await screen.findByRole('button', { name: /remove them permanently/i }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/remove 48 messages and 170 files/i)).toBeInTheDocument();
    expect(within(dialog).getByText(/this frees 14\.4 MB/i)).toBeInTheDocument();
    expect(within(dialog).getByText(/who sent it, when, what the subject was/i)).toBeInTheDocument();
    expect(within(dialog).getByText(/nothing on an invoice, order, live deal or legal hold/i))
      .toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: /remove them$/i })).toBeDisabled();
  });

  it('asks for the number of items as a second confirmation before anything is removed', async () => {
    renderPage();
    await screen.findByText(/mail that never became an inquiry/i);
    await tickBucket(/mail that never became an inquiry/i);
    await previewCleanup();
    fireEvent.change(screen.getByLabelText(/reason for clearing this/i), {
      target: { value: 'Removing our own test mail.' },
    });
    fireEvent.click(await screen.findByRole('button', { name: /remove them permanently/i }));

    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText(/type delete to confirm/i), { target: { value: 'DELETE' } });
    fireEvent.click(within(dialog).getByRole('button', { name: /remove them$/i }));
    // Only the preview has been sent so far.
    expect(runTenantDataCleanup).toHaveBeenCalledTimes(1);

    // 48 messages + 170 files.
    const finalCheck = await screen.findByRole('dialog', { name: /last check before 218 messages and files go/i });
    expect(within(finalCheck).getByRole('button', { name: /remove them for good/i })).toBeDisabled();
    fireEvent.click(within(finalCheck).getByRole('button', { name: /go back/i }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: /last check/i })).not.toBeInTheDocument());
    expect(runTenantDataCleanup).toHaveBeenCalledTimes(1);
  });

  it('sends the typed confirmation to the server so the check is not browser-only', async () => {
    runTenantDataCleanup.mockResolvedValueOnce(CLEANUP_PREVIEW).mockResolvedValueOnce(CLEANUP_RECEIPT);
    renderPage();
    await screen.findByText(/mail that never became an inquiry/i);
    await tickBucket(/stored files nothing points to any more/i);
    await previewCleanup();
    fireEvent.change(screen.getByLabelText(/reason for clearing this/i), {
      target: { value: 'Reclaiming leftover storage.' },
    });
    fireEvent.click(await screen.findByRole('button', { name: /remove them permanently/i }));

    const dialog = await screen.findByRole('dialog');
    const finalCheck = await confirmTwice(dialog, /remove them$/i, 218);
    fireEvent.click(within(finalCheck).getByRole('button', { name: /remove them for good/i }));

    await waitFor(() => expect(runTenantDataCleanup).toHaveBeenLastCalledWith(
      expect.objectContaining({
        dryRun: false,
        confirmation: 'DELETE',
        confirmedCount: 218,
        buckets: [TENANT_DATA_BUCKETS.orphanedFiles],
        reason: 'Reclaiming leftover storage.',
      }),
    ));
  });

  it('reports what was deliberately left alone rather than dropping it', async () => {
    runTenantDataCleanup.mockResolvedValueOnce(CLEANUP_PREVIEW).mockResolvedValueOnce(CLEANUP_RECEIPT);
    renderPage();
    await screen.findByText(/mail that never became an inquiry/i);
    await tickBucket(/stored files nothing points to any more/i);
    await previewCleanup();
    fireEvent.change(screen.getByLabelText(/reason for clearing this/i), {
      target: { value: 'Reclaiming leftover storage.' },
    });
    fireEvent.click(await screen.findByRole('button', { name: /remove them permanently/i }));
    const dialog = await screen.findByRole('dialog');
    const finalCheck = await confirmTwice(dialog, /remove them$/i, 218);
    fireEvent.click(within(finalCheck).getByRole('button', { name: /remove them for good/i }));

    expect(await screen.findByText(/left alone on purpose/i)).toBeInTheDocument();
    expect(screen.getByText(/we do not recognise this file's name/i)).toBeInTheDocument();
  });

  it('disables an empty or blocked row and prints the reason in words', async () => {
    getTenantDataControl.mockResolvedValue(TENANT_DATA_EMPTY);
    renderPage();
    await screen.findByText(/mail that never became an inquiry/i);

    for (const box of within(screen.getByRole('region', { name: /clear out what produced nothing/i }))
      .getAllByRole('checkbox')) {
      expect(box).toBeDisabled();
    }
    expect(screen.getByText(/there is nothing here to clear/i)).toBeInTheDocument();
    // "We could not look" must never render as "there is nothing there".
    expect(screen.getByText(/cannot list what it holds/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /remove them permanently/i })).toBeDisabled();
  });

  it('says so when a deployment reports no buckets at all, instead of implying none exist', async () => {
    getTenantDataControl.mockResolvedValue(readTenantDataControl({}));
    renderPage();
    expect(await screen.findByText(/did not report what can be cleared/i)).toBeInTheDocument();
    expect(screen.getByText(/not the same as "you have nothing to clear"/i)).toBeInTheDocument();
  });

  it('describes governed administrator review without inventing an in-product erasure workflow', async () => {
    renderPage();
    await screen.findByText('256 MB');
    expect(screen.queryByText(/data subject request/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/edit or delete the lead that holds them/i)).not.toBeInTheDocument();
    expect(screen.getAllByText(/governed privacy process/i).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/authorised administrator/i).length).toBeGreaterThan(0);
  });

  it('states the configured retention default without claiming blanket legal compliance', async () => {
    renderPage();
    await screen.findByText('256 MB');

    expect(screen.getByText(/90 days is the default/i)).toBeInTheDocument();
    expect(screen.queryByText(/compliance-approved default/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/satisfy UAE and KSA/i)).not.toBeInTheDocument();
  });
});
