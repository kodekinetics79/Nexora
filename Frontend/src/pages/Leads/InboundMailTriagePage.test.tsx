import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import InboundMailTriagePage, { presentableReason } from './InboundMailTriagePage';
import {
  describeAssemblyState,
  describeMessageProgress,
  describeReopenAbility,
  isTriageUnavailable,
  readPollFailureReport,
  readPollReport,
  readTriagePage,
  readTriageRow,
} from '../../api/services/emailTriageService';

/**
 * What is locked down here is the reason the screen exists:
 *  - the landing view is the REJECTIONS, because that is the only list that can hide a lost deal;
 *  - the machine's reasoning is shown as human wording, never as raw snake_case;
 *  - a rejected message can always be put back, and only with a reason;
 *  - the prose that arrived IS the evidence, so it is rendered next to what came out of it;
 *  - a field the backend has not shipped reads as an absence, never as 0 or a blank cell;
 *  - a poll states what it DID, and a poll that touched no mailbox is never dressed as a success;
 *  - "Open lead" exists only where a lead does — a message needing review has none, and offering
 *    the action anyway is the defect this screen is meant to remove.
 */

const listTriage = vi.fn();
const reprocess = vi.fn();
const pollMailboxes = vi.fn();
const getMessage = vi.fn();

vi.mock('../../api/services/emailTriageService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/emailTriageService')>();
  return {
    ...actual,
    default: {
      listTriage: (params: unknown) => listTriage(params),
      reprocess: (id: number, reason: string, key?: string) => reprocess(id, reason, key),
      pollMailboxes: () => pollMailboxes(),
      getMessage: (id: number) => getMessage(id),
    },
  };
});

const getMailboxes = vi.fn();

vi.mock('../../api/services/mailboxService', () => ({
  default: { getAll: () => getMailboxes() },
}));

const hasPermission = vi.fn();

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ hasPermission: (module: string, action?: string) => hasPermission(module, action) }),
}));

const navigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => navigate };
});

/** An out-of-office that swallowed a real enquiry thread — the case the owner cares about. */
const NOISE_PAGE = readTriagePage(
  {
    items: [
      {
        id: 41,
        receivedOn: '2026-08-03T06:12:00Z',
        from: 'ops@alfuttaim-contracting.ae',
        subject: 'Automatic reply: Cable tray enquiry',
        outcome: 'Noise',
        reasonCodes: ['auto_submitted_header'],
        hasAttachments: false,
        linkedBatchId: null,
        bodyPreview: 'I am out of the office until 12 August. For urgent matters contact procurement@…',
        bodySubmitted: false,
        attachmentCount: 0,
        extractedItemCount: 0,
      },
      {
        // Deliberately sparse: a deployment that has not shipped the enrichment fields yet.
        id: 42,
        outcome: 'Noise',
        reasonCodes: ['noreply_sender', 'bulk_list_header'],
      },
    ],
    totalCount: 2,
    pageNumber: 1,
    pageSize: 25,
  },
  1,
);

const INQUIRY_PAGE = readTriagePage(
  {
    items: [
      {
        id: 77,
        receivedOn: '2026-08-04T05:40:00Z',
        from: 'buyer@gulfmep.ae',
        subject: 'Requirement — Jebel Ali',
        outcome: 'Inquiry',
        reasonCodes: ['qty_uom_pattern', 'request_verb'],
        hasAttachments: true,
        attachmentNames: ['drawing-rev-b.pdf'],
        attachmentCount: 1,
        linkedBatchId: 'batch-9001',
        leadId: 5150,
        bodyPreview:
          'Hi, please quote 40 nos cable tray 300mm and 12 nos junction box IP65, delivery to Jebel Ali by 20th',
        bodySubmitted: true,
        extractedItemCount: 2,
      },
    ],
    totalCount: 1,
    pageNumber: 1,
    pageSize: 25,
  },
  1,
);

/**
 * The three assembly outcomes that must look different: finished with a lead, still in flight, and
 * stopped for a person. The last two have no lead — and must offer no way to pretend otherwise.
 *
 * Component arrays are deliberately absent here, exactly as the list endpoint returns them. Parts
 * arrive only from the per-message read, and the screen must not fake them from the list.
 */
const ASSEMBLY_PAGE = readTriagePage(
  {
    items: [
      {
        id: 101,
        receivedOn: '2026-08-05T07:00:00Z',
        from: 'projects@dana-cont.qa',
        subject: 'RFQ 8891 — pipe supports',
        outcome: 'Inquiry',
        reasonCodes: ['rfq_reference'],
        assemblyState: 'Assembled',
        expectedComponentCount: 2,
        completedComponentCount: 2,
        // The assembly worker's key for the lead, not the triage row's.
        assembledLeadId: 6120,
        ingestedAtUtc: '2026-08-05T07:01:00Z',
      },
      {
        id: 102,
        receivedOn: '2026-08-05T08:00:00Z',
        from: 'tenders@mep-gulf.ae',
        subject: 'Tender pack — three drawings',
        outcome: 'Inquiry',
        reasonCodes: [],
        assemblyState: 'Extracting',
        expectedComponentCount: 3,
        completedComponentCount: 1,
      },
      {
        id: 103,
        receivedOn: '2026-08-05T09:00:00Z',
        from: 'buying@qcon.qa',
        subject: 'Scanned enquiry',
        outcome: 'Uncertain',
        reasonCodes: ['no_signal'],
        assemblyState: 'NeedsReview',
        assemblyReason: 'An attachment could not be read, so a person has to look at this message.',
        expectedComponentCount: 2,
        completedComponentCount: 2,
      },
    ],
    totalCount: 3,
    pageNumber: 1,
    pageSize: 25,
  },
  1,
);

/** What the per-message read returns for the message that needs review. */
const REVIEW_MESSAGE = readTriageRow({
  id: 103,
  outcome: 'Uncertain',
  assemblyState: 'NeedsReview',
  assemblyReason: 'An attachment could not be read, so a person has to look at this message.',
  components: [
    { id: 10, ordinal: 0, kind: 'Body', fileName: null, state: 'Completed' },
    {
      id: 11,
      ordinal: 1,
      kind: 'Attachment',
      fileName: 'scan.pdf',
      state: 'Skipped',
      // Code only, no sentence: the screen must humanise it rather than print snake_case.
      reasonCode: 'attachment_unreadable',
    },
  ],
  skippedAttachments: ['legacy-price-sheet.xls (unsupported_format)'],
  rawEvidenceStored: true,
  rawEvidenceVerifiable: true,
  senderSentAtUtc: '2026-08-05T08:59:00Z',
  parsedAt: '2026-08-05T09:02:00Z',
});

/**
 * Fully assembled — and still short of what the customer sent. This is the case ING-06 exists for:
 * every count on the row is complete, so nothing reads as a failure, while two of the sender's
 * files were never handed to extraction. The third entry is deliberately malformed.
 */
const SKIPPED_PAGE = readTriagePage(
  {
    items: [
      {
        id: 201,
        receivedOn: '2026-08-06T06:00:00Z',
        from: 'projects@fitout-gulf.ae',
        subject: 'Fit-out package — three files',
        outcome: 'Inquiry',
        reasonCodes: ['rfq_reference'],
        hasAttachments: true,
        attachmentCount: 3,
        assemblyState: 'Assembled',
        expectedComponentCount: 2,
        completedComponentCount: 2,
        assembledLeadId: 7010,
        skippedAttachments: [
          "deck.pptx (unsupported file type '.pptx')",
          'site-video.mp4 (exceeds the 10 MB size limit (18874368 bytes))',
          42,
        ],
      },
    ],
    totalCount: 1,
    pageNumber: 1,
    pageSize: 25,
  },
  1,
);

