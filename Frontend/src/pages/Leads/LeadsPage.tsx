import React, { useCallback, useMemo, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, IconButton,
  Tooltip, Stack, TextField, MenuItem, CircularProgress,
  Alert,
  Link, Menu, ListItemIcon, ListItemText,
  ToggleButton, ToggleButtonGroup,
  Skeleton, Collapse,
} from '@mui/material';
import {
  DataGrid, type GridColDef, type GridPaginationModel, type GridRowId, type GridRowSelectionModel,
} from '@mui/x-data-grid';
import {
  Visibility as ViewIcon,
  Refresh as RefreshIcon,
  Email as EmailIcon,
  AutoAwesome as SparkleIcon,
  MoreVert as MoreIcon,
  FilterAltOff as ClearFiltersIcon,
  MarkEmailRead as InboxIcon,
  AssignmentInd as AssignIcon,
  Person as UserIcon,
  Tune as TuneIcon,
} from '@mui/icons-material';
import useColumnPreferences from '../../hooks/useColumnPreferences';
import ColumnPreferences from '../../components/common/ColumnPreferences';
import leadService, { type LeadResponseDTO } from '../../api/services/leadService';
import decisionService, { type LeadDecisionSummary } from '../../api/services/decisionService';
import LateIngestedBadge from './LateIngestedBadge';
import ClientCell from './ClientCell';
import ResolveClientDialog from './ResolveClientDialog';
import SearchField from '../../components/common/SearchField';
import gridEmptyOverlay from '../../components/common/gridOverlays';
import ViewTabs from '../../components/layout/ViewTabs';
import { useSnackbar } from 'notistack';
import { formatDateSafe, parseDateSafe } from '../../utils/dates';
import { useAuth } from '../../context/AuthContext';
import { presentableErrorMessage } from '../../utils/apiErrors';
import commercialRoutingService, {
  LEAD_OWNERSHIP_ACTION, type RoutingOwnerOption,
} from '../../api/services/commercialRoutingService';
import {
  OwnerPickerMenu, AssignReasonDialog, assignmentNeedsReason, useOwnerOptions,
} from './LeadOwnerPicker';
import { commercialActionPermissions } from '../../utils/commercialActionPermissions';

// ---------------------------------------------------------------------------
// Column visibility and order are AA-01 server-side per-user preferences now
// (useColumnPreferences + ColumnPreferences). The old localStorage column model
// was per-browser, order-less and invisible to the server, so a user who moved
// machines lost their layout and no tenant-defined field could ever appear in
// it. Defaults for this grid live in the server catalog under `leads.list`.
//
// Density stays local: it is a rendering comfort setting with no server
// contract, and there is nothing to compose it with.
// ---------------------------------------------------------------------------

const DENSITY_KEY_BASE = 'nexora.leadsPage.density';

const userScopedKey = (base: string): string => {
  try {
    const raw = localStorage.getItem('userData');
    if (raw) {
      const parsed: unknown = JSON.parse(raw);
      if (parsed && typeof parsed === 'object' && 'id' in parsed) {
        const id = (parsed as { id?: unknown }).id;
        if (typeof id === 'number' || typeof id === 'string') return `${base}:user-${id}`;
      }
    }
  } catch {
    // Corrupted userData — fall back to a global preference key.
  }
  return `${base}:global`;
};

type DensityChoice = 'comfortable' | 'compact';

const loadDensity = (): DensityChoice => {
  try {
    return localStorage.getItem(userScopedKey(DENSITY_KEY_BASE)) === 'compact' ? 'compact' : 'comfortable';
  } catch {
    return 'comfortable';
  }
};

// ---------------------------------------------------------------------------
// Presentation helpers
// ---------------------------------------------------------------------------

const INTERNAL_EMAIL_SUFFIX = '@pipeline.local';

/** Internal pipeline addresses and blanks must never be shown to users. */
const buyerContact = (row: LeadResponseDTO): { text: string; internal: boolean } => {
  const email = (row.clientemail ?? '').trim();
  if (!email || email.toLowerCase().endsWith(INTERNAL_EMAIL_SUFFIX)) {
    const source = (row.leadSource ?? '').toLowerCase();
    if (source === 'bulk') return { text: 'Bulk upload', internal: true };
    if (source === 'email') return { text: 'No contact email', internal: true };
    return { text: 'Manual upload', internal: true };
  }
  return { text: email, internal: false };
};

/** Deadline urgency applies only to real (non-sentinel) dates. */
const deadlineSx = (dateStr: string | null | undefined): { color: string; fontWeight: number } => {
  const d = parseDateSafe(dateStr);
  if (!d) return { color: 'text.disabled', fontWeight: 400 };
  const hoursLeft = (d.getTime() - Date.now()) / (1000 * 60 * 60);
  if (hoursLeft < 0) return { color: 'error.main', fontWeight: 700 };
  if (hoursLeft < 72) return { color: 'warning.main', fontWeight: 700 };
  return { color: 'text.primary', fontWeight: 500 };
};

interface StatusMeta {
  label: string;
  color: 'success' | 'error' | 'warning' | 'primary';
  variant: 'filled' | 'outlined';
}

const leadStatus = (row: LeadResponseDTO): StatusMeta => {
  if (row.isAccepted) return { label: 'Accepted', color: 'success', variant: 'filled' };
  if (row.isRejected) return { label: 'Rejected', color: 'error', variant: 'outlined' };
  if (row.headerRemarks?.startsWith('[NEEDS REVIEW]')) return { label: 'Needs review', color: 'warning', variant: 'filled' };
  return { label: 'New', color: 'primary', variant: 'outlined' };
};

// NOTE: this grid used to carry a "Confidence" column driven by
// Lead.Aiconfidence, rendered High/Medium/Low in green/amber/red. That score is
// not a measured accuracy — on the structured path it is a literal written per
// cell, on the model path it is the model's own self-report against a rubric in
// its own prompt — so the column is gone. The "Status" column already carries
// the fact a user can act on: whether a person has reviewed the document.

// Plain-language rendering of the Decision Brief recommendation — raw enum
// values ("bid"/"review"/"skip") are never shown to users.
interface DecisionMeta {
  label: string;
  color: 'success' | 'warning' | 'default';
}

const DECISION_META: Record<string, DecisionMeta | undefined> = {
  bid: { label: 'Worth bidding', color: 'success' },
  review: { label: 'Needs a look', color: 'warning' },
  skip: { label: 'Likely skip', color: 'default' },
};

