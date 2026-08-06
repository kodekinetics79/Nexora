import { useMemo, useState } from 'react';
import Stack from '../components/Flex';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  Divider,
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
  Inventory2Outlined as ArchiveIcon,
  Login as ImpersonateIcon,
  PauseCircleOutlined as SuspendIcon,
  PlayCircleOutlined as ResumeIcon,
  RestorePageOutlined as RestoreIcon,
  Search as SearchIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import { platformApi } from '../api/client';
import { platformErrorMessage } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import { setImpersonation } from '../../api/impersonation';
import type { ProvisionTenantResult, Tenant, TenantStatus } from '../types';
import PageHeader from '../components/PageHeader';
import { PlanChip, TenantStatusChip } from '../components/StatusChip';
import { ErrorState } from '../components/States';
import { fmtRelative } from '../components/format';

type ActionKind = 'suspend' | 'resume' | 'archive' | 'restore' | 'impersonate';

const ACTION_COPY: Record<ActionKind, { title: string; verb: string }> = {
  suspend: { title: 'Suspend tenant', verb: 'Suspend' },
  resume: { title: 'Resume tenant', verb: 'Resume' },
  archive: { title: 'Archive tenant', verb: 'Archive' },
  restore: { title: 'Restore tenant', verb: 'Restore' },
  impersonate: { title: 'Impersonate tenant', verb: 'Impersonate' },
};

const emptyForm = {
  name: '',
  slug: '',
  planId: '',
  adminEmail: '',
  adminFirstName: '',
  adminLastName: '',
  adminPassword: '',
};