/** The parts that WERE scheduled for that message: the body and the one readable drawing. */
const SKIPPED_MESSAGE = readTriageRow({
  id: 201,
  outcome: 'Inquiry',
  assemblyState: 'Assembled',
  components: [
    { id: 20, ordinal: 0, kind: 'Body', fileName: null, state: 'Completed' },
    { id: 21, ordinal: 1, kind: 'Attachment', fileName: 'layout-rev-c.pdf', state: 'Completed' },
  ],
});

/** A connected, active, healthy IMAP inbox — the case where an empty list really is empty. */
const HEALTHY_MAILBOX = {
  id: 1,
  configurationName: 'Enquiries',
  emailAddress: 'enquiries@example.test',
  protocol: 'IMAP' as const,
  host: 'imap.example.test',
  port: 993,
  username: 'enquiries',
  useSsl: true,
  pollingInterval: 5,
  isActive: true,
  createdOn: '2026-08-01T00:00:00Z',
  lastSuccessfulPollOn: '2026-08-06T06:00:00Z',
  lastPollAttemptOn: '2026-08-06T06:00:00Z',
  lastPollError: null,
  consecutivePollFailures: 0,
  healthState: 'Healthy' as const,
  healthDetail: 'Polling normally.',
  credentialsSentInClear: false,
};

/**
 * The three assembly states that leave a message with NO lead and no usable reprocess — the dead
 * ends this screen used to render as a row with one button that could only answer 422.
 */
const DEAD_END_PAGE = readTriagePage(
  {
    items: [
      {
        id: 301,
        receivedOn: '2026-08-07T06:00:00Z',
        from: 'buying@qcon.qa',
        subject: 'Scanned enquiry needing a person',
        outcome: 'Uncertain',
        reasonCodes: [],
        assemblyState: 'NeedsReview',
        linkedBatchId: 'batch-3001',
      },
      {
        id: 302,
        receivedOn: '2026-08-07T07:00:00Z',
        from: 'projects@dana-cont.qa',
        subject: 'Attachment refused on security grounds',
        outcome: 'Uncertain',
        reasonCodes: [],
        assemblyState: 'RejectedSecurity',
      },
      {
        id: 303,
        receivedOn: '2026-08-07T08:00:00Z',
        from: 'noreply@portal.example',
        subject: 'Held while storage was down',
        outcome: 'Inquiry',
        reasonCodes: [],
        assemblyState: 'FailedRecoverable',
      },
      {
        // THE LIVE SHAPE OF THE DEAD-LETTERED POPULATION. No assemblyState at all — 20 of the 80
        // stopped messages on mailbox 9 have no assembly row, all but two of them from
        // 2026-08-13/14, before the message aggregate existed. A fixture that gave this row an
        // assemblyState would exercise a shape the product does not emit for them.
        id: 304,
        receivedOn: '2026-08-13T09:00:00Z',
        from: 'procurement@buyer.example',
        subject: 'Fwd: Request for Quotation against PR# 111',
        outcome: null,
        reasonCodes: [],
        stoppedInProcessing: true,
      },
    ],
    totalCount: 4,
    pageNumber: 1,
    pageSize: 25,
  },
  1,
);

const renderPage = () => {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <MemoryRouter>
      <QueryClientProvider client={queryClient}>
        <InboundMailTriagePage />
      </QueryClientProvider>
    </MemoryRouter>,
  );
};

const rowFor = async (subject: RegExp): Promise<HTMLElement> => {
  const cell = await screen.findByText(subject);
  const row = cell.closest('tr');
  if (!row) throw new Error('row not found');
  return row;
};

beforeEach(() => {
  vi.clearAllMocks();
  hasPermission.mockReturnValue(true);
  // A healthy inbound mailbox is the default, so the empty-state tests below are about the LIST
  // and not about the plumbing. The obstacle cases set their own fixture.
  getMailboxes.mockResolvedValue([HEALTHY_MAILBOX]);
  listTriage.mockResolvedValue(NOISE_PAGE);
  reprocess.mockResolvedValue({ id: 41, status: 'Queued', batchId: 'batch-42', replayed: false });
  pollMailboxes.mockResolvedValue(
    readPollReport({
      message: '2 mailbox(es) polled, 4 message(s) in the poll window, 3 new, 3 captured.',
      mailboxes: 2,
      newMessages: 3,
      totals: {
        mailboxesPolled: 2,
        mailboxesFailed: 0,
        messagesFound: 4,
        messagesDownloaded: 3,
        messagesAlreadyIngested: 1,
        messagesCaptured: 3,
        componentsScheduled: 5,
        messagesHeldForReview: 1,
        messagesRejected: 0,
        messagesNotAcknowledged: 0,
      },
      polled: [{ mailbox: 'enquiries@example.test', succeeded: true, lookbackCappedDays: 0 }],
    }),
  );
  // A message whose parts this deployment does not report — the default for the sparse fixtures.
  getMessage.mockImplementation((id: number) => Promise.resolve(readTriageRow({ id, outcome: 'Noise' })));
});

