import React, { useMemo } from 'react';
import { useQueries, type UseQueryResult } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  Box,
  Button,
  Chip,
  CircularProgress,
  Divider,
  Paper,
  Stack,
  Typography,
} from '@mui/material';
import {
  ArrowForward as ArrowIcon,
  CheckCircleOutlined as ClearIcon,
  Refresh as RefreshIcon,
} from '@mui/icons-material';
import ApiErrorNotice from '../../components/common/ApiErrorNotice';
import ViewTabs from '../../components/layout/ViewTabs';
import { useAuth } from '../../context/AuthContext';
import { formatDateSafe } from '../../utils/dates';
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
 */

interface QueueResult {
  definition: QueueDefinition;
  query: UseQueryResult<InboxItem[]>;
}

const byDateAscending = (a: InboxItem, b: InboxItem) => {
  if (!a.sortKey) return 1;
  if (!b.sortKey) return -1;
  return a.sortKey.localeCompare(b.sortKey);
};

/** Whole days between now and a deadline, as a phrase a person would say. */
const deadlinePhrase = (dateStr: string | null | undefined): string | undefined => {
  if (!dateStr) return undefined;
  const due = new Date(dateStr);
  if (Number.isNaN(due.getTime())) return undefined;
  const days = Math.ceil((due.getTime() - Date.now()) / 86_400_000);
  if (days < 0) return `Closed ${Math.abs(days)} day${Math.abs(days) === 1 ? '' : 's'} ago`;
  if (days === 0) return 'Closes today';
  if (days === 1) return 'Closes tomorrow';
  return `Closes in ${days} days`;
};

const ageInHoursPhrase = (hours: number | null | undefined): string | undefined => {
  if (hours == null) return undefined;
  if (hours < 1) return 'Arrived in the last hour';
  if (hours < 24) return `Waiting ${Math.round(hours)} hour${Math.round(hours) === 1 ? '' : 's'}`;
  const days = Math.round(hours / 24);
  return `Waiting ${days} day${days === 1 ? '' : 's'}`;
};

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
      queryKey: ['inbox', queue.key, businessUnitId] as const,
      queryFn: (): Promise<InboxItem[]> => loadQueue(queue.key, businessUnitId),
      // The landing screen is opened many times a day; a short stale window keeps it honest
      // without re-firing six requests on every tab-back.
      staleTime: 30_000,
      // The global mutation/query error backstop already toasts. This screen renders the failure
      // in place as well, because a queue that silently vanished reads as "no work".
      meta: { silenceGlobalError: true },
    })),
  }) as UseQueryResult<InboxItem[]>[];

  const queues: QueueResult[] = visibleQueues.map((definition, index) => ({
    definition,
    query: results[index],
  }));

  const anyLoading = queues.some((entry) => entry.query.isLoading);
  const failedCount = queues.filter((entry) => entry.query.isError).length;
  const waitingCount = queues.reduce(
    (total, entry) => total + (entry.query.isError ? 0 : entry.query.data?.length ?? 0),
    0,
  );
  const allClear = !anyLoading && failedCount === 0 && waitingCount === 0 && queues.length > 0;

  const refreshAll = () => queues.forEach((entry) => void entry.query.refetch());

  return (
    <Box sx={{ p: { xs: 2, md: 3 }, maxWidth: 1200, mx: 'auto' }}>
      <Stack
        direction={{ xs: 'column', sm: 'row' }}
        spacing={2}
        sx={{ justifyContent: 'space-between', alignItems: { sm: 'flex-start' }, mb: 1 }}
      >
        <Box sx={{ minWidth: 0 }}>
          <Typography variant="h4" component="h1" sx={{ fontWeight: 800, letterSpacing: '-0.02em' }}>
            Inbox
          </Typography>
          <Typography
            variant="body1"
            color="text.secondary"
            sx={{ mt: 0.5, maxWidth: 720, lineHeight: 1.6 }}
            aria-live="polite"
          >
            {queues.length === 0
              ? 'Your role does not have any Inbox work queues.'
              : anyLoading
              ? 'Checking what is waiting on you…'
              : failedCount > 0 && waitingCount === 0
                ? 'Some of your queues could not be read. What is shown below is not the whole picture.'
                : waitingCount === 0
                  ? 'Nothing is waiting on you right now.'
                  : `${waitingCount} ${waitingCount === 1 ? 'thing needs' : 'things need'} you. Work down the list — the top of it is the oldest part of the process.`}
          </Typography>
        </Box>
        <Button
          variant="outlined"
          startIcon={<RefreshIcon />}
          onClick={refreshAll}
          disabled={queues.length === 0}
          sx={{ flexShrink: 0, fontWeight: 700 }}
        >
          Refresh
        </Button>
      </Stack>

      <ViewTabs primaryKey="inbox" ariaLabel="Inbox views" />

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

      {allClear && (
        <Paper
          variant="outlined"
          sx={{ p: 3, borderRadius: 3, mb: 3, display: 'flex', gap: 2, alignItems: 'center' }}
        >
          <ClearIcon sx={{ fontSize: 40, color: 'success.main' }} aria-hidden />
          <Box sx={{ minWidth: 0 }}>
            <Typography variant="h6" sx={{ fontWeight: 700 }}>
              You are clear.
            </Typography>
            <Typography variant="body2" color="text.secondary">
              Every queue below is empty and all of them were read successfully. New work arrives on
              its own from the mailbox and watched folders.
            </Typography>
          </Box>
        </Paper>
      )}

      <Stack spacing={2.5} sx={{ mt: 2 }}>
        {queues.map(({ definition, query }) => (
          <QueueSection key={definition.key} definition={definition} query={query} />
        ))}
      </Stack>
    </Box>
  );
};

