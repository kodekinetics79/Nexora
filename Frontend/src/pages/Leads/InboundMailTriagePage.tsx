import { Fragment, useMemo, useState } from 'react';
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link as RouterLink, useNavigate } from 'react-router-dom';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  IconButton,
  Link,
  Paper,
  Stack,
  Tab,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Tabs,
  TextField,
  Typography,
} from '@mui/material';
import {
  ExpandLess as CollapseIcon,
  ExpandMore as ExpandIcon,
  MarkEmailRead as InboxIcon,
  OpenInNew as OpenIcon,
  Refresh as RefreshIcon,
  Sync as PollIcon,
  Undo as ReprocessIcon,
} from '@mui/icons-material';
import ApiErrorNotice from '../../components/common/ApiErrorNotice';
import { EmptyState, LoadingState } from '../../platform/components/States';
import { useAuth } from '../../context/AuthContext';
import { INBOX_ROOT } from '../../components/layout/navCatalog';
import { presentableServerText } from '../../utils/apiErrors';
import emailTriageService, {
  describeAssemblyState,
  describeComponentKind,
  describeComponentState,
  describeMessageProgress,
  describeReopenAbility,
  describeTriageOutcome,
  describeTriageReason,
  isTriageUnavailable,
  readPollFailureReport,
  TRIAGE_REASON_DESCRIPTIONS,
  type EmailTriageOutcome,
  type EmailTriageRow,
  type MailPollReport,
} from '../../api/services/emailTriageService';
import mailboxService, { type Mailbox } from '../../api/services/mailboxService';

/**
 * Inbound Mail — the Email Intake screen. What the system decided about every message that reached
 * the ingestion mailbox, how far each one got towards becoming a lead, and a way to poll now.
 *
 * The default tab is deliberately "Rejected as noise". A rejection is the only triage outcome that
 * produces nothing downstream, so it is the only one that can hide a lost deal. Making it the
 * landing view is the whole point of the screen: a rep can see, in one place, every enquiry the
 * machine decided not to look at — and put any of them back with a reason.
 *
 * Rules that govern the copy here:
 *  - a field the backend has not shipped renders as "Not reported", never as 0, blank or a guess;
 *  - for a conversational enquiry the prose IS the evidence, so the original message text is shown
 *    beside what came out of it, and the body/attachment split is always stated;
 *  - an attachment the door refused to schedule is shown as SKIPPED beside the parts and never
 *    among them: no job was ever queued for it, so it sits outside the assembly totals on purpose,
 *    and it is the one thing that can otherwise leave this screen with no trace at all;
 *  - "Open lead" appears only where a lead id actually exists. A message that needs review, was
 *    rejected, or is still being assembled has no lead, and offering the action anyway would send
 *    a rep to a dead route and teach them the screen lies;
 *  - the same rule now governs every other action. "Reprocess as inquiry" is rendered only where
 *    the endpoint can actually accept it, and a row that has neither a lead nor a usable reprocess
 *    carries the action it DOES have — its ingestion batch, and for an administrator the
 *    dead-letter replay — so no row is a dead end;
 *  - an empty list never says "nothing is being hidden from you" without checking the mailboxes
 *    first. With no active inbox, or one that has been failing to sign in, everything IS hidden;
 *  - a poll reports what it DID (mailboxes checked, messages found, captured, scheduled, flagged,
 *    rejected, failures) rather than a green tick. A 200 from /api/Email/fetch is not uniformly a
 *    success — with no active IMAP mailbox nothing was polled and nothing ever will be (ING-08).
 */

const PAGE_SIZE = 25;

/**
 * How often the list re-reads itself while any message is still being assembled.
 *
 * Assembly happens on a background worker, so a row that says "Assembling" becomes stale the
 * moment it renders. Without this, the delayed-worker case looks identical to a stuck one and the
 * only cure a rep has is to keep pressing Refresh. Nothing polls when nothing is in flight.
 */
const ASSEMBLY_REFRESH_MS = 15_000;

interface TriageTab {
  /** Stable identity for the tab. Not the outcome, because "All" sends no outcome at all. */
  key: string;
  /** Null sends no `outcome` parameter, which the endpoint reads as "every decision". */
  outcome: EmailTriageOutcome | null;
  label: string;
  blurb: string;
  emptyTitle: string;
  emptyMessage: string;
}

const TABS: TriageTab[] = [
  {
    // EVERY message, including the ones no other tab can show.
    //
    // A row whose TriageOutcome is null — written today by manual upload, watched folders, the
    // lead uploader and lead identity reconciliation — matches no outcome filter, so it appeared
    // on none of the four decision tabs. Mail the system holds and cannot show is the one failure
    // this screen exists to make impossible, and the fix is a tab that filters nothing.
    key: 'All',
    outcome: null,
    label: 'All messages',
    blurb:
      'Every message this mailbox has taken, whatever the system decided about it — including mail that arrived before the triage gate existed and documents brought in from an upload or a watched folder, which carry no decision at all.',
    emptyTitle: 'No message has reached this screen',
    emptyMessage:
      'Nothing has been ingested for this business unit yet. If you are expecting mail, check that an inbox is connected and polling.',
  },
  {
    key: 'Noise',
    outcome: 'Noise',
    label: 'Rejected as noise',
    blurb:
      'Messages stopped before any AI was spent — auto-replies, mailing lists, no-reply senders, calendar invites and replies with nothing new in them. Nothing was extracted, but every original email is retained and can be put back.',
    emptyTitle: 'No message has been rejected',
    emptyMessage: 'Nothing that reached the mailbox was classed as noise. Nothing is being hidden from you.',
  },
  {
    key: 'CommercialNonInquiry',
    outcome: 'CommercialNonInquiry',
    label: 'Routed as supplier document',
    blurb:
      'Messages from a supplier that read as a quotation or an invoice. They are handled as commercial documents instead of being turned into a customer inquiry.',
    emptyTitle: 'No supplier document has been routed',
    emptyMessage: 'No inbound message has been recognised as a supplier quotation or invoice yet.',
  },
  {
    key: 'Uncertain',
    outcome: 'Uncertain',
    label: 'Sent for extraction — uncertain',
    blurb:
      'Nothing decisive was found either way, so the message was sent to extraction and flagged. Uncertainty never stops a message — only positive evidence of noise does.',
    emptyTitle: 'No uncertain message',
    emptyMessage: 'Every message so far carried enough evidence for a definite decision.',
  },
  {
    key: 'Inquiry',
    outcome: 'Inquiry',
    label: 'Sent for inquiry extraction',
    blurb:
      'Messages recognised as customer enquiries and sent for extraction — including free-prose enquiries written straight into the email body.',
    emptyTitle: 'No message has been sent for inquiry extraction',
    emptyMessage: 'No inbound email has been recognised as a customer enquiry yet.',
  },
];

/**
 * The triage outcome is a routing decision, not proof that extraction succeeded. The API-level
 * copy predates the message-level assembly result and says "Extracted" for Inquiry/Uncertain,
 * which reads as a completed transformation beside an honest "No inquiry — needs review" result.
 * Keep the API contract intact and name the two extraction routes for what actually happened.
 */
const describeRoutingDecision = (outcome: string) => {
  const copy = describeTriageOutcome(outcome);
  if (outcome === 'Inquiry') return { ...copy, label: 'Sent to inquiry extraction' };
  if (outcome === 'Uncertain') return { ...copy, label: 'Sent to extraction — uncertain' };
  return copy;
};