describe('InboundMailTriagePage', () => {
  it('landsOnWhatIsWaitingOnAPerson_notOnDecisionsThatNeedNobody', async () => {
    // The screen used to open on "Rejected as noise". A rejection is a DECISION — the reader has
    // to notice unaided that a different tab holds the work — and on the live tenant 80 of 332
    // messages had stopped without one, spread across four outcome tabs with no way to gather
    // them. The landing tab asks the state question, and it must send `state` to get an answer:
    // the outcome parameter cannot express "stopped" at all.
    renderPage();
    await waitFor(() => expect(listTriage).toHaveBeenCalled());
    // pageSize 25 pins this to the PAGED LIST call and not to the one-row count query, which
    // also carries the state. Asserted loosely first, the test passed with the filter removed
    // from the list entirely — green over a screen that showed every message on every tab.
    expect(listTriage).toHaveBeenCalledWith({ outcome: undefined, state: 'stopped', page: 1, pageSize: 25 });
    expect(screen.getByRole('tab', { name: /needs a person/i })).toHaveAttribute('aria-selected', 'true');
    expect(await screen.findByText(/Automatic reply: Cable tray enquiry/)).toBeInTheDocument();
  });

  it('stillOffersTheRejectionsTab_soAnOverturnedDecisionStaysOneClickAway', async () => {
    renderPage();
    await waitFor(() => expect(listTriage).toHaveBeenCalled());
    fireEvent.click(screen.getByRole('tab', { name: /rejected as noise/i }));
    // The state filter is the stopped tab's alone; it must not leak onto an outcome tab and
    // silently hide the rejections the reader came to check.
    await waitFor(() =>
      expect(listTriage).toHaveBeenCalledWith({ outcome: 'Noise', state: undefined, page: 1, pageSize: 25 }));
  });

  it('rendersReasonCodesAsHumanWording_notRawCodes', async () => {
    renderPage();
    const row = await rowFor(/Automatic reply: Cable tray enquiry/);
    expect(within(row).getByText('Auto-submitted')).toBeInTheDocument();
    expect(within(row).queryByText('auto_submitted_header')).not.toBeInTheDocument();
  });

  it('rendersAbsentFieldsAsNotReported_neverAsBlankOrZero', async () => {
    renderPage();
    const sparse = await rowFor(/\(no subject\)/);
    // Row 42 carries neither sender nor received date; both must say so out loud.
    expect(within(sparse).getAllByText('Not reported').length).toBeGreaterThanOrEqual(2);
  });

  it('showsTheOriginalProseBesideWhatCameOutOfIt', async () => {
    listTriage.mockResolvedValue(INQUIRY_PAGE);
    renderPage();
    const row = await rowFor(/Requirement — Jebel Ali/);
    fireEvent.click(within(row).getByRole('button', { name: /show message/i }));

    const detail = await screen.findByRole('region', { name: /message and extraction/i });
    expect(within(detail).getByText(/please quote 40 nos cable tray 300mm/)).toBeInTheDocument();
    expect(within(detail).getByText(/Line items extracted from this message: 2/)).toBeInTheDocument();
  });

  it('statesTheBodyVersusAttachmentSplit', async () => {
    listTriage.mockResolvedValue(INQUIRY_PAGE);
    renderPage();
    const row = await rowFor(/Requirement — Jebel Ali/);
    fireEvent.click(within(row).getByRole('button', { name: /show message/i }));

    const detail = await screen.findByRole('region', { name: /message and extraction/i });
    expect(
      within(detail).getByText(
        /The message text was submitted for extraction\. 1 attachment came with this message and is extracted separately/,
      ),
    ).toBeInTheDocument();
    expect(within(detail).getByText('drawing-rev-b.pdf')).toBeInTheDocument();
  });

  it('saysSoWhenTheMessageTextIsNotExposedYet', async () => {
    renderPage();
    const sparse = await rowFor(/\(no subject\)/);
    fireEvent.click(within(sparse).getByRole('button', { name: /show message/i }));

    const detail = await screen.findByRole('region', { name: /message and extraction/i });
    expect(within(detail).getByText(/message text is not exposed by this deployment yet/i)).toBeInTheDocument();
  });

  it('refusesToOverturnADecisionWithoutAReason', async () => {
    renderPage();
    const row = await rowFor(/Automatic reply: Cable tray enquiry/);
    fireEvent.click(within(row).getByRole('button', { name: /reprocess as inquiry/i }));

    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: /^reprocess as inquiry$/i }));

    expect(reprocess).not.toHaveBeenCalled();
    expect(await within(dialog).findByText(/give a reason/i)).toBeInTheDocument();
  });

  it('sendsTheMessageBackWithTheReason', async () => {
    renderPage();
    const row = await rowFor(/Automatic reply: Cable tray enquiry/);
    fireEvent.click(within(row).getByRole('button', { name: /reprocess as inquiry/i }));

    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText(/why is this an inquiry/i), {
      target: { value: 'Real enquiry hidden behind an auto-reply.' },
    });
    fireEvent.click(within(dialog).getByRole('button', { name: /^reprocess as inquiry$/i }));

    await waitFor(() =>
      expect(reprocess).toHaveBeenCalledWith(
        41, 'Real enquiry hidden behind an auto-reply.', expect.any(String)));
    expect(await screen.findByText(/was sent back through extraction as an inquiry/i)).toBeInTheDocument();
  });

  it('sendsOneIdempotencyKeyPerOverride_soADoubleClickIsOneCommand', async () => {
    // The key used to be minted inside the service on every call, so two clicks were two
    // DIFFERENT audited overrides and the second landed on a message the first had moved.
    let resolveFirst: (value: unknown) => void = () => {};
    reprocess.mockImplementation(() => new Promise((resolve) => { resolveFirst = resolve; }));
    renderPage();
    const row = await rowFor(/Automatic reply: Cable tray enquiry/);
    fireEvent.click(within(row).getByRole('button', { name: /reprocess as inquiry/i }));

    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText(/why is this an inquiry/i), {
      target: { value: 'Real enquiry hidden behind an auto-reply.' },
    });
    const confirm = within(dialog).getByRole('button', { name: /^reprocess as inquiry$/i });
    fireEvent.click(confirm);
    fireEvent.click(confirm);

    await waitFor(() => expect(reprocess).toHaveBeenCalled());
    const keys = new Set(reprocess.mock.calls.map((call) => call[2]));
    expect(keys.size).toBe(1);
    expect([...keys][0]).toBeTruthy();
    resolveFirst({ id: 41, status: 'Queued', batchId: 'batch-42', replayed: false });
  });

  it('hidesTheOverrideWhenTheRoleCannotUseIt', async () => {
    hasPermission.mockReturnValue(false);
    renderPage();
    await screen.findByText(/Automatic reply: Cable tray enquiry/);
    expect(screen.queryByRole('button', { name: /reprocess as inquiry/i })).not.toBeInTheDocument();
    expect(screen.getByText(/needs the .edit leads. permission/i)).toBeInTheDocument();
  });

  it('queriesTheSelectedOutcomeWhenTheTabChanges', async () => {
    renderPage();
    await screen.findByText(/Automatic reply: Cable tray enquiry/);
    listTriage.mockResolvedValue(INQUIRY_PAGE);

    fireEvent.click(screen.getByRole('tab', { name: /^sent for inquiry extraction \(\d+\)$/i }));

    await waitFor(() => expect(listTriage).toHaveBeenCalledWith(expect.objectContaining({ outcome: 'Inquiry' })));
    expect(await screen.findByText(/Requirement — Jebel Ali/)).toBeInTheDocument();
  });

  it('explainsAMissingBackendInsteadOfShowingAFailure', async () => {
    listTriage.mockRejectedValue({ response: { status: 404 }, isAxiosError: true });
    renderPage();
    expect(await screen.findByText(/not switched on for this account/i)).toBeInTheDocument();
    expect(screen.getByText(/no message has been hidden/i)).toBeInTheDocument();
  });

  it('surfacesARealFailureWithARetry', async () => {
    listTriage.mockRejectedValue({ response: { status: 500, data: {} }, isAxiosError: true });
    renderPage();
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /try again/i })).toBeInTheDocument();
  });

  it('sendsNoOutcomeFilterOnTheAllTab_soMailWithNoDecisionIsReachable', async () => {
    // A row whose TriageOutcome is null matches none of the four decision filters, so before
    // this tab existed a message written by manual upload, a watched folder or lead identity
    // reconciliation appeared on NO tab at all.
    renderPage();
    await screen.findByText(/Automatic reply: Cable tray enquiry/);
    listTriage.mockClear();

    fireEvent.click(screen.getByRole('tab', { name: /^all messages\b/i }));

    await waitFor(() => expect(listTriage).toHaveBeenCalled());
    const params = listTriage.mock.calls.at(-1)?.[0] as { outcome?: unknown };
    expect(params.outcome).toBeUndefined();
  });

  it('countsWhatIsSittingBehindEachTab', async () => {
    // Nothing counted stranded mail: thirteen held messages looked exactly like none.
    renderPage();
    expect(await screen.findByRole('tab', { name: /rejected as noise \(2\)/i })).toBeInTheDocument();
  });
});