/**
 * One queue: heading with a count, up to five rows each ending in a verb, and a link to the rest.
 *
 * The four states are all handled here and none of them can be mistaken for another — loading is a
 * spinner, failure is an `ApiErrorNotice` with a retry, zero is a stated reason plus a button, and
 * rows are rows.
 */
const QueueSection: React.FC<{ definition: QueueDefinition; query: UseQueryResult<InboxItem[]> }> = ({
  definition,
  query,
}) => {
  const navigate = useNavigate();
  const { hasPermission } = useAuth();
  const items = query.data ?? [];
  const headingId = `inbox-queue-${definition.key}`;

  const emptyActionAllowed =
    !definition.emptyAction.moduleName || hasPermission(definition.emptyAction.moduleName);

  return (
    <Paper
      component="section"
      aria-labelledby={headingId}
      variant="outlined"
      sx={{ borderRadius: 3, overflow: 'hidden' }}
    >
      <Box sx={{ px: { xs: 2, sm: 2.5 }, pt: 2, pb: 1.5 }}>
        <Stack
          direction="row"
          spacing={1.5}
          sx={{ alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap' }}
        >
          <Stack direction="row" spacing={1.25} sx={{ alignItems: 'center', minWidth: 0 }}>
            <Typography id={headingId} variant="h6" component="h2" sx={{ fontWeight: 800 }}>
              {definition.title}
            </Typography>
            {!query.isLoading && !query.isError && (
              <Chip
                size="small"
                label={items.length}
                color={items.length > 0 ? 'primary' : 'default'}
                sx={{ height: 22, fontWeight: 800 }}
              />
            )}
          </Stack>
          {!query.isError && items.length > 0 && (
            <Button
              size="small"
              endIcon={<ArrowIcon />}
              onClick={() => navigate(definition.seeAllPath)}
              sx={{ fontWeight: 700 }}
            >
              {definition.seeAllLabel}
            </Button>
          )}
        </Stack>
        <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
          {definition.purpose}
        </Typography>
      </Box>

      <Divider />

      {query.isLoading ? (
        <Box sx={{ display: 'grid', placeItems: 'center', py: 4 }}>
          <CircularProgress size={26} aria-label={`Loading ${definition.title}`} />
        </Box>
      ) : query.isError ? (
        // Never an empty list on a failure: the rep would read it as a clear queue.
        <Box sx={{ p: 2 }}>
          <ApiErrorNotice
            error={query.error}
            fallbackMessage={definition.errorFallback}
            onRetry={() => void query.refetch()}
          />
        </Box>
      ) : items.length === 0 ? (
        <Box sx={{ px: { xs: 2, sm: 2.5 }, py: 3.5, textAlign: 'center' }}>
          <Typography sx={{ fontWeight: 700 }}>{definition.emptyTitle}</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5, maxWidth: 560, mx: 'auto' }}>
            {definition.emptyMessage}
          </Typography>
          {emptyActionAllowed && (
            <Button
              variant="outlined"
              sx={{ mt: 2, fontWeight: 700 }}
              onClick={() => navigate(definition.emptyAction.path)}
            >
              {definition.emptyAction.label}
            </Button>
          )}
        </Box>
      ) : (
        <Box>
          {items.slice(0, INBOX_PREVIEW_ROWS).map((item, index) => (
            <Stack
              key={`${definition.key}-${item.id}`}
              direction={{ xs: 'column', sm: 'row' }}
              spacing={1}
              sx={{
                px: { xs: 2, sm: 2.5 },
                py: 1.5,
                alignItems: { sm: 'center' },
                justifyContent: 'space-between',
                borderTop: index === 0 ? 0 : '1px solid',
                borderColor: 'divider',
              }}
            >
              <Box sx={{ minWidth: 0 }}>
                <Typography sx={{ fontWeight: 700, overflowWrap: 'anywhere' }}>
                  {item.reference}
                </Typography>
                <Typography variant="body2" color="text.secondary" sx={{ overflowWrap: 'anywhere' }}>
                  {item.party}
                  {item.detail ? ` · ${item.detail}` : ''}
                </Typography>
              </Box>
              <Button
                variant="contained"
                size="small"
                onClick={() => navigate(item.path)}
                sx={{ flexShrink: 0, fontWeight: 700, alignSelf: { xs: 'flex-start', sm: 'center' } }}
              >
                {item.actionLabel}
              </Button>
            </Stack>
          ))}
          {items.length > INBOX_PREVIEW_ROWS && (
            <>
              <Divider />
              <Box sx={{ px: { xs: 2, sm: 2.5 }, py: 1.25 }}>
                <Button
                  size="small"
                  endIcon={<ArrowIcon />}
                  onClick={() => navigate(definition.seeAllPath)}
                  sx={{ fontWeight: 700 }}
                >
                  {`${items.length - INBOX_PREVIEW_ROWS} more — ${definition.seeAllLabel.toLowerCase()}`}
                </Button>
              </Box>
            </>
          )}
        </Box>
      )}
    </Paper>
  );
};

