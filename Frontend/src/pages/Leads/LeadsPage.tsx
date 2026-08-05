import React, { useMemo, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, IconButton,
  Tooltip, Stack, TextField, MenuItem, CircularProgress,
  Alert,
  Link, Menu, ListItemIcon, ListItemText, Popover, FormGroup,
  FormControlLabel, Checkbox, Divider, ToggleButton, ToggleButtonGroup,
  Skeleton,
} from '@mui/material';
import {
  DataGrid, type GridColDef, type GridPaginationModel, type GridColumnVisibilityModel,
} from '@mui/x-data-grid';
import {
  Visibility as ViewIcon,
  Refresh as RefreshIcon,
  Email as EmailIcon,
  AutoAwesome as SparkleIcon,
  MoreVert as MoreIcon,
  ViewColumn as ColumnsIcon,
} from '@mui/icons-material';
import leadService, { type LeadResponseDTO } from '../../api/services/leadService';
import decisionService, { type LeadDecisionSummary } from '../../api/services/decisionService';
import SearchField from '../../components/common/SearchField';
import { useSnackbar } from 'notistack';
import { formatDateSafe, parseDateSafe } from '../../utils/dates';
import { confidenceLevel } from '../Intelligence/common';
import { useAuth } from '../../context/AuthContext';
import { presentableErrorMessage } from '../../utils/apiErrors';

// ---------------------------------------------------------------------------
// Per-user view preferences (column visibility + density), persisted locally.
// Key shape: `<base>:user-<id>` when a user id can be read from the stored
// userData blob, otherwise `<base>:global`.
// ---------------------------------------------------------------------------

const COLUMNS_KEY_BASE = 'nexora.leadsPage.columnVisibility';
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

// Curated default column set. "actions" is always on and cannot be hidden.
const DEFAULT_COLUMN_VISIBILITY: GridColumnVisibilityModel = {
  nexoraSerial: true,
  rfqno: true,
  buyer: true,
  recDate: true,
  ingestedAtUtc: true,
  bidClosingDate: true,
  itemCount: true,
  leadSource: true,
  aiconfidence: true,
  status: true,
  decision: true,
  estimatedValue: false,
  actions: true,
};

const HIDEABLE_COLUMNS: ReadonlyArray<{ field: string; label: string }> = [
  { field: 'nexoraSerial', label: 'Nexora Serial' },
  { field: 'rfqno', label: 'RFQ #' },
  { field: 'buyer', label: 'Buyer' },
  { field: 'recDate', label: 'Received' },
  { field: 'ingestedAtUtc', label: 'Nexora Ingestion Date' },
  { field: 'bidClosingDate', label: 'Deadline' },
  { field: 'itemCount', label: 'Items' },
  { field: 'leadSource', label: 'Source' },
  { field: 'aiconfidence', label: 'Confidence' },
  { field: 'status', label: 'Status' },
  { field: 'decision', label: 'Decision' },
  { field: 'estimatedValue', label: 'Estimated value' },
];

const loadColumnVisibility = (): GridColumnVisibilityModel => {
  try {
    const raw = localStorage.getItem(userScopedKey(COLUMNS_KEY_BASE));
    if (raw) {
      const parsed: unknown = JSON.parse(raw);
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
        return { ...DEFAULT_COLUMN_VISIBILITY, ...(parsed as GridColumnVisibilityModel), actions: true };
      }
    }
  } catch {
    // Unreadable preference — use the curated defaults.
  }
  return { ...DEFAULT_COLUMN_VISIBILITY };
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

// High/Medium/Low levels reuse the app-wide thresholds from Intelligence/common.
const CONFIDENCE_META: Record<'high' | 'medium' | 'low', { label: string; color: 'success' | 'warning' | 'error' }> = {
  high: { label: 'High', color: 'success' },
  medium: { label: 'Medium', color: 'warning' },
  low: { label: 'Low', color: 'error' },
};

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

const ConfidenceCell: React.FC<{ score: number | null | undefined }> = ({ score }) => {
  const level = confidenceLevel(score);
  if (level === 'unknown') {
    return <Typography variant="body2" sx={{ color: 'text.disabled' }}>—</Typography>;
  }
  const meta = CONFIDENCE_META[level];
  return (
    <Chip
      label={meta.label}
      color={meta.color}
      size="small"
      variant="outlined"
      sx={{ fontWeight: 700, fontSize: '0.7rem' }}
      aria-label={`AI confidence ${meta.label.toLowerCase()}`}
    />
  );
};