describe('InboundMailTriagePage — a row is never a dead end', () => {
  beforeEach(() => {
    listTriage.mockResolvedValue(DEAD_END_PAGE);
  });

  it('offersTheIngestionBatchWhereAMessageNeedsAPersonAndHasNoLead', async () => {
    renderPage();
    const row = await rowFor(/Scanned enquiry needing a person/);

    fireEvent.click(within(row).getByRole('button', { name: /open ingestion batch/i }));
    expect(navigate).toHaveBeenCalledWith('/procurement/leads/ingestion/batch-3001');
  });

  it('offersTheDeadLetterReplayToAnAdministrator', async () => {
    renderPage();
    const row = await rowFor(/Scanned enquiry needing a person/);
    fireEvent.click(within(row).getByRole('button', { name: /retry failed processing/i }));
    expect(navigate).toHaveBeenCalledWith('/admin/operations');
  });

  it('hidesTheDeadLetterReplayFromARoleThatCannotOpenIt', async () => {
    // A link the reader's role cannot open is another dead end, not a fix for one.
    hasPermission.mockImplementation((module: string) => module !== 'Users');
    renderPage();
    const row = await rowFor(/Scanned enquiry needing a person/);
    expect(within(row).queryByRole('button', { name: /retry failed processing/i })).not.toBeInTheDocument();
    // The batch link is unaffected: it is the action this row actually has.
    expect(within(row).getByRole('button', { name: /open ingestion batch/i })).toBeInTheDocument();
  });

  it('hidesReprocessWhereItCanOnlyFail_andSaysWhyInPlainEnglish', async () => {
    renderPage();

    const review = await rowFor(/Scanned enquiry needing a person/);
    expect(within(review).queryByRole('button', { name: /reprocess as inquiry/i })).not.toBeInTheDocument();
    expect(within(review).getAllByText(/already waiting for a person/i).length).toBeGreaterThan(0);

    const security = await rowFor(/Attachment refused on security grounds/);
    expect(within(security).queryByRole('button', { name: /reprocess as inquiry/i })).not.toBeInTheDocument();
    expect(
      within(security).getAllByText(/cannot be sent back through extraction/i).length,
    ).toBeGreaterThan(0);
  });

  it('offersTheExceptionsScreenToAMessageThatStoppedInProcessingWithNoAssembly', async () => {
    // The population that had NO route out of its own row. `describeAssemblyState(null).needsHuman`
    // is false — honestly, since nothing was reported — so these fell out of every needs-a-person
    // branch, and the one screen carrying their retry and their "this can never be retried"
    // verdict was never offered. The row now says processing stopped and where to go.
    renderPage();
    const row = await rowFor(/Request for Quotation against PR# 111/);

    expect(within(row).getAllByText(/processing stopped for this message/i).length).toBeGreaterThan(0);
    fireEvent.click(within(row).getByRole('button', { name: /retry failed processing/i }));
    expect(navigate).toHaveBeenCalledWith('/admin/operations');
  });

  it('doesNotCallEveryUnreportedAssemblyStopped', async () => {
    // THE CONTROL. A row whose deployment simply does not report assembly state, and which did
    // NOT stop, must keep saying so — the flag is the server's derived fact, never an inference
    // from a missing field.
    listTriage.mockResolvedValue(
      readTriagePage(
        {
          items: [{
            id: 401, receivedOn: '2026-08-20T09:00:00Z', from: 'a@b.example',
            subject: 'Deployment reports no assembly', outcome: 'Inquiry', reasonCodes: [],
          }],
          totalCount: 1, pageNumber: 1, pageSize: 25,
        },
        1,
      ),
    );
    renderPage();
    const row = await rowFor(/Deployment reports no assembly/);
    expect(within(row).queryByText(/processing stopped for this message/i)).not.toBeInTheDocument();
    expect(
      within(row).queryByRole('button', { name: /retry failed processing/i }),
    ).not.toBeInTheDocument();
  });

  it('keepsReprocessOnAHeldMessage_becauseThatIsTheOneThingThatFreesIt', async () => {
    // The P0: nothing sweeps a held message, so this button is its only way back into flight.
    renderPage();
    const held = await rowFor(/Held while storage was down/);
    expect(within(held).getByRole('button', { name: /reprocess as inquiry/i })).toBeInTheDocument();
  });

  it('neverPrintsAnInternalStateNameAtTheReader', async () => {
    renderPage();
    await screen.findByText(/Scanned enquiry needing a person/);
    for (const enumName of ['NeedsReview', 'RejectedSecurity', 'FailedRecoverable', 'ReadyForAssembly']) {
      expect(screen.queryByText(new RegExp(enumName))).not.toBeInTheDocument();
    }
  });
});

/**
 * The live inversion, driven through the screen.
 *
 * Both rows carry the checkpoint the API actually returns, and it means the OPPOSITE of what it
 * reads as in each case. Neither string may appear, and the two rows must not look alike.
 */
const INVERTED_CHECKPOINT_PAGE = readTriagePage(
  {
    items: [
      {
        // Live shape of ingests 997/999/1001/1003: closed at NeedsReview, checkpoint says Queued.
        id: 997,
        receivedOn: '2026-08-24T06:00:00Z',
        from: 'buying@qcon.qa',
        subject: 'Finished and produced nothing',
        outcome: 'Uncertain',
        reasonCodes: [],
        assemblyState: 'NeedsReview',
        parseStatus: 'Queued',
      },
      {
        // The successful shape: a lead exists, checkpoint says NeedsReview.
        id: 998,
        receivedOn: '2026-08-24T07:00:00Z',
        from: 'buyer@gulfmep.ae',
        subject: 'Finished and produced a lead',
        outcome: 'Inquiry',
        reasonCodes: [],
        assemblyState: 'NeedsReview',
        assembledLeadId: 8110,
        parseStatus: 'NeedsReview',
      },
    ],
    totalCount: 2,
    pageNumber: 1,
    pageSize: 25,
  },
  1,
);

describe('InboundMailTriagePage — the per-ingest checkpoint never reaches the reader', () => {
  beforeEach(() => {
    listTriage.mockResolvedValue(INVERTED_CHECKPOINT_PAGE);
  });

  it('showsTheLossAsALoss_notAsWorkStillInProgress', async () => {
    renderPage();
    const row = await rowFor(/Finished and produced nothing/);

    expect(screen.getByRole('columnheader', { name: 'Routing decision' })).toBeInTheDocument();
    expect(within(row).getByText('Sent to extraction — uncertain')).toBeInTheDocument();
    expect(within(row).getByText('No inquiry — needs review')).toBeInTheDocument();
    expect(within(row).queryByRole('button', { name: /open lead/i })).not.toBeInTheDocument();
    // The word the checkpoint would have put here, which means the opposite of the truth.
    expect(within(row).queryByText(/queued/i)).not.toBeInTheDocument();
  });

  it('showsTheSuccessAsASuccess_notAsAProblem', async () => {
    renderPage();
    const row = await rowFor(/Finished and produced a lead/);

    expect(within(row).getByText('Sent to inquiry extraction')).toBeInTheDocument();
    expect(within(row).queryByText('Extracted as inquiry')).not.toBeInTheDocument();
    expect(within(row).getByText('Inquiry created — needs your review')).toBeInTheDocument();
    fireEvent.click(within(row).getByRole('button', { name: /open lead/i }));
    expect(navigate).toHaveBeenCalledWith('/procurement/leads/view/8110');
  });

  it('doesNotMakeTheTwoLookAlike', async () => {
    renderPage();
    const loss = await rowFor(/Finished and produced nothing/);
    const win = await rowFor(/Finished and produced a lead/);

    // Same assembly state, same checkpoint family, opposite commercial outcomes. If these two
    // ever render the same string the screen is back to hiding losses in plain sight.
    expect(within(loss).getByText(/^No inquiry/).textContent)
      .not.toEqual(within(win).getByText(/^Inquiry created/).textContent);
  });
});

describe('InboundMailTriagePage — an empty list states its real cause', () => {
  const EMPTY = readTriagePage({ items: [], totalCount: 0, pageNumber: 1, pageSize: 25 }, 1);

  it('doesNotSayNothingIsHiddenWhenNoInboxIsConnected', async () => {
    listTriage.mockResolvedValue(EMPTY);
    getMailboxes.mockResolvedValue([]);
    renderPage();

    expect(await screen.findByText(/no inbox is connected/i)).toBeInTheDocument();
    expect(screen.queryByText(/nothing is being hidden from you/i)).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /open email inboxes/i }));
    expect(navigate).toHaveBeenCalledWith('/setup/mailboxes');
  });

  it('namesAFailingInboxRatherThanReassuringTheReader', async () => {
    listTriage.mockResolvedValue(EMPTY);
    getMailboxes.mockResolvedValue([
      { ...HEALTHY_MAILBOX, healthState: 'Failing', lastPollError: 'authentication failed' },
    ]);
    renderPage();

    expect(await screen.findByText(/cannot be read/i)).toBeInTheDocument();
    expect(screen.getByText(/enquiries@example\.test/)).toBeInTheDocument();
  });

  it('keepsTheReassuringEmptyStateWhenTheMailboxIsGenuinelyHealthy', async () => {
    listTriage.mockResolvedValue(EMPTY);
    renderPage();

    expect(await screen.findByText(/nothing is waiting on you/i)).toBeInTheDocument();
  });

  it('saysNothingAboutMailboxesToARoleThatCannotSeeThem', async () => {
    // A 403 on /api/Mailbox is indistinguishable from "no mailbox exists", and a warning built
    // on that guess would be false. The query is not made at all.
    listTriage.mockResolvedValue(EMPTY);
    hasPermission.mockImplementation((module: string) => module !== 'Email & SMTP');
    renderPage();

    await screen.findByText(/nothing is waiting on you/i);
    expect(getMailboxes).not.toHaveBeenCalled();
  });
});

