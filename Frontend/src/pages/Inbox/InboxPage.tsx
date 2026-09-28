import React, { useMemo } from 'react';
import { useQueries, useQuery, type UseQueryResult } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  Box,
  Button,
  IconButton,
  Paper,
  Skeleton,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material';
import { alpha } from '@mui/material/styles';
import {
  ArrowForward as ArrowIcon,
  CheckCircleOutlined as ClearIcon,
  ChevronRight as ChevronIcon,
  Done as DoneIcon,
  Refresh as RefreshIcon,
} from '@mui/icons-material';
import ApiErrorNotice from '../../components/common/ApiErrorNotice';
import InboxFrame, { INBOX_CARD_SX } from './InboxFrame';
import { useAuth } from '../../context/AuthContext';
import { formatDateSafe } from '../../utils/dates';
import emailTriageService, {
  describeAssemblyState,
  isTriageUnavailable,
  TRIAGE_STATE_STOPPED,
} from '../../api/services/emailTriageService';
import extractionReviewService from '../../api/services/extractionReviewService';
import leadService from '../../api/services/leadService';
import rfqService from '../../api/services/rfqService';
import quoteService from '../../api/services/quoteService';
import supplierQuoteService from '../../api/services/supplierQuoteService';
import customerAwardService from '../../api/services/customerAwardService';
import {
  INBOX_PREVIEW_ROWS,
  INBOX_QUEUES,
  type InboxItem,
  type QueueDefinition,
  type QueueKey,
} from './inboxQueues';

/**
 * The screen a rep lands on, and the only screen that answers "what do I do next".
 *
 * What it replaced: `/analytics/deadlines`, a board of enquiries bucketed by closing date whose
 * only outbound link was a per-row jump to one lead. It could not show a document that had just
 * arrived, a supplier that had just replied, or a quote that was waiting to be sent — so the
 * answer to "what now" was always "expand a sidebar group and guess". That is a chooser of
 * modules, not a queue of work.
 *
 * The rules this screen is built to:
 *
 *  - Position is the priority. The queues run down the commercial spine in order, so reading top
 *    to bottom is reading the process. Nothing here is scored: the product's one ranking model is
 *    a hand-weighted heuristic of unmeasured accuracy, and a landing page must not present that as
 *    an instruction.
 *  - Every row ends in a verb. A row you cannot act on from here is a row that belongs on a list.
 *  - A queue at zero says WHY it is empty and offers the button that would put something in it.
 *  - A queue that FAILED never renders as empty. `isError` is read from the query, and the whole
 *    section says so — an empty grid on an outage is how a rep concludes the pipeline is dead.
 *  - A queue the user has no permission for is not asked for and not shown.
 *  - A queue whose CHANNEL this tenant does not have is not shown either. That is what a null
 *    from `loadQueue` means: not zero work, no such queue here.
 */

interface QueueResult {
  definition: QueueDefinition;
  query: UseQueryResult<InboxItem[] | null>;
}

export interface InboxQueueContext {
  businessUnitId?: number;
  userId?: number;
  /** Managers and tenant super administrators may see the assigned queue for the whole team. */
  teamScope?: boolean;
}

const byDateAscending = (a: InboxItem, b: InboxItem) => {
  if (!a.sortKey) return 1;
  if (!b.sortKey) return -1;
  return a.sortKey.localeCompare(b.sortKey);
};

/** Whole days between now and a deadline, as a phrase a person would say, and how urgent it is. */
const deadline = (dateStr: string | null | undefined): Pick<InboxItem, 'detail' | 'tone'> | undefined => {
  if (!dateStr) return undefined;
  const due = new Date(dateStr);
  if (Number.isNaN(due.getTime())) return undefined;
  const days = Math.ceil((due.getTime() - Date.now()) / 86_400_000);
  if (days < 0) return { detail: `Closed ${Math.abs(days)} day${Math.abs(days) === 1 ? '' : 's'} ago`, tone: 'late' };
  if (days === 0) return { detail: 'Closes today', tone: 'soon' };
  if (days === 1) return { detail: 'Closes tomorrow', tone: 'soon' };
  return { detail: `Closes in ${days} days` };
};