export default function TenantsPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();

  const [search, setSearch] = useState('');
  const [planFilter, setPlanFilter] = useState<string>('all');
  const [statusFilter, setStatusFilter] = useState<TenantStatus | 'all'>('all');

  const [provisionOpen, setProvisionOpen] = useState(false);
  const [form, setForm] = useState(emptyForm);
  const [confirm, setConfirm] = useState<{ kind: ActionKind; tenant: Tenant } | null>(null);
  // Shown after a successful provision. The generated credential lives ONLY in this response,
  // so the operator gets one deliberate handover step rather than a toast that scrolls away.
  const [handover, setHandover] = useState<ProvisionTenantResult | null>(null);
  const [credentialCopied, setCredentialCopied] = useState(false);
  const [actionReason, setActionReason] = useState('');

  const { data: tenants, isLoading, isError, refetch } = useQuery({
    queryKey: platformKeys.tenants(),
    queryFn: () => platformApi.listTenants(),
  });

  const { data: plans } = useQuery({
    queryKey: platformKeys.plans(),
    queryFn: () => platformApi.listPlans(),
  });

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: platformKeys.tenants() });
    queryClient.invalidateQueries({ queryKey: platformKeys.overview() });
  };

  const provisionMutation = useMutation({
    mutationFn: () => platformApi.provisionTenant({ ...form, planId: form.planId || null }),
    onSuccess: (result) => {
      enqueueSnackbar(`${result.tenant.name} provisioned`, { variant: 'success' });
      setProvisionOpen(false);
      setForm(emptyForm);
      setCredentialCopied(false);
      setHandover(result);
      invalidate();
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Failed to provision tenant'), { variant: 'error' }),
  });

  const actionMutation = useMutation({
    mutationFn: async ({ kind, tenant }: { kind: ActionKind; tenant: Tenant }) => {
      const reason = actionReason.trim();
      if (kind === 'suspend') return { kind, ticket: null, result: await platformApi.suspendTenant(tenant.id, reason) };
      if (kind === 'resume') return { kind, ticket: null, result: await platformApi.resumeTenant(tenant.id, reason) };
      if (kind === 'archive') return { kind, ticket: null, result: await platformApi.archiveTenant(tenant.id, reason) };
      if (kind === 'restore') return { kind, ticket: null, result: await platformApi.restoreTenant(tenant.id, reason) };
      return { kind, ticket: await platformApi.impersonateTenant(tenant.id, reason), result: null };
    },
    onSuccess: (res, vars) => {
      if (res.kind === 'impersonate' && res.ticket) {
        // Store the read-only ticket in its dedicated sessionStorage record —
        // never in the tenant localStorage 'token' — then enter the tenant app
        // with a full navigation so its auth context boots from the record.
        setImpersonation({
          token: res.ticket.token,
          jti: res.ticket.jti,
          tenantId: res.ticket.tenantId,
          tenantName: vars.tenant.name,
          expiresAt: res.ticket.expiresAt,
          reason: actionReason.trim(),
        });
        window.location.assign('/dashboard');
        return;
      }
      enqueueSnackbar(`${vars.tenant.name} ${res.kind}d`, { variant: 'success' });
      invalidate();
      setConfirm(null);
      setActionReason('');
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Action failed'), { variant: 'error' }),
  });

  const planCodes = useMemo(() => {
    const codes = new Set<string>();
    (tenants ?? []).forEach((t) => codes.add(t.planCode ?? 'none'));
    return [...codes].sort();
  }, [tenants]);

  const rows = useMemo(() => {
    let list = tenants ?? [];
    if (planFilter !== 'all') list = list.filter((t) => (t.planCode ?? 'none') === planFilter);
    if (statusFilter !== 'all') list = list.filter((t) => t.status === statusFilter);
    if (search.trim()) {
      const q = search.toLowerCase();
      list = list.filter((t) => t.name.toLowerCase().includes(q) || t.slug.toLowerCase().includes(q));
    }
    return list;
  }, [tenants, planFilter, statusFilter, search]);

  const slugValid = /^[a-z0-9-]{2,}$/.test(form.slug);
  const adminEmailValid = /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(form.adminEmail.trim());
  // A supplied password is optional, but a too-short one must not reach the server only to
  // come back as a validation error after the tenant name is already typed.
  const adminPasswordValid = form.adminPassword.length === 0 || form.adminPassword.length >= 8;
  const formValid =
    form.name.trim().length > 1 &&
    slugValid &&
    adminEmailValid &&
    form.adminFirstName.trim().length > 0 &&
    form.adminLastName.trim().length > 0 &&
    adminPasswordValid;
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
      field: 'planCode',
      headerName: 'Plan',
      width: 130,
      renderCell: (p) => <PlanChip tier={p.row.planCode ?? 'none'} />,
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
      width: 190,
      sortable: false,
      filterable: false,
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5} onClick={(e) => e.stopPropagation()}>
          {p.row.status === 'suspended' || p.row.status === 'archived' ? (
            <Tooltip title={p.row.status === 'archived' ? 'Restore to suspended' : 'Resume'}>
              <IconButton
                size="small"
                color="success"
                onClick={() => openConfirm(p.row.status === 'archived' ? 'restore' : 'resume', p.row)}
              >
                {p.row.status === 'archived' ? <RestoreIcon fontSize="small" /> : <ResumeIcon fontSize="small" />}
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
          <Tooltip title="Archive (suspended tenants only)">
            <span>
              <IconButton
                size="small"
                color="error"
                disabled={p.row.status !== 'suspended'}
                onClick={() => openConfirm('archive', p.row)}
              >
                <ArchiveIcon fontSize="small" />
              </IconButton>
            </span>
          </Tooltip>
          <Tooltip title="Impersonate">
            <span>
              <IconButton
                size="small"
                color="primary"
                disabled={p.row.status !== 'active'}
                onClick={() => openConfirm('impersonate', p.row)}
              >
                <ImpersonateIcon fontSize="small" />
              </IconButton>
            </span>
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
          <TextField size="small" select label="Plan" value={planFilter} onChange={(e) => setPlanFilter(e.target.value)} sx={{ minWidth: 160 }}>
            <MenuItem value="all">All Plans</MenuItem>
            {planCodes.map((code) => (
              <MenuItem key={code} value={code} sx={{ textTransform: 'capitalize' }}>
                {code}
              </MenuItem>
            ))}
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
            <TextField
              label="Plan"
              select
              value={form.planId}
              onChange={(e) => setForm({ ...form, planId: e.target.value })}
              helperText="Optional — a tenant without a plan runs without plan limits."
              fullWidth
            >
              <MenuItem value="">No plan</MenuItem>
              {(plans ?? []).map((plan) => (
                <MenuItem key={plan.id} value={plan.id}>
                  {plan.name} ({plan.code})
                </MenuItem>
              ))}
            </TextField>

            <Divider textAlign="left" sx={{ pt: 1 }}>
              <Typography variant="overline" sx={{ fontWeight: 800, letterSpacing: '0.06em' }}>
                Founding administrator
              </Typography>
            </Divider>
            <Typography variant="body2" color="text.secondary" sx={{ mt: -1 }}>
              This person receives full authority over the new workspace and creates the customer&apos;s
              own users from there. A tenant provisioned without one cannot be logged into at all.
            </Typography>

            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              <TextField
                label="First name"
                value={form.adminFirstName}
                onChange={(e) => setForm({ ...form, adminFirstName: e.target.value })}
                fullWidth
                required
              />
              <TextField
                label="Last name"
                value={form.adminLastName}
                onChange={(e) => setForm({ ...form, adminLastName: e.target.value })}
                fullWidth
                required
              />
            </Stack>
            <TextField
              label="Work email"
              type="email"
              value={form.adminEmail}
              onChange={(e) => setForm({ ...form, adminEmail: e.target.value })}
              error={form.adminEmail.length > 0 && !adminEmailValid}
              helperText={
                form.adminEmail.length > 0 && !adminEmailValid
                  ? 'Enter a valid email address.'
                  : 'Also their sign-in username. One address belongs to one workspace.'
              }
              fullWidth
              required
            />
            <TextField
              label="Initial password"
              type="text"
              value={form.adminPassword}
              onChange={(e) => setForm({ ...form, adminPassword: e.target.value })}
              error={!adminPasswordValid}
              helperText={
                !adminPasswordValid
                  ? 'At least 8 characters.'
                  : 'Leave blank and one will be generated for you — shown once, on the next screen.'
              }
              fullWidth
            />
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

      {/* Credential handover. A separate, deliberate step: the generated password exists in the
          provisioning response and NOWHERE else — not in the audit log, not retrievable later —
          so it must not be delivered as a toast the operator can miss or dismiss. */}
      <Dialog
        open={handover !== null}
        onClose={() => { /* deliberately not dismissible by backdrop — see the close button */ }}
        fullWidth
        maxWidth="sm"
      >
        <DialogTitle sx={{ fontWeight: 800 }}>
          {handover?.tenant.name} is ready
        </DialogTitle>
        <DialogContent dividers>
          <Stack spacing={2}>
            <Alert severity="success" sx={{ borderRadius: 2 }}>
              The workspace is active and its founding administrator can sign in now.
            </Alert>

            <Stack spacing={0.5}>
              <Typography variant="overline" sx={{ fontWeight: 800, color: 'text.secondary' }}>
                Sign-in email
              </Typography>
              <Typography sx={{ fontFamily: 'monospace', fontSize: '0.95rem' }}>
                {handover?.foundingAdmin.email}
              </Typography>
            </Stack>

            {handover?.foundingAdmin.generatedPassword ? (
              <>
                <Stack spacing={0.5}>
                  <Typography variant="overline" sx={{ fontWeight: 800, color: 'text.secondary' }}>
                    Temporary password
                  </Typography>
                  <Stack direction="row" spacing={1} alignItems="center">
                    <Typography
                      sx={{
                        fontFamily: 'monospace', fontSize: '1.05rem', fontWeight: 700,
                        px: 1.5, py: 1, borderRadius: 1.5, flex: 1,
                        bgcolor: 'action.hover', wordBreak: 'break-all',
                      }}
                    >
                      {handover.foundingAdmin.generatedPassword}
                    </Typography>
                    <Button
                      variant="outlined"
                      onClick={() => {
                        navigator.clipboard
                          ?.writeText(handover.foundingAdmin.generatedPassword ?? '')
                          .then(() => setCredentialCopied(true))
                          .catch(() => enqueueSnackbar('Copy failed — select the password manually.', { variant: 'warning' }));
                      }}
                      sx={{ fontWeight: 700, whiteSpace: 'nowrap' }}
                    >
                      {credentialCopied ? 'Copied' : 'Copy'}
                    </Button>
                  </Stack>
                </Stack>
                <Alert severity="warning" sx={{ borderRadius: 2 }}>
                  <AlertTitle sx={{ fontWeight: 800 }}>Shown once — it cannot be retrieved</AlertTitle>
                  Only a one-way hash is stored, so nobody can look this up later, including us.
                  Send it to {handover.foundingAdmin.email} through a secure channel and have them
                  change it on first sign-in. If it is lost, the password must be reset instead.
                </Alert>
              </>
            ) : (
              <Alert severity="info" sx={{ borderRadius: 2 }}>
                You set the password yourself, so it is not repeated here. Share it with{' '}
                {handover?.foundingAdmin.email} through a secure channel.
              </Alert>
            )}
          </Stack>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button
            variant="contained"
            onClick={() => setHandover(null)}
            disabled={Boolean(handover?.foundingAdmin.generatedPassword) && !credentialCopied}
            sx={{ fontWeight: 700, px: 3 }}
          >
            {handover?.foundingAdmin.generatedPassword && !credentialCopied
              ? 'Copy the password first'
              : 'Done'}
          </Button>
        </DialogActions>
      </Dialog>

      {/* Confirm action dialog */}
      <Dialog open={!!confirm} onClose={() => setConfirm(null)} maxWidth="xs" fullWidth>
        <DialogTitle sx={{ fontWeight: 800 }}>{confirm ? ACTION_COPY[confirm.kind].title : ''}</DialogTitle>
        <DialogContent>
          <DialogContentText>
            {confirm?.kind === 'suspend' && (
              <>Mark <strong>{confirm.tenant.name}</strong> as suspended and record the reason in the platform audit trail?</>
            )}
            {confirm?.kind === 'resume' && (
              <>Return <strong>{confirm.tenant.name}</strong> to active status and record the action?</>
            )}
            {confirm?.kind === 'archive' && (
              <>Archive <strong>{confirm.tenant.name}</strong>? Archived tenants stay fully blocked until restored, and the reason is recorded in the audit trail.</>
            )}
            {confirm?.kind === 'restore' && (
              <>Restore <strong>{confirm.tenant.name}</strong> from archive back to suspended? Resume it separately to reactivate access.</>
            )}
            {confirm?.kind === 'impersonate' && (
              <>Issue a 15-minute read-only impersonation session for <strong>{confirm.tenant.name}</strong> and enter its workspace? This is recorded in the platform audit log.</>
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
            color={
              confirm?.kind === 'suspend' ? 'warning'
                : confirm?.kind === 'archive' ? 'error'
                : confirm?.kind === 'impersonate' ? 'primary'
                : 'success'
            }
            onClick={() => confirm && actionMutation.mutate(confirm)}
            disabled={actionMutation.isPending || actionReason.trim().length < 3}
            sx={{ fontWeight: 700 }}
          >
            {actionMutation.isPending ? 'Working…' : confirm ? ACTION_COPY[confirm.kind].verb : ''}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