/** Plain-language facts for the Decision chip tooltip. */
const decisionFacts = (s: LeadDecisionSummary): string[] => {
  const facts: string[] = [];
  if (s.estimatedValue != null) {
    facts.push(`Est. value: ${s.estimatedValue.toLocaleString('en-US', { maximumFractionDigits: 0 })}`);
  }
  if (s.coveragePct != null) {
    facts.push(`We stock ~${Math.round(s.coveragePct)}%`);
  }
  if (s.daysLeft != null) {
    if (s.daysLeft < 0) {
      const overdueDays = Math.abs(s.daysLeft);
      facts.push(`${overdueDays} ${overdueDays === 1 ? 'day' : 'days'} past deadline`);
    } else if (s.daysLeft === 0) {
      facts.push('Due today');
    } else {
      facts.push(`${s.daysLeft} ${s.daysLeft === 1 ? 'day' : 'days'} left`);
    }
  }
  return facts;
};

// ---------------------------------------------------------------------------
// Owner filter
// ---------------------------------------------------------------------------

/**
 * The three questions a rep actually asks this list: what has nobody picked up, what is on me,
 * and show me everything. It is ONE control, not three rail rows and not a second grid.
 *
 * It travels to the server on the same `view` parameter as the queue tabs, comma-joined, because
 * it NARROWS the queue rather than replacing it — "Revisions" plus "Unassigned" means both.
 * `mine` carries the reader's own id since `/api/Lead` forwards no identity to the repository;
 * see `LeadRepository.ParseLeadListView`.
 */
type OwnerView = 'unassigned' | 'mine' | 'all';

/**
 * The list opens on somebody's real working set, not on "every row ever".
 *
 * A manager's job on this screen is the pile nobody has picked up; a rep's is their own. Both are
 * one click from everything, the toggle shows which one is on, and an empty result says which
 * filter emptied it — so a narrowed default is never mistaken for an empty pipeline.
 *
 * Falls back to "Everyone" only when the session carries no identity to compute a working set
 * from: guessing at that point would be a filter the reader cannot see the reason for.
 */
export const defaultOwnerView = (
  myUserId: number | null | undefined,
  isManager: boolean,
): OwnerView => {
  if (isManager) return 'unassigned';
  return myUserId != null ? 'mine' : 'all';
};

export const composeLeadsView = (
  queueView: string | undefined,
  ownerView: OwnerView,
  myUserId: number | null | undefined,
): string | undefined => {
  const tokens: string[] = [];
  if (queueView) tokens.push(queueView);
  if (ownerView === 'unassigned') tokens.push('unassigned');
  else if (ownerView === 'mine' && myUserId != null) tokens.push(`mine:${myUserId}`);
  return tokens.length > 0 ? tokens.join(',') : undefined;
};

/** How a lead names itself in a failure report — never a bare database id. */
const leadLabel = (lead: LeadResponseDTO): string =>
  (lead.nexoraSerial || lead.commercialCaseReference || lead.rfqno || '').trim() || `Inquiry from ${lead.buyersName || 'an unnamed buyer'}`;

interface AssignFailure {
  leadId: number;
  label: string;
  message: string;
}

const EMPTY_SELECTION: GridRowSelectionModel = { type: 'include', ids: new Set<GridRowId>() };