describe('InboundMailTriagePage — polling on demand', () => {
  it('statesWhatThePollDid_ratherThanABareConfirmation', async () => {
    renderPage();
    await screen.findByText(/Automatic reply: Cable tray enquiry/);

    fireEvent.click(screen.getByRole('button', { name: /poll now/i }));

    await waitFor(() => expect(pollMailboxes).toHaveBeenCalled());
    expect(await screen.findByText('Polled 2 mailboxes — 3 new messages')).toBeInTheDocument();
    expect(
      screen.getByText(
        '4 messages in the poll window · 3 new · 1 already ingested · 3 captured · '
        + '5 parts queued for extraction · 1 held for review · 0 rejected.',
      ),
    ).toBeInTheDocument();
  });

  it('warnsWhenMailWasLeftInTheMailboxDespiteASuccessfulPoll', async () => {
    pollMailboxes.mockResolvedValue(
      readPollReport({
        mailboxes: 1,
        newMessages: 2,
        totals: { mailboxesPolled: 1, messagesDownloaded: 2, messagesNotAcknowledged: 1 },
        polled: [{ mailbox: 'enquiries@example.test', succeeded: true, lookbackCappedDays: 3 }],
      }),
    );
    renderPage();
    await screen.findByText(/Automatic reply: Cable tray enquiry/);

    fireEvent.click(screen.getByRole('button', { name: /poll now/i }));

    expect(
      await screen.findByText(/1 message could not be taken and was left unread for the next cycle/),
    ).toBeInTheDocument();
    expect(screen.getByText(/Mail older than the poll window was not read — up to 3 days/)).toBeInTheDocument();
  });

  it('rereadsTheListAfterAPoll_soCapturedMailIsVisibleWithoutRefreshing', async () => {
    renderPage();
    await screen.findByText(/Automatic reply: Cable tray enquiry/);
    const before = listTriage.mock.calls.length;

    fireEvent.click(screen.getByRole('button', { name: /poll now/i }));

    await waitFor(() => expect(listTriage.mock.calls.length).toBeGreaterThan(before));
  });

  it('refusesToDressAPollThatTouchedNoMailboxAsASuccess', async () => {
    pollMailboxes.mockResolvedValue(
      readPollReport({ message: 'No active IMAP mailbox is configured, so no mail was fetched.' }),
    );
    renderPage();
    await screen.findByText(/Automatic reply: Cable tray enquiry/);

    fireEvent.click(screen.getByRole('button', { name: /poll now/i }));

    expect(await screen.findByText('No mailbox was polled')).toBeInTheDocument();
    expect(screen.getByText(/No active IMAP mailbox is configured/)).toBeInTheDocument();
  });

  it('namesTheMailboxAPollCouldNotRead', async () => {
    pollMailboxes.mockRejectedValue({
      isAxiosError: true,
      response: {
        status: 502,
        data: {
          message: '1 of 2 mailbox(es) could not be polled. No mail has been ingested from them.',
          reasons: [
            {
              mailbox: 'enquiries@example.test',
              reason: 'The mail server refused the sign-in.',
              lastSuccessfulPoll: '2026-08-04T05:00:00Z',
            },
          ],
        },
      },
    });
    renderPage();
    await screen.findByText(/Automatic reply: Cable tray enquiry/);

    fireEvent.click(screen.getByRole('button', { name: /poll now/i }));

    expect(await screen.findByText('1 mailbox could not be polled')).toBeInTheDocument();
    expect(screen.getByText('enquiries@example.test')).toBeInTheDocument();
    expect(screen.getByText(/The mail server refused the sign-in/)).toBeInTheDocument();
  });

  it('disablesThePollWhenTheRoleCannotRunIt', async () => {
    // Read plus edit, but not create — the exact permission the endpoint requires.
    hasPermission.mockImplementation((_module: string, action?: string) => action !== 'create');
    renderPage();
    await screen.findByText(/Automatic reply: Cable tray enquiry/);

    expect(screen.getByRole('button', { name: /poll now/i })).toBeDisabled();
    expect(screen.getByText(/"create leads" permission to poll the mailboxes/i)).toBeInTheDocument();
  });
});

describe('InboundMailTriagePage — assembly', () => {
  beforeEach(() => {
    listTriage.mockResolvedValue(ASSEMBLY_PAGE);
  });

  it('showsTheAssemblyStateAndItsReasonPerMessage', async () => {
    renderPage();

    const assembled = await rowFor(/RFQ 8891 — pipe supports/);
    // Named for the OUTCOME, not the internal state: this message produced an inquiry.
    expect(within(assembled).getByText('Inquiry created')).toBeInTheDocument();
    expect(within(assembled).getByText('2 of 2 parts assembled')).toBeInTheDocument();

    const review = await rowFor(/Scanned enquiry/);
    // Finished, and it produced NOTHING. That is the fact the reader has to see.
    expect(within(review).getByText('No inquiry — needs review')).toBeInTheDocument();
    expect(
      within(review).getByText('An attachment could not be read, so a person has to look at this message.'),
    ).toBeInTheDocument();
  });

  it('offersOpenLeadOnlyWhereALeadActuallyExists', async () => {
    renderPage();

    const assembled = await rowFor(/RFQ 8891 — pipe supports/);
    fireEvent.click(within(assembled).getByRole('button', { name: /open lead/i }));
    expect(navigate).toHaveBeenCalledWith('/procurement/leads/view/6120');

    const review = await rowFor(/Scanned enquiry/);
    expect(within(review).queryByRole('button', { name: /open lead/i })).not.toBeInTheDocument();
    expect(within(review).getByText('No lead was created for this message.')).toBeInTheDocument();
  });

  it('saysAMessageIsStillBeingAssembledInsteadOfLookingStuck', async () => {
    renderPage();

    const inFlight = await rowFor(/Tender pack — three drawings/);
    expect(within(inFlight).getByText('Reading')).toBeInTheDocument();
    expect(within(inFlight).getByText('1 of 3 parts assembled')).toBeInTheDocument();
    expect(within(inFlight).queryByRole('button', { name: /open lead/i })).not.toBeInTheDocument();
    expect(
      within(inFlight).getByText('No lead exists yet — this message is still being assembled.'),
    ).toBeInTheDocument();
    expect(screen.getByText(/1 message on this page is still being assembled/)).toBeInTheDocument();
  });

  it('readsThePartsOnlyWhenTheRowIsOpened_andShowsStateAndReasonForEach', async () => {
    getMessage.mockResolvedValue(REVIEW_MESSAGE);
    renderPage();
    const review = await rowFor(/Scanned enquiry/);
    // The list carries no parts, so nothing may have been requested before the row is opened.
    expect(getMessage).not.toHaveBeenCalled();

    fireEvent.click(within(review).getByRole('button', { name: /show message/i }));

    await waitFor(() => expect(getMessage).toHaveBeenCalledWith(103));
    const parts = await screen.findByRole('table', { name: /parts of scanned enquiry/i });
    expect(within(parts).getByText('scan.pdf')).toBeInTheDocument();
    expect(within(parts).getByText('Could not be read')).toBeInTheDocument();
    // A bare reason code must not reach a salesperson as snake_case.
    expect(within(parts).getByText('Attachment Unreadable')).toBeInTheDocument();
    expect(within(parts).queryByText('attachment_unreadable')).not.toBeInTheDocument();
    // The body has no filename; naming it by what it is beats an empty cell.
    expect(within(parts).getAllByText('Message text').length).toBeGreaterThan(0);
  });

  it('saysSoWhenTheDeploymentReportsNoPartsAtAll', async () => {
    listTriage.mockResolvedValue(NOISE_PAGE);
    renderPage();
    const sparse = await rowFor(/\(no subject\)/);
    fireEvent.click(within(sparse).getByRole('button', { name: /show message/i }));

    const detail = await screen.findByRole('region', { name: /message and extraction/i });
    expect(
      await within(detail).findByText(/does not report the individual parts of a message yet/i),
    ).toBeInTheDocument();
    expect(screen.queryByRole('table', { name: /parts of/i })).not.toBeInTheDocument();
  });

  it('keepsTheMessageReadableWhenItsPartsCannotBeRead', async () => {
    getMessage.mockRejectedValue({ isAxiosError: true, response: { status: 500, data: {} } });
    renderPage();
    const review = await rowFor(/Scanned enquiry/);
    fireEvent.click(within(review).getByRole('button', { name: /show message/i }));

    const detail = await screen.findByRole('region', { name: /message and extraction/i });
    expect(await within(detail).findByRole('alert')).toBeInTheDocument();
    // The decision the panel already had must survive the parts failing to load.
    expect(within(detail).getByText(/What became of it — No inquiry — needs review/)).toBeInTheDocument();
  });
});

