import { useMemo, useState } from 'react';
import Stack from '../components/Flex';
import { Link as RouterLink, useNavigate } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  IconButton,
  InputAdornment,
  Link,
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
  DeleteOutlined as DeleteIcon,
  EditOutlined as EditIcon,
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
import { BILLING_MODES } from '../types';
import type { BillingMode, SubmitProvisioningResult, Tenant, TenantStatus } from '../types';
import { usePlatformPermissions } from '../auth/usePlatformPermissions';
import { REQUIRED_ROLE_COPY } from '../auth/permissions';
import PageHeader from '../components/PageHeader';
import ProvisionTenantWizard from '../components/ProvisionTenantWizard';
import ProvisioningProgressDialog from '../components/ProvisioningProgressDialog';
import RoleGate from '../components/RoleGate';
import { BillingModeChip, Dash, PlanChip, TenantStatusChip } from '../components/StatusChip';
import { ErrorState } from '../components/States';
import { countryLabel, countryName } from '../components/localeData';
import { isTrialExpired } from '../components/provisionValidation';
import { fmtDate, fmtRelative } from '../components/format';
import { tenantOffboardingPath } from './tenantNavigation';

type ActionKind = 'suspend' | 'resume' | 'archive' | 'restore' | 'impersonate';

const ACTION_COPY: Record<ActionKind, { title: string; verb: string }> = {
  suspend: { title: 'Suspend tenant', verb: 'Suspend' },
  resume: { title: 'Resume tenant', verb: 'Resume' },
  archive: { title: 'Archive tenant', verb: 'Archive' },
  restore: { title: 'Restore tenant', verb: 'Restore' },
  impersonate: { title: 'Impersonate tenant', verb: 'Impersonate' },
};

export function TenantNameLink({ tenant }: { tenant: Tenant }) {
  return (
    <Box sx={{ lineHeight: 1.2 }}>
      <Link
        component={RouterLink}
        to={`/platform/tenants/${tenant.id}`}
        variant="body2"
        underline="hover"
        onClick={(event) => event.stopPropagation()}
        sx={{ fontWeight: 700 }}
      >
        {tenant.name}
      </Link>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
        {tenant.slug}
      </Typography>
    </Box>
  );
}

