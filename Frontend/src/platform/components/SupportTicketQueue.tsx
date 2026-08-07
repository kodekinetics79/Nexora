import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Box,
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  InputAdornment,
  ListItemText,
  MenuItem,
  OutlinedInput,
  Paper,
  Select,
  TextField,
  Typography,
} from '@mui/material';
import { DataGrid, type GridColDef } from '@mui/x-data-grid';
import { Add as AddIcon, FilterAltOff as ClearIcon, Search as SearchIcon } from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import Stack from './Flex';
import RoleGate from './RoleGate';
import SupportTicketDialog, { SEVERITY_TONE, STATUS_TONE } from './SupportTicketDialog';
import { EmptyState, ErrorState } from './States';
import { SoftChip } from './StatusChip';
import { fmtRelative } from './format';
import { platformApi, type SupportTicketQuery } from '../api/client';
import { platformErrorMessage } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import { usePlatformPermissions } from '../auth/usePlatformPermissions';
import { REQUIRED_ROLE_COPY } from '../auth/permissions';
import { SUPPORT_TICKET_SEVERITIES, SUPPORT_TICKET_STATUSES } from '../types';
import type { SupportTicketSeverity, SupportTicketSummary } from '../types';

const PAGE_SIZE = 25;

interface Props {
  /** Locks the queue to one customer. Omitted on the fleet-wide desk. */
  tenantId?: string;
  tenantName?: string;
  height?: number | string;
}