const lineCount = (count: number | null | undefined): string | undefined =>
  count ? `${count} line${count === 1 ? '' : 's'}` : undefined;

const ageInHoursPhrase = (hours: number | null | undefined): string | undefined => {
  if (hours == null) return undefined;
  if (hours < 1) return 'Arrived in the last hour';
  if (hours < 24) return `Waiting ${Math.round(hours)} hour${Math.round(hours) === 1 ? '' : 's'}`;
  const days = Math.round(hours / 24);
  return `Waiting ${days} day${days === 1 ? '' : 's'}`;
};

/** A date for a row's detail, or nothing — never the "—" placeholder a table cell uses. */
const dateOrNothing = (dateStr: string | null | undefined): string | undefined =>
  formatDateSafe(dateStr, '') || undefined;

/**
 * SearchPurchaseOrders is deliberately a historical search endpoint, not an open-work endpoint.
 * Keep partial/open records visible, fail open for future statuses, and remove only lifecycle
 * states the customer can no longer act on from the urgent Inbox.
 */
const TERMINAL_CLIENT_PO_STATUSES = new Set(['FULLY_AWARDED', 'CLOSED', 'CANCELLED']);

const InboxPage: React.FC = () => {
  const navigate = useNavigate();
  const { userData, hasPermission } = useAuth();
  const businessUnitId = userData?.businessUnitId || undefined;
  const teamScope = userData?.isManager === true || userData?.isSuperAdmin === true;
  const queueContext = useMemo<InboxQueueContext>(() => ({
    businessUnitId,
    userId: userData?.id,
    teamScope,
  }), [businessUnitId, teamScope, userData?.id]);

  /**
   * Only queues this user may open are requested. Asking for a queue the server will refuse turns
   * a permission boundary into an error banner on the first screen after login.
   */
  const visibleQueues = useMemo(
    () => INBOX_QUEUES.filter((queue) => hasPermission(queue.moduleName)),
    [hasPermission],
  );
  const canManageRoles = hasPermission('Roles & Permissions', 'edit');

  /**
   * One `useQueries` rather than six `useQuery` calls, so the set of queues can be permission-
   * filtered without breaking the rules of hooks. Each `queryFn` maps its own endpoint's shape
   * onto `InboxItem` right here — the Inbox never invents a field the server did not send.
   */
  const results = useQueries({
    queries: visibleQueues.map((queue) => ({
      queryKey: ['inbox', queue.key, businessUnitId, userData?.id, teamScope] as const,
      queryFn: (): Promise<InboxItem[] | null> => loadQueue(queue.key, queueContext),
      // The landing screen is opened many times a day; a short stale window keeps it honest
      // without re-firing every queue request on each tab-back.
      staleTime: 30_000,
      // The global mutation/query error backstop already toasts. This screen renders the failure
      // in place as well, because a queue that silently vanished reads as "no work".
      meta: { silenceGlobalError: true },
    })),
  }) as UseQueryResult<InboxItem[] | null>[];

  const queues: QueueResult[] = visibleQueues.map((definition, index) => ({
    definition,
    query: results[index],
  }));

  /**
   * "Enquiries without an owner" is the ROUTING queue: accepted enquiries nobody has claimed. An
   * enquiry that is unowned because nobody has accepted it yet is not in that queue, but it is
   * still unowned — the Leads list (Owner = Unassigned) and Sales today both count it. Calling the
   * queue clear over two such enquiries was a lie by omission, so the same list the Leads page
   * reads is asked for its count and the clear line says what is really true.
   */
  const asksForUnowned = visibleQueues.some((queue) => queue.key === 'leads-to-own');
  const unownedOpen = useQuery({
    queryKey: ['inbox', 'unowned-open', businessUnitId] as const,
    queryFn: () => leadService.getAll({ pageNumber: 1, pageSize: 1, view: 'open,unassigned' }),
    enabled: asksForUnowned,
    staleTime: 30_000,
    retry: false,
    meta: { silenceGlobalError: true },
  });
  const unownedStillChecking = unownedOpen.data?.totalCount ?? 0;

  const anyLoading = queues.some((entry) => entry.query.isLoading);
  const failedCount = queues.filter((entry) => entry.query.isError).length;
  const waitingCount = queues.reduce(
    (total, entry) => total + (entry.query.isError ? 0 : entry.query.data?.length ?? 0),
    0,
  );
  const allClear = !anyLoading && failedCount === 0 && waitingCount === 0 && unownedStillChecking === 0 && queues.length > 0;

  // Null is "this tenant does not have this channel" — not zero work, no such queue here.
  const present = queues.filter((entry) => entry.query.data !== null);
  const withWork = present.filter((entry) => entry.query.isError || (entry.query.data?.length ?? 0) > 0);
  const clear = present.filter((entry) => entry.query.isSuccess && entry.query.data?.length === 0);
  const pending = present.filter((entry) => entry.query.isLoading);

  const refreshAll = () => queues.forEach((entry) => void entry.query.refetch());

  const status = queues.length === 0
    ? 'Your role does not have any Inbox work queues.'
    : anyLoading
      ? 'Checking what is waiting on you…'
      : failedCount > 0 && waitingCount === 0
        ? 'Some of your queues could not be read. What is shown below is not the whole picture.'
        : waitingCount === 0
          ? 'Nothing is waiting on you right now.'
          : `${waitingCount} ${waitingCount === 1 ? 'thing needs' : 'things need'} you`;

  return (
    <InboxFrame
      summary={status}
      summaryWarning={failedCount > 0 || queues.length === 0}
      actions={(
        <Tooltip title="Refresh">
          <span>
            <IconButton aria-label="Refresh" onClick={refreshAll} disabled={queues.length === 0} sx={{ width: 36, height: 36 }}>
              <RefreshIcon fontSize="small" />
            </IconButton>
          </span>
        </Tooltip>
      )}
    >
      {queues.length === 0 && (
        <Paper variant="outlined" sx={{ p: 4, borderRadius: 3, textAlign: 'center' }}>
          <Typography variant="h6" sx={{ fontWeight: 700 }}>
            Your role has no Inbox work queues.
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mt: 1, maxWidth: 560, mx: 'auto' }}>
            {canManageRoles
              ? 'This role has not been granted Leads, RFQ Management, Supplier History, Quotations or Customer Awards. Grant the modules it needs under Roles & Permissions.'
              : 'The Inbox shows enquiries, RFQs, supplier replies, quotes and customer orders. Ask your Nexora administrator to grant this role the modules it needs under Roles & Permissions.'}
          </Typography>
          {canManageRoles ? (
            <Button variant="contained" sx={{ mt: 2.5 }} onClick={() => navigate('/security/roles')}>
              Open Roles &amp; Permissions
            </Button>
          ) : null}
        </Paper>
      )}

      {queues.length > 0 && (
        <Paper variant="outlined" sx={INBOX_CARD_SX}>
          {allClear && (
            <Stack sx={{ alignItems: 'center', gap: 1, py: 5 }}>
              <ClearIcon sx={{ fontSize: 36, color: 'success.main' }} aria-hidden />
              <Typography sx={{ fontWeight: 700, fontSize: '1.05rem' }}>You are clear.</Typography>
            </Stack>
          )}
          {withWork.map(({ definition, query }, index) => (
            <QueueGroup key={definition.key} definition={definition} query={query} first={index === 0} />
          ))}
          {pending.length > 0 && (
            <Box
              role="status"
              aria-label="Loading your queues"
              sx={{ px: 2.5, py: 1.5, borderTop: withWork.length > 0 ? '1px solid' : 'none', borderColor: 'divider' }}
            >
              {[0, 1, 2].map((row) => (
                <Skeleton key={row} variant="text" sx={{ fontSize: '1.1rem', my: 0.5, width: `${90 - row * 12}%` }} />
              ))}
            </Box>
          )}
          {clear.length > 0 && (
            <ClearLine
              entries={clear}
              unownedStillChecking={unownedStillChecking}
              divided={withWork.length > 0 || pending.length > 0 || allClear}
            />
          )}
        </Paper>
      )}
    </InboxFrame>
  );
};

