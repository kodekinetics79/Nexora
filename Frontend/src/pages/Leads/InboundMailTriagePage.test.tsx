import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import InboundMailTriagePage from './InboundMailTriagePage';
import {
  describeAssemblyState,
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
      reprocess: (id: number, reason: string) => reprocess(id, reason),
      pollMailboxes: () => pollMailboxes(),
      getMessage: (id: number) => getMessage(id),
    },
  };
});

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
});

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
  it('landsOnRejections_soALostDealIsTheFirstThingVisible', async () => {
    renderPage();
    await waitFor(() => expect(listTriage).toHaveBeenCalled());
    expect(listTriage).toHaveBeenCalledWith(expect.objectContaining({ outcome: 'Noise' }));
    expect(screen.getByRole('tab', { name: /rejected as noise/i })).toHaveAttribute('aria-selected', 'true');
    expect(await screen.findByText(/Automatic reply: Cable tray enquiry/)).toBeInTheDocument();
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

    await waitFor(() => expect(reprocess).toHaveBeenCalledWith(41, 'Real enquiry hidden behind an auto-reply.'));
    expect(await screen.findByText(/was sent back through extraction as an inquiry/i)).toBeInTheDocument();
  });

  it('hidesTheOverrideWhenTheRoleCannotUseIt', async () => {
    hasPermission.mockReturnValue(false);
    renderPage();
    await screen.findByText(/Automatic reply: Cable tray enquiry/);
    expect(screen.queryByRole('button', { name: /reprocess as inquiry/i })).not.toBeInTheDocument();
    expect(screen.getByText(/needs the Leads permission/i)).toBeInTheDocument();
  });

  it('queriesTheSelectedOutcomeWhenTheTabChanges', async () => {
    renderPage();
    await screen.findByText(/Automatic reply: Cable tray enquiry/);
    listTriage.mockResolvedValue(INQUIRY_PAGE);

    fireEvent.click(screen.getByRole('tab', { name: /^extracted$/i }));

    await waitFor(() => expect(listTriage).toHaveBeenCalledWith(expect.objectContaining({ outcome: 'Inquiry' })));
    expect(await screen.findByText(/Requirement — Jebel Ali/)).toBeInTheDocument();
  });

  it('explainsAMissingBackendInsteadOfShowingAFailure', async () => {
    listTriage.mockRejectedValue({ response: { status: 404 }, isAxiosError: true });
    renderPage();
    expect(await screen.findByText(/not available in this deployment yet/i)).toBeInTheDocument();
    expect(screen.getByText(/no message has been hidden/i)).toBeInTheDocument();
  });

  it('surfacesARealFailureWithARetry', async () => {
    listTriage.mockRejectedValue({ response: { status: 500, data: {} }, isAxiosError: true });
    renderPage();
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /try again/i })).toBeInTheDocument();
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
    expect(within(assembled).getByText('Assembled')).toBeInTheDocument();
    expect(within(assembled).getByText('2 of 2 parts assembled')).toBeInTheDocument();

    const review = await rowFor(/Scanned enquiry/);
    expect(within(review).getByText('Needs review')).toBeInTheDocument();
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
    expect(within(detail).getByText(/Assembly — Needs review/)).toBeInTheDocument();
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

    const silent = readTriageRow({ id: 10, outcome: 'Noise' });
    expect(silent.assemblyState).toBeNull();
    expect(silent.componentsExpected).toBeNull();
    // Null components mean "not asked for", never "this message has no parts".
    expect(silent.componentsReported).toBe(false);
    expect(describeAssemblyState(silent.assemblyState).recognised).toBe(false);
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

  it('readsABareArrayResponseAsOnePage', () => {
    const page = readTriagePage([{ id: 1, outcome: 'Noise' }], 3);
    expect(page.items).toHaveLength(1);
    expect(page.pageNumber).toBe(3);
    expect(page.pageSize).toBeNull();
  });
});