describe('InboundMailTriagePage — skipped attachments', () => {
  beforeEach(() => {
    listTriage.mockResolvedValue(SKIPPED_PAGE);
    getMessage.mockResolvedValue(SKIPPED_MESSAGE);
  });

  const openSkippedRow = async (): Promise<HTMLElement> => {
    renderPage();
    const row = await rowFor(/Fit-out package — three files/);
    fireEvent.click(within(row).getByRole('button', { name: /show message/i }));
    return screen.findByRole('region', { name: /message and extraction/i });
  };

  it('showsWhatWasSkipped_soAnAttachmentCannotVanishSilently', async () => {
    const detail = await openSkippedRow();

    const table = await within(detail).findByRole('table', { name: /skipped attachments of/i });
    expect(within(table).getByText('deck.pptx')).toBeInTheDocument();
    expect(within(table).getByText("unsupported file type '.pptx'")).toBeInTheDocument();
    expect(within(table).getByText('site-video.mp4')).toBeInTheDocument();
    expect(within(table).getByText('exceeds the 10 MB size limit (18874368 bytes)')).toBeInTheDocument();
    // Each one says what happened to it in the record's own word.
    expect(within(table).getAllByText('Skipped')).toHaveLength(3);
    // A malformed entry still occupies a row: dropping it is the disappearance this fixes.
    expect(within(table).getByText('Not reported')).toBeInTheDocument();
  });

  it('doesNotCountASkippedAttachmentAsAPart', async () => {
    const detail = await openSkippedRow();
    await within(detail).findByRole('table', { name: /skipped attachments of/i });

    // The message is finished and says so — skips do not turn an assembled message into a failure.
    expect(within(detail).getByText(/What became of it — Inquiry created/)).toBeInTheDocument();
    expect(screen.getByText('2 of 2 parts assembled')).toBeInTheDocument();

    // The parts table is the scheduled work only: two components, neither of them a skip.
    const parts = within(detail).getByRole('table', { name: /parts of fit-out package/i });
    expect(within(parts).getAllByRole('row')).toHaveLength(3); // header + 2 parts
    expect(within(parts).queryByText('deck.pptx')).not.toBeInTheDocument();
    expect(within(parts).queryByText('site-video.mp4')).not.toBeInTheDocument();
    // And the cost of the skip is stated where the counts are, so "complete" cannot read as "all".
    expect(
      within(detail).getByText(/not counted among the parts above/i),
    ).toBeInTheDocument();
  });

  it('rendersNothingAtAllWhenNoAttachmentWasSkipped', async () => {
    listTriage.mockResolvedValue(
      readTriagePage(
        {
          items: [
            {
              id: 202,
              subject: 'Clean package',
              outcome: 'Inquiry',
              reasonCodes: [],
              hasAttachments: true,
              attachmentCount: 1,
              assemblyState: 'Assembled',
              skippedAttachments: [],
            },
          ],
          totalCount: 1,
          pageNumber: 1,
          pageSize: 25,
        },
        1,
      ),
    );
    renderPage();
    const row = await rowFor(/Clean package/);
    fireEvent.click(within(row).getByRole('button', { name: /show message/i }));

    const detail = await screen.findByRole('region', { name: /message and extraction/i });
    expect(within(detail).queryByText(/never sent for extraction/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('table', { name: /skipped attachments of/i })).not.toBeInTheDocument();
    // Reported and empty IS an answer, so nothing is owed to the operator here.
    expect(within(detail).queryByText(/whether any attachment was skipped/i)).not.toBeInTheDocument();
  });

  it('doesNotClaimNothingWasSkippedWhenTheDeploymentNeverSaid', async () => {
    listTriage.mockResolvedValue(INQUIRY_PAGE);
    renderPage();
    const row = await rowFor(/Requirement — Jebel Ali/);
    fireEvent.click(within(row).getByRole('button', { name: /show message/i }));

    const detail = await screen.findByRole('region', { name: /message and extraction/i });
    expect(screen.queryByRole('table', { name: /skipped attachments of/i })).not.toBeInTheDocument();
    expect(
      within(detail).getByText(/Whether any attachment was skipped .* is not reported by this deployment/i),
    ).toBeInTheDocument();
  });
});

describe('readPollReport', () => {
  it('treatsAnAbsentMailboxCountAsNothingPolled', () => {
    const report = readPollReport({
      message: 'No active IMAP mailbox is configured, so no mail was fetched.',
    });
    expect(report.anyMailboxPolled).toBe(false);
    expect(report.messagesFound).toBeNull();
    expect(report.failures).toEqual([]);
  });

  it('readsTheCountsFromTotals_andKeepsUnreportedOnesNullRatherThanZero', () => {
    const report = readPollReport({
      mailboxes: 1,
      newMessages: 0,
      totals: { mailboxesPolled: 1, messagesDownloaded: 0, messagesCaptured: 0 },
    });
    expect(report.anyMailboxPolled).toBe(true);
    expect(report.messagesNew).toBe(0);
    expect(report.captured).toBe(0);
    expect(report.rejected).toBeNull();
    expect(report.scheduled).toBeNull();
    expect(report.lookbackCappedDays).toBeNull();
  });

  it('countsAMailboxThatReportedFailureEvenWithoutAReasonsArray', () => {
    const report = readPollReport({
      mailboxes: 2,
      totals: { mailboxesPolled: 2, mailboxesFailed: 1 },
      polled: [
        { mailbox: 'good@example.test', succeeded: true },
        { mailbox: 'bad@example.test', succeeded: false, reason: 'The sign-in was refused.' },
      ],
    });
    expect(report.failures).toHaveLength(1);
    expect(report.failures[0]).toMatchObject({ mailbox: 'bad@example.test' });
  });

  it('keepsThePartlyFailedCyclesWorkVisible', () => {
    // The 502 branch reports the same work detail as the success branch: a partly-failed cycle
    // can still have captured mail, and hiding that sends an operator hunting for it.
    const report = readPollFailureReport({
      response: {
        status: 502,
        data: {
          message: '1 of 2 mailbox(es) could not be polled.',
          reasons: [{ mailbox: 'bad@example.test', reason: 'The sign-in was refused.' }],
          mailboxes: 2,
          totals: { mailboxesPolled: 2, mailboxesFailed: 1, messagesCaptured: 4 },
        },
      },
    });
    expect(report?.captured).toBe(4);
    expect(report?.failures).toHaveLength(1);
  });
});

describe('assembly reading', () => {
  it('readsTheRealPipelineStates', () => {
    expect(describeAssemblyState('Captured').inProgress).toBe(true);
    expect(describeAssemblyState('Inspecting').inProgress).toBe(true);
    expect(describeAssemblyState('Extracting').inProgress).toBe(true);
    expect(describeAssemblyState('ReadyForAssembly').inProgress).toBe(true);
    expect(describeAssemblyState('Assembled')).toMatchObject({ inProgress: false, needsHuman: false });
    expect(describeAssemblyState('NeedsReview').needsHuman).toBe(true);
    expect(describeAssemblyState('RejectedSecurity').needsHuman).toBe(true);
  });

  it('doesNotPromiseAnAutomaticRetryForAHeldMessage', () => {
    // Nothing sweeps held components in this build, so "held" must name a human action, not a
    // retry that never comes.
    const held = describeAssemblyState('FailedRecoverable');
    expect(held.inProgress).toBe(false);
    expect(held.needsHuman).toBe(true);
    expect(held.meaning).toMatch(/nothing picks this up on its own/i);
  });

  it('doesNotColourAnEmptyMessageAsAFailure', () => {
    // "Nothing to quote" and "we could not read it" are different facts about different mail.
    const empty = describeAssemblyState('NoInquiry');
    expect(empty.needsHuman).toBe(false);
    expect(empty.chipColor).toBe('default');
  });

  it('neverTreatsAnUnknownStateAsFinished', () => {
    const unknown = describeAssemblyState('QuantumSuperposition');
    expect(unknown.recognised).toBe(false);
    expect(unknown.inProgress).toBe(false);
    expect(unknown.needsHuman).toBe(false);
  });

  it('acceptsTheWorkerKeySpellingsAndKeepsAbsenceDistinct', () => {
    const reported = readTriageRow({
      id: 9,
      outcome: 'Inquiry',
      assemblyState: 'NeedsReview',
      assembledLeadId: 12,
      expectedComponentCount: 1,
      lastUpdatedAtUtc: '2026-08-05T10:00:00Z',
      components: [
        { id: 4, ordinal: 0, fileName: 'a.pdf', kind: 'Attachment', state: 'Skipped', reasonCode: 'unreadable_pdf' },
      ],
    });
    expect(reported.leadId).toBe(12);
    expect(reported.componentsExpected).toBe(1);
    expect(reported.lastUpdatedOn).toBe('2026-08-05T10:00:00Z');
    expect(reported.componentsReported).toBe(true);
    expect(reported.components[0]).toMatchObject({
      fileName: 'a.pdf',
      state: 'Skipped',
      reason: 'unreadable_pdf',
    });
    expect(reported.skippedAttachments).toEqual([]);

    const silent = readTriageRow({ id: 10, outcome: 'Noise' });
    expect(silent.assemblyState).toBeNull();
    expect(silent.componentsExpected).toBeNull();
    // Null components mean "not asked for", never "this message has no parts".
    expect(silent.componentsReported).toBe(false);
    expect(describeAssemblyState(silent.assemblyState).recognised).toBe(false);
  });

  it('keepsLegacySkippedAttachmentsVisibleBesideCanonicalComponents', async () => {
    listTriage.mockResolvedValue(ASSEMBLY_PAGE);
    getMessage.mockResolvedValue(REVIEW_MESSAGE);

    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: /show message scanned enquiry/i }));

    expect(await screen.findByText('Attachments skipped before component tracking')).toBeInTheDocument();
    expect(screen.getByText('legacy-price-sheet.xls (unsupported_format)')).toBeInTheDocument();
    expect(screen.getByText(/original email is retained and verified/i)).toBeInTheDocument();
    expect(screen.getByText(/sender timestamp/i)).toBeInTheDocument();
    expect(screen.getByText(/extraction finished/i)).toBeInTheDocument();
  });
});