const LeadsPage: React.FC = () => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const view = searchParams.get('view') || searchParams.get('state') || undefined;
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { hasPermission, userData } = useAuth();
  const myUserId = userData?.id ?? null;
  const isManager = userData?.isManager === true || Boolean(userData?.isSuperAdmin);
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 10, page: 0 });
  const [search, setSearch] = useState('');
  const [leadSource, setLeadSource] = useState('all');
  const [ownerView, setOwnerView] = useState<OwnerView>(() => defaultOwnerView(myUserId, isManager));
  // Column layout and row density are settings, not the day's work. They stay one click away
  // rather than sitting on the default path beside the filters a salesperson actually uses.
  const [displayOpen, setDisplayOpen] = useState(false);

  // Row overflow menu
  const [rowMenuAnchor, setRowMenuAnchor] = useState<HTMLElement | null>(null);
  const [rowMenuLeadId, setRowMenuLeadId] = useState<number | null>(null);

  // Assignment: one picker serves the row cell and the bulk toolbar, so both paths take the
  // same two clicks and print the same eligibility reasons.
  const [selection, setSelection] = useState<GridRowSelectionModel>(EMPTY_SELECTION);
  const [quickAssign, setQuickAssign] = useState<{ el: HTMLElement; leads: LeadResponseDTO[] } | null>(null);
  const [reasonPrompt, setReasonPrompt] = useState<{ owner: RoutingOwnerOption; leads: LeadResponseDTO[]; owned: LeadResponseDTO[] } | null>(null);
  // Per-lead failures survive the snackbar: a batch that half worked has to say WHICH half.
  const [assignFailures, setAssignFailures] = useState<AssignFailure[]>([]);

  // One resolve dialog for the whole grid (never one per row).
  const [resolveLead, setResolveLead] = useState<LeadResponseDTO | null>(null);
  const commercialAccess = commercialActionPermissions(hasPermission);
  const canEditLeads = commercialAccess.canEditLeadDecision;

  // AA-01: which columns, in which order, for THIS user — resolved server-side and
  // shared with every other grid that opts in.
  const columnPreferences = useColumnPreferences('leads.list');
  const [density, setDensity] = useState<DensityChoice>(loadDensity);

  const applyDensity = (value: DensityChoice) => {
    setDensity(value);
    try {
      localStorage.setItem(userScopedKey(DENSITY_KEY_BASE), value);
    } catch {
      // Storage unavailable — preference just won't persist.
    }
  };

  const closeRowMenu = () => {
    setRowMenuAnchor(null);
    setRowMenuLeadId(null);
  };

  // Everything that can narrow this list. `view` is included because it is a filter the reader
  // did not type: arriving from a dashboard tile can empty the grid with nothing on screen
  // explaining it, which is exactly the "no data" / "filtered to zero" confusion below.
  const filtersActive = search.trim().length > 0 || leadSource !== 'all' || Boolean(view) || ownerView !== 'all';
  // useCallback, not a bare closure: the no-rows overlay below is memoised because DataGrid
  // takes a component TYPE, and a fresh function identity each render would rebuild that type
  // and remount the overlay under the user.
  const clearFilters = useCallback(() => {
    setSearch('');
    setLeadSource('all');
    setOwnerView('all');
    setPaginationModel((current) => ({ ...current, page: 0 }));
    if (view) {
      // Drop only the view/state keys; anything else on the URL belongs to someone else.
      const next = new URLSearchParams(searchParams);
      next.delete('view');
      next.delete('state');
      setSearchParams(next, { replace: true });
    }
  }, [view, searchParams, setSearchParams]);

  const syncEmailsMutation = useMutation({
    mutationFn: () => leadService.fetchEmails(),
    onSuccess: (report) => {
      // A 200 is not uniformly a success. The server answers 200 with no `mailboxes`
      // count when the tenant has NO active IMAP mailbox — nothing was polled and
      // nothing ever will be — and puts the reason in `message`. Showing a green
      // "synchronized successfully" over that is the exact lie the backend removed
      // from its own side (ING-08, EmailController.ManualFetchAndSaveLeads), and it
      // sends a tenant back to this button forever instead of to mailbox settings.
      if (!report?.mailboxes) {
        enqueueSnackbar(
          report?.message ?? 'No mailbox was polled, so no new leads were fetched.',
          { variant: 'warning', autoHideDuration: 8000 },
        );
        return;
      }
      const found = report.newMessages ?? 0;
      enqueueSnackbar(
        found > 0
          ? `Checked ${report.mailboxes} mailbox(es) — ${found} new message(s) ingested.`
          : `Checked ${report.mailboxes} mailbox(es) — no new messages.`,
        { variant: 'success' },
      );
      queryClient.invalidateQueries({ queryKey: ['leads'] });
    },
    onError: (error: unknown) => enqueueSnackbar(
      presentableErrorMessage(error, 'Email synchronization could not be started. Nothing was changed — try again.'),
      { variant: 'error' },
    ),
  });

  const requestedView = composeLeadsView(view, ownerView, myUserId);

  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['leads', paginationModel, search, leadSource, requestedView],
    queryFn: () => leadService.getAll({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      rfqno: search || undefined,
      search: search || undefined,
      leadSource: leadSource === 'all' ? undefined : leadSource,
      view: requestedView,
    }),
  });

  /**
   * The top-of-funnel grid shipped MUI's bare "No rows" — the string a rep reads on day one when
   * the mailbox has not yet been configured, and the same string they read when a search matched
   * nothing. Neither reading tells them what to do, and one of the two is a setup problem they can
   * fix themselves. Memoised because DataGrid takes a component TYPE here.
   */
  const noRowsOverlay = useMemo(() => gridEmptyOverlay({
    title: 'No inquiries yet',
    message: 'Enquiries arrive on their own once a mailbox is connected — or you can read documents in from your machine right now.',
    action: (
      <Box sx={{ display: 'flex', gap: 1, flexWrap: 'wrap', justifyContent: 'center' }}>
        <Button variant="contained" onClick={() => navigate('/procurement/leads/manual-upload')} sx={{ fontWeight: 700 }}>
          Upload a document
        </Button>
        {/* An empty grid is also what a BROKEN intake looks like. Inbound Mail is the only screen
            that can tell the user which of the two they are looking at, so it must be reachable
            from here rather than only from the sidebar. */}
        <Button variant="outlined" startIcon={<InboxIcon />} onClick={() => navigate('/procurement/leads/inbound-mail')} sx={{ fontWeight: 700 }}>
          Open Inbound Mail
        </Button>
      </Box>
    ),
    filtered: filtersActive,
    // The list now OPENS on a working set, so "nothing here" most often means "nothing of
    // yours", not "nothing at all" — and the two must never read the same. Each says which
    // filter emptied it and offers the one button that widens it by a single step.
    filteredTitle: ownerView === 'mine'
      ? 'Nothing is assigned to you'
      : ownerView === 'unassigned'
        ? 'Every inquiry here already has an owner'
        : 'No inquiries match these filters',
    filteredMessage: ownerView === 'mine'
      ? 'No inquiry in this list carries your name right now. The ones nobody has picked up are one click away.'
      : ownerView === 'unassigned'
        ? 'Nothing in this list is waiting to be picked up. Everything already belongs to somebody.'
        : 'Nothing matches the search and filters currently applied. Clearing them shows every inquiry this business unit has.',
    filteredAction: (
      <Box sx={{ display: 'flex', gap: 1, flexWrap: 'wrap', justifyContent: 'center' }}>
        {ownerView === 'mine' && (
          <Button variant="contained" onClick={() => setOwnerView('unassigned')} sx={{ fontWeight: 700 }}>
            Show unassigned inquiries
          </Button>
        )}
        {ownerView === 'unassigned' && (
          <Button variant="contained" onClick={() => setOwnerView('all')} sx={{ fontWeight: 700 }}>
            Show everyone&apos;s inquiries
          </Button>
        )}
        <Button
          variant="outlined"
          startIcon={<ClearFiltersIcon />}
          onClick={clearFilters}
          sx={{ fontWeight: 700 }}
        >
          Clear filters
        </Button>
      </Box>
    ),
  }), [filtersActive, clearFilters, navigate, ownerView]);

  const rows = useMemo(() => data?.items ?? [], [data]);

  /**
   * Can the reader take a lead THEMSELVES?
   *
   * `PUT .../owner` answers 409 for a user governed routing will not accept, so an "Assign to me"
   * that is always offered is a false affordance that fails after the click. The eligible-owner
   * list is the server's own verdict, so it is read once for the page and the button appears only
   * when the answer is yes — with the reason printed above the grid when it is no.
   */
  const ownerOptions = useOwnerOptions(canEditLeads);
  const myOwnerOption = useMemo(
    () => (ownerOptions.data ?? []).find((option) => option.userId === myUserId) ?? null,
    [ownerOptions.data, myUserId],
  );
  const iCanTakeLeads = myOwnerOption?.isAvailable === true;
  /** Only stated once we actually know — never inferred from a list that has not loaded. */
  const whyICannotTakeLeads = canEditLeads && !ownerOptions.isLoading && !ownerOptions.isError && !iCanTakeLeads
    ? (myOwnerOption?.eligibilityReason?.trim()
      || 'You do not have a Sales Rep profile yet, so leads cannot be routed to you. Ask an administrator to add one under Sales > Rep directory.')
    : null;

  /**
   * The rows the checkboxes point at. Resolved against the CURRENT PAGE only: this grid pages
   * server-side, so MUI's "exclude" model means "everything not ticked" over rows the client has
   * never seen, and acting on that would be a promise the client cannot keep.
   */
  const selectedLeads = useMemo(() => (
    selection.type === 'exclude'
      ? rows.filter((row) => !selection.ids.has(row.id))
      : rows.filter((row) => selection.ids.has(row.id))
  ), [selection, rows]);

  /**
   * One assignment per lead through the endpoint that already exists —
   * `PUT /api/commercial-routing/leads/{id}/owner`. Deliberately NOT `queue/bulk-assign`: that
   * takes WorkItemIds, so it can only reach leads already sitting in the routing queue, which is
   * a strict subset of what a reader can tick here.
   *
   * Sequential, and every failure is caught and NAMED rather than aborting the run: a batch that
   * stops at the first 409 leaves the reader with no idea which inquiries moved.
   */
  const assignMutation = useMutation({
    mutationFn: async ({ owner, leads, reason }: { owner: RoutingOwnerOption; leads: LeadResponseDTO[]; reason?: string }) => {
      const failures: AssignFailure[] = [];
      let assigned = 0;
      for (const lead of leads) {
        const identity = `lead-owner-${lead.id}-${crypto.randomUUID()}`;
        try {
          await commercialRoutingService.changeLeadOwner(lead.id, {
            action: LEAD_OWNERSHIP_ACTION.Assign,
            assignedToUserId: owner.userId,
            expectedAssignmentVersion: lead.assignmentVersion ?? 1,
            idempotencyKey: identity,
            correlationId: identity,
            comment: reason ?? null,
          });
          assigned += 1;
        } catch (error: unknown) {
          failures.push({
            leadId: lead.id,
            label: leadLabel(lead),
            message: presentableErrorMessage(
              error,
              'The owner could not be changed. This inquiry still belongs to whoever held it before.',
            ),
          });
        }
      }
      return { assigned, failures, ownerName: owner.name, total: leads.length };
    },
    onSuccess: ({ assigned, failures, ownerName, total }) => {
      setQuickAssign(null);
      setReasonPrompt(null);
      setAssignFailures(failures);
      if (failures.length === 0) {
        enqueueSnackbar(
          total === 1
            ? `Assigned to ${ownerName}.`
            : `${assigned} ${assigned === 1 ? 'inquiry' : 'inquiries'} assigned to ${ownerName}.`,
          { variant: 'success' },
        );
        setSelection(EMPTY_SELECTION);
      } else {
        // The ones that failed stay ticked, so retrying is one click and not a re-selection.
        setSelection({ type: 'include', ids: new Set<GridRowId>(failures.map((f) => f.leadId)) });
        enqueueSnackbar(
          assigned === 0
            ? `Nothing was assigned to ${ownerName}. The reasons are listed above the grid.`
            : `${assigned} of ${total} assigned to ${ownerName}. ${failures.length} could not be — the reasons are listed above the grid.`,
          { variant: assigned === 0 ? 'error' : 'warning', autoHideDuration: 10000 },
        );
      }
      queryClient.invalidateQueries({ queryKey: ['leads'] });
    },
  });

  /**
   * A reason is asked for ONLY when at least one of the targets already belongs to somebody else.
   * Taking an unowned lead never prompts, which is the point of the whole screen: the cheapest
   * action here is picking up your own work.
   */
  const assignTo = useCallback((owner: RoutingOwnerOption, targets: LeadResponseDTO[]) => {
    const toAssign = targets.filter((lead) => lead.assignedToId !== owner.userId);
    if (toAssign.length === 0) {
      setQuickAssign(null);
      enqueueSnackbar(
        targets.length === 1
          ? `That inquiry already belongs to ${owner.name}.`
          : `Every selected inquiry already belongs to ${owner.name}.`,
        { variant: 'info' },
      );
      return;
    }
    const owned = toAssign.filter((lead) => assignmentNeedsReason(lead.assignedToId, owner.userId));
    if (owned.length > 0) {
      setQuickAssign(null);
      setReasonPrompt({ owner, leads: toAssign, owned });
      return;
    }
    assignMutation.mutate({ owner, leads: toAssign });
  }, [assignMutation, enqueueSnackbar]);

  /** Click 2 of 2 from the picker. */
  const pickOwner = useCallback((owner: RoutingOwnerOption) => {
    assignTo(owner, quickAssign?.leads ?? []);
  }, [assignTo, quickAssign]);

  /**
   * What the reader may actually act on out of what they ticked.
   *
   * A rep may take work nobody holds; only a manager moves a colleague's. Rather than letting the
   * server refuse half a batch with a 403 whose sentence the error layer generalises away, the
   * bar states up front which rows it will leave alone.
   */
  const takeableSelected = useMemo(
    () => (isManager ? selectedLeads : selectedLeads.filter((lead) => lead.assignedToId == null)),
    [isManager, selectedLeads],
  );
  const notMineToMove = selectedLeads.length - takeableSelected.length;

  /** Click 1 of 1 — taking your own work costs a single click and opens nothing. */
  const takeLeads = useCallback((leads: LeadResponseDTO[]) => {
    if (myOwnerOption) assignTo(myOwnerOption, leads);
  }, [assignTo, myOwnerOption]);

  // Decision Brief summaries: one batched call for the current page's ids,
  // fired only after the leads query resolves. This never blocks the grid —
  // it is a separate query, and on error (e.g. the engine isn't deployed yet)
  // the Decision / Estimated value cells simply render nothing.
  const visibleLeadIds = useMemo(() => rows.map((l) => l.id), [rows]);
  const decisionQuery = useQuery({
    queryKey: ['lead-decision-summaries', visibleLeadIds],
    queryFn: () => decisionService.getDecisionSummaries(visibleLeadIds),
    enabled: visibleLeadIds.length > 0,
    retry: false,
    staleTime: 60_000,
  });
  const decisionSummaries = decisionQuery.data?.summaries;
  const decisionsLoading = visibleLeadIds.length > 0 && decisionQuery.isPending;

  const columns: GridColDef<LeadResponseDTO>[] = [
    {
      field: 'nexoraSerial',
      headerName: 'Nexora Serial',
      // WIDE ENOUGH FOR THE WHOLE SERIAL, and that is a correctness requirement rather than
      // cosmetics. The serial is `NOOR-SONS-LLC-2026-000059` — 25 characters, and the only
      // part that differs between two leads of the same tenant and year is the tail. At 180px
      // the bold monospace clipped it to `NOOR-SONS-LLC-2026-000`, so every lead on the list
      // rendered an IDENTICAL identifier: two different inquiries, indistinguishable at a
      // glance, with no visual cue that anything had been cut.
      width: 260,
      valueGetter: (_value, row) => row.nexoraSerial || row.commercialCaseReference || '',
      renderCell: (p) => {
        const serial = p.row.nexoraSerial || p.row.commercialCaseReference;
        return serial ? (
          <Link component="button" type="button" underline="hover" onClick={() => navigate(`/leads/view/${p.row.id}`)}
            sx={{ fontWeight: 800, fontFamily: 'monospace', fontSize: '0.8rem' }}>
            {serial}
          </Link>
        ) : <Typography variant="body2" color="text.disabled">Unassigned</Typography>;
      },
    },
    {
      field: 'rfqno',
      headerName: 'RFQ #',
      width: 180,
      renderCell: (p) => {
        const raw = (p.row.rfqno ?? '').trim();
        const isMissing = !raw || raw.toUpperCase() === 'NO RFQ #';
        if (isMissing) {
          return (
            <Typography variant="body2" sx={{ color: 'text.disabled', fontStyle: 'italic' }}>
              No RFQ # yet
            </Typography>
          );
        }
        return (
          <Link
            component="button"
            type="button"
            underline="hover"
            onClick={() => navigate(`/leads/view/${p.row.id}`)}
            sx={{ fontWeight: 500, fontSize: '0.85rem', color: 'primary.main', textAlign: 'left' }}
          >
            {raw}
          </Link>
        );
      },
    },
    {
      field: 'client',
      headerName: 'Client',
      flex: 1,
      minWidth: 190,
      sortable: false,
      filterable: false,
      valueGetter: (_value, row) => row.customerName || '',
      renderCell: (p) => (
        <ClientCell
          lead={p.row}
          canEdit={canEditLeads}
          onResolve={() => setResolveLead(p.row)}
        />
      ),
    },
    {
      field: 'buyer',
      // Renamed from "Buyer": this column holds a PERSON and their email, and the
      // old heading read like a company — which is half the reason nobody could
      // tell which client a lead came from.
      headerName: 'Buyer contact',
      flex: 1,
      minWidth: 200,
      sortable: false,
      renderCell: (p) => {
        const name = (p.row.buyersName ?? '').trim();
        const unknownBuyer = !name || name.toLowerCase() === 'unknown buyer';
        const contact = buyerContact(p.row);
        return (
          <Box sx={{ lineHeight: 1.3, py: 0.25 }}>
            {unknownBuyer ? (
              <Typography sx={{ fontSize: '0.85rem', color: 'text.disabled' }}>
                Buyer not identified yet
              </Typography>
            ) : (
              <Typography sx={{ fontWeight: 600, fontSize: '0.85rem', color: 'text.primary' }}>
                {name}
              </Typography>
            )}
            <Typography
              variant="caption"
              sx={{
                color: contact.internal ? 'text.disabled' : 'text.secondary',
                fontSize: '0.7rem',
                display: 'flex',
                alignItems: 'center',
                gap: 0.5,
              }}
            >
              {!contact.internal && <EmailIcon sx={{ fontSize: 12 }} />}
              {contact.text}
            </Typography>
          </Box>
        );
      },
    },
    {
      field: 'recDate',
      headerName: 'Received',
      width: 110,
      renderCell: (p) => {
        const label = formatDateSafe(p.row.recDate);
        return (
          <Typography variant="body2" sx={{ fontSize: '0.8rem', color: label === '—' ? 'text.disabled' : 'text.primary' }}>
            {label}
          </Typography>
        );
      },
    },
    {
      field: 'ingestedAtUtc',
      headerName: 'Ingested',
      width: 170,
      // Audit-grade ingestion timestamp: earliest source received_on from the
      // backend (`ingestedOn`), with the legacy pipeline timestamp and
      // createdDate as display fallbacks for older payloads.
      valueGetter: (_value, row) => row.ingestedOn || row.ingestedAtUtc || row.createdDate || '',
      renderCell: (p) => {
        const value = p.row.ingestedOn || p.row.ingestedAtUtc || p.row.createdDate;
        const label = formatDateSafe(value);
        return (
          <Box sx={{ lineHeight: 1.3, py: 0.25 }}>
            <Tooltip title={value ? `Entered Nexora ${new Date(value).toLocaleString()}` : 'Not recorded'}>
              <Typography variant="body2" sx={{ fontSize: '0.8rem', color: label === '—' ? 'text.disabled' : 'text.primary' }}>
                {label === '—' ? label : `Ingested ${label}`}
              </Typography>
            </Tooltip>
            {/* Audit fairness: flag leads that entered Nexora after their deadline. */}
            <LateIngestedBadge
              lateIngested={p.row.lateIngested}
              ingestedOn={value}
              dueDate={p.row.bidClosingDate || p.row.subDate}
            />
          </Box>
        );
      },
    },
    {
      field: 'bidClosingDate',
      headerName: 'Deadline',
      width: 120,
      renderCell: (p) => {
        const sx = deadlineSx(p.row.bidClosingDate);
        return (
          <Typography variant="body2" sx={{ fontSize: '0.8rem', ...sx }}>
            {formatDateSafe(p.row.bidClosingDate)}
          </Typography>
        );
      },
    },
    {
      // FR-RFQ-04. The buyer's own required delivery date, beside the bid deadline and
      // never merged with it: one says when the bid is due back, the other says when the
      // goods are wanted. A missing value is an explicit "Not stated", not a blank cell
      // that reads like a loading state.
      field: 'requiredDeliveryDate',
      headerName: 'Required delivery',
      width: 150,
      renderCell: (p) => {
        const value = p.row.requiredDeliveryDate;
        return value ? (
          <Tooltip title="Delivery date requested by the buyer — not the bid deadline">
            <Typography variant="body2" sx={{ fontSize: '0.8rem' }}>
              {formatDateSafe(value)}
            </Typography>
          </Tooltip>
        ) : (
          <Typography variant="body2" sx={{ fontSize: '0.8rem', color: 'text.disabled', fontStyle: 'italic' }}>
            Not stated
          </Typography>
        );
      },
    },
    {
      // FR-RFQ-04. Hidden by default (see the server catalog); a Saudi tender publishes
      // its closing date in Hijri and this is the cross-check against the Gregorian one.
      field: 'bidClosingDateHijri',
      headerName: 'Deadline (Hijri)',
      width: 140,
      renderCell: (p) => (
        <Typography
          variant="body2"
          sx={{ fontSize: '0.8rem', fontFamily: 'monospace', color: p.row.bidClosingDateHijri ? 'text.primary' : 'text.disabled' }}
        >
          {p.row.bidClosingDateHijri || 'Not stated'}
        </Typography>
      ),
    },
    {
      // FR-RFQ-03. The standing agreement this inquiry is called off against — not the
      // inquiry's own reference, which is the RFQ # column.
      field: 'agreementReference',
      headerName: 'Agreement reference',
      width: 170,
      renderCell: (p) => (
        <Typography
          variant="body2"
          sx={{ fontSize: '0.8rem', color: p.row.agreementReference ? 'text.primary' : 'text.disabled' }}
        >
          {p.row.agreementReference || 'None'}
        </Typography>
      ),
    },
    {
      field: 'itemCount',
      headerName: 'Items',
      width: 80,
      type: 'number',
      align: 'right',
      headerAlign: 'right',
      renderCell: (p) => (
        <Typography variant="body2" sx={{ fontSize: '0.85rem', fontWeight: 500 }}>
          {p.row.itemCount ?? 0}
        </Typography>
      ),
    },
    {
      field: 'leadSource',
      headerName: 'Source',
      width: 110,
      renderCell: (p) => (
        <Chip
          label={p.row.leadSource || '—'}
          size="small"
          variant="outlined"
          sx={{ fontWeight: 600, fontSize: '0.7rem' }}
        />
      ),
    },
    {
      field: 'status',
      headerName: 'Status',
      width: 130,
      sortable: false,
      renderCell: (p) => {
        const meta = leadStatus(p.row);
        return (
          <Chip
            label={meta.label}
            color={meta.color}
            variant={meta.variant}
            size="small"
            sx={{ fontWeight: 600, fontSize: '0.7rem' }}
          />
        );
      },
    },
    {
      // WHO OWNS IT, and the control that changes it — one cell, because they are the same
      // question. Assigning a lead used to mean opening it, opening an Owner menu, opening a
      // dialog, picking a name and navigating back: four clicks and two page loads per lead,
      // from the screen a rep spends the day on. Lifted from OutstandingLeadsPage, which has
      // had the two-click version all along on a queue most reps never open.
      field: 'assignee',
      headerName: 'Owner',
      width: 210,
      sortable: false,
      filterable: false,
      valueGetter: (_value, row) => row.assignedToFullName || '',
      renderCell: (p) => {
        const owner = (p.row.assignedToFullName ?? '').trim();
        if (!owner) {
          if (!canEditLeads) {
            return (
              <Typography variant="body2" sx={{ fontSize: '0.8rem', color: 'text.disabled', fontStyle: 'italic' }}>
                Unassigned
              </Typography>
            );
          }
          return (
            <Box sx={{ lineHeight: 1.3, py: 0.25 }}>
              {/* One click, no menu, no dialog — the commonest action on the screen. Shown only
                  when governed routing would actually accept this reader; when it would not, the
                  sentence saying so is printed once above the grid rather than fifty times here. */}
              {iCanTakeLeads && (
                <Button
                  size="small"
                  variant="contained"
                  disableElevation
                  disabled={assignMutation.isPending}
                  onClick={() => takeLeads([p.row])}
                  sx={{ fontWeight: 800, fontSize: '0.7rem', py: 0.25, px: 1, borderRadius: 1.5, textTransform: 'none' }}
                >
                  Assign to me
                </Button>
              )}
              {/* Handing a lead to a COLLEAGUE is a manager's decision — the server answers 403
                  otherwise, and a 403 whose sentence the error layer replaces with a generic one
                  teaches nothing. So the control is simply not offered. */}
              {isManager && (
                <Link
                  component="button"
                  type="button"
                  underline="hover"
                  onClick={(event) => setQuickAssign({ el: event.currentTarget, leads: [p.row] })}
                  sx={{ display: 'block', fontSize: '0.7rem', fontWeight: 700, mt: iCanTakeLeads ? 0.25 : 0 }}
                >
                  {iCanTakeLeads ? 'Someone else…' : 'Assign to…'}
                </Link>
              )}
              {!iCanTakeLeads && !isManager && (
                <Typography variant="body2" sx={{ fontSize: '0.8rem', color: 'text.disabled', fontStyle: 'italic' }}>
                  Unassigned
                </Typography>
              )}
            </Box>
          );
        }
        return (
          <Box sx={{ lineHeight: 1.3, py: 0.25 }}>
            <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
              <UserIcon sx={{ fontSize: 14, color: 'primary.main' }} />
              <Typography sx={{ fontWeight: 700, fontSize: '0.8rem' }}>{owner}</Typography>
            </Stack>
            {/* Moving work that already belongs to somebody is a manager's call. */}
            {canEditLeads && isManager && (
              <Link
                component="button"
                type="button"
                underline="hover"
                onClick={(event) => setQuickAssign({ el: event.currentTarget, leads: [p.row] })}
                sx={{ fontSize: '0.7rem', fontWeight: 700 }}
              >
                Reassign
              </Link>
            )}
          </Box>
        );
      },
    },
    {
      field: 'decision',
      headerName: 'Decision',
      width: 140,
      sortable: false,
      filterable: false,
      renderCell: (p) => {
        if (decisionQuery.isError) return null;
        if (decisionsLoading) {
          return <Skeleton variant="rounded" width={96} height={22} sx={{ borderRadius: 3 }} />;
        }
        const summary = decisionSummaries?.[String(p.row.id)];
        if (!summary) return null;
        const meta = DECISION_META[summary.recommendation];
        if (!meta) return null;
        const facts = decisionFacts(summary);
        const chip = (
          <Chip
            label={meta.label}
            color={meta.color}
            size="small"
            sx={{ fontWeight: 600, fontSize: '0.7rem' }}
          />
        );
        if (facts.length === 0) return chip;
        return (
          <Tooltip
            title={
              <Box>
                {facts.map((fact) => (
                  <Typography key={fact} variant="caption" sx={{ display: 'block' }}>
                    {fact}
                  </Typography>
                ))}
              </Box>
            }
          >
            {chip}
          </Tooltip>
        );
      },
    },
    {
      field: 'estimatedValue',
      headerName: 'Estimated value',
      width: 130,
      align: 'right',
      headerAlign: 'right',
      sortable: false,
      filterable: false,
      renderCell: (p) => {
        if (decisionQuery.isError) return null;
        if (decisionsLoading) {
          return <Skeleton width={56} height={18} />;
        }
        const summary = decisionSummaries?.[String(p.row.id)];
        if (!summary) return null;
        if (summary.estimatedValue == null) {
          return <Typography variant="body2" sx={{ fontSize: '0.8rem', color: 'text.disabled' }}>—</Typography>;
        }
        return (
          <Typography variant="body2" sx={{ fontSize: '0.8rem', fontWeight: 500 }}>
            {summary.estimatedValue.toLocaleString('en-US', { maximumFractionDigits: 0 })}
          </Typography>
        );
      },
    },
    {
      field: 'actions',
      headerName: t('actions'),
      width: 130,
      sortable: false,
      filterable: false,
      hideable: false,
      renderCell: (p) => {
        const decided = p.row.isAccepted || p.row.isRejected;
        return (
          <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
            <Tooltip title="View">
              <IconButton
                size="small"
                aria-label="View"
                sx={{ color: 'primary.main' }}
                onClick={() => navigate(`/leads/view/${p.row.id}`)}
              >
                <ViewIcon fontSize="small" />
              </IconButton>
            </Tooltip>
            {commercialAccess.canOpenLeadWorkbench && <Tooltip title="Open decision workbench">
              <IconButton
                size="small"
                aria-label="Open decision workbench"
                sx={{ color: 'secondary.main' }}
                onClick={() => navigate(`/procurement/leads/${p.row.id}/workbench`)}
              >
                <SparkleIcon fontSize="small" />
              </IconButton>
            </Tooltip>}
            {!decided && (
              <Tooltip title="More actions">
                <IconButton
                  size="small"
                  aria-label="More actions"
                  onClick={(e) => {
                    setRowMenuAnchor(e.currentTarget);
                    setRowMenuLeadId(p.row.id);
                  }}
                >
                  <MoreIcon fontSize="small" />
                </IconButton>
              </Tooltip>
            )}
          </Stack>
        );
      },
    },
  ];

  // Reordered to this user's saved layout. Falls back to the declared order above when the
  // preference call has not resolved or failed.
  const orderedColumns = columnPreferences.arrangeColumns(columns);

  const totalCount = data?.totalCount ?? 0;

  return (
    <Box sx={{ p: { xs: 1, sm: 2, md: 3 }, bgcolor: 'background.default', minHeight: '100vh', minWidth: 0 }}>
      {/* Header Section */}
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5} sx={{ justifyContent: 'space-between', alignItems: { xs: 'stretch', sm: 'center' }, mb: 2 }}>
        <Typography variant="h5" sx={{ fontWeight: 700 }}>
          {t('leads')}
        </Typography>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', justifyContent: { xs: 'space-between', sm: 'flex-end' } }}>
          <Tooltip title="Fetches new emails from your connected inboxes now">
            <span>
              <Button
                variant="outlined"
                startIcon={syncEmailsMutation.isPending ? <CircularProgress size={18} color="inherit" /> : <EmailIcon />}
                onClick={() => syncEmailsMutation.mutate()}
                disabled={syncEmailsMutation.isPending}
                sx={{ fontWeight: 600, borderRadius: 2 }}
              >
                {syncEmailsMutation.isPending ? 'Checking…' : 'Check for new leads'}
              </Button>
            </span>
          </Tooltip>
          <Tooltip title="Refresh">
            <IconButton aria-label="Refresh" onClick={() => refetch()} sx={{ bgcolor: 'background.paper', boxShadow: 1 }}>
              <RefreshIcon />
            </IconButton>
          </Tooltip>
        </Stack>
      </Stack>

      {/* The lead queues, as one level of tabs on the screen they filter. "Unassigned", "Assigned"
          and "Revisions" were separate rail destinations over what a rep reads as one list. */}
      <ViewTabs primaryKey="leads" ariaLabel="Inquiry views" />

      {/* Filters + view controls */}
      <Paper sx={{ p: 1.5, mb: 1.5, display: 'flex', flexWrap: 'wrap', gap: 1.5, alignItems: 'center', borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <Box sx={{ width: { xs: '100%', sm: 360 }, maxWidth: '100%' }}>
          <SearchField width="100%" value={search} onChange={setSearch} placeholder="Search Nexora Serial, RFQ, buyer or email" />
        </Box>
        <TextField select size="small" value={leadSource} onChange={(e) => setLeadSource(e.target.value)} sx={{ width: { xs: '100%', sm: 'auto' }, minWidth: { sm: 160 } }} label="Lead Source">
          <MenuItem value="all">All Sources</MenuItem>
          <MenuItem value="Email">Email</MenuItem>
          <MenuItem value="Manual">Manual</MenuItem>
          <MenuItem value="Bulk">Bulk Upload</MenuItem>
        </TextField>
        {/* "What has nobody picked up" and "what is on me" were, until now, two other screens.
            They are questions about THIS list, so they are a filter on it. */}
        <ToggleButtonGroup
          size="small"
          exclusive
          value={ownerView}
          onChange={(_e, value: OwnerView | null) => {
            if (!value) return;
            setOwnerView(value);
            setPaginationModel((current) => ({ ...current, page: 0 }));
          }}
          aria-label="Owner"
        >
          <ToggleButton value="unassigned" aria-label="Unassigned">Unassigned</ToggleButton>
          <ToggleButton value="mine" aria-label="Mine" disabled={myUserId == null}>Mine</ToggleButton>
          <ToggleButton value="all" aria-label="Everyone">Everyone</ToggleButton>
        </ToggleButtonGroup>
        {/* A disabled control that will not say why becomes a support call. */}
        {myUserId == null && (
          <Typography variant="caption" sx={{ color: 'text.secondary', maxWidth: 220 }}>
            “Mine” needs to know who you are signed in as, and this session does not carry it. Sign
            out and back in to use it.
          </Typography>
        )}
        <Box sx={{ flexGrow: 1 }} />
        {!isLoading && !isError && (
          <Typography variant="body2" sx={{ color: 'text.secondary', whiteSpace: 'nowrap' }}>
            {totalCount} {totalCount === 1 ? 'lead' : 'leads'}
          </Typography>
        )}
        {/* Progressive disclosure: the layout controls are still here, one click away, rather
            than sitting on the default path competing with the day's work. */}
        <Button
          size="small"
          variant="text"
          startIcon={<TuneIcon />}
          onClick={() => setDisplayOpen((open) => !open)}
          aria-expanded={displayOpen}
          sx={{ fontWeight: 700, textTransform: 'none' }}
        >
          Display
        </Button>
        <Collapse in={displayOpen} sx={{ width: '100%' }}>
          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, alignItems: 'center', pt: 1.5, borderTop: '1px solid', borderColor: 'divider' }}>
            <ColumnPreferences preferences={columnPreferences} />
            <ToggleButtonGroup
              size="small"
              exclusive
              value={density}
              onChange={(_e, value: DensityChoice | null) => {
                if (value) applyDensity(value);
              }}
              aria-label="Row density"
            >
              <ToggleButton value="comfortable" aria-label="Comfortable rows">Comfortable</ToggleButton>
              <ToggleButton value="compact" aria-label="Compact rows">Compact</ToggleButton>
            </ToggleButtonGroup>
          </Box>
        </Collapse>
      </Paper>

      {/* Constraint 7: a control that cannot work is not silently missing. Said ONCE, in words,
          instead of a disabled button repeated down every row. */}
      {whyICannotTakeLeads && (
        <Alert severity="info" sx={{ mb: 1.5, borderRadius: 2 }}>
          <Typography variant="body2" sx={{ fontWeight: 700 }}>
            {isManager
              ? 'You can assign inquiries to other people, but not to yourself yet.'
              : 'You cannot pick up inquiries yet.'}
          </Typography>
          <Typography variant="body2">{whyICannotTakeLeads}</Typography>
        </Alert>
      )}

      {/* Bulk assign. Appears only when something is ticked, so it never occupies the screen for
          the reader who is not using it. */}
      {canEditLeads && selectedLeads.length > 0 && (
        <Paper
          sx={{ p: 1.25, mb: 1.5, display: 'flex', flexWrap: 'wrap', gap: 1.5, alignItems: 'center', borderRadius: 2, border: '1px solid', borderColor: 'primary.main', boxShadow: 'none' }}
        >
          <Typography variant="body2" sx={{ fontWeight: 800 }}>
            {selectedLeads.length} selected on this page
          </Typography>
          {iCanTakeLeads && (
            <Button
              variant="contained"
              size="small"
              disableElevation
              disabled={assignMutation.isPending || takeableSelected.length === 0}
              onClick={() => takeLeads(takeableSelected)}
              sx={{ fontWeight: 800, borderRadius: 2, textTransform: 'none' }}
            >
              Assign selected to me
            </Button>
          )}
          {isManager && (
            <Button
              variant={iCanTakeLeads ? 'outlined' : 'contained'}
              size="small"
              startIcon={assignMutation.isPending ? <CircularProgress size={16} color="inherit" /> : <AssignIcon />}
              disabled={assignMutation.isPending}
              onClick={(event) => setQuickAssign({ el: event.currentTarget, leads: selectedLeads })}
              sx={{ fontWeight: 800, borderRadius: 2, textTransform: 'none' }}
            >
              {assignMutation.isPending ? 'Assigning…' : 'Assign selected to…'}
            </Button>
          )}
          {notMineToMove > 0 && (
            <Typography variant="caption" sx={{ color: 'text.secondary', maxWidth: 320 }}>
              {notMineToMove} of these already belong to someone else. Only a manager can move
              those, so they will be left alone.
            </Typography>
          )}
          <Button size="small" color="inherit" onClick={() => setSelection(EMPTY_SELECTION)} sx={{ fontWeight: 700 }}>
            Clear selection
          </Button>
        </Paper>
      )}

      {/* What did NOT work. A batch that half succeeded must name the half that did not, and say
          why for each one — a green "assigned" over five failures is the lie this panel exists
          to prevent. */}
      {assignFailures.length > 0 && (
        <Alert
          severity="warning"
          onClose={() => setAssignFailures([])}
          sx={{ mb: 1.5, borderRadius: 2 }}
        >
          <Typography variant="body2" sx={{ fontWeight: 800, mb: 0.5 }}>
            {assignFailures.length} {assignFailures.length === 1 ? 'inquiry' : 'inquiries'} could not be assigned
          </Typography>
          <Box component="ul" sx={{ m: 0, pl: 2.5 }}>
            {assignFailures.map((failure) => (
              <Typography component="li" variant="body2" key={failure.leadId}>
                <strong>{failure.label}</strong> — {failure.message}
              </Typography>
            ))}
          </Box>
          <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>
            They are still ticked, so you can try them again without re-selecting.
          </Typography>
        </Alert>
      )}

      {/* Grid */}
      <Paper sx={{ height: { xs: 'calc(100vh - 330px)', sm: 'calc(100vh - 240px)' }, minHeight: 420, width: '100%', minWidth: 0, borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        {isError ? (
          <Box sx={{ height: '100%', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 2, p: 3, textAlign: 'center' }}>
            <Alert severity="error" sx={{ borderRadius: 2, maxWidth: 480 }}>
              We couldn't load leads. The service may be temporarily unavailable.
            </Alert>
            <Button variant="contained" startIcon={<RefreshIcon />} onClick={() => refetch()} sx={{ fontWeight: 700, borderRadius: 2 }}>
              Retry
            </Button>
          </Box>
        ) : (
          <DataGrid
            rows={rows}
            columns={orderedColumns}
            rowCount={totalCount}
            loading={isLoading}
            slots={{ noRowsOverlay }}
            pageSizeOptions={[10, 25, 50]}
            paginationModel={paginationModel}
            paginationMode="server"
            onPaginationModelChange={setPaginationModel}
            // Checkboxes only for a reader who can actually change an owner — a tick box that
            // leads to nothing is a false affordance. `disableRowSelectionOnClick` stays: the
            // checkbox selects, clicking a row still just reads it.
            checkboxSelection={canEditLeads}
            rowSelectionModel={selection}
            onRowSelectionModelChange={setSelection}
            disableRowSelectionOnClick
            getRowId={(r) => r.id}
            density={density}
            columnVisibilityModel={columnPreferences.columnVisibilityModel}
            onColumnVisibilityModelChange={columnPreferences.onColumnVisibilityModelChange}
          />
        )}
      </Paper>

      {/* Client resolution — one dialog for the grid, driven by the client cell */}
      <ResolveClientDialog
        open={resolveLead !== null}
        leadId={resolveLead?.id ?? null}
        lead={resolveLead}
        onClose={() => setResolveLead(null)}
        onResolved={() => queryClient.invalidateQueries({ queryKey: ['leads'] })}
      />

      {/* Click 2 of 2 — one picker for a single row and for the whole ticked set */}
      <OwnerPickerMenu
        anchorEl={quickAssign?.el ?? null}
        open={Boolean(quickAssign)}
        onClose={() => setQuickAssign(null)}
        onPick={pickOwner}
        busy={assignMutation.isPending}
        heading={
          (quickAssign?.leads.length ?? 0) > 1
            ? `Assign ${quickAssign?.leads.length} inquiries to`
            : 'Pick a person'
        }
        currentOwnerId={quickAssign?.leads.length === 1 ? quickAssign.leads[0].assignedToId : null}
      />

      {/* Asked for only when a lead is being taken off somebody else. */}
      <AssignReasonDialog
        open={Boolean(reasonPrompt)}
        ownerName={reasonPrompt?.owner.name ?? ''}
        currentOwnerName={reasonPrompt?.owned.length === 1 ? reasonPrompt.owned[0].assignedToFullName : null}
        leadCount={reasonPrompt?.owned.length ?? 0}
        busy={assignMutation.isPending}
        onCancel={() => setReasonPrompt(null)}
        onConfirm={(reason) => {
          if (reasonPrompt) assignMutation.mutate({ owner: reasonPrompt.owner, leads: reasonPrompt.leads, reason });
        }}
      />

      {/* Row overflow menu */}
      <Menu anchorEl={rowMenuAnchor} open={Boolean(rowMenuAnchor)} onClose={closeRowMenu}>
        <MenuItem
          onClick={() => {
            if (rowMenuLeadId != null) navigate(`/leads/view/${rowMenuLeadId}`);
            closeRowMenu();
          }}
        >
          <ListItemIcon>
            <ViewIcon fontSize="small" color="primary" />
          </ListItemIcon>
          <ListItemText>Open lifecycle</ListItemText>
        </MenuItem>
      </Menu>


    </Box>
  );
};

export default LeadsPage;