/**
 * Each queue's request, and the mapping from its endpoint's shape onto one row.
 *
 * Kept out of the component so the mapping can be tested without rendering, and so it is obvious
 * that every one of these is an endpoint an existing screen already calls.
 */
export async function loadQueue(key: QueueKey, businessUnitId?: number): Promise<InboxItem[]> {
  switch (key) {
    case 'documents-to-check': {
      const page = await extractionReviewService.getNeedsReview({ pageNumber: 1, pageSize: 25 });
      return (page.items ?? [])
        .map((row) => ({
          id: row.id,
          reference: row.rfqno || `Document ${row.id}`,
          party: row.buyersName || 'Buyer not read yet',
          detail:
            deadlinePhrase(row.bidClosingDate) ??
            (row.itemCount ? `${row.itemCount} line${row.itemCount === 1 ? '' : 's'}` : undefined),
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
          detail: ageInHoursPhrase(row.unassignedHours) ?? formatDateSafe(row.acceptedDate),
          path: `/procurement/leads/view/${row.id}`,
          actionLabel: 'Open it',
          sortKey: row.acceptedDate,
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
          detail:
            deadlinePhrase(row.bidClosingDate) ??
            (row.noOfLineItems ? `${row.noOfLineItems} line${row.noOfLineItems === 1 ? '' : 's'}` : undefined),
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
          detail: row.itemCount ? `${row.itemCount} line${row.itemCount === 1 ? '' : 's'}` : undefined,
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
