import { useMemo, useState } from 'react';
import Stack from '../components/Flex';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  IconButton,
  InputAdornment,
  MenuItem,
  Paper,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import {
  DataGrid,
  type GridColDef,
  type GridRenderCellParams,
} from '@mui/x-data-grid';
import {
  Add as AddIcon,
  Login as ImpersonateIcon,
  PauseCircleOutlined as SuspendIcon,
  PlayCircleOutlined as ResumeIcon,
  Search as SearchIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import { platformApi } from '../api/client';
import { platformKeys } from '../api/queryKeys';
import type { PlanTier, Tenant, TenantStatus } from '../types';
import PageHeader from '../components/PageHeader';
import { PlanChip, TenantStatusChip } from '../components/StatusChip';
import { ErrorState } from '../components/States';
import { fmtRelative } from '../components/format';

type ActionKind = 'suspend' | 'resume' | 'impersonate';

const emptyForm = {
  name: '',
  slug: '',
  planTier: 'pro' as PlanTier,
};

export default function TenantsPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();

  const [search, setSearch] = useState('');
  const [planFilter, setPlanFilter] = useState<PlanTier | 'all'>('all');
  const [statusFilter, setStatusFilter] = useState<TenantStatus | 'all'>('all');

  const [provisionOpen, setProvisionOpen] = useState(false);
  const [form, setForm] = useState(emptyForm);
  const [confirm, setConfirm] = useState<{ kind: ActionKind; tenant: Tenant } | null>(null);
  const [actionReason, setActionReason] = useState('');

  const { data: tenants, isLoading, isError, refetch } = useQuery({
    queryKey: platformKeys.tenants(),
    queryFn: () => platformApi.listTenants(),
  });

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: platformKeys.tenants() });
    queryClient.invalidateQueries({ queryKey: platformKeys.overview() });
  };

  const provisionMutation = useMutation({
    mutationFn: () => platformApi.provisionTenant(form),
    onSuccess: (t) => {
      enqueueSnackbar(`${t.name} provisioned`, { variant: 'success' });
      setProvisionOpen(false);
      setForm(emptyForm);
      invalidate();
    },
    onError: () => enqueueSnackbar('Failed to provision tenant', { variant: 'error' }),
  });

  const actionMutation = useMutation({
    mutationFn: async ({ kind, tenant }: { kind: ActionKind; tenant: Tenant }) => {
      if (kind === 'suspend') return { kind, result: await platformApi.suspendTenant(tenant.id, actionReason.trim()) };
      if (kind === 'resume') return { kind, result: await platformApi.resumeTenant(tenant.id, actionReason.trim()) };
      return { kind, result: await platformApi.impersonateTenant(tenant.id, actionReason.trim()) };
    },
    onSuccess: (res, vars) => {
      if (res.kind === 'impersonate') {
        enqueueSnackbar(`Impersonation session issued for ${vars.tenant.name} (expires in 15m)`, { variant: 'info' });
      } else {
        enqueueSnackbar(`${vars.tenant.name} ${res.kind === 'suspend' ? 'suspended' : 'resumed'}`, { variant: 'success' });
        invalidate();
      }
      setConfirm(null);
      setActionReason('');
    },
    onError: () => enqueueSnackbar('Action failed', { variant: 'error' }),
  });

  const rows = useMemo(() => {
    let list = tenants ?? [];
    if (planFilter !== 'all') list = list.filter((t) => t.planTier === planFilter);
    if (statusFilter !== 'all') list = list.filter((t) => t.status === statusFilter);
    if (search.trim()) {
      const q = search.toLowerCase();
      list = list.filter((t) => t.name.toLowerCase().includes(q) || t.slug.toLowerCase().includes(q));
    }
    return list;
  }, [tenants, planFilter, statusFilter, search]);

  const slugValid = /^[a-z0-9-]{2,}$/.test(form.slug);
  const formValid = form.name.trim().length > 1 && slugValid;
  const openConfirm = (kind: ActionKind, tenant: Tenant) => {
    setActionReason('');
    setConfirm({ kind, tenant });
  };

  const columns: GridColDef<Tenant>[] = [
    {
      field: 'name',
      headerName: 'Tenant',
      flex: 1.4,
      minWidth: 200,
      renderCell: (p: GridRenderCellParams<Tenant>) => (
        <Box sx={{ lineHeight: 1.2 }}>
          <Typography variant="body2" sx={{ fontWeight: 700 }}>
            {p.row.name}
          </Typography>
          <Typography variant="caption" color="text.secondary">
            {p.row.slug}
          </Typography>
        </Box>
      ),
    },
    {
      field: 'planTier',
      headerName: 'Plan',
      width: 130,
      renderCell: (p) => <PlanChip tier={p.row.planTier} />,
    },
    {
      field: 'status',
      headerName: 'Status',
      width: 140,
      renderCell: (p) => <TenantStatusChip status={p.row.status} />,
    },
    {
      field: 'createdAt',
      headerName: 'Created',
      width: 120,
      valueGetter: (_v, row) => row.createdAt,
      renderCell: (p) => (
        <Typography variant="caption" color="text.secondary">
          {fmtRelative(p.row.createdAt)}
        </Typography>
      ),
    },
    {
      field: 'actions',
      headerName: 'Actions',
      width: 150,
      sortable: false,
      filterable: false,
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5} onClick={(e) => e.stopPropagation()}>
          {p.row.status === 'suspended' ? (
            <Tooltip title="Resume">
              <IconButton size="small" color="success" onClick={() => openConfirm('resume', p.row)}>
                <ResumeIcon fontSize="small" />
              </IconButton>
            </Tooltip>
          ) : (
            <Tooltip title="Suspend">
              <IconButton
                size="small"
                color="warning"
                disabled={p.row.status === 'provisioning'}
                onClick={() => openConfirm('suspend', p.row)}
              >
                <SuspendIcon fontSize="small" />
              </IconButton>
            </Tooltip>
          )}
          <Tooltip title="Impersonate">
            <IconButton
              size="small"
              color="primary"
              disabled={p.row.status !== 'active'}
              onClick={() => openConfirm('impersonate', p.row)}
            >
              <ImpersonateIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        </Stack>
      ),
    },
  ];

  return (
    <Box>
      <PageHeader
        title="Tenants"
        subtitle="Provision, manage, and inspect every workspace on the platform."
        actions={
          <Button variant="contained" startIcon={<AddIcon />} onClick={() => setProvisionOpen(true)} sx={{ fontWeight: 700 }}>
            Provision Tenant
          </Button>
        }
      />

      <Paper sx={{ p: 1.5, mb: 2, borderRadius: 3 }}>
        <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5}>
          <TextField
            size="small"
            placeholder="Search tenants…"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            sx={{ flex: 1 }}
            slotProps={{ input: { startAdornment: <InputAdornment position="start"><SearchIcon fontSize="small" /></InputAdornment> } }}
          />
          <TextField size="small" select label="Plan" value={planFilter} onChange={(e) => setPlanFilter(e.target.value as PlanTier | 'all')} sx={{ minWidth: 160 }}>
            <MenuItem value="all">All Plans</MenuItem>
            <MenuItem value="free">Free</MenuItem>
            <MenuItem value="pro">Pro</MenuItem>
            <MenuItem value="enterprise">Enterprise</MenuItem>
            <MenuItem value="unassigned">Unassigned</MenuItem>
          </TextField>
          <TextField size="small" select label="Status" value={statusFilter} onChange={(e) => setStatusFilter(e.target.value as TenantStatus | 'all')} sx={{ minWidth: 160 }}>
            <MenuItem value="all">All Statuses</MenuItem>
            <MenuItem value="active">Active</MenuItem>
            <MenuItem value="trial">Trial</MenuItem>
            <MenuItem value="suspended">Suspended</MenuItem>
            <MenuItem value="provisioning">Provisioning</MenuItem>
            <MenuItem value="archived">Archived</MenuItem>
          </TextField>
        </Stack>
      </Paper>

      <Paper sx={{ borderRadius: 3, overflow: 'hidden', height: 'calc(100vh - 300px)', minHeight: 420 }}>
        {isError ? (
          <ErrorState message="The tenant registry did not respond." onRetry={() => refetch()} />
        ) : (
          <DataGrid
            rows={rows}
            columns={columns}
            loading={isLoading}
            getRowId={(r) => r.id}
            onRowClick={(p) => navigate(`/platform/tenants/${p.id}`)}
            disableRowSelectionOnClick
            rowHeight={64}
            initialState={{ pagination: { paginationModel: { pageSize: 10, page: 0 } } }}
            pageSizeOptions={[10, 25, 50]}
            sx={{
              border: 'none',
              '& .MuiDataGrid-row': { cursor: 'pointer' },
              '& .MuiDataGrid-columnHeaders': { bgcolor: 'action.hover' },
              '& .MuiDataGrid-columnHeaderTitle': { fontWeight: 700 },
            }}
          />
        )}
      </Paper>

      {/* Provision dialog */}
      <Dialog open={provisionOpen} onClose={() => setProvisionOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>Provision New Tenant</DialogTitle>
        <DialogContent dividers>
          <Stack spacing={2.5} sx={{ mt: 0.5 }}>
            <TextField
              label="Organization name"
              value={form.name}
              onChange={(e) => setForm({ ...form, name: e.target.value, slug: form.slug || e.target.value.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '') })}
              fullWidth
              required
            />
            <TextField
              label="Slug"
              value={form.slug}
              onChange={(e) => setForm({ ...form, slug: e.target.value })}
              error={form.slug.length > 0 && !slugValid}
              helperText={form.slug.length > 0 && !slugValid ? 'Lowercase letters, numbers, and hyphens only.' : 'Used in tenant URLs and the tenant id.'}
              fullWidth
              required
            />
            <TextField label="Plan" select value={form.planTier} onChange={(e) => setForm({ ...form, planTier: e.target.value as PlanTier })} fullWidth>
              <MenuItem value="free">Free</MenuItem>
              <MenuItem value="pro">Pro</MenuItem>
              <MenuItem value="enterprise">Enterprise</MenuItem>
            </TextField>
          </Stack>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setProvisionOpen(false)} color="inherit">
            Cancel
          </Button>
          <Button variant="contained" onClick={() => provisionMutation.mutate()} disabled={!formValid || provisionMutation.isPending} sx={{ fontWeight: 700, px: 3 }}>
            {provisionMutation.isPending ? 'Provisioning…' : 'Provision'}
          </Button>
        </DialogActions>
      </Dialog>

      {/* Confirm action dialog */}
      <Dialog open={!!confirm} onClose={() => setConfirm(null)} maxWidth="xs" fullWidth>
        <DialogTitle sx={{ fontWeight: 800, textTransform: 'capitalize' }}>{confirm?.kind} tenant</DialogTitle>
        <DialogContent>
          <DialogContentText>
            {confirm?.kind === 'suspend' && (
              <>Mark <strong>{confirm.tenant.name}</strong> as suspended and record the reason in the platform audit trail?</>
            )}
            {confirm?.kind === 'resume' && (
              <>Return <strong>{confirm.tenant.name}</strong> to active status and record the action?</>
            )}
            {confirm?.kind === 'impersonate' && (
              <>Issue a 15-minute impersonation session for <strong>{confirm.tenant.name}</strong>? This is recorded in the platform audit log.</>
            )}
          </DialogContentText>
          <TextField
            autoFocus
            fullWidth
            required
            label="Audit reason"
            value={actionReason}
            onChange={(event) => setActionReason(event.target.value)}
            sx={{ mt: 2 }}
          />
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setConfirm(null)} color="inherit">
            Cancel
          </Button>
          <Button
            variant="contained"
            color={confirm?.kind === 'suspend' ? 'warning' : confirm?.kind === 'resume' ? 'success' : 'primary'}
            onClick={() => confirm && actionMutation.mutate(confirm)}
            disabled={actionMutation.isPending || actionReason.trim().length < 3}
            sx={{ fontWeight: 700, textTransform: 'capitalize' }}
          >
            {actionMutation.isPending ? 'Working…' : confirm?.kind}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
