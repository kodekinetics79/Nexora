import React, { useMemo, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, IconButton,
  Tooltip, Stack, TextField, MenuItem, CircularProgress,
  Alert,
  Link, Menu, ListItemIcon, ListItemText,
  ToggleButton, ToggleButtonGroup,
  Skeleton,
} from '@mui/material';
import {
  DataGrid, type GridColDef, type GridPaginationModel,
} from '@mui/x-data-grid';
import {
  Visibility as ViewIcon,
  Refresh as RefreshIcon,
  Email as EmailIcon,
  AutoAwesome as SparkleIcon,
  MoreVert as MoreIcon,
  FilterAltOff as ClearFiltersIcon,
  MarkEmailRead as InboxIcon,
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

const LeadsPage: React.FC = () => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const view = searchParams.get('view') || searchParams.get('state') || undefined;
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { hasPermission } = useAuth();
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 10, page: 0 });
  const [search, setSearch] = useState('');
  const [leadSource, setLeadSource] = useState('all');

  // Row overflow menu
  const [rowMenuAnchor, setRowMenuAnchor] = useState<HTMLElement | null>(null);
  const [rowMenuLeadId, setRowMenuLeadId] = useState<number | null>(null);

  // One resolve dialog for the whole grid (never one per row).
  const [resolveLead, setResolveLead] = useState<LeadResponseDTO | null>(null);
  const canEditLeads = hasPermission('Leads', 'edit');

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
  const filtersActive = search.trim().length > 0 || leadSource !== 'all' || Boolean(view);
  const clearFilters = () => {
    setSearch('');
    setLeadSource('all');
    setPaginationModel((current) => ({ ...current, page: 0 }));
    if (view) {
      // Drop only the view/state keys; anything else on the URL belongs to someone else.
      const next = new URLSearchParams(searchParams);
      next.delete('view');
      next.delete('state');
      setSearchParams(next, { replace: true });
    }
  };

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

  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['leads', paginationModel, search, leadSource, view],
    queryFn: () => leadService.getAll({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      rfqno: search || undefined,
      search: search || undefined,
      leadSource: leadSource === 'all' ? undefined : leadSource,
      view,
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
      <Button variant="contained" onClick={() => navigate('/procurement/leads/manual-upload')} sx={{ fontWeight: 700 }}>
        Upload a document
      </Button>
    ),
    filtered: Boolean(search) || leadSource !== 'all' || Boolean(view),
    filteredTitle: 'No inquiry matches this view',
    filteredMessage: 'Clear the search, the source filter and the tab to see every enquiry.',
    filteredAction: (
      <Button
        variant="outlined"
        onClick={() => { setSearch(''); setLeadSource('all'); navigate('/procurement/leads/all'); }}
        sx={{ fontWeight: 700 }}
      >
        Show all inquiries
      </Button>
    ),
  }), [search, leadSource, view, navigate]);

  // Decision Brief summaries: one batched call for the current page's ids,
  // fired only after the leads query resolves. This never blocks the grid —
  // it is a separate query, and on error (e.g. the engine isn't deployed yet)
  // the Decision / Estimated value cells simply render nothing.
  const visibleLeadIds = useMemo(() => (data?.items ?? []).map((l) => l.id), [data]);
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
            {hasPermission('Leads', 'edit') && <Tooltip title="Prepare RFQ">
              <IconButton
                size="small"
                aria-label="Prepare RFQ"
                sx={{ color: 'secondary.main' }}
                onClick={() => navigate(`/procurement/leads/${p.row.id}/convert`)}
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
        <Box sx={{ flexGrow: 1 }} />
        {!isLoading && !isError && (
          <Typography variant="body2" sx={{ color: 'text.secondary', whiteSpace: 'nowrap' }}>
            {totalCount} {totalCount === 1 ? 'lead' : 'leads'}
          </Typography>
        )}
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
      </Paper>

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
            rows={data?.items ?? []}
            columns={orderedColumns}
            rowCount={totalCount}
            loading={isLoading}
            slots={{ noRowsOverlay }}
            pageSizeOptions={[10, 25, 50]}
            paginationModel={paginationModel}
            paginationMode="server"
            onPaginationModelChange={setPaginationModel}
            disableRowSelectionOnClick
            getRowId={(r) => r.id}
            density={density}
            columnVisibilityModel={columnPreferences.columnVisibilityModel}
            onColumnVisibilityModelChange={columnPreferences.onColumnVisibilityModelChange}
            slots={{
              // "Filtered to zero" and "nothing has ever arrived" are completely different
              // situations with completely different next actions, and the grid's default
              // overlay — a bare "No rows" — makes them look identical. The first needs the
              // filters cleared; the second means no inquiry has reached the system at all,
              // and the place to find out why is Inbound Mail.
              noRowsOverlay: () => (
                <Box sx={{ height: '100%', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 1.5, p: 3, textAlign: 'center' }}>
                  {filtersActive ? (
                    <>
                      <ClearFiltersIcon sx={{ fontSize: 56, color: 'text.disabled' }} />
                      <Typography sx={{ fontWeight: 800 }}>No inquiries match these filters</Typography>
                      <Typography variant="body2" color="text.secondary">
                        Nothing matches the search and filters currently applied. Clearing them
                        shows every inquiry this business unit has.
                      </Typography>
                      <Button
                        variant="outlined"
                        startIcon={<ClearFiltersIcon />}
                        onClick={clearFilters}
                        sx={{ fontWeight: 700, borderRadius: 2 }}
                      >
                        Clear filters
                      </Button>
                    </>
                  ) : (
                    <>
                      <InboxIcon sx={{ fontSize: 56, color: 'text.disabled' }} />
                      <Typography sx={{ fontWeight: 800 }}>No inquiries yet</Typography>
                      <Typography variant="body2" color="text.secondary">
                        Nothing has become an inquiry yet. Inbound Mail shows every message that
                        reached the mailbox and what the system decided about each one.
                      </Typography>
                      <Button
                        variant="outlined"
                        startIcon={<InboxIcon />}
                        onClick={() => navigate('/procurement/leads/inbound-mail')}
                        sx={{ fontWeight: 700, borderRadius: 2 }}
                      >
                        Open Inbound Mail
                      </Button>
                    </>
                  )}
                </Box>
              ),
            }}
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
