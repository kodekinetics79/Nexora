import React, { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  Box, Typography, Paper, Stack, Chip, Button, Tooltip, Alert, Divider,
  Table, TableBody, TableCell, TableContainer, TableHead, TableRow, TableSortLabel,
} from '@mui/material';
import {
  Refresh as RefreshIcon,
  AutoFixHigh as MatchIcon,
  EventBusy as OverdueIcon,
  Today as TodayIcon,
  Schedule as SoonIcon,
  DateRange as LaterIcon,
  HelpOutlined as NoDateIcon,
} from '@mui/icons-material';
import { toast } from 'react-hot-toast';
import leadService, { type LeadResponseDTO } from '../../api/services/leadService';
import ResolveClientDialog from '../Leads/ResolveClientDialog';
import { useAuth } from '../../context/AuthContext';
import { parseDateSafe } from '../../utils/dates';
import { presentableErrorMessage } from '../../utils/apiErrors';
import { LoadingState, ErrorState, EmptyState } from '../../platform/components/States';

// ---------------------------------------------------------------------------
// Deadline board — what closes when, and how much work each one is.
//
// Forward-looking and built only on data the platform already holds today:
// Lead.BidClosingDate — or, where the enquiry never stated one, Lead.SubDate — plus the line
// count and the client the enquiry is linked to. The fallback matches the one the server uses
// for `lateIngested` (api/services/leadService.ts), but the two dates mean different things,
// so the date cell names which field it read rather than presenting a submission date as a
// bid-closing deadline.
//
// The client column is not decoration. An enquiry with no client record cannot be
// qualified, cannot become an RFQ and therefore cannot be quoted, so on this board an
// unlinked row is not a cosmetic gap — it is the reason that deadline will be missed.
// It used to render as the greyed-out sentence "Not linked to a client record", which
// named the blocker and offered nothing to do about it. It is now the control that
// clears it, because a board of work should be a board of work you can do.
//
// Every number states its denominator, and leads whose deadline was already
// past when Nexora first saw the document are disclosed rather than quietly
// mixed in: they are not a response-time failure and counting them as urgent
// work would misstate the queue.
//
// SCOPE: the whole live pipeline. This board asked the leads API for its DEFAULT
// view, which is the untriaged inbox — so advancing a tender took it off the
// board, and the board that counts down to bid deadlines was showing only mail
// nobody had touched. It now asks for the `open` view and names, in the footnote,
// the only work it leaves out: enquiries that are actually finished.
// ---------------------------------------------------------------------------

/** How many leads to pull. The pilot tenant holds 27; the cap is disclosed. */
const FETCH_LIMIT = 500;

type BucketKey = 'overdue' | 'today' | 'next3' | 'thisWeek' | 'later' | 'noDate';

interface BucketMeta {
  key: BucketKey;
  label: string;
  hint: string;
  icon: React.ReactNode;
  /** Status colour — reserved for state, never reused as a series colour. */
  color: 'error' | 'warning' | 'info' | 'success' | 'default';
}

const BUCKETS: readonly BucketMeta[] = [
  { key: 'overdue', label: 'Past deadline', hint: 'Closed before today', icon: <OverdueIcon />, color: 'error' },
  { key: 'today', label: 'Closes today', hint: 'Due before midnight', icon: <TodayIcon />, color: 'error' },
  { key: 'next3', label: 'Next 3 days', hint: '1–3 days out', icon: <SoonIcon />, color: 'warning' },
  { key: 'thisWeek', label: '4–7 days', hint: 'Within the week', icon: <SoonIcon />, color: 'info' },
  { key: 'later', label: '8 days or more', hint: 'Comfortable', icon: <LaterIcon />, color: 'success' },
  { key: 'noDate', label: 'No deadline recorded', hint: 'Nothing extracted to schedule against', icon: <NoDateIcon />, color: 'default' },
];

/** Whole days from today (local) to the deadline. Null when no usable date. */
const daysToClose = (lead: LeadResponseDTO): number | null => {
  const due = parseDateSafe(lead.bidClosingDate) ?? parseDateSafe(lead.subDate ?? null);
  if (!due) return null;
  const startOfToday = new Date();
  startOfToday.setHours(0, 0, 0, 0);
  const startOfDue = new Date(due);
  startOfDue.setHours(0, 0, 0, 0);
  return Math.round((startOfDue.getTime() - startOfToday.getTime()) / 86_400_000);
};