const NOT_REPORTED = 'Not reported';

const VISUALLY_HIDDEN = {
  position: 'absolute',
  width: 1,
  height: 1,
  p: 0,
  m: '-1px',
  overflow: 'hidden',
  whiteSpace: 'nowrap',
  border: 0,
  clipPath: 'inset(50%)',
} as const;

/** Absent, unparseable and present are three different things; only the last one shows a date. */
export const formatReceived = (value: string | null): string => {
  if (!value) return NOT_REPORTED;
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
};

const readableHint = (hint: string): string =>
  hint
    .replaceAll('_', ' ')
    .toLowerCase()
    .replace(/(^|\s)\S/g, (letter) => letter.toUpperCase());

/**
 * States the body/attachment split without inventing it. `Noise` is the one case the frontend can
 * state from the contract alone: a rejected message enqueues no jobs at all.
 */
const describeBodyRouting = (row: EmailTriageRow): string => {
  if (row.bodySubmitted === true) return 'The message text was submitted for extraction.';
  if (row.bodySubmitted === false) return 'The message text was not submitted for extraction.';
  if (row.outcome === 'Noise') return 'No extraction was attempted for this message.';
  return `Whether the message text was submitted is ${NOT_REPORTED.toLowerCase()}.`;
};

/**
 * Turns a server-supplied reason into product copy.
 *
 * A stable snake_case code is Nexora's own vocabulary and is simply title-cased. Anything else is
 * free text the server authored, and goes through the same gate every other backend string in the
 * product does — bounded, printable, no hostnames, no stack frames. A reason that fails the gate
 * renders as nothing rather than as operator diagnostics.
 */
export const presentableReason = (value: string | null): string | null => {
  if (!value) return null;
  if (/^[a-z0-9]+(_[a-z0-9]+)+$/.test(value)) return readableHint(value);
  return presentableServerText(value);
};

/** "3 of 5 parts assembled" — omitted entirely when the deployment reports no component totals. */
const describeComponentProgress = (row: EmailTriageRow): string | null => {
  if (row.componentsExpected === null) return null;
  const done = row.componentsCompleted ?? 0;
  return `${done} of ${row.componentsExpected} part${row.componentsExpected === 1 ? '' : 's'} assembled`;
};

/**
 * The sentence stating why there is no way through to a lead. Returns null when a lead exists —
 * the caller renders the "Open lead" action instead.
 */
const describeMissingLead = (row: EmailTriageRow): string | null => {
  if (row.leadId !== null) return null;
  const assembly = describeAssemblyState(row.assemblyState);
  if (assembly.inProgress) return 'No lead exists yet — this message is still being assembled.';
  if (assembly.needsHuman) return 'No lead was created for this message.';
  if (!assembly.recognised && row.assemblyState !== null)
    return 'No lead has been recorded against this message.';
  return null;
};

const describeAttachmentRouting = (row: EmailTriageRow): string => {
  if (row.hasAttachments === false) return 'No attachments came with this message.';
  if (row.hasAttachments === null) return `Attachments are ${NOT_REPORTED.toLowerCase()} for this message.`;
  if (row.attachmentCount === null) return 'Attachments came with this message and are extracted separately from the text.';
  return row.attachmentCount === 1
    ? '1 attachment came with this message and is extracted separately from the text.'
    : `${row.attachmentCount} attachments came with this message and are extracted separately from the text.`;
};

const plural = (count: number, singular: string, pluralForm = `${singular}s`): string =>
  `${count} ${count === 1 ? singular : pluralForm}`;

/**
 * What a skipped attachment costs, in the operator's own terms (ING-06).
 *
 * Deliberately not worded as a failure: a message can be fully assembled AND carry skips, and that
 * is a correct outcome, not a stuck pipeline. It is also a piece of the customer's request that
 * nobody read, so the one thing this sentence must never do is let it pass as ordinary.
 */
const describeSkippedImpact = (count: number): string =>
  `${plural(count, 'attachment')} came with this message and ${count === 1 ? 'was' : 'were'} never `
  + `sent for extraction, so nothing inside ${count === 1 ? 'it' : 'them'} can be quoted. `
  + `${count === 1 ? 'It is' : 'They are'} not counted among the parts above — a message can finish `
  + 'assembling and still be short of what the customer sent.';

const pollSeverity = (report: MailPollReport): 'success' | 'info' | 'warning' | 'error' => {
  if (report.failures.length > 0) return 'error';
  if (!report.anyMailboxPolled) return 'warning';
  // Mail this cycle could not take, or could not even see, is not a clean run.
  if ((report.notAcknowledged ?? 0) > 0 || (report.lookbackCappedDays ?? 0) > 0) return 'warning';
  return (report.messagesNew ?? 0) > 0 ? 'success' : 'info';
};

const pollHeadline = (report: MailPollReport): string => {
  if (report.failures.length > 0)
    return `${plural(report.failures.length, 'mailbox', 'mailboxes')} could not be polled`;
  // Nothing was polled and nothing ever will be until a mailbox is connected. Rendering this as a
  // success is what sends a tenant back to this button forever instead of to mailbox settings.
  if (!report.anyMailboxPolled) return 'No mailbox was polled';
  const checked = plural(report.mailboxesChecked ?? 0, 'mailbox', 'mailboxes');
  if (report.messagesNew === null) return `Polled ${checked}`;
  return report.messagesNew > 0
    ? `Polled ${checked} — ${plural(report.messagesNew, 'new message')}`
    : `Polled ${checked} — no new mail`;
};

/**
 * Why an empty Inbound Mail list is empty — read from the mailboxes, not assumed.
 *
 * "Nothing is being hidden from you" was printed without ever asking whether a mailbox exists.
 * The overwhelmingly common cause of an empty list is that no inbox is connected, or that the
 * connected one has been failing to sign in for days, and reassuring a tenant that nothing is
 * hidden is exactly wrong there: everything is hidden, in their mailbox, unread.
 *
 * Returns null when the mailboxes are healthy enough that the empty list really is an empty list.
 */
const describeMailboxObstacle = (
  mailboxes: Mailbox[],
): { title: string; message: string } | null => {
  const inbound = mailboxes.filter((mailbox) => mailbox.protocol === 'IMAP');
  const active = inbound.filter((mailbox) => mailbox.isActive);
  if (inbound.length === 0)
    return {
      title: 'No inbox is connected, so no mail can arrive',
      message:
        'This list is empty because nothing is being collected — not because nothing was sent. '
        + 'Connect the mailbox enquiries are sent to, and this screen will show what the system '
        + 'decides about each message.',
    };
  if (active.length === 0)
    return {
      title: 'Every connected inbox is switched off',
      message:
        'The mailboxes are set up but none of them is active, so nothing is being collected. '
        + 'Mail sent to them is still sitting in the mailbox, unread by this system.',
    };
  const failing = active.filter((mailbox) => mailbox.healthState === 'Failing');
  if (failing.length === active.length)
    return {
      title:
        active.length === 1
          ? 'The connected inbox cannot be read'
          : 'None of the connected inboxes can be read',
      message:
        `${failing.map((mailbox) => mailbox.emailAddress).join(', ')} — the last attempts to sign `
        + 'in failed, so nothing new has been collected. Mail sent since then is still in the '
        + 'mailbox. Check the credentials and the host settings.',
    };
  const neverPolled = active.filter((mailbox) => mailbox.healthState === 'Never polled');
  if (neverPolled.length === active.length)
    return {
      title: 'The inbox has never been read',
      message:
        `${neverPolled.map((mailbox) => mailbox.emailAddress).join(', ')} — this inbox has been `
        + 'saved but never successfully polled, so nothing has been collected from it yet. Use '
        + '"Poll now" above, or check the connection settings if it keeps failing.',
    };
  return null;
};