const LeadsPage: React.FC = () => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
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

  // View preferences (persisted per user)
  const [columnVisibility, setColumnVisibility] = useState<GridColumnVisibilityModel>(loadColumnVisibility);
  const [density, setDensity] = useState<DensityChoice>(loadDensity);
  const [columnsAnchor, setColumnsAnchor] = useState<HTMLElement | null>(null);

  const applyColumnVisibility = (model: GridColumnVisibilityModel) => {
    const next: GridColumnVisibilityModel = { ...model, actions: true };
    setColumnVisibility(next);
    try {
      localStorage.setItem(userScopedKey(COLUMNS_KEY_BASE), JSON.stringify(next));
    } catch {
      // Storage unavailable — preference just won't persist.
    }
  };

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

  const syncEmailsMutation = useMutation({
    mutationFn: () => leadService.fetchEmails(),
    onSuccess: () => {
      enqueueSnackbar('Email synchronization started successfully!', { variant: 'success' });
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
      width: 180,
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
      field: 'buyer',
      headerName: 'Buyer',
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
      width: 150,
      valueGetter: (_value, row) => row.ingestedAtUtc || row.createdDate || '',
      renderCell: (p) => {
        const value = p.row.ingestedAtUtc || p.row.createdDate;
        const label = formatDateSafe(value);
        return (
          <Tooltip title={value ? new Date(value).toLocaleString() : 'Not recorded'}>
            <Typography variant="body2" sx={{ fontSize: '0.8rem', color: label === '—' ? 'text.disabled' : 'text.primary' }}>
              {label}
            </Typography>
          </Tooltip>
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
      field: 'aiconfidence',
      headerName: 'Confidence',
      width: 120,
      renderCell: (p) => <ConfidenceCell score={p.row.aiconfidence} />,
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
        <Button
          size="small"
          startIcon={<ColumnsIcon />}
          onClick={(e) => setColumnsAnchor(e.currentTarget)}
          sx={{ fontWeight: 600, color: 'text.secondary', whiteSpace: 'nowrap' }}
        >
          Columns
        </Button>
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
            columns={columns}
            rowCount={totalCount}
            loading={isLoading}
            pageSizeOptions={[10, 25, 50]}
            paginationModel={paginationModel}
            paginationMode="server"
            onPaginationModelChange={setPaginationModel}
            disableRowSelectionOnClick
            getRowId={(r) => r.id}
            density={density}
            columnVisibilityModel={columnVisibility}
            onColumnVisibilityModelChange={applyColumnVisibility}
          />
        )}
      </Paper>

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

      {/* Columns & density popover */}
      <Popover
        open={Boolean(columnsAnchor)}
        anchorEl={columnsAnchor}
        onClose={() => setColumnsAnchor(null)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
        transformOrigin={{ vertical: 'top', horizontal: 'right' }}
      >
        <Box sx={{ p: 2, width: 240 }}>
          <Typography variant="subtitle2" sx={{ fontWeight: 700, mb: 0.5 }}>
            Show columns
          </Typography>
          <FormGroup>
            {HIDEABLE_COLUMNS.map((c) => (
              <FormControlLabel
                key={c.field}
                control={
                  <Checkbox
                    size="small"
                    checked={columnVisibility[c.field] !== false}
                    onChange={(e) => applyColumnVisibility({ ...columnVisibility, [c.field]: e.target.checked })}
                  />
                }
                label={<Typography variant="body2">{c.label}</Typography>}
              />
            ))}
            <FormControlLabel
              control={<Checkbox size="small" checked disabled />}
              label={<Typography variant="body2" sx={{ color: 'text.disabled' }}>Actions (always shown)</Typography>}
            />
          </FormGroup>
          <Divider sx={{ my: 1.5 }} />
          <Typography variant="subtitle2" sx={{ fontWeight: 700, mb: 1 }}>
            Density
          </Typography>
          <ToggleButtonGroup
            size="small"
            exclusive
            fullWidth
            value={density}
            onChange={(_e, value: DensityChoice | null) => {
              if (value) applyDensity(value);
            }}
          >
            <ToggleButton value="comfortable">Comfortable</ToggleButton>
            <ToggleButton value="compact">Compact</ToggleButton>
          </ToggleButtonGroup>
        </Box>
      </Popover>

    </Box>
  );
};

export default LeadsPage;