export default function TenantsPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();

  const [search, setSearch] = useState('');
  const [planFilter, setPlanFilter] = useState<string>('all');
  const [statusFilter, setStatusFilter] = useState<TenantStatus | 'all'>('all');
  const [billingFilter, setBillingFilter] = useState<BillingMode | 'all' | 'expired-trial'>('all');
  const [countryFilter, setCountryFilter] = useState<string>('all');

  const permissions = usePlatformPermissions();

  const [provisionOpen, setProvisionOpen] = useState(false);
  const [confirm, setConfirm] = useState<{ kind: ActionKind; tenant: Tenant } | null>(null);
  // Held after the submit is accepted. The generated credential lives ONLY in this
  // response, so it is handed to the progress dialog rather than a toast that scrolls away.
  const [submission, setSubmission] = useState<SubmitProvisioningResult | null>(null);
  const [actionReason, setActionReason] = useState('');

  const { data: tenants, isLoading, isError, refetch } = useQuery({
    queryKey: platformKeys.tenants(),
    queryFn: () => platformApi.listTenants(),
  });

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: platformKeys.tenants() });
    queryClient.invalidateQueries({ queryKey: platformKeys.overview() });
  };

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

  // Only countries actually present in the fleet are offered, so the filter never
  // lists 249 options of which two match anything.
  const countryCodes = useMemo(() => {
    const codes = new Set<string>();
    (tenants ?? []).forEach((t) => { if (t.countryCode) codes.add(t.countryCode); });
    return [...codes].sort((a, b) => countryName(a).localeCompare(countryName(b)));
  }, [tenants]);

  const expiredTrials = useMemo(
    () => (tenants ?? []).filter((t) => isTrialExpired(t.billingMode, t.trialEndsOn)),
    [tenants],
  );

  const rows = useMemo(() => {
    let list = tenants ?? [];
    if (planFilter !== 'all') list = list.filter((t) => (t.planCode ?? 'none') === planFilter);
    if (statusFilter !== 'all') list = list.filter((t) => t.status === statusFilter);
    if (billingFilter === 'expired-trial') list = list.filter((t) => isTrialExpired(t.billingMode, t.trialEndsOn));
    else if (billingFilter !== 'all') list = list.filter((t) => t.billingMode === billingFilter);
    if (countryFilter !== 'all') list = list.filter((t) => t.countryCode === countryFilter);
    if (search.trim()) {
      const q = search.toLowerCase();
      list = list.filter((t) =>
        t.name.toLowerCase().includes(q) ||
        t.slug.toLowerCase().includes(q) ||
        (t.legalName ?? '').toLowerCase().includes(q) ||
        (t.contactEmail ?? '').toLowerCase().includes(q));
    }
    return list;
  }, [tenants, planFilter, statusFilter, billingFilter, countryFilter, search]);

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
      renderCell: (p: GridRenderCellParams<Tenant>) => <TenantNameLink tenant={p.row} />,
    },
    {
      field: 'countryCode',
      headerName: 'Country',
      width: 130,
      valueGetter: (_v, row) => row.countryCode ?? '',
      renderCell: (p) =>
        p.row.countryCode ? (
          <Tooltip title={countryLabel(p.row.countryCode)}>
            <Typography variant="body2" sx={{ fontWeight: 700 }}>
              {p.row.countryCode}
            </Typography>
          </Tooltip>
        ) : (
          <Dash />
        ),
    },
    {
      field: 'planCode',
      headerName: 'Plan',
      width: 120,
      renderCell: (p) => <PlanChip tier={p.row.planCode ?? 'none'} />,
    },
    {
      field: 'billingMode',
      headerName: 'Billing',
      width: 120,
      valueGetter: (_v, row) => row.billingMode ?? '',
      renderCell: (p) => <BillingModeChip mode={p.row.billingMode} />,
    },
    {
      field: 'trialEndsOn',
      headerName: 'Trial ends',
      width: 150,
      valueGetter: (_v, row) => row.trialEndsOn ?? '',
      renderCell: (p) => {
        if (!p.row.trialEndsOn) return <Dash />;
        const expired = isTrialExpired(p.row.billingMode, p.row.trialEndsOn);
        return (
          <Typography
            variant="caption"
            sx={{ fontWeight: expired ? 800 : 600, color: expired ? 'error.main' : 'text.secondary' }}
          >
            {expired ? `Expired ${fmtDate(p.row.trialEndsOn)}` : fmtDate(p.row.trialEndsOn)}
          </Typography>
        );
      },
    },
    {
      field: 'status',
      headerName: 'Status',
      width: 130,
      renderCell: (p) => <TenantStatusChip status={p.row.status} />,
    },
    {
      field: 'createdAt',
      headerName: 'Created',
      width: 110,
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
      width: 230,
      sortable: false,
      filterable: false,
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5} role="group" aria-label={`Actions for ${p.row.name}`}>
          <Tooltip title={permissions.canAdministerTenants ? 'Edit tenant profile and administrator access' : REQUIRED_ROLE_COPY.tenantAdmin}>
            <span>
              <IconButton
                size="small"
                aria-label="Edit tenant"
                disabled={!permissions.canAdministerTenants}
                onClick={(event) => {
                  event.stopPropagation();
                  navigate(`/platform/tenants/${p.row.id}?tab=profile-access`);
                }}
              >
                <EditIcon fontSize="small" />
              </IconButton>
            </span>
          </Tooltip>
          <Tooltip title={permissions.isOwner ? 'Offboard / delete tenant with retention and approval controls' : REQUIRED_ROLE_COPY.owner}>
            <span>
              <IconButton
                size="small"
                color="error"
                aria-label="Offboard or delete tenant"
                disabled={!permissions.isOwner}
                onClick={(event) => {
                  event.stopPropagation();
                  navigate(tenantOffboardingPath(p.row.id));
                }}
              >
                <DeleteIcon fontSize="small" />
              </IconButton>
            </span>
          </Tooltip>
          {/* Every lifecycle verb here is Owner|SupportAdmin server-side, so a BillingAdmin
              or ReadOnlyOps operator sees them disabled with the reason rather than a 403. */}
          <Tooltip
            title={
              permissions.canAdministerTenants
                ? p.row.status === 'archived'
                  ? 'Restore to suspended'
                  : p.row.status === 'suspended'
                    ? 'Resume'
                    : 'Suspend'
                : REQUIRED_ROLE_COPY.tenantAdmin
            }
          >
            <span>
              {p.row.status === 'suspended' || p.row.status === 'archived' ? (
                <IconButton
                  size="small"
                  color="success"
                  disabled={!permissions.canAdministerTenants}
                  aria-label={p.row.status === 'archived' ? 'Restore tenant' : 'Resume tenant'}
                  onClick={(event) => {
                    event.stopPropagation();
                    openConfirm(p.row.status === 'archived' ? 'restore' : 'resume', p.row);
                  }}
                >
                  {p.row.status === 'archived' ? <RestoreIcon fontSize="small" /> : <ResumeIcon fontSize="small" />}
                </IconButton>
              ) : (
                <IconButton
                  size="small"
                  color="warning"
                  aria-label="Suspend tenant"
                  disabled={p.row.status === 'provisioning' || !permissions.canAdministerTenants}
                  onClick={(event) => {
                    event.stopPropagation();
                    openConfirm('suspend', p.row);
                  }}
                >
                  <SuspendIcon fontSize="small" />
                </IconButton>
              )}
            </span>
          </Tooltip>
          <Tooltip
            title={
              permissions.canAdministerTenants
                ? 'Archive (suspended tenants only)'
                : REQUIRED_ROLE_COPY.tenantAdmin
            }
          >
            <span>
              <IconButton
                size="small"
                color="error"
                aria-label="Archive tenant"
                disabled={p.row.status !== 'suspended' || !permissions.canAdministerTenants}
                onClick={(event) => {
                  event.stopPropagation();
                  openConfirm('archive', p.row);
                }}
              >
                <ArchiveIcon fontSize="small" />
              </IconButton>
            </span>
          </Tooltip>
          <Tooltip title={permissions.canImpersonate ? 'Impersonate' : REQUIRED_ROLE_COPY.impersonate}>
            <span>
              <IconButton
                size="small"
                color="primary"
                aria-label="Impersonate tenant"
                disabled={p.row.status !== 'active' || !permissions.canImpersonate}
                onClick={(event) => {
                  event.stopPropagation();
                  openConfirm('impersonate', p.row);
                }}
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
          <RoleGate allowed={permissions.canAdministerTenants} requirement={REQUIRED_ROLE_COPY.tenantAdmin}>
            {(disabled) => (
              <Button
                variant="contained"
                startIcon={<AddIcon />}
                disabled={disabled}
                onClick={() => setProvisionOpen(true)}
                sx={{ fontWeight: 700 }}
              >
                Create Company
              </Button>
            )}
          </RoleGate>
        }
      />

      {/* An expired trial is a workspace still being served for free. It gets a
          standing banner, not just a red cell somebody has to scroll to. */}
      {expiredTrials.length > 0 && billingFilter !== 'expired-trial' && (
        <Alert
          severity="error"
          sx={{ mb: 2, borderRadius: 2 }}
          action={
            <Button color="inherit" size="small" onClick={() => setBillingFilter('expired-trial')} sx={{ fontWeight: 700 }}>
              Show them
            </Button>
          }
        >
          {expiredTrials.length === 1
            ? '1 trial has passed its end date and is still being served.'
            : `${expiredTrials.length} trials have passed their end date and are still being served.`}
        </Alert>
      )}

      <Paper sx={{ p: 1.5, mb: 2, borderRadius: 3 }}>
        <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5} sx={{ flexWrap: 'wrap' }}>
          <TextField
            size="small"
            placeholder="Search name, slug, legal name or contact…"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            aria-label="Search tenants"
            sx={{ flex: 1, minWidth: 220 }}
            slotProps={{ input: { startAdornment: <InputAdornment position="start"><SearchIcon fontSize="small" /></InputAdornment> } }}
          />
          <TextField size="small" select label="Plan" value={planFilter} onChange={(e) => setPlanFilter(e.target.value)} sx={{ minWidth: 140 }}>
            <MenuItem value="all">All Plans</MenuItem>
            {planCodes.map((code) => (
              <MenuItem key={code} value={code} sx={{ textTransform: 'capitalize' }}>
                {code}
              </MenuItem>
            ))}
          </TextField>
          <TextField
            size="small"
            select
            label="Billing"
            value={billingFilter}
            onChange={(e) => setBillingFilter(e.target.value as BillingMode | 'all' | 'expired-trial')}
            sx={{ minWidth: 160 }}
          >
            <MenuItem value="all">All Billing Modes</MenuItem>
            {BILLING_MODES.map((mode) => (
              <MenuItem key={mode} value={mode}>
                {mode}
              </MenuItem>
            ))}
            <MenuItem value="expired-trial">Expired trials</MenuItem>
          </TextField>
          <TextField size="small" select label="Country" value={countryFilter} onChange={(e) => setCountryFilter(e.target.value)} sx={{ minWidth: 160 }}>
            <MenuItem value="all">All Countries</MenuItem>
            {countryCodes.map((code) => (
              <MenuItem key={code} value={code}>
                {countryLabel(code)}
              </MenuItem>
            ))}
          </TextField>
          <TextField size="small" select label="Status" value={statusFilter} onChange={(e) => setStatusFilter(e.target.value as TenantStatus | 'all')} sx={{ minWidth: 140 }}>
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

      <ProvisionTenantWizard
        open={provisionOpen}
        onClose={() => setProvisionOpen(false)}
        onSubmitted={(result) => {
          // 202, not 201: nothing exists yet. The operator moves straight to the progress
          // list rather than a toast claiming a workspace that is still eight steps away.
          setProvisionOpen(false);
          setSubmission(result);
        }}
      />

      <ProvisioningProgressDialog
        executionId={submission?.execution.id ?? null}
        submission={submission}
        onClose={() => {
          setSubmission(null);
          invalidate();
        }}
      />

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