/**
 * The queues at zero, on one line at the foot of the list: each is its name and a tick, and each
 * opens the place that queue lives. Eight boxes that each said "nothing here" in their own sentence
 * was what made this screen look like a pile — a clear queue is not work and gets no box.
 *
 * "Enquiries without an owner" is the exception that is not really clear — see `unownedOpen`.
 */
const ClearLine: React.FC<{
  entries: QueueResult[];
  unownedStillChecking: number;
  divided: boolean;
}> = ({ entries, unownedStillChecking, divided }) => {
  const navigate = useNavigate();
  return (
    <Stack
      direction="row"
      sx={{
        alignItems: 'center',
        flexWrap: 'wrap',
        gap: 0.75,
        px: 2.5,
        py: 1.5,
        bgcolor: 'action.hover',
        borderTop: divided ? '1px solid' : 'none',
        borderColor: 'divider',
      }}
    >
      <Typography sx={{ fontSize: 12, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase', color: 'text.secondary', mr: 1 }}>
        Clear
      </Typography>
      {entries.map(({ definition }) => {
        const headingId = `inbox-queue-${definition.key}`;
        const note = definition.key === 'leads-to-own' ? unownedNote(unownedStillChecking) : undefined;
        return (
          <Box component="section" aria-labelledby={headingId} key={definition.key}>
            <Button
              id={headingId}
              size="small"
              startIcon={note
                ? <Box component="span" aria-hidden sx={(theme) => ({ width: 8, height: 8, borderRadius: '50%', bgcolor: soonDot(theme.palette.mode) })} />
                : <DoneIcon sx={{ fontSize: '16px !important', color: 'success.main' }} />}
              onClick={() => navigate(note ? note.path : definition.seeAllPath)}
              sx={{
                color: 'text.secondary',
                fontWeight: 600,
                borderRadius: 99,
                px: 1.25,
                minHeight: 30,
                '&:hover': { color: 'text.primary' },
              }}
            >
              {definition.title}
              {note && (
                <Box component="span" sx={{ ml: 0.75, color: 'text.primary', fontWeight: 700 }}>
                  · {note.label}
                </Box>
              )}
            </Button>
          </Box>
        );
      })}
    </Stack>
  );
};

/**
 * What "Enquiries without an owner" says when its routing queue is empty but unowned enquiries
 * exist upstream of it (nobody has accepted them yet, so they are in Documents to check).
 * Undefined when there are none, so the queue stands as clear.
 */
export const unownedNote = (stillChecking: number): { label: string; path: string } | undefined => {
  if (stillChecking <= 0) return undefined;
  return { label: `${stillChecking} still being checked`, path: '/procurement/extraction/review' };
};

/**
 * The "closes soon" dot. The theme's warning is a rust tuned for 4.5:1 small TEXT, and beside the
 * red "closed" dot it read as the same colour; a dot only needs 3:1, so it can be a true amber.
 */
const soonDot = (mode: 'light' | 'dark') => (mode === 'dark' ? '#fbbf24' : '#d97706');

const QUEUE_ROW_COLUMNS = {
  xs: 'minmax(0, 1fr) auto',
  md: 'minmax(170px, 1fr) minmax(0, 1.4fr) minmax(170px, 1fr) 140px',
};

const QUEUE_ROW_AREAS = {
  xs: '"ref act" "party act" "when act"',
  md: '"ref party when act"',
};

/**
 * One queue with work in it: a quiet heading with its count, up to five rows, and "See all".
 *
 * Every row is the same four columns across every queue — the reference, who it is for or from,
 * the one fact that decides urgency, and the verb — so the eye runs down one set of edges instead
 * of a different shape per box. Failure is an `ApiErrorNotice` with a retry and never an empty list.
 */
const QueueGroup: React.FC<{
  definition: QueueDefinition;
  query: UseQueryResult<InboxItem[] | null>;
  first: boolean;
}> = ({ definition, query, first }) => {
  const navigate = useNavigate();
  const items = query.data ?? [];
  const headingId = `inbox-queue-${definition.key}`;

  return (
    <Box
      component="section"
      aria-labelledby={headingId}
      sx={{ borderTop: first ? 'none' : '1px solid', borderColor: 'divider' }}
    >
      <Stack direction="row" sx={{ alignItems: 'center', gap: 1, px: 2.5, pt: 2, pb: 0.75 }}>
        <Typography
          id={headingId}
          component="h2"
          sx={{ fontSize: 12, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase', color: 'text.secondary' }}
        >
          {definition.title}
        </Typography>
        {!query.isError && (
          <Box
            component="span"
            className="tabular-nums"
            sx={(theme) => ({
              fontSize: 12,
              fontWeight: 700,
              lineHeight: '20px',
              minWidth: 20,
              px: 0.75,
              textAlign: 'center',
              borderRadius: 99,
              color: 'primary.dark',
              bgcolor: alpha(theme.palette.primary.main, theme.palette.mode === 'dark' ? 0.2 : 0.12),
            })}
          >
            {items.length}
          </Box>
        )}
        <Box sx={{ flex: 1 }} />
        {!query.isError && (
          <Button
            size="small"
            endIcon={<ArrowIcon sx={{ fontSize: '16px !important' }} />}
            onClick={() => navigate(definition.seeAllPath)}
            sx={{ fontWeight: 600, minHeight: 28 }}
          >
            {items.length > INBOX_PREVIEW_ROWS ? `See all ${items.length}` : 'See all'}
          </Button>
        )}
      </Stack>

      {query.isError ? (
        // Never an empty list on a failure: the rep would read it as a clear queue.
        <Box sx={{ px: 2.5, pb: 2 }}>
          <ApiErrorNotice
            error={query.error}
            context="list"
            fallbackMessage={definition.errorFallback}
            onRetry={() => void query.refetch()}
          />
        </Box>
      ) : (
        <Box sx={{ pb: 1 }}>
          {items.slice(0, INBOX_PREVIEW_ROWS).map((item) => (
            // The whole row is a larger mouse target; the button in it is the keyboard route.
            // eslint-disable-next-line jsx-a11y/no-static-element-interactions, jsx-a11y/click-events-have-key-events
            <Box
              key={`${definition.key}-${item.id}`}
              onClick={() => navigate(item.path)}
              sx={{
                display: 'grid',
                gridTemplateColumns: QUEUE_ROW_COLUMNS,
                gridTemplateAreas: QUEUE_ROW_AREAS,
                columnGap: 2,
                alignItems: 'center',
                mx: 1,
                px: 1.5,
                py: { xs: 1, md: 0.25 },
                minHeight: 42,
                borderRadius: 2,
                cursor: 'pointer',
                transition: 'background-color 120ms ease',
                '&:hover': { bgcolor: 'action.hover' },
                '&:hover .inbox-row-action': { color: 'primary.dark' },
              }}
            >
              <Typography noWrap title={item.reference} sx={{ gridArea: 'ref', fontWeight: 700, fontSize: '0.9rem', fontVariantNumeric: 'tabular-nums' }}>
                {item.reference}
              </Typography>
              <Typography noWrap variant="body2" title={item.party} sx={{ gridArea: 'party', color: 'text.primary', minWidth: 0 }}>
                {item.party}
              </Typography>
              <Stack direction="row" sx={{ gridArea: 'when', alignItems: 'center', gap: 0.75, minWidth: 0 }}>
                {item.tone && (
                  <Box
                    component="span"
                    aria-hidden
                    sx={(theme) => ({
                      width: 7,
                      height: 7,
                      borderRadius: '50%',
                      flexShrink: 0,
                      bgcolor: item.tone === 'late' ? theme.palette.error.main : soonDot(theme.palette.mode),
                    })}
                  />
                )}
                <Typography
                  noWrap
                  variant="body2"
                  sx={{
                    color: item.tone === 'late' ? 'error.main' : item.tone === 'soon' ? 'text.primary' : 'text.secondary',
                    fontWeight: item.tone ? 600 : 400,
                  }}
                >
                  {item.detail ?? ''}
                </Typography>
              </Stack>
              <Button
                className="inbox-row-action"
                size="small"
                endIcon={<ChevronIcon />}
                onClick={(event) => { event.stopPropagation(); navigate(item.path); }}
                sx={{ gridArea: 'act', justifySelf: 'end', fontWeight: 700, whiteSpace: 'nowrap', '&&': { minHeight: 32, py: 0.25 } }}
              >
                {item.actionLabel}
              </Button>
            </Box>
          ))}
        </Box>
      )}
    </Box>
  );
};

/**
 * Each queue's request, and the mapping from its endpoint's shape onto one row.
 *
 * Kept out of the component so the mapping can be tested without rendering, and so it is obvious
 * that every one of these is an endpoint an existing screen already calls.
 *
 * Null means the queue does not apply to this tenant at all — see `mail-to-rescue`. An empty
 * array means it applies and is at zero, and the two must never collapse into each other.
 */
export async function loadQueue(
  key: QueueKey,
  context: InboxQueueContext = {},
): Promise<InboxItem[] | null> {
  const { businessUnitId, userId, teamScope = false } = context;
  switch (key) {
    case 'mail-to-rescue': {
      // The same read Inbound Mail opens on: `state=stopped` is the only filter that asks what is
      // waiting on a person, because every `outcome` value answers what the arrival gate decided
      // and a message stops long after that.
      //
      // Email Intake is an ENTITLEMENT — a plan-level switch, not a role — so a tenant without it
      // gets a refusal here however its permissions are set. That is neither an outage nor work,
      // and a permanent red banner on the first screen after sign-in would be a worse lie than
      // the silence it replaced. Any other failure is a real failure and is rethrown.
      const page = await emailTriageService
        .listTriage({ state: TRIAGE_STATE_STOPPED, page: 1, pageSize: 25 })
        .catch((error: unknown) => {
          if (isTriageUnavailable(error)) return null;
          throw error;
        });
      if (page === null) return null;
      return (page.items ?? [])
        .map((row) => ({
          id: row.id,
          reference: row.subject || `Message ${row.id}`,
          party: row.from || 'Sender not recorded',
          detail: row.assemblyState
            ? describeAssemblyState(row.assemblyState).label
            : row.receivedOn
              ? `Arrived ${formatDateSafe(row.receivedOn)}`
              : undefined,
          // Inbound Mail opens on its "Needs a person" tab, which is this same read — so the row
          // the rep pressed is on the screen they land on. There is no per-message route yet.
          path: '/procurement/leads/inbound-mail',
          actionLabel: 'Open it',
          sortKey: row.receivedOn,
        }))
        .sort(byDateAscending);
    }

    case 'documents-to-check': {
      const page = await extractionReviewService.getNeedsReview({ pageNumber: 1, pageSize: 25 });
      return (page.items ?? [])
        .map((row) => ({
          id: row.id,
          reference: row.rfqno || `Document ${row.id}`,
          party: row.buyersName || 'Buyer not read yet',
          ...(deadline(row.bidClosingDate) ?? { detail: lineCount(row.itemCount) }),
          path: `/procurement/extraction/review/${row.id}`,
          actionLabel: 'Check it',
          sortKey: row.bidClosingDate ?? row.receivedOn ?? row.recDate,
        }))
        .sort(byDateAscending);
    }

    case 'leads-to-own': {
      const page = await leadService.getOutstandingLeads({
        pageNumber: 1,
        pageSize: 25,
        excludeAssigned: true,
      });
      return (page.items ?? [])
        .map((row) => ({
          id: row.id,
          reference: row.rfqno || `Enquiry ${row.id}`,
          party: row.customerName || row.buyersName || 'Customer not resolved',
          detail: ageInHoursPhrase(row.unassignedHours) ?? dateOrNothing(row.acceptedDate),
          path: `/procurement/leads/view/${row.id}`,
          actionLabel: 'Open it',
          sortKey: row.acceptedDate,
        }))
        .sort(byDateAscending);
    }

    case 'leads-to-decide': {
      // A rep request without an actor id must fail closed. Omitting assignedToId would broaden it
      // to the manager/team view and expose another rep's assigned work in the landing queue.
      if (!teamScope && !userId) {
        throw new Error('The signed-in user identity is unavailable, so assigned enquiries cannot be scoped safely.');
      }
      const page = await leadService.getAssignedLeads({
        pageNumber: 1,
        pageSize: 25,
        businessUnitId,
        assignedToId: teamScope ? undefined : userId,
      });
      return (page.items ?? [])
        .map((row) => ({
          id: row.id,
          reference: row.rfqno || `Enquiry ${row.id}`,
          party: row.customerName || row.buyersName || 'Customer not resolved',
          detail: [
            teamScope && row.assignedToFullName ? `Assigned to ${row.assignedToFullName}` : null,
            dateOrNothing(row.acceptedDate),
          ].filter(Boolean).join(' · ') || undefined,
          path: `/procurement/leads/${row.id}/workbench`,
          actionLabel: 'Make decision',
          sortKey: row.assignedOn ?? row.acceptedDate,
        }))
        .sort(byDateAscending);
    }

    case 'rfqs-in-draft': {
      const page = await rfqService.getAll({
        pageNumber: 1,
        pageSize: 25,
        rfqStatusCode: 'DRAFT',
        businessUnitId,
      });
      return (page.items ?? [])
        .map((row) => ({
          id: row.id,
          reference: row.rfqno || `RFQ-${row.id}`,
          party: row.buyersName || 'Buyer not recorded',
          ...(deadline(row.bidClosingDate) ?? { detail: lineCount(row.noOfLineItems) }),
          path: `/procurement/rfqs/view/${row.id}`,
          actionLabel: 'Open RFQ',
          sortKey: row.bidClosingDate ?? row.recDate,
        }))
        .sort(byDateAscending);
    }

    case 'supplier-replies': {
      // The inbox endpoint only ever returns the two open states (REVIEW_REQUIRED and
      // READY_FOR_COMPARISON) — accepted and rejected replies leave it — so everything it hands
      // back is work. No client-side status filter here: inventing one would silently hide a row
      // the day the server adds a third open state.
      const rows = await supplierQuoteService.getInbox();
      return rows
        .map((row) => ({
          id: row.supplierQuoteId,
          reference: row.supplierQuoteReference || `Supplier quote ${row.supplierQuoteId}`,
          party: row.supplierName || 'Supplier not named',
          detail: row.reviewRequiredCount
            ? `${row.reviewRequiredCount} field${row.reviewRequiredCount === 1 ? '' : 's'} to confirm`
            : row.nexoraSerial || undefined,
          path: `/procurement/supplier-quotes/${row.supplierQuoteId}`,
          actionLabel: 'Read reply',
          sortKey: row.updatedOn,
        }))
        .sort(byDateAscending);
    }

    case 'quotes-to-send': {
      const page = await quoteService.getAll({
        pageNumber: 1,
        pageSize: 25,
        state: 'draft',
        businessUnitId,
      });
      return (page.items ?? [])
        .map((row) => ({
          id: row.id,
          reference: row.quoteNo || `Quote ${row.id}`,
          party: row.customerName || 'Customer not linked',
          detail: lineCount(row.itemCount),
          path: `/sales/quotes/view/${row.id}`,
          actionLabel: 'Open quote',
          sortKey: row.quoteDate,
        }))
        .sort(byDateAscending);
    }

    case 'client-pos': {
      const rows = await customerAwardService.searchPurchaseOrders('', 25);
      return rows
        .filter((row) => !TERMINAL_CLIENT_PO_STATUSES.has((row.status ?? '').trim().toUpperCase()))
        .map((row) => ({
          id: row.id,
          reference: row.externalPoNumber || row.internalNumber || `PO ${row.id}`,
          party: row.customerName || 'Customer not named',
          detail: row.discrepancyCount
            ? `${row.discrepancyCount} difference${row.discrepancyCount === 1 ? '' : 's'} against the quote`
            : row.quoteNumber
              ? `Against ${row.quoteNumber}`
              : undefined,
          path: `/sales/client-pos/${row.id}`,
          actionLabel: 'Match it',
          sortKey: row.receivedOn,
        }))
        .sort(byDateAscending);
    }

    default: {
      // Exhaustiveness: a new QueueKey without a loader is a compile error, not an empty section.
      const unreachable: never = key;
      throw new Error(`No loader for inbox queue ${String(unreachable)}`);
    }
  }
}

export default InboxPage;