const bucketFor = (days: number | null): BucketKey => {
  if (days == null) return 'noDate';
  if (days < 0) return 'overdue';
  if (days === 0) return 'today';
  if (days <= 3) return 'next3';
  if (days <= 7) return 'thisWeek';
  return 'later';
};

/**
 * The server view that means "still live work": every enquiry the tenant holds that has not
 * finished, INCLUDING the ones somebody has already started.
 *
 * The board used to send no view at all, which is the untriaged-inbox default — LeadRepository
 * applies `LeadStatusId == null` to it, and any lifecycle transition stamps a status. So the rep
 * who opened the top tender, pressed "Advance · RECEIVED" and worked it landed here the next
 * morning to find it GONE from the only screen in the product that counts down to a deadline.
 */
const OPEN_WORK_VIEW = 'open';

/**
 * Lifecycle states that END an enquiry — LifecyclePolicy's LeadTerminal set verbatim
 * (Backend/CommercialCases/Lifecycle/LifecyclePolicy.cs). Everything else is live work,
 * qualified and converted included.
 */
const FINISHED_STATUSES: ReadonlySet<string> = new Set([
  'DISQUALIFIED', 'LOST', 'CANCELLED', 'COMPLETED', 'DUPLICATED',
]);

/**
 * A lead still needing a decision.
 *
 * This read `!lead.isRejected`, which could never return false: `isRejected` is
 * `LeadStatusId == 25`, and the view the board asked for returned only rows where that column
 * was null. So the footnote's "27 open of 27" was a tautology reporting a filter that had
 * already removed everything it claimed to be counting, and it named REJECTION as the exclusion
 * while the real one — having any status at all — went unmentioned.
 *
 * It now reads the lead's own governed status code. The `open` view already refuses finished
 * work server-side, so this is the second line rather than the first: a tenant whose LeadStatus
 * rows are incomplete — the case LifecycleStatusCatalog.EnsureAsync exists to repair — leaves the
 * server unable to recognise its own terminal states, and a deadline board is the last screen
 * where closed work should reappear as urgent.
 */
const isOpen = (lead: LeadResponseDTO): boolean =>
  !lead.isRejected && !FINISHED_STATUSES.has((lead.leadStatusCode ?? '').trim().toUpperCase());

/** Somebody has advanced this one at least once: it carries a lifecycle status. */
const isInProgress = (lead: LeadResponseDTO): boolean => !!lead.leadStatusCode;

const clientLabel = (lead: LeadResponseDTO): string =>
  lead.customerName?.trim()
  || lead.customerCompanyNameExtracted?.trim()
  || lead.buyersName?.trim()
  || 'Client not resolved';

const dayLabel = (days: number | null): string => {
  if (days == null) return 'No deadline';
  if (days < 0) return `${Math.abs(days)} day${Math.abs(days) === 1 ? '' : 's'} past`;
  if (days === 0) return 'Today';
  return `${days} day${days === 1 ? '' : 's'}`;
};

type SortKey = 'deadline' | 'lines' | 'client';