/**
 * One override, one key. `crypto.randomUUID` is not available on every browser this product
 * supports over plain HTTP, and a thrown ReferenceError here would take the whole screen down,
 * so the fallback is deliberate — uniqueness within a session is all the key has to provide.
 */
const newIdempotencyKey = (): string =>
  typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
    ? crypto.randomUUID()
    : `reprocess-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;

/**
 * The counts, stated one by one. Only what the server actually reported appears: "0 rejected" and
 * "rejections not reported" are different claims, and a poll summary that invents the first is
 * exactly the bare-toast problem this panel replaces.
 */
const pollCountLine = (report: MailPollReport): string | null => {
  const parts = [
    report.messagesFound === null ? null : `${plural(report.messagesFound, 'message')} in the poll window`,
    report.messagesNew === null ? null : `${report.messagesNew} new`,
    report.alreadyIngested === null ? null : `${report.alreadyIngested} already ingested`,
    report.captured === null ? null : `${report.captured} captured`,
    report.scheduled === null ? null : `${plural(report.scheduled, 'part')} queued for extraction`,
    report.needsReview === null ? null : `${report.needsReview} held for review`,
    report.rejected === null ? null : `${report.rejected} rejected`,
  ].filter((part): part is string => part !== null);
  return parts.length > 0 ? `${parts.join(' · ')}.` : null;
};

export default function InboundMailTriagePage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  // EXACTLY the gate the endpoint enforces: EmailTriageController.Reprocess requires
  // Leads:Edit. It used to be checked as create OR edit, so a role with create-but-not-edit was
  // shown a control that could only ever 403 — which reads as "the system is broken", not as
  // "you are not allowed".
  const canReprocess = hasPermission('Leads', 'edit');
  // Same gate the endpoint enforces (EmailController.ManualFetchAndSaveLeads requires
  // Leads:Create) and the same one Watched folders uses for its on-demand sweep. Offering a
  // control that can only ever 403 is its own defect — the operator cannot tell "not allowed"
  // from "broken".
  const canPoll = hasPermission('Leads', 'create');
  /** /setup/mailboxes and /api/Mailbox are both behind the "Email & SMTP" module. */
  const canSeeMailboxes = hasPermission('Email & SMTP', 'view');
  /** /admin/operations, where a dead-lettered extraction job can be replayed. */
  const canReplayDeadLetters = hasPermission('Users', 'view');

  // The landing tab is deliberately "Rejected as noise" and NOT the first tab. A rejection is the
  // only outcome that produces nothing downstream, so it is the only one that can hide a lost
  // deal; "All messages" sits first because that is where a reader looks for everything, but it
  // is not what this screen is for.
  const defaultTabIndex = Math.max(0, TABS.findIndex((tab) => tab.key === 'Noise'));
  const [tabIndex, setTabIndex] = useState(defaultTabIndex);
  const [page, setPage] = useState(1);
  const [expandedId, setExpandedId] = useState<number | null>(null);
  const [target, setTarget] = useState<EmailTriageRow | null>(null);
  const [reason, setReason] = useState('');
  const [reasonError, setReasonError] = useState<string | null>(null);
  const [confirmation, setConfirmation] = useState<string | null>(null);
  const [pollReport, setPollReport] = useState<MailPollReport | null>(null);
  /** Minted once per opened dialog — see the reprocess mutation for why it is not per send. */
  const [idempotencyKey, setIdempotencyKey] = useState(() => newIdempotencyKey());

  const activeTab = TABS[tabIndex] ?? TABS[0];

  const query = useQuery({
    queryKey: ['email-triage', activeTab.key, page],
    queryFn: () =>
      emailTriageService.listTriage({
        outcome: activeTab.outcome ?? undefined,
        page,
        pageSize: PAGE_SIZE,
      }),
    // The assembler finishes on its own clock. While anything is in flight the list re-reads
    // itself, so "still assembling" resolves without the rep having to guess and press Refresh.
    refetchInterval: (activeQuery) =>
      (activeQuery.state.data?.items ?? []).some((item) => describeAssemblyState(item.assemblyState).inProgress)
        ? ASSEMBLY_REFRESH_MS
        : false,
  });

  /**
   * How many messages sit behind each tab.
   *
   * Nothing counted stranded mail before this, so thirteen messages held on one tab looked
   * identical to none: the reader had to open every tab to find out whether anything was waiting.
   * The endpoint already returns the total for the filter it was given, so one one-row read per
   * tab is the whole cost, and a tab whose count could not be read shows no number rather than a
   * zero it cannot support.
   */
  const countQueries = useQueries({
    queries: TABS.map((tab) => ({
      queryKey: ['email-triage-count', tab.key],
      queryFn: () =>
        emailTriageService.listTriage({ outcome: tab.outcome ?? undefined, page: 1, pageSize: 1 }),
      select: (result: { totalCount: number | null }) => result.totalCount,
      staleTime: 30_000,
    })),
  });
  // Five numbers off an already-resolved array — cheaper to recompute than to memoise, and a
  // memo keyed on a rebuilt-every-render array would be a lie about what it depends on.
  const tabCounts: (number | null)[] = TABS.map(
    (_tab, index) => countQueries[index]?.data ?? null,
  );

  /**
   * The mailboxes, read on mount — the only way to tell "nothing was sent" from "nothing is
   * being collected". Skipped entirely for a role that cannot see them, because a 403 here would
   * be indistinguishable from "no mailbox exists" and would produce a warning that is false.
   */
  const mailboxQuery = useQuery({
    queryKey: ['inbound-mail-mailboxes'],
    queryFn: () => mailboxService.getAll(),
    enabled: canSeeMailboxes,
    staleTime: 60_000,
    retry: false,
  });
  const mailboxObstacle = mailboxQuery.isSuccess
    ? describeMailboxObstacle(mailboxQuery.data)
    : null;

  /**
   * The parts of the ONE open message. The list endpoint returns no components on purpose, and
   * only one row expands at a time, so a single query keyed on the open id is the whole need —
   * a per-row query would fetch every message's parts to render a table nobody has opened.
   */
  const detailQuery = useQuery({
    queryKey: ['email-triage-message', expandedId],
    queryFn: () => emailTriageService.getMessage(expandedId as number),
    enabled: expandedId !== null,
    // Matches the list: a panel left open on a message the worker is still moving would otherwise
    // freeze at the state it had when it was opened.
    refetchInterval: (activeQuery) =>
      describeAssemblyState(activeQuery.state.data?.assemblyState ?? null).inProgress
        ? ASSEMBLY_REFRESH_MS
        : false,
  });

  /** Both reads of the same message, and the tab totals. A change to one is a change to all. */
  const refreshTriage = () => {
    void queryClient.invalidateQueries({ queryKey: ['email-triage'] });
    void queryClient.invalidateQueries({ queryKey: ['email-triage-message'] });
    void queryClient.invalidateQueries({ queryKey: ['email-triage-count'] });
  };

  const pollMutation = useMutation({
    mutationFn: () => emailTriageService.pollMailboxes(),
    // The previous run's counts must not sit next to a spinner describing a different one.
    onMutate: () => setPollReport(null),
    onSuccess: (result) => {
      setPollReport(result);
      // A poll that captured mail changes every tab, not just this one.
      refreshTriage();
    },
    onError: (error) => {
      // The 502 branch still carries a report — which mailboxes refused, and why. Keeping it lets
      // the panel name the inbox to go and fix instead of showing a bare "request failed".
      setPollReport(readPollFailureReport(error));
    },
  });

  const reprocessMutation = useMutation({
    // The key is minted once when the dialog opens and reused for every send from it, so a
    // double-click is ONE command the server can recognise as a replay. Minting it per call —
    // which is what the service default does — turns a double-click into two distinct overrides,
    // and the second one lands on a message the first has already moved.
    mutationFn: ({ id, why, key }: { id: number; why: string; key: string }) =>
      emailTriageService.reprocess(id, why, key),
    onSuccess: (result, variables) => {
      setTarget(null);
      setReason('');
      setReasonError(null);
      setConfirmation(
        result.replayed === true
          ? `Message #${variables.id} was already sent back for extraction. Nothing was queued twice.`
          : `Message #${variables.id} was sent back through extraction as an inquiry.`,
      );
      refreshTriage();
    },
  });

  const rows = query.data?.items ?? [];
  const pageSize = query.data?.pageSize ?? PAGE_SIZE;
  const totalCount = query.data?.totalCount ?? null;
  const hasNextPage = useMemo(() => {
    if (totalCount !== null && pageSize) return page * pageSize < totalCount;
    return rows.length >= pageSize;
  }, [page, pageSize, rows.length, totalCount]);

  const assemblingCount = useMemo(
    () => rows.filter((row) => describeAssemblyState(row.assemblyState).inProgress).length,
    [rows],
  );

  const changeTab = (nextIndex: number) => {
    setTabIndex(nextIndex);
    setPage(1);
    setExpandedId(null);
    setConfirmation(null);
  };

  const openReprocess = (row: EmailTriageRow) => {
    setTarget(row);
    setReason('');
    setReasonError(null);
    setIdempotencyKey(newIdempotencyKey());
    reprocessMutation.reset();
  };

  const submitReprocess = () => {
    if (!target) return;
    const trimmed = reason.trim();
    if (trimmed.length === 0) {
      // Overturning a machine decision is an audited act; the reason IS the record of who
      // disagreed and why. Never let it through empty.
      setReasonError('Give a reason. It is recorded against the override.');
      return;
    }
    setReasonError(null);
    reprocessMutation.mutate({ id: target.id, why: trimmed, key: idempotencyKey });
  };

  const unavailable = query.isError && isTriageUnavailable(query.error);

  return (
    <Box sx={{ maxWidth: 1500, mx: 'auto', p: { xs: 2, md: 3 } }}>
      <Stack
        direction={{ xs: 'column', sm: 'row' }}
        spacing={2}
        sx={{ mb: 2, justifyContent: 'space-between', alignItems: { sm: 'flex-start' } }}
      >
        <Box>
          {/*
            This screen already spends its one level of tabs on the triage outcome (Inquiry route /
            Uncertain route / Supplier / Noise), so it does NOT also carry the Inbox tab strip its three
            sibling intake screens do — two stacked strips of the same shape is precisely the
            tab-within-tab confusion this navigation pass exists to remove. A plain link back to the
            queue does the same job without a second row of tabs.
          */}
          <Link
            component={RouterLink}
            to={INBOX_ROOT}
            variant="body2"
            sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.5, fontWeight: 700, mb: 0.5 }}
          >
            ← Inbox
          </Link>
          <Typography variant="h5" component="h1">
            Inbound Mail
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 780 }}>
            What the system decided about each message that arrived in the ingestion mailbox, and why. A decision here
            is reversible — every original email is kept.
          </Typography>
        </Box>
        <Stack direction="row" spacing={1.5} sx={{ flexShrink: 0 }}>
          <Button
            variant="contained"
            startIcon={pollMutation.isPending ? <CircularProgress size={18} color="inherit" /> : <PollIcon />}
            onClick={() => pollMutation.mutate()}
            disabled={!canPoll || pollMutation.isPending}
            sx={{ fontWeight: 800 }}
          >
            {pollMutation.isPending ? 'Polling…' : 'Poll now'}
          </Button>
          <Button
            variant="outlined"
            startIcon={<RefreshIcon />}
            onClick={() => void query.refetch()}
            disabled={query.isFetching}
          >
            Refresh
          </Button>
        </Stack>
      </Stack>

      {!canPoll && (
        <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 2 }}>
          You need the &quot;create leads&quot; permission to poll the mailboxes. The server checks
          them on its own schedule regardless.
        </Typography>
      )}

      {/* What the poll actually did. A bare "email synchronized" toast is what this replaces: it
          cannot distinguish a mailbox that answered with nothing from one that never answered. */}
      {pollMutation.isPending && (
        <Alert severity="info" role="status" icon={<CircularProgress size={18} />} sx={{ mb: 2 }}>
          Checking the mailboxes now. Nothing is changed until the poll finishes.
        </Alert>
      )}

      {!pollMutation.isPending && pollReport && (
        <Alert
          severity={pollSeverity(pollReport)}
          role="status"
          // Resetting the mutation too: without it, dismissing a failed poll's report would let
          // the generic failure card take its place, and the same failure would be announced twice.
          onClose={() => {
            setPollReport(null);
            pollMutation.reset();
          }}
          sx={{ mb: 2 }}
        >
          <AlertTitle sx={{ fontWeight: 800 }}>{pollHeadline(pollReport)}</AlertTitle>
          {pollCountLine(pollReport) && (
            <Typography variant="body2">{pollCountLine(pollReport)}</Typography>
          )}
          {pollReport.message && (
            <Typography variant="body2" sx={{ mt: pollCountLine(pollReport) ? 0.5 : 0 }}>
              {presentableServerText(pollReport.message) ?? pollReport.message}
            </Typography>
          )}
          {pollCountLine(pollReport) === null && pollReport.failures.length === 0 && (
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
              This deployment did not report per-message counts for the poll, so none are shown. The
              list below is the record of what arrived.
            </Typography>
          )}
          {pollReport.failures.length > 0 && (
            <Box component="ul" sx={{ pl: 2.5, mt: 1, mb: 0 }}>
              {pollReport.failures.map((failure, index) => (
                <Typography key={failure.mailbox ?? `failure-${index}`} component="li" variant="body2">
                  <strong>{failure.mailbox ?? NOT_REPORTED}</strong>
                  {presentableReason(failure.reason) ? ` — ${presentableReason(failure.reason)}` : ''}
                  {failure.lastSuccessfulPollOn
                    ? ` (last successful poll ${formatReceived(failure.lastSuccessfulPollOn)})`
                    : ''}
                </Typography>
              ))}
            </Box>
          )}
          {/* The two ways mail can still be sitting in the mailbox after a "successful" poll.
              Neither is visible in a found/captured count, and both mean messages exist that this
              screen is not yet showing. */}
          {(pollReport.notAcknowledged ?? 0) > 0 && (
            <Typography variant="body2" sx={{ mt: 0.5, fontWeight: 700 }}>
              {`${plural(pollReport.notAcknowledged ?? 0, 'message')} could not be taken and ${
                (pollReport.notAcknowledged ?? 0) === 1 ? 'was' : 'were'
              } left unread for the next cycle.`}
            </Typography>
          )}
          {(pollReport.lookbackCappedDays ?? 0) > 0 && (
            <Typography variant="body2" sx={{ mt: 0.5, fontWeight: 700 }}>
              {`Mail older than the poll window was not read — up to ${plural(
                pollReport.lookbackCappedDays ?? 0,
                'day',
              )} of it. Those messages are still in the mailbox and are not listed below.`}
            </Typography>
          )}
          {(pollReport.scheduled ?? 0) > 0 && (
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
              A queued part becomes a lead only once extraction and assembly finish, so the list
              below may take a moment to catch up.
            </Typography>
          )}
        </Alert>
      )}

      {/* The structured report is only in the 502 body. Everything else — a network drop, a 403,
          a proxy page — has no counts to show, so it falls through to the standard failure card. */}
      {!pollMutation.isPending && pollMutation.isError && pollReport === null && (
        <ApiErrorNotice
          error={pollMutation.error}
          fallbackMessage="The mailboxes could not be polled. No mail was ingested and nothing was changed — try again."
          onRetry={() => pollMutation.mutate()}
          retryLabel="Poll again"
          sx={{ mb: 2 }}
        />
      )}

      <Tabs
        value={tabIndex}
        onChange={(_event, next: number) => changeTab(next)}
        aria-label="Triage decision"
        variant="scrollable"
        scrollButtons="auto"
        allowScrollButtonsMobile
        sx={{ borderBottom: 1, borderColor: 'divider', mb: 2 }}
      >
        {TABS.map((tab, index) => (
          <Tab
            key={tab.key}
            id={`triage-tab-${tab.key}`}
            aria-controls="triage-panel"
            // The number is part of the accessible name, not a decoration beside it: a count
            // announced separately from its tab is a number with nothing attached to it.
            label={tabCounts[index] === null ? tab.label : `${tab.label} (${tabCounts[index]})`}
          />
        ))}
      </Tabs>

      <Box role="tabpanel" id="triage-panel" aria-labelledby={`triage-tab-${activeTab.key}`}>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2, maxWidth: 900 }}>
          {activeTab.blurb}
        </Typography>

        {confirmation && (
          <Alert severity="success" role="status" onClose={() => setConfirmation(null)} sx={{ mb: 2 }}>
            {confirmation}
          </Alert>
        )}

        {reprocessMutation.isError && (
          <ApiErrorNotice
            error={reprocessMutation.error}
            fallbackMessage="That message could not be sent back for extraction. Its stored original and current decision are unchanged — try again."
            sx={{ mb: 2 }}
          />
        )}

        {!canReprocess && (
          <Alert severity="info" sx={{ mb: 2 }}>
            You can review these decisions. Sending a message back through extraction needs the
            &quot;edit leads&quot; permission.
          </Alert>
        )}

        {/* The delayed-worker case. Without this it is impossible to tell a message the assembler
            has not reached yet from one it will never finish, and both look like a missing lead. */}
        {assemblingCount > 0 && (
          <Alert severity="info" sx={{ mb: 2 }}>
            {`${plural(assemblingCount, 'message')} on this page ${assemblingCount === 1 ? 'is' : 'are'} still being assembled. `}
            Nothing is stuck — the assembler runs on its own schedule, and this list refreshes itself
            until it finishes.
          </Alert>
        )}

        {query.isLoading && <LoadingState label="Loading inbound mail decisions…" />}

        {unavailable && (
          <EmptyState
            title="Email intake is not switched on for this account"
            message="This screen lists what the mailbox decided about each message. Email intake is not part of this account's plan, or has not been enabled on this deployment — so there is nothing to show, and no message has been hidden. This is not a permission your administrator can grant; it is a change to what the account includes."
            icon={<InboxIcon sx={{ fontSize: 44 }} />}
          />
        )}

        {query.isError && !unavailable && (
          <ApiErrorNotice
            error={query.error}
            fallbackMessage="Inbound mail decisions could not be loaded. Nothing was changed — try again."
            onRetry={() => void query.refetch()}
          />
        )}

        {/* An empty list has two very different causes, and only one of them is reassuring.
            The mailboxes are read before anything comforting is said: when nothing is being
            COLLECTED, "nothing is being hidden from you" is precisely backwards — everything is
            hidden, sitting unread in a mailbox nobody is polling. */}
        {!query.isLoading && !query.isError && rows.length === 0 && mailboxObstacle && (
          <Alert severity="warning" sx={{ mb: 2 }}>
            <AlertTitle sx={{ fontWeight: 800 }}>{mailboxObstacle.title}</AlertTitle>
            <Typography variant="body2">{mailboxObstacle.message}</Typography>
            <Button
              size="small"
              variant="outlined"
              color="inherit"
              startIcon={<OpenIcon />}
              onClick={() => navigate('/setup/mailboxes')}
              sx={{ mt: 1 }}
            >
              Open Email Inboxes
            </Button>
          </Alert>
        )}

        {!query.isLoading && !query.isError && rows.length === 0 && !mailboxObstacle && (
          <EmptyState title={activeTab.emptyTitle} message={activeTab.emptyMessage} icon={<InboxIcon sx={{ fontSize: 44 }} />} />
        )}

        {!query.isError && rows.length > 0 && (
          <>
            <TableContainer component={Paper} variant="outlined" sx={{ overflowX: 'auto' }}>
              <Table size="small" aria-label={`Messages ${activeTab.label.toLowerCase()}`} sx={{ minWidth: 1260 }}>
                <TableHead>
                  <TableRow>
                    <TableCell sx={{ width: 48 }}>
                      {/* A column header that reads as empty is announced as nothing; name it for screen readers. */}
                      <Box component="span" sx={VISUALLY_HIDDEN}>
                        Show message
                      </Box>
                    </TableCell>
                    <TableCell>Received</TableCell>
                    <TableCell>From</TableCell>
                    <TableCell>Subject</TableCell>
                    <TableCell>Routing decision</TableCell>
                    <TableCell>Why</TableCell>
                    <TableCell>What became of it</TableCell>
                    <TableCell align="right">Action</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {rows.map((row) => {
                    const outcome = describeRoutingDecision(row.outcome);
                    const assembly = describeAssemblyState(row.assemblyState);
                    const assemblyReason = presentableReason(row.assemblyReason);
                    const progress = describeComponentProgress(row);
                    const missingLead = describeMissingLead(row);
                    // Whether the ONE override on this screen can possibly work for this row.
                    const reopen = describeReopenAbility(row.assemblyState);
                    // THE status a reader acts on, derived from the assembly and whether a lead
                    // exists — never from the per-ingest checkpoint, which on live data reads
                    // backwards (a closed, lead-less message carries "Queued"; a message that DID
                    // produce a lead carries "NeedsReview"). See describeMessageProgress.
                    const progressStatus = describeMessageProgress(row.assemblyState, row.leadId !== null);
                    // A message stopped for a person, with no lead to open. Without a route out
                    // of here these rows were a dead end: no "Open lead" (there is none), and a
                    // "Reprocess" that could only answer 422.
                    const strandedForHuman = assembly.needsHuman && row.leadId === null;
                    // Read off the row itself rather than the per-message query: the loss is
                    // stated the moment the panel opens, and it survives the parts read failing.
                    const skipped = row.skippedAttachments;
                    const expanded = expandedId === row.id;
                    // The same record as `skipped`, on the older payload that carries it only on
                    // the per-message read. Nothing above would ever show those, so they are kept —
                    // minus any entry the row already stated, so one skip is never listed twice.
                    // Two unreadable entries are not assumed to be the same entry.
                    const legacySkipped =
                      expanded && detailQuery.isSuccess
                        ? detailQuery.data.skippedAttachments.filter(
                            (entry) =>
                              entry.displayText === null
                              || !skipped.some((shown) => shown.displayText === entry.displayText),
                          )
                        : [];
                    const detailId = `triage-detail-${row.id}`;
                    const subject = row.subject ?? '(no subject)';
                    return (
                      <Fragment key={row.id}>
                      <TableRow hover>
                        <TableCell>
                          <IconButton
                            size="small"
                            aria-expanded={expanded}
                            aria-controls={detailId}
                            aria-label={expanded ? `Hide message ${subject}` : `Show message ${subject}`}
                            onClick={() => setExpandedId(expanded ? null : row.id)}
                          >
                            {expanded ? <CollapseIcon fontSize="small" /> : <ExpandIcon fontSize="small" />}
                          </IconButton>
                        </TableCell>
                        <TableCell>{formatReceived(row.receivedOn)}</TableCell>
                        <TableCell sx={{ overflowWrap: 'anywhere' }}>{row.from ?? NOT_REPORTED}</TableCell>
                        <TableCell sx={{ overflowWrap: 'anywhere' }}>
                          <Typography variant="body2" sx={{ fontWeight: row.subject ? 600 : 400 }}>
                            {subject}
                          </Typography>
                          {row.threadContinuation === true && (
                            <Typography variant="caption" color="text.secondary">
                              Reply in an existing thread
                            </Typography>
                          )}
                        </TableCell>
                        <TableCell>
                          <Chip size="small" color={outcome.chipColor} variant="outlined" label={outcome.label} />
                          {row.commercialDocumentTypeHint && (
                            <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>
                              {readableHint(row.commercialDocumentTypeHint)}
                            </Typography>
                          )}
                        </TableCell>
                        <TableCell>
                          {row.reasonCodes.length === 0 ? (
                            <Typography variant="caption" color="text.secondary">
                              No reason recorded
                            </Typography>
                          ) : (
                            <Stack direction="row" spacing={0.5} useFlexGap sx={{ flexWrap: 'wrap' }}>
                              {row.reasonCodes.map((code) => (
                                <Chip key={code} size="small" variant="outlined" label={describeTriageReason(code)} />
                              ))}
                            </Stack>
                          )}
                        </TableCell>
                        <TableCell>
                          {row.assemblyState === null ? (
                            <Typography variant="caption" color="text.secondary">
                              {NOT_REPORTED}
                            </Typography>
                          ) : (
                            <>
                              <Chip
                                size="small"
                                color={progressStatus.chipColor}
                                variant="outlined"
                                label={progressStatus.label}
                              />
                              {progress && (
                                <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>
                                  {progress}
                                </Typography>
                              )}
                              {assemblyReason && (
                                <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                                  {assemblyReason}
                                </Typography>
                              )}
                            </>
                          )}
                        </TableCell>
                        <TableCell align="right">
                          <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap', justifyContent: 'flex-end' }}>
                            {/* Gated on the lead id itself, never on the assembly state. A state
                                this build cannot read must not conjure a route to a lead that
                                does not exist. */}
                            {row.leadId !== null && (
                              <Button
                                size="small"
                                variant="outlined"
                                startIcon={<OpenIcon />}
                                onClick={() => navigate(`/procurement/leads/view/${row.leadId}`)}
                              >
                                Open lead
                              </Button>
                            )}
                            {/* Rendered only where it can work. It used to appear on every row,
                                including the many where the endpoint can only refuse — and the
                                refusal named the internal state, so pressing the only control on
                                the screen answered "this message is already NeedsReview". */}
                            {canReprocess && reopen.canReopen && (
                              <Button
                                size="small"
                                startIcon={<ReprocessIcon />}
                                onClick={() => openReprocess(row)}
                                disabled={reprocessMutation.isPending}
                              >
                                Reprocess as inquiry
                              </Button>
                            )}
                            {/* The real next action for a message stopped for a person. The batch
                                is where its files, their security verdicts and the retry live. */}
                            {strandedForHuman && row.linkedBatchId && (
                              <Button
                                size="small"
                                variant="outlined"
                                startIcon={<OpenIcon />}
                                onClick={() => navigate(`/procurement/leads/ingestion/${row.linkedBatchId}`)}
                              >
                                Open ingestion batch
                              </Button>
                            )}
                          </Stack>
                          {missingLead && (
                            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
                              {missingLead}
                            </Typography>
                          )}
                          {canReprocess && !reopen.canReopen && reopen.disabledReason && (
                            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
                              {reopen.disabledReason}
                            </Typography>
                          )}
                          {/* Only for the rows that are genuinely stuck, and only for a role that
                              can reach the screen. A link a reader cannot open is another dead end. */}
                          {strandedForHuman && canReplayDeadLetters && (
                            <Button
                              size="small"
                              sx={{ mt: 0.5 }}
                              onClick={() => navigate('/admin/operations')}
                            >
                              Retry failed processing
                            </Button>
                          )}
                        </TableCell>
                      </TableRow>
                      {expanded && (
                        <TableRow>
                          <TableCell colSpan={8} sx={{ bgcolor: 'action.hover' }}>
                            <Box
                              id={detailId}
                              role="region"
                              aria-label={`Message and extraction for ${subject}`}
                              sx={{
                                display: 'grid',
                                gap: 2,
                                gridTemplateColumns: { xs: '1fr', md: '1fr 1fr' },
                                py: 1,
                              }}
                            >
                              <Box>
                                <Typography variant="subtitle2" component="h2" sx={{ fontWeight: 700 }}>
                                  Original message
                                </Typography>
                                {row.bodyPreview ? (
                                  <Typography
                                    variant="body2"
                                    component="pre"
                                    sx={{
                                      m: 0,
                                      mt: 0.5,
                                      p: 1.5,
                                      maxHeight: 320,
                                      overflow: 'auto',
                                      borderRadius: 1,
                                      bgcolor: 'background.paper',
                                      border: 1,
                                      borderColor: 'divider',
                                      whiteSpace: 'pre-wrap',
                                      overflowWrap: 'anywhere',
                                      fontFamily: 'inherit',
                                    }}
                                  >
                                    {row.bodyPreview}
                                  </Typography>
                                ) : (
                                  <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
                                    The message text is not exposed by this deployment yet. The original email is
                                    retained, so nothing has been lost.
                                  </Typography>
                                )}
                                <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
                                  {describeBodyRouting(row)} {describeAttachmentRouting(row)}
                                </Typography>
                                {row.attachmentNamesReported && row.attachmentNames.length > 0 && (
                                  <Stack direction="row" spacing={0.5} useFlexGap sx={{ flexWrap: 'wrap', mt: 1 }}>
                                    {row.attachmentNames.map((name) => (
                                      <Chip key={name} size="small" label={name} variant="outlined" />
                                    ))}
                                  </Stack>
                                )}
                                {detailQuery.isSuccess && (
                                  <Alert
                                    severity={detailQuery.data.rawEvidenceStored === false ? 'warning' : 'info'}
                                    variant="outlined"
                                    sx={{ mt: 1 }}
                                  >
                                    {detailQuery.data.rawEvidenceStored === true
                                      ? detailQuery.data.rawEvidenceVerifiable === true
                                        ? 'The original email is retained and verified against its capture-time digest.'
                                        : 'The original email is retained. Digest verification was not reported.'
                                      : detailQuery.data.rawEvidenceStored === false
                                        ? 'The original email is not reported as retained; contact support before relying on reprocessing.'
                                        : 'Whether the original email is retained is not reported by this deployment.'}
                                  </Alert>
                                )}
                              </Box>

                              <Box>
                                <Typography variant="subtitle2" component="h2" sx={{ fontWeight: 700 }}>
                                  What the system did with it
                                </Typography>
                                <Typography variant="body2" sx={{ mt: 0.5 }}>
                                  {describeTriageOutcome(row.outcome).meaning}
                                </Typography>
                                {row.reasonCodes.length > 0 && (
                                  <Box component="ul" sx={{ pl: 2.5, mt: 1, mb: 0 }}>
                                    {row.reasonCodes.map((code) => (
                                      <Typography key={code} component="li" variant="caption" color="text.secondary">
                                        <strong>{describeTriageReason(code)}</strong>
                                        {TRIAGE_REASON_DESCRIPTIONS[code] ? ` — ${TRIAGE_REASON_DESCRIPTIONS[code]}` : ''}
                                      </Typography>
                                    ))}
                                  </Box>
                                )}
                                <Typography variant="body2" sx={{ mt: 1 }}>
                                  {row.extractedItemCount === null
                                    ? `Line items extracted: ${NOT_REPORTED.toLowerCase()}.`
                                    : `Line items extracted from this message: ${row.extractedItemCount}.`}
                                </Typography>

                                <Box sx={{ mt: 1.5 }}>
                                  <Typography variant="body2" sx={{ fontWeight: 700 }}>
                                    What became of it — {progressStatus.label}
                                  </Typography>
                                  <Typography variant="body2">{progressStatus.meaning}</Typography>
                                  {progress && <Typography variant="body2">{progress}.</Typography>}
                                  {assemblyReason && (
                                    <Typography variant="body2" sx={{ mt: 0.5 }}>
                                      Reason: {assemblyReason}
                                    </Typography>
                                  )}
                                  {/* The reason existed but the copy gate refused it. Saying so beats
                                      silence: the operator knows there IS a recorded reason to ask for. */}
                                  {row.assemblyReason !== null && assemblyReason === null && (
                                    <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
                                      A reason was recorded for support and is not shown here.
                                    </Typography>
                                  )}
                                  {missingLead && (
                                    <Typography variant="body2" sx={{ mt: 0.5 }}>{missingLead}</Typography>
                                  )}
                                </Box>

                                {/* "Open lead" lives in the row's Action column, where it is visible
                                    without expanding. Only the batch link is unique to this panel. */}
                                {row.linkedBatchId && (
                                  <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap', mt: 1 }}>
                                    <Button
                                      size="small"
                                      startIcon={<OpenIcon />}
                                      onClick={() => navigate(`/procurement/leads/ingestion/${row.linkedBatchId}`)}
                                    >
                                      Open ingestion batch
                                    </Button>
                                  </Stack>
                                )}
                                {row.decidedOn && (
                                  <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
                                    Decided {formatReceived(row.decidedOn)}
                                  </Typography>
                                )}
                                {detailQuery.isSuccess && detailQuery.data.senderSentOn && (
                                  <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                                    Sender timestamp {formatReceived(detailQuery.data.senderSentOn)}
                                  </Typography>
                                )}
                                {(detailQuery.isSuccess ? detailQuery.data.ingestedOn : row.ingestedOn) && (
                                  <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                                    Ingested {formatReceived(
                                      detailQuery.isSuccess ? detailQuery.data.ingestedOn : row.ingestedOn,
                                    )}
                                  </Typography>
                                )}
                                {detailQuery.isSuccess && detailQuery.data.parsedOn && (
                                  <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                                    Extraction finished {formatReceived(detailQuery.data.parsedOn)}
                                  </Typography>
                                )}
                                {/* Named for what the column actually records. Nothing distinguishes
                                    a recovery sweep from the worker's own barrier here, so calling
                                    this "recovered" would state something no row supports. */}
                                {(detailQuery.isSuccess ? detailQuery.data.lastUpdatedOn : row.lastUpdatedOn) && (
                                  <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                                    Last moved {formatReceived(
                                      detailQuery.isSuccess ? detailQuery.data.lastUpdatedOn : row.lastUpdatedOn,
                                    )}
                                  </Typography>
                                )}
                              </Box>

                              <Box sx={{ gridColumn: { md: '1 / -1' } }}>
                                <Typography variant="subtitle2" component="h2" sx={{ fontWeight: 700 }}>
                                  Parts of this message
                                </Typography>
                                {detailQuery.isPending && (
                                  <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
                                    Reading the parts of this message…
                                  </Typography>
                                )}
                                {detailQuery.isError && (
                                  <ApiErrorNotice
                                    error={detailQuery.error}
                                    fallbackMessage="The parts of this message could not be read. The message itself and its decision above are unaffected."
                                    onRetry={() => void detailQuery.refetch()}
                                    sx={{ mt: 1 }}
                                  />
                                )}
                                {detailQuery.isSuccess && !detailQuery.data.componentsReported && (
                                  <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
                                    This deployment does not report the individual parts of a message yet. The
                                    message itself and its decision above are complete.
                                  </Typography>
                                )}
                                {detailQuery.isSuccess
                                  && detailQuery.data.componentsReported
                                  && detailQuery.data.components.length === 0 && (
                                    <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
                                      No part was recorded for this message — there was nothing to assemble.
                                    </Typography>
                                  )}
                                {detailQuery.isSuccess && detailQuery.data.components.length > 0 && (
                                  <TableContainer component={Paper} variant="outlined" sx={{ mt: 1 }}>
                                    <Table size="small" aria-label={`Parts of ${subject}`}>
                                      <TableHead>
                                        <TableRow>
                                          <TableCell>File</TableCell>
                                          <TableCell>Type</TableCell>
                                          <TableCell>State</TableCell>
                                          <TableCell>Why</TableCell>
                                        </TableRow>
                                      </TableHead>
                                      <TableBody>
                                        {detailQuery.data.components.map((component) => {
                                          const componentState = describeComponentState(component.state);
                                          const componentReason = presentableReason(component.reason);
                                          return (
                                            <TableRow key={component.key}>
                                              <TableCell sx={{ overflowWrap: 'anywhere' }}>
                                                {/* The body has no filename, and "Not reported" would
                                                    read as a missing value rather than as a part that
                                                    never had one. */}
                                                {component.fileName
                                                  ?? (describeComponentKind(component.kind) ?? NOT_REPORTED)}
                                              </TableCell>
                                              <TableCell>
                                                {describeComponentKind(component.kind) ?? NOT_REPORTED}
                                              </TableCell>
                                              <TableCell>
                                                <Chip
                                                  size="small"
                                                  variant="outlined"
                                                  color={componentState.chipColor}
                                                  label={componentState.label}
                                                />
                                              </TableCell>
                                              <TableCell sx={{ overflowWrap: 'anywhere' }}>
                                                {componentReason ?? (
                                                  <Typography variant="caption" color="text.secondary">
                                                    {component.reason === null
                                                      ? 'No reason recorded'
                                                      : 'Recorded for support'}
                                                  </Typography>
                                                )}
                                              </TableCell>
                                            </TableRow>
                                          );
                                        })}
                                      </TableBody>
                                    </Table>
                                  </TableContainer>
                                )}

                                {/* ING-06. Its own heading and its own table, below the parts and
                                    never inside them: a skipped attachment produced no job, so it
                                    is absent from the counts above by design. Folding it into the
                                    parts table would make the totals disagree with the pipeline —
                                    and leaving it out altogether is the only way an attachment can
                                    still vanish off this screen without a trace. */}
                                {skipped.length > 0 && (
                                  <Box sx={{ mt: 2 }}>
                                    <Typography variant="subtitle2" component="h2" sx={{ fontWeight: 700 }}>
                                      Skipped — never sent for extraction
                                    </Typography>
                                    <Typography variant="body2" sx={{ mt: 0.5 }}>
                                      {describeSkippedImpact(skipped.length)}
                                    </Typography>
                                    <TableContainer component={Paper} variant="outlined" sx={{ mt: 1 }}>
                                      <Table size="small" aria-label={`Skipped attachments of ${subject}`}>
                                        <TableHead>
                                          <TableRow>
                                            <TableCell>File</TableCell>
                                            <TableCell>Type</TableCell>
                                            <TableCell>State</TableCell>
                                            <TableCell>Why</TableCell>
                                          </TableRow>
                                        </TableHead>
                                        <TableBody>
                                          {skipped.map((attachment) => {
                                            const skippedReason = presentableReason(attachment.reason);
                                            return (
                                              <TableRow key={attachment.key}>
                                                <TableCell sx={{ overflowWrap: 'anywhere' }}>
                                                  {attachment.fileName ?? NOT_REPORTED}
                                                </TableCell>
                                                <TableCell>
                                                  {describeComponentKind('attachment') ?? NOT_REPORTED}
                                                </TableCell>
                                                <TableCell>
                                                  {/* Named for what happened, not for how it looks.
                                                      "Skipped" is the word the record uses. */}
                                                  <Chip size="small" variant="outlined" color="warning" label="Skipped" />
                                                </TableCell>
                                                <TableCell sx={{ overflowWrap: 'anywhere' }}>
                                                  {skippedReason ?? (
                                                    <Typography variant="caption" color="text.secondary">
                                                      {attachment.reason === null
                                                        ? 'No reason recorded'
                                                        : 'Recorded for support'}
                                                    </Typography>
                                                  )}
                                                </TableCell>
                                              </TableRow>
                                            );
                                          })}
                                        </TableBody>
                                      </Table>
                                    </TableContainer>
                                  </Box>
                                )}

                                {/* Silence here would read as "nothing was skipped", which is a
                                    claim no such deployment has made. Said only where it changes
                                    the answer: a message with attachments. */}
                                {!row.skippedAttachmentsReported && row.hasAttachments === true && (
                                  <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 2 }}>
                                    Whether any attachment was skipped instead of being sent for extraction is{' '}
                                    {NOT_REPORTED.toLowerCase()} by this deployment.
                                  </Typography>
                                )}

                                {/* The same loss, from the other source. A canonical assembly reports
                                    a refused attachment on the LIST row, which is what the table above
                                    reads; a legacy, pre-assembly message carries it only on the
                                    per-message read, and nothing above would ever show it. Both are
                                    kept, and an entry the table already stated is filtered out here so
                                    one skipped attachment is never counted twice on the screen. */}
                                {legacySkipped.length > 0 && (
                                  <Alert severity="warning" variant="outlined" sx={{ mt: 1 }}>
                                    <AlertTitle>Attachments skipped before component tracking</AlertTitle>
                                    <Typography variant="body2">
                                      These attachments were not handed to extraction. They remain visible here so
                                      a salesperson cannot price the inquiry as though every attachment was read.
                                    </Typography>
                                    <Box component="ul" sx={{ pl: 2.5, mt: 0.75, mb: 0 }}>
                                      {legacySkipped.map((attachment) => (
                                        <Typography
                                          key={attachment.key}
                                          component="li"
                                          variant="caption"
                                          sx={{ overflowWrap: 'anywhere' }}
                                        >
                                          {presentableServerText(attachment.displayText)
                                            ?? 'Attachment details recorded for support'}
                                        </Typography>
                                      ))}
                                    </Box>
                                  </Alert>
                                )}
                              </Box>
                            </Box>
                          </TableCell>
                        </TableRow>
                      )}
                      </Fragment>
                    );
                  })}
                </TableBody>
              </Table>
            </TableContainer>

            <Stack
              direction="row"
              spacing={2}
              sx={{ mt: 2, alignItems: 'center', justifyContent: 'flex-end' }}
            >
              <Typography variant="body2" color="text.secondary" aria-live="polite">
                {totalCount === null
                  ? `Page ${page} — ${rows.length} message${rows.length === 1 ? '' : 's'} shown`
                  : `Page ${page} — showing ${rows.length} of ${totalCount}`}
              </Typography>
              <Button size="small" disabled={page <= 1 || query.isFetching} onClick={() => setPage((value) => Math.max(1, value - 1))}>
                Previous
              </Button>
              <Button size="small" disabled={!hasNextPage || query.isFetching} onClick={() => setPage((value) => value + 1)}>
                Next
              </Button>
            </Stack>
          </>
        )}
      </Box>

      <Dialog
        open={target !== null}
        onClose={() => (reprocessMutation.isPending ? undefined : setTarget(null))}
        aria-labelledby="reprocess-dialog-title"
        fullWidth
        maxWidth="sm"
      >
        <DialogTitle id="reprocess-dialog-title">Reprocess as inquiry</DialogTitle>
        <DialogContent>
          <DialogContentText component="div">
            <Typography variant="body2" component="p">
              {target?.subject ?? '(no subject)'} — from {target?.from ?? NOT_REPORTED}
            </Typography>
            <Typography variant="body2" component="p" sx={{ mt: 1 }}>
              The stored original is put back through ingestion and treated as uncertain, so it is sent to extraction
              and flagged for review rather than dropped. Nothing already recorded is deleted.
            </Typography>
          </DialogContentText>
          <TextField
            label="Why is this an inquiry?"
            value={reason}
            onChange={(event) => {
              setReason(event.target.value);
              if (reasonError) setReasonError(null);
            }}
            required
            fullWidth
            multiline
            minRows={2}
            error={reasonError !== null}
            helperText={reasonError ?? 'Recorded against the override so the decision can be audited later.'}
            sx={{ mt: 2 }}
          />
          {reprocessMutation.isError && (
            <ApiErrorNotice
              error={reprocessMutation.error}
              fallbackMessage="That message could not be sent back for extraction. Its stored original and current decision are unchanged — try again."
              sx={{ mt: 2 }}
            />
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setTarget(null)} disabled={reprocessMutation.isPending}>
            Cancel
          </Button>
          <Button variant="contained" onClick={submitReprocess} disabled={reprocessMutation.isPending}>
            {reprocessMutation.isPending ? 'Sending…' : 'Reprocess as inquiry'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