export default function SupportTicketQueue({ tenantId, tenantName, height = 'calc(100vh - 340px)' }: Props) {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const permissions = usePlatformPermissions();

  const [statuses, setStatuses] = useState<string[]>([]);
  const [severities, setSeverities] = useState<string[]>([]);
  const [unassignedOnly, setUnassignedOnly] = useState(false);
  const [includeFinished, setIncludeFinished] = useState(false);
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(0);
  const [openTicketId, setOpenTicketId] = useState<string | null>(null);
  const [createOpen, setCreateOpen] = useState(false);
  const [draft, setDraft] = useState({
    tenantId: tenantId ?? '',
    subject: '',
    body: '',
    severity: 'Normal' as SupportTicketSeverity,
    requesterEmail: '',
  });

  const query: SupportTicketQuery = useMemo(
    () => ({
      tenantId,
      status: statuses.length > 0 ? statuses : undefined,
      severity: severities.length > 0 ? severities : undefined,
      unassigned: unassignedOnly ? true : undefined,
      includeFinished,
      search: search.trim() || undefined,
      page: page + 1,
      pageSize: PAGE_SIZE,
    }),
    [tenantId, statuses, severities, unassignedOnly, includeFinished, search, page],
  );

  const ticketsQuery = useQuery({
    queryKey: platformKeys.supportTickets(query),
    queryFn: () => platformApi.listSupportTickets(query),
  });

  const tenantsQuery = useQuery({
    queryKey: platformKeys.tenants(),
    queryFn: () => platformApi.listTenants(),
    enabled: createOpen && !tenantId,
  });

  const createMutation = useMutation({
    mutationFn: () =>
      platformApi.createSupportTicket({
        tenantId: draft.tenantId,
        subject: draft.subject.trim(),
        body: draft.body.trim(),
        severity: draft.severity,
        requesterEmail: draft.requesterEmail.trim() || null,
        assignToPlatformUserId: null,
      }),
    onSuccess: (ticket) => {
      enqueueSnackbar(`Ticket #${ticket.id} raised`, { variant: 'success' });
      setCreateOpen(false);
      setDraft({
        tenantId: tenantId ?? '',
        subject: '',
        body: '',
        severity: 'Normal',
        requesterEmail: '',
      });
      queryClient.invalidateQueries({ queryKey: [...platformKeys.all, 'support'] });
      if (tenantId) queryClient.invalidateQueries({ queryKey: platformKeys.tenantOperations(tenantId) });
      setOpenTicketId(ticket.id);
    },
    onError: (error) => enqueueSnackbar(platformErrorMessage(error, 'The ticket was not raised'), { variant: 'error' }),
  });

  const hasFilters =
    statuses.length > 0 || severities.length > 0 || unassignedOnly || includeFinished || search.trim() !== '';

  const columns: GridColDef<SupportTicketSummary>[] = [
    {
      field: 'severity',
      headerName: 'Sev',
      width: 100,
      renderCell: (p) => (
        <SoftChip label={p.row.severity} tone={SEVERITY_TONE[p.row.severity] ?? 'neutral'} dot={false} />
      ),
    },
    {
      field: 'subject',
      headerName: 'Subject',
      flex: 1.6,
      minWidth: 240,
      renderCell: (p) => (
        <Box sx={{ lineHeight: 1.2 }}>
          <Typography variant="body2" sx={{ fontWeight: 700 }}>
            #{p.row.id} {p.row.subject}
          </Typography>
          <Typography variant="caption" color="text.secondary">
            {p.row.noteCount} note{p.row.noteCount === 1 ? '' : 's'}
            {p.row.isRedacted ? ' · redacted' : ''}
          </Typography>
        </Box>
      ),
    },
    ...(tenantId
      ? []
      : ([
          {
            field: 'tenantName',
            headerName: 'Tenant',
            flex: 1,
            minWidth: 170,
            renderCell: (p) => (
              <Box sx={{ lineHeight: 1.2 }}>
                <Typography variant="body2">{p.row.tenantName ?? '—'}</Typography>
                {/* The tenant's lifecycle state rides on every row: "cannot log in" on a
                    Suspended customer is an invoice, not a bug. */}
                <Typography variant="caption" color="text.secondary">
                  {p.row.tenantStatus ?? '—'}
                </Typography>
              </Box>
            ),
          },
        ] as GridColDef<SupportTicketSummary>[])),
    {
      field: 'status',
      headerName: 'Status',
      width: 120,
      renderCell: (p) => <SoftChip label={p.row.status} tone={STATUS_TONE[p.row.status] ?? 'neutral'} />,
    },
    {
      field: 'assignedToEmail',
      headerName: 'Assignee',
      flex: 1,
      minWidth: 160,
      renderCell: (p) =>
        p.row.assignedToEmail ? (
          <Typography variant="body2">{p.row.assignedToEmail}</Typography>
        ) : (
          <Typography variant="caption" color="warning.main" sx={{ fontWeight: 700 }}>
            Unassigned
          </Typography>
        ),
    },
    {
      field: 'updatedAtUtc',
      headerName: 'Updated',
      width: 120,
      renderCell: (p) => (
        <Typography variant="caption" color="text.secondary">
          {fmtRelative(p.row.updatedAtUtc)}
        </Typography>
      ),
    },
  ];

  const createValid =
    draft.tenantId !== '' && draft.subject.trim().length > 0 && draft.body.trim().length > 0;

  return (
    <Box>
      <Paper sx={{ p: 1.5, mb: 2, borderRadius: 3 }}>
        <Stack direction={{ xs: 'column', lg: 'row' }} spacing={1.5} sx={{ flexWrap: 'wrap' }} alignItems={{ lg: 'center' }}>
          <TextField
            size="small"
            placeholder="Search subject, body and notes…"
            value={search}
            onChange={(event) => {
              setSearch(event.target.value);
              setPage(0);
            }}
            aria-label="Search support tickets"
            sx={{ flex: 1, minWidth: 200 }}
            slotProps={{
              input: {
                startAdornment: (
                  <InputAdornment position="start">
                    <SearchIcon fontSize="small" />
                  </InputAdornment>
                ),
              },
            }}
          />
          <Box sx={{ minWidth: 170 }}>
            <Typography variant="caption" color="text.secondary" id="support-status-filter-label">
              Status
            </Typography>
            <Select
              multiple
              size="small"
              fullWidth
              displayEmpty
              value={statuses}
              onChange={(event) => {
                setStatuses(event.target.value as string[]);
                setPage(0);
              }}
              input={<OutlinedInput />}
              renderValue={(selected) => (selected.length === 0 ? 'Any' : selected.join(', '))}
              inputProps={{ 'aria-labelledby': 'support-status-filter-label' }}
            >
              {SUPPORT_TICKET_STATUSES.map((status) => (
                <MenuItem key={status} value={status}>
                  <Checkbox checked={statuses.includes(status)} size="small" />
                  <ListItemText primary={status} />
                </MenuItem>
              ))}
            </Select>
          </Box>
          <Box sx={{ minWidth: 170 }}>
            <Typography variant="caption" color="text.secondary" id="support-severity-filter-label">
              Severity
            </Typography>
            <Select
              multiple
              size="small"
              fullWidth
              displayEmpty
              value={severities}
              onChange={(event) => {
                setSeverities(event.target.value as string[]);
                setPage(0);
              }}
              input={<OutlinedInput />}
              renderValue={(selected) => (selected.length === 0 ? 'Any' : selected.join(', '))}
              inputProps={{ 'aria-labelledby': 'support-severity-filter-label' }}
            >
              {SUPPORT_TICKET_SEVERITIES.map((severity) => (
                <MenuItem key={severity} value={severity}>
                  <Checkbox checked={severities.includes(severity)} size="small" />
                  <ListItemText primary={severity} />
                </MenuItem>
              ))}
            </Select>
          </Box>
          <FormControlLabel
            control={
              <Checkbox
                checked={unassignedOnly}
                onChange={(event) => {
                  setUnassignedOnly(event.target.checked);
                  setPage(0);
                }}
              />
            }
            label="Unassigned only"
          />
          <FormControlLabel
            control={
              <Checkbox
                checked={includeFinished}
                onChange={(event) => {
                  setIncludeFinished(event.target.checked);
                  setPage(0);
                }}
              />
            }
            label="Include finished"
          />
          {hasFilters && (
            <Button
              color="inherit"
              startIcon={<ClearIcon />}
              onClick={() => {
                setStatuses([]);
                setSeverities([]);
                setUnassignedOnly(false);
                setIncludeFinished(false);
                setSearch('');
                setPage(0);
              }}
              sx={{ fontWeight: 700 }}
            >
              Clear
            </Button>
          )}
          <RoleGate allowed={permissions.canAdministerTenants} requirement={REQUIRED_ROLE_COPY.tenantAdmin}>
            {(disabled) => (
              <Button
                variant="contained"
                startIcon={<AddIcon />}
                disabled={disabled}
                onClick={() => setCreateOpen(true)}
                sx={{ fontWeight: 700 }}
              >
                Raise ticket
              </Button>
            )}
          </RoleGate>
        </Stack>
      </Paper>

      <Paper sx={{ borderRadius: 3, overflow: 'hidden', height, minHeight: 380 }}>
        {ticketsQuery.isError ? (
          <ErrorState
            message={platformErrorMessage(ticketsQuery.error, 'The support desk did not respond.')}
            onRetry={() => ticketsQuery.refetch()}
          />
        ) : !ticketsQuery.isLoading && (ticketsQuery.data?.items.length ?? 0) === 0 ? (
          <EmptyState
            title="No tickets"
            message={hasFilters ? 'Nothing matches the current filters.' : 'Nothing outstanding on this queue.'}
          />
        ) : (
          <DataGrid
            rows={ticketsQuery.data?.items ?? []}
            columns={columns}
            loading={ticketsQuery.isLoading}
            getRowId={(row) => row.id}
            onRowClick={(params) => setOpenTicketId(String(params.id))}
            disableRowSelectionOnClick
            rowHeight={62}
            paginationMode="server"
            rowCount={ticketsQuery.data?.totalCount ?? 0}
            paginationModel={{ page, pageSize: PAGE_SIZE }}
            onPaginationModelChange={(model) => setPage(model.page)}
            pageSizeOptions={[PAGE_SIZE]}
            sx={{
              border: 'none',
              '& .MuiDataGrid-row': { cursor: 'pointer' },
              '& .MuiDataGrid-columnHeaders': { bgcolor: 'action.hover' },
              '& .MuiDataGrid-columnHeaderTitle': { fontWeight: 700 },
            }}
          />
        )}
      </Paper>

      <SupportTicketDialog ticketId={openTicketId} onClose={() => setOpenTicketId(null)} />

      <Dialog open={createOpen} onClose={() => setCreateOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>
          Raise a ticket{tenantName ? ` for ${tenantName}` : ''}
        </DialogTitle>
        <DialogContent dividers>
          <Stack spacing={2} sx={{ mt: 0.5 }}>
            {!tenantId && (
              <TextField
                select
                fullWidth
                required
                label="Tenant"
                value={draft.tenantId}
                onChange={(event) => setDraft({ ...draft, tenantId: event.target.value })}
              >
                <MenuItem value="">Select a tenant…</MenuItem>
                {(tenantsQuery.data ?? []).map((tenant) => (
                  <MenuItem key={tenant.id} value={tenant.id}>
                    {tenant.name} ({tenant.slug})
                  </MenuItem>
                ))}
              </TextField>
            )}
            <TextField
              fullWidth
              required
              label="Subject"
              value={draft.subject}
              onChange={(event) => setDraft({ ...draft, subject: event.target.value })}
            />
            <TextField
              fullWidth
              required
              multiline
              minRows={4}
              label="What is happening?"
              value={draft.body}
              onChange={(event) => setDraft({ ...draft, body: event.target.value })}
            />
            <TextField
              select
              fullWidth
              label="Severity"
              value={draft.severity}
              onChange={(event) => setDraft({ ...draft, severity: event.target.value as SupportTicketSeverity })}
              helperText="Critical means someone is working it now."
            >
              {SUPPORT_TICKET_SEVERITIES.map((severity) => (
                <MenuItem key={severity} value={severity}>
                  {severity}
                </MenuItem>
              ))}
            </TextField>
            <TextField
              fullWidth
              type="email"
              label="Requester email (optional)"
              value={draft.requesterEmail}
              onChange={(event) => setDraft({ ...draft, requesterEmail: event.target.value })}
              helperText="Who at the customer asked. Left blank when the desk noticed it first."
            />
            <Alert severity="info" sx={{ borderRadius: 2 }}>
              Raised as an operator ticket on the customer's behalf, and recorded as such — the difference
              between what we typed and what they typed matters once a customer-facing channel exists.
            </Alert>
          </Stack>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setCreateOpen(false)} color="inherit">
            Cancel
          </Button>
          <Button
            variant="contained"
            disabled={!createValid || createMutation.isPending}
            onClick={() => createMutation.mutate()}
            sx={{ fontWeight: 700 }}
          >
            {createMutation.isPending ? 'Raising…' : 'Raise ticket'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