describe('readTriageRow', () => {
  it('keepsAbsenceDistinctFromFalseAndZero', () => {
    const row = readTriageRow({ id: 9, outcome: 'Uncertain' });
    expect(row.hasAttachments).toBeNull();
    expect(row.attachmentCount).toBeNull();
    expect(row.extractedItemCount).toBeNull();
    expect(row.attachmentNamesReported).toBe(false);
    expect(row.reasonCodes).toEqual([]);
  });

  it('derivesAttachmentPresenceFromNamesWhenTheFlagIsAbsent', () => {
    const row = readTriageRow({ id: 9, outcome: 'Inquiry', attachmentNames: ['boq.xlsx', ' '] });
    expect(row.attachmentNames).toEqual(['boq.xlsx']);
    expect(row.attachmentCount).toBe(1);
    expect(row.hasAttachments).toBe(true);
    expect(row.attachmentNamesReported).toBe(true);
  });

  it('splitsASkippedAttachmentIntoItsNameAndItsReason', () => {
    const row = readTriageRow({
      id: 9,
      outcome: 'Inquiry',
      skippedAttachments: [
        "deck.pptx (unsupported file type '.pptx')",
        // Brackets inside the reason, and brackets inside the name: both are everyday mail, and
        // anchoring on the first or the last bracket gets one of them wrong.
        'site-video.mp4 (exceeds the 10 MB size limit (18874368 bytes))',
        "quote (1).pdf (unsupported file type '.pdf')",
        // A path is never rendered as one — only the leaf survives.
        'C:\\Users\\buyer\\Desktop\\boq.xlsx (could not be read)',
        'orphan.dwg',
      ],
    });
    expect(row.skippedAttachmentsReported).toBe(true);
    expect(row.skippedAttachments.map((entry) => [entry.fileName, entry.reason])).toEqual([
      ['deck.pptx', "unsupported file type '.pptx'"],
      ['site-video.mp4', 'exceeds the 10 MB size limit (18874368 bytes)'],
      ['quote (1).pdf', "unsupported file type '.pdf'"],
      ['boq.xlsx', 'could not be read'],
      ['orphan.dwg', null],
    ]);
  });

  it('keepsAMalformedSkipEntryRatherThanDroppingIt', () => {
    const row = readTriageRow({ id: 9, outcome: 'Inquiry', skippedAttachments: [42, '  ', 'a.pdf (too large)'] });
    // Three attachments were recorded as skipped, so three are shown. An entry this build cannot
    // read is unnamed, never absent — absence is the defect the record exists to prevent.
    expect(row.skippedAttachments).toHaveLength(3);
    expect(row.skippedAttachments[0]).toMatchObject({ fileName: null, reason: null });
    expect(row.skippedAttachments[2]).toMatchObject({ fileName: 'a.pdf', reason: 'too large' });
  });

  it('keepsAnAbsentSkipListDistinctFromAnEmptyOne', () => {
    const silent = readTriageRow({ id: 9, outcome: 'Inquiry' });
    expect(silent.skippedAttachmentsReported).toBe(false);
    expect(silent.skippedAttachments).toEqual([]);

    const clean = readTriageRow({ id: 10, outcome: 'Inquiry', skippedAttachments: [] });
    expect(clean.skippedAttachmentsReported).toBe(true);
    expect(clean.skippedAttachments).toEqual([]);
  });

  it('readsABareArrayResponseAsOnePage', () => {
    const page = readTriagePage([{ id: 1, outcome: 'Noise' }], 3);
    expect(page.items).toHaveLength(1);
    expect(page.pageNumber).toBe(3);
    expect(page.pageSize).toBeNull();
  });
});