const DeadlineBoardPage: React.FC = () => {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { hasPermission, userData } = useAuth();
  const canLinkClients = hasPermission('Leads', 'edit');
  // The tenant-wide re-run is manager-gated on the server (RequireManagerRole). Offering it
  // to someone the server will refuse is a worse experience than not offering it at all.
  const canRunMatcher = canLinkClients && userData?.isManager === true;
  // One dialog for the whole table, not one per row: 500 mounted dialogs is 500 mounted
  // candidate queries. The row being resolved is state, the dialog is a singleton.
  const [resolveLead, setResolveLead] = useState<LeadResponseDTO | null>(null);
  const [activeBucket, setActiveBucket] = useState<BucketKey | 'all'>('all');

  /**
   * Re-runs deterministic client matching over the tenant's undecided leads.
   *
   * It is offered HERE, next to the unlinked rows, because this is the only screen that
   * shows how many enquiries the missing client is actually blocking. It earns its place
   * after a person links one client by hand: that link teaches the matcher the buyer's
   * domain, and this applies the lesson to every other enquiry already in the pile. The
   * real counts are reported back — including how many are still unresolved — because
   * "matched 0 of 31" is the answer an operator needs in order to know the customer record
   * simply does not exist yet.
   */
  const runMatcher = useMutation({
    mutationFn: () => leadService.resolveClients({ includeSuggested: true }),
    onSuccess: (result) => {
      queryClient.invalidateQueries({ queryKey: ['deadline-board'] });
      queryClient.invalidateQueries({ queryKey: ['leads'] });
      toast.success(
        result.autoMatched > 0
          ? `Linked ${result.autoMatched} of ${result.examined} unresolved enquir${result.examined === 1 ? 'y' : 'ies'}`
            + `${result.suggested + result.ambiguous > 0 ? `, and proposed a client for ${result.suggested + result.ambiguous} more` : ''}.`
          : `Checked ${result.examined} unresolved enquir${result.examined === 1 ? 'y' : 'ies'} and matched none. `
            + 'Nexora can only match clients it already has a record of — link one by hand and it will recognise the rest.',
        { duration: 8_000 },
      );
    },
    onError: (error: unknown) => toast.error(presentableErrorMessage(
      error, 'Client matching could not be run. Nothing was changed.')),
  });
  const [sortKey, setSortKey] = useState<SortKey>('deadline');
  const [sortAsc, setSortAsc] = useState(true);

  const leads = useQuery({
    queryKey: ['deadline-board', OPEN_WORK_VIEW, FETCH_LIMIT],
    queryFn: () => leadService.getAll({ pageNumber: 1, pageSize: FETCH_LIMIT, view: OPEN_WORK_VIEW }),
    staleTime: 60_000,
  });

  const model = useMemo(() => {
    const all = leads.data?.items ?? [];
    const open = all.filter(isOpen);
    // Disclosed, not silently dropped: the document reached Nexora after its own
    // deadline had passed, so it is neither a live opportunity nor a miss.
    const lateIngested = open.filter((lead) => lead.lateIngested === true);
    const scheduled = open.filter((lead) => lead.lateIngested !== true);

    const rows = scheduled.map((lead) => {
      const days = daysToClose(lead);
      return { lead, days, bucket: bucketFor(days), lines: lead.itemCount ?? 0 };
    });

    const byBucket = new Map<BucketKey, { leads: number; lines: number }>();
    for (const bucket of BUCKETS) byBucket.set(bucket.key, { leads: 0, lines: 0 });
    for (const row of rows) {
      const tally = byBucket.get(row.bucket)!;
      tally.leads += 1;
      tally.lines += row.lines;
    }

    return {
      rows,
      byBucket,
      totalLeadsReturned: all.length,
      totalOpen: open.length,
      // Not decoration: this is the number that proves the board is showing worked enquiries
      // rather than untriaged mail, and it is the half the old footnote could never report.
      inProgressCount: open.filter(isInProgress).length,
      // Counted over ALL open enquiries, including the late-ingested ones excluded from the
      // buckets: they are still enquiries the tenant cannot quote, and the matcher would
      // still try them.
      unlinkedCount: open.filter((lead) => !lead.customerId).length,
      unlinkedLines: open.filter((lead) => !lead.customerId)
        .reduce((sum, lead) => sum + (lead.itemCount ?? 0), 0),
      lateIngestedCount: lateIngested.length,
      lateIngestedLines: lateIngested.reduce((sum, lead) => sum + (lead.itemCount ?? 0), 0),
      truncated: (leads.data?.totalCount ?? 0) > all.length,
      totalCount: leads.data?.totalCount ?? all.length,
    };
  }, [leads.data]);

  const visibleRows = useMemo(() => {
    const filtered = activeBucket === 'all' ? model.rows : model.rows.filter((row) => row.bucket === activeBucket);
    const direction = sortAsc ? 1 : -1;
    return [...filtered].sort((a, b) => {
      if (sortKey === 'lines') return (a.lines - b.lines) * direction;
      if (sortKey === 'client') return clientLabel(a.lead).localeCompare(clientLabel(b.lead)) * direction;
      // Undated leads sort last regardless of direction — they are not "soonest".
      if (a.days == null && b.days == null) return 0;
      if (a.days == null) return 1;
      if (b.days == null) return -1;
      return (a.days - b.days) * direction;
    });
  }, [model.rows, activeBucket, sortKey, sortAsc]);

  const toggleSort = (key: SortKey) => {
    if (key === sortKey) setSortAsc((prev) => !prev);
    else { setSortKey(key); setSortAsc(true); }
  };

  const totalScheduledLines = model.rows.reduce((sum, row) => sum + row.lines, 0);

  return (
    <Box sx={{ p: { xs: 1.5, md: 3 }, maxWidth: 1440, mx: 'auto' }}>
      <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { md: 'flex-end' }, mb: 2.5 }}>
        <Box>
          <Typography variant="h4" sx={{ fontWeight: 900 }}>Deadline board</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
            Open enquiries — new and already in progress — by how long is left to respond, with the
            amount of work each one carries.
          </Typography>
        </Box>
        <Stack direction="row" spacing={1}>
          {canRunMatcher && model.unlinkedCount > 0 && (
            <Tooltip title="Re-checks every enquiry nobody has decided against the clients this tenant already knows. It never changes a client a person chose.">
              <span>
                <Button
                  variant="outlined"
                  color="warning"
                  startIcon={<MatchIcon />}
                  onClick={() => runMatcher.mutate()}
                  disabled={runMatcher.isPending}
                  sx={{ fontWeight: 800, borderRadius: 2 }}
                >
                  {runMatcher.isPending ? 'Matching…' : 'Match clients automatically'}
                </Button>
              </span>
            </Tooltip>
          )}
          <Button
            variant="outlined"
            startIcon={<RefreshIcon />}
            onClick={() => void leads.refetch()}
            disabled={leads.isFetching}
            sx={{ fontWeight: 800, borderRadius: 2 }}
          >
            Refresh
          </Button>
        </Stack>
      </Stack>

      {leads.isLoading ? (
        <LoadingState label="Loading open enquiries…" />
      ) : leads.isError ? (
        <ErrorState
          message={presentableErrorMessage(leads.error, 'The open enquiries could not be loaded. Nothing was changed — try again.')}
          onRetry={() => void leads.refetch()}
        />
      ) : model.totalOpen === 0 ? (
        <EmptyState
          title="No open enquiries"
          message={`Nothing is awaiting a response. ${model.totalCount} enquir${model.totalCount === 1 ? 'y has' : 'ies have'} been recorded in total.`}
        />
      ) : (
        <>
          {/* Buckets. Counts and line totals only — no percentage, no target. */}
          <Box
            role="group"
            aria-label="Deadline buckets"
            sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr 1fr', md: 'repeat(3, 1fr)', lg: 'repeat(6, 1fr)' }, gap: 1.5, mb: 2.5 }}
          >
            {BUCKETS.map((bucket) => {
              const tally = model.byBucket.get(bucket.key)!;
              const selected = activeBucket === bucket.key;
              return (
                // A native <button> so keyboard activation, focus ring and the
                // pressed state come from the platform rather than being
                // re-implemented on a div. jsx-a11y resolves rules by JSX name
                // and cannot see through MUI's `component` prop, so it reads
                // this as a static element; the rendered DOM — which is what the
                // axe gate inspects — is a real <button type="button">.
                /* eslint-disable-next-line jsx-a11y/no-static-element-interactions, jsx-a11y/click-events-have-key-events */
                <Box
                  key={bucket.key}
                  component="button"
                  type="button"
                  onClick={() => setActiveBucket(selected ? 'all' : bucket.key)}
                  aria-pressed={selected}
                  sx={{
                    font: 'inherit', color: 'inherit', appearance: 'none',
                    p: 1.75, borderRadius: 2, textAlign: 'left', cursor: 'pointer', width: '100%',
                    border: '1px solid',
                    borderWidth: selected ? 2 : 1,
                    borderColor: selected ? 'primary.main' : 'divider',
                    bgcolor: 'background.paper',
                    display: 'flex', flexDirection: 'column', gap: 0.5,
                    '&:hover': { borderColor: 'primary.main' },
                  }}
                >
                  <Stack direction="row" spacing={0.75} sx={{ alignItems: 'center', color: bucket.color === 'default' ? 'text.disabled' : `${bucket.color}.main` }}>
                    <Box sx={{ display: 'flex', fontSize: 16 }}>{bucket.icon}</Box>
                    <Typography component="span" sx={{ fontWeight: 800, fontSize: '0.7rem', textTransform: 'uppercase', letterSpacing: '0.02em' }}>
                      {bucket.label}
                    </Typography>
                  </Stack>
                  <Typography component="span" sx={{ fontWeight: 900, fontSize: '1.6rem', lineHeight: 1.1 }}>{tally.leads}</Typography>
                  <Typography component="span" variant="caption" color="text.secondary">
                    {tally.leads === 1 ? 'enquiry' : 'enquiries'} · {tally.lines.toLocaleString()} line{tally.lines === 1 ? '' : 's'}
                  </Typography>
                  <Typography component="span" variant="caption" color="text.disabled" sx={{ fontSize: '0.65rem' }}>{bucket.hint}</Typography>
                </Box>
              );
            })}
          </Box>

          <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>
            {model.rows.length} open enquir{model.rows.length === 1 ? 'y' : 'ies'} carrying {totalScheduledLines.toLocaleString()} line
            {totalScheduledLines === 1 ? '' : 's'}
            {activeBucket !== 'all' && ` · showing ${visibleRows.length} in "${BUCKETS.find((b) => b.key === activeBucket)?.label}"`}
            {activeBucket !== 'all' && (
              <Button size="small" onClick={() => setActiveBucket('all')} sx={{ ml: 1, textTransform: 'none' }}>Show all</Button>
            )}
          </Typography>

          <TableContainer component={Paper} variant="outlined" sx={{ borderRadius: 2 }}>
            <Table size="small" aria-label="Open enquiries by deadline">
              <TableHead>
                <TableRow>
                  <TableCell sortDirection={sortKey === 'deadline' ? (sortAsc ? 'asc' : 'desc') : false}>
                    <TableSortLabel active={sortKey === 'deadline'} direction={sortAsc ? 'asc' : 'desc'} onClick={() => toggleSort('deadline')}>
                      Time left
                    </TableSortLabel>
                  </TableCell>
                  <TableCell>Deadline</TableCell>
                  <TableCell>RFQ #</TableCell>
                  <TableCell sortDirection={sortKey === 'client' ? (sortAsc ? 'asc' : 'desc') : false}>
                    <TableSortLabel active={sortKey === 'client'} direction={sortAsc ? 'asc' : 'desc'} onClick={() => toggleSort('client')}>
                      Client
                    </TableSortLabel>
                  </TableCell>
                  <TableCell align="right" sortDirection={sortKey === 'lines' ? (sortAsc ? 'asc' : 'desc') : false}>
                    <TableSortLabel active={sortKey === 'lines'} direction={sortAsc ? 'asc' : 'desc'} onClick={() => toggleSort('lines')}>
                      Lines
                    </TableSortLabel>
                  </TableCell>
                  <TableCell align="right">Open</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {visibleRows.map(({ lead, days, lines, bucket }) => {
                  const meta = BUCKETS.find((b) => b.key === bucket)!;
                  const due = parseDateSafe(lead.bidClosingDate) ?? parseDateSafe(lead.subDate ?? null);
                  return (
                    <TableRow key={lead.id} hover>
                      <TableCell>
                        <Chip
                          size="small"
                          label={dayLabel(days)}
                          color={meta.color === 'default' ? 'default' : meta.color}
                          variant="outlined"
                          sx={{ fontWeight: 800, fontSize: '0.7rem' }}
                        />
                      </TableCell>
                      <TableCell sx={{ color: 'text.secondary', whiteSpace: 'nowrap' }}>
                        {due ? (
                          <Tooltip title={parseDateSafe(lead.bidClosingDate)
                            ? 'Bid closing date, as stated on the enquiry'
                            : 'Submission date — the enquiry stated no bid closing date'}>
                            <Box component="span" sx={{ borderBottom: '1px dotted', borderColor: 'divider', cursor: 'help' }}>
                              {due.toLocaleDateString(undefined, { day: '2-digit', month: 'short', year: 'numeric' })}
                              {!parseDateSafe(lead.bidClosingDate) && <Box component="span" sx={{ ml: 0.5, fontStyle: 'italic', fontSize: '0.75rem' }}>(submitted)</Box>}
                            </Box>
                          </Tooltip>
                        ) : 'Not recorded'}
                      </TableCell>
                      <TableCell sx={{ fontWeight: 700 }}>
                        {/* The reference keeps an element of its own: it is what a rep searches
                            the screen for, and burying it in a cell alongside other text makes it
                            unfindable to both a person scanning and a test asserting. */}
                        <Box component="span" sx={{ display: 'block', fontFamily: 'monospace' }}>
                          {lead.rfqno || `#${lead.id}`}
                        </Box>
                        {/* Where this one stands. Without it the board reads as one undifferentiated
                            pile of new mail, and the rep cannot see which tenders she has already
                            started — the exact question that sends her back into each row. */}
                        <Typography variant="caption" component="div" color="text.secondary" sx={{ fontWeight: 600 }}>
                          {lead.leadStatusLabel?.trim() || 'Not yet triaged'}
                        </Typography>
                      </TableCell>
                      <TableCell>
                        <Typography variant="body2" sx={{ fontWeight: 700 }}>{clientLabel(lead)}</Typography>
                        {!lead.customerId && (canLinkClients ? (
                          <Button
                            size="small"
                            variant="text"
                            color="warning"
                            onClick={() => setResolveLead(lead)}
                            sx={{ p: 0, minWidth: 0, textTransform: 'none', fontWeight: 700, fontSize: '0.75rem' }}
                          >
                            Not linked to a client record — link it
                          </Button>
                        ) : (
                          <Typography variant="caption" color="text.disabled">Not linked to a client record</Typography>
                        ))}
                      </TableCell>
                      <TableCell align="right" sx={{ fontWeight: 800 }}>{lines.toLocaleString()}</TableCell>
                      <TableCell align="right">
                        <Button size="small" onClick={() => navigate(`/procurement/leads/view/${lead.id}`)} sx={{ textTransform: 'none', fontWeight: 800 }}>
                          Open
                        </Button>
                      </TableCell>
                    </TableRow>
                  );
                })}
                {visibleRows.length === 0 && (
                  <TableRow>
                    <TableCell colSpan={6} align="center" sx={{ py: 4, color: 'text.secondary' }}>
                      Nothing in this bucket.
                    </TableCell>
                  </TableRow>
                )}
              </TableBody>
            </Table>
          </TableContainer>

          {/* Disclosures. Everything excluded from the board above is named. */}
          <Divider sx={{ my: 2.5 }} />
          <Stack spacing={1}>
            {model.lateIngestedCount > 0 && (
              <Tooltip title="The document reached Nexora after its own deadline had already passed, so it is neither live work nor a missed response.">
                <Typography variant="caption" color="text.secondary">
                  {model.lateIngestedCount} open enquir{model.lateIngestedCount === 1 ? 'y' : 'ies'} carrying{' '}
                  {model.lateIngestedLines.toLocaleString()} line{model.lateIngestedLines === 1 ? '' : 's'} arrived after their own
                  deadline and are excluded from the buckets above.
                </Typography>
              </Tooltip>
            )}
            {model.unlinkedCount > 0 && (
              <Tooltip title="A lead with no client record cannot be qualified, converted to an RFQ, or quoted. These are blocked, not merely incomplete.">
                <Typography variant="caption" color="warning.main" sx={{ fontWeight: 700 }}>
                  {model.unlinkedCount} of {model.totalOpen} open enquir{model.unlinkedCount === 1 ? 'y' : 'ies'}, carrying{' '}
                  {model.unlinkedLines.toLocaleString()} line{model.unlinkedLines === 1 ? '' : 's'}, are not linked to a client
                  record and cannot be quoted until they are.
                </Typography>
              </Tooltip>
            )}
            {/*
              * The exclusion this sentence names is now the one the query actually applies. It
              * used to say "Rejected enquiries are not shown", which was false twice over: nothing
              * rejected could reach the board anyway, and the real exclusion — every enquiry
              * carrying ANY lifecycle status, i.e. everything anyone had started working — went
              * unnamed. The in-progress count is stated because it is the proof the board is no
              * longer just the inbox.
              */}
            <Typography variant="caption" color="text.secondary">
              Counted from {model.totalOpen} open of {model.totalLeadsReturned} enquir
              {model.totalLeadsReturned === 1 ? 'y' : 'ies'} loaded, {model.inProgressCount} of them
              already in progress. Excluded: enquiries disqualified, lost, cancelled, completed or
              merged as a duplicate.
            </Typography>
            {model.truncated && (
              <Alert severity="info" sx={{ mt: 1 }}>
                Showing the most recent {FETCH_LIMIT} of {model.totalCount.toLocaleString()} enquiries. Older ones are not counted on
                this board.
              </Alert>
            )}
          </Stack>
        </>
      )}

      <ResolveClientDialog
        open={resolveLead != null}
        leadId={resolveLead?.id ?? null}
        lead={resolveLead}
        prefill={resolveLead ? {
          name: resolveLead.customerCompanyNameExtracted,
          email: resolveLead.clientemail,
          contactName: resolveLead.buyersName,
          evidence: resolveLead.customerCompanyEvidence,
        } : null}
        onClose={() => setResolveLead(null)}
        onResolved={() => {
          // The board's own query, and the lead caches every other screen reads, so the
          // row stops saying "not linked" the moment it is.
          queryClient.invalidateQueries({ queryKey: ['deadline-board'] });
          queryClient.invalidateQueries({ queryKey: ['leads'] });
        }}
      />
    </Box>
  );
};

export default DeadlineBoardPage;