describe('isTriageUnavailable', () => {
  it('treatsAnUnentitledTenantAsNotSwitchedOn_notAsAPermissionToAskFor', () => {
    // The generic 403 copy is "ask an administrator if you need it", and no administrator can
    // grant an entitlement — it is a plan-level switch. Matching it here is what replaces that
    // dead end with an honest explanation.
    expect(
      isTriageUnavailable({
        response: {
          status: 403,
          data: {
            type: 'https://nexora.invalid/problems/feature-not-entitled',
            title: 'Feature is not entitled',
          },
        },
      }),
    ).toBe(true);
  });

  it('leavesAnOrdinaryRoleDenialAlone', () => {
    // A role denial IS something an administrator can fix, so it must keep its own wording.
    expect(isTriageUnavailable({ response: { status: 403, data: { title: 'Forbidden' } } })).toBe(false);
    expect(isTriageUnavailable({ response: { status: 403 } })).toBe(false);
  });

  it('stillTreatsAnAbsentEndpointAsNotShipped', () => {
    expect(isTriageUnavailable({ response: { status: 404 } })).toBe(true);
    expect(isTriageUnavailable({ response: { status: 501 } })).toBe(true);
    expect(isTriageUnavailable({ response: { status: 500 } })).toBe(false);
  });
});

describe('describeReopenAbility', () => {
  it('allowsTheTwoShapesOfStrandedMessage', () => {
    // These are exactly the two the governed reopen accepts server-side. A held message is the
    // P0: nothing else in the system sweeps one back into flight.
    expect(describeReopenAbility('NoInquiry')).toEqual({ canReopen: true, disabledReason: null });
    expect(describeReopenAbility('FailedRecoverable')).toEqual({ canReopen: true, disabledReason: null });
  });

  it('refusesTheStatesTheEndpointRefuses_andSaysWhyWithoutNamingThem', () => {
    for (const state of ['Assembled', 'NeedsReview', 'RejectedSecurity', 'Extracting', 'ReadyForAssembly']) {
      const ability = describeReopenAbility(state);
      expect(ability.canReopen).toBe(false);
      expect(ability.disabledReason).toBeTruthy();
      expect(ability.disabledReason).not.toContain(state);
    }
  });

  it('resolvesTheWorkerKeySpellingsThroughTheSameAliasTable', () => {
    // `completed` is the worker's spelling of Assembled. Reading it as an unknown state would put
    // the button back on a message that already became an inquiry.
    expect(describeReopenAbility('completed').canReopen).toBe(false);
    expect(describeReopenAbility('review').canReopen).toBe(false);
    expect(describeReopenAbility('held').canReopen).toBe(true);
  });

  it('keepsTheControlWhereThisBuildCannotJudge', () => {
    // Mail that predates the assembly aggregate reports no state, and its legacy reopen path is
    // still live. Hiding a real recovery because the wording drifted is the worse mistake.
    expect(describeReopenAbility(null).canReopen).toBe(true);
    expect(describeReopenAbility('SomethingThisBuildHasNeverSeen').canReopen).toBe(true);
  });
});

describe('describeMessageProgress — the checkpoint reads backwards, so the screen never uses it', () => {
  it('callsAFinishedMessageThatProducedNothingALoss_evenWhenTheCheckpointSaysQueued', () => {
    // Measured live: ingests whose assembly is CLOSED at NeedsReview carry ParseStatus = Queued,
    // which reads as "still being worked on". It is finished, and it produced nothing.
    const progress = describeMessageProgress('NeedsReview', false);
    expect(progress.kind).toBe('no-inquiry');
    expect(progress.label).toMatch(/^No inquiry/i);
    expect(progress.chipColor).not.toBe('success');
  });

  it('callsAMessageThatProducedALeadASuccess_evenWhenTheCheckpointSaysNeedsReview', () => {
    // The other half of the inversion: a SUCCESSFUL ingest carries ParseStatus = NeedsReview,
    // which reads as a problem. A lead exists — the only thing outstanding is a human decision.
    const progress = describeMessageProgress('NeedsReview', true);
    expect(progress.kind).toBe('inquiry-created');
    expect(progress.label).toBe('Inquiry created — needs your review');
    expect(progress.chipColor).toBe('success');
  });

  it('keepsInFlightDistinctFromBothFinishedAnswers', () => {
    for (const state of ['Captured', 'Inspecting', 'Extracting', 'ReadyForAssembly']) {
      expect(describeMessageProgress(state, false).kind).toBe('in-flight');
    }
  });

  it('readsTheAssemblyAsTheTruthWhereTheTwoDisagree', () => {
    // A message with a lead is an inquiry however it got there; one without is not, whatever the
    // per-ingest checkpoint says. The checkpoint is not an input to this function at all.
    expect(describeMessageProgress('Assembled', true).kind).toBe('inquiry-created');
    expect(describeMessageProgress('NoInquiry', false).kind).toBe('no-inquiry');
    expect(describeMessageProgress('FailedRecoverable', false).kind).toBe('no-inquiry');
    expect(describeMessageProgress('RejectedSecurity', false).kind).toBe('no-inquiry');
  });

  it('judgesPreAssemblyMailOnTheLeadAlone', () => {
    expect(describeMessageProgress(null, true).kind).toBe('inquiry-created');
    expect(describeMessageProgress(null, false).kind).toBe('unknown');
    // An unrecognised state is never called finished-and-successful on its own.
    expect(describeMessageProgress('QuantumSuperposition', false).kind).toBe('unknown');
  });

  it('neverPrintsAnInternalStateNameAsALabel', () => {
    for (const state of ['Captured', 'Inspecting', 'Assembled', 'ReadyForAssembly', 'NeedsReview',
      'FailedRecoverable', 'NoInquiry', 'RejectedSecurity']) {
      for (const hasLead of [true, false]) {
        expect(describeMessageProgress(state, hasLead).label).not.toContain(state);
      }
    }
  });
});

describe('presentableReason — an operator reads a sentence, never a code', () => {
  // The exact strings the live tenant stores. EmailInquiryAssemblyCoordinator.HoldForReviewAsync
  // writes "{code}: {detail}" into one column because the code is what a query groups by, so both
  // audiences were being served the same string and every held message opened its explanation
  // with a snake_case token.
  const LIVE_LEAD_NOT_PRODUCED =
    'assembly_lead_not_produced: This message repeats an inquiry Nexora already has, so no second '
    + 'inquiry was created. Nothing is lost. Open Possible Matches to say whether it is the same '
    + 'request or a new one; the message finishes as soon as you decide.';
  const LIVE_NO_REQUESTABLE_CONTENT =
    'assembly_no_requestable_content: This message was read in full and names no product, quantity '
    + 'or specification anywhere, so no inquiry was created.';

  it('stripsTheMachineCodeAndKeepsTheWholeSentence', () => {
    const shown = presentableReason(LIVE_LEAD_NOT_PRODUCED);
    expect(shown).not.toContain('assembly_lead_not_produced');
    expect(shown).not.toContain('_');
    expect(shown).toMatch(/^This message repeats an inquiry Nexora already has/);
    expect(shown).toContain('Open Possible Matches');
  });

  it('stripsItForEveryHoldCodeAndNotJustTheOneThatWasReported', () => {
    // 37 of the 57 held messages on the live tenant carry this code, not the other one. A fix
    // keyed on the string that happened to be in the report would have left them unchanged.
    const shown = presentableReason(LIVE_NO_REQUESTABLE_CONTENT);
    expect(shown).toMatch(/^This message was read in full/);
    expect(shown).not.toContain('_');
  });

  it('stillTitleCasesABareCodeThatCarriesNoSentence', () => {
    // The other shape the wire carries: a component reason code with no detail beside it. There
    // is nothing to strip, and rendering it as nothing would delete the only explanation there is.
    expect(presentableReason('stranded_extraction_job_missing')).toBe('Stranded Extraction Job Missing');
  });

  it('leavesAPlainSentenceExactlyAsAuthored', () => {
    const plain = 'No part of this message could be read. Its attachments were refused or are unsupported.';
    expect(presentableReason(plain)).toBe(plain);
  });
});
