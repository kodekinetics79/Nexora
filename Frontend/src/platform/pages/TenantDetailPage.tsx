import { useState } from 'react';
import Stack from '../components/Flex';
import { useNavigate, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  ArrowBack as BackIcon,
  Dns as PipelineIcon,
  Inventory2Outlined as ArchiveIcon,
  PauseCircleOutlined as SuspendIcon,
  PlayCircleOutlined as ResumeIcon,
  ReceiptLong as AuditIcon,
  RestorePageOutlined as RestoreIcon,
  SwapHoriz as PlanIcon,
} from '@mui/icons-material';
import {
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  Grid,
  MenuItem,
  Paper,
  TextField,
  Typography,
} from '@mui/material';
import { useSnackbar } from 'notistack';
import { platformApi } from '../api/client';
import { platformErrorMessage } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import PageHeader from '../components/PageHeader';
import { PlanChip, TenantStatusChip } from '../components/StatusChip';
import { ErrorState, LoadingState } from '../components/States';
import { fmtDateTime } from '../components/format';

type LifecycleKind = 'suspend' | 'resume' | 'archive' | 'restore';

const LIFECYCLE_VERB: Record<LifecycleKind, string> = {
  suspend: 'Suspend',
  resume: 'Resume',
  archive: 'Archive',
  restore: 'Restore',
};

export default function TenantDetailPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();

  const [lifecycle, setLifecycle] = useState<LifecycleKind | null>(null);
  const [reason, setReason] = useState('');
  const [planOpen, setPlanOpen] = useState(false);
  const [planId, setPlanId] = useState('');
  const [planReason, setPlanReason] = useState('');

  const { data: tenant, isLoading, isError, refetch } = useQuery({
    queryKey: platformKeys.tenant(id),
    queryFn: () => platformApi.getTenant(id),
    enabled: !!id,
  });

  const { data: plans } = useQuery({
    queryKey: platformKeys.plans(),
    queryFn: () => platformApi.listPlans(),
  });

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: platformKeys.tenant(id) });
    queryClient.invalidateQueries({ queryKey: platformKeys.tenants() });
    queryClient.invalidateQueries({ queryKey: platformKeys.overview() });
  };

  const lifecycleMutation = useMutation({
    mutationFn: async (kind: LifecycleKind) => {
      const trimmed = reason.trim();
      if (kind === 'suspend') return platformApi.suspendTenant(id, trimmed);
      if (kind === 'resume') return platformApi.resumeTenant(id, trimmed);
      if (kind === 'archive') return platformApi.archiveTenant(id, trimmed);
      return platformApi.restoreTenant(id, trimmed);
    },
    onSuccess: (updated, kind) => {
      enqueueSnackbar(`${updated.name} ${kind}d`, { variant: 'success' });
      setLifecycle(null);
      setReason('');
      invalidate();
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Lifecycle change failed'), { variant: 'error' }),
  });

  const planMutation = useMutation({
    mutationFn: () => platformApi.changeTenantPlan(id, planId, planReason.trim() || undefined),
    onSuccess: (updated) => {
      enqueueSnackbar(`${updated.name} moved to plan ${updated.planCode ?? planId}`, { variant: 'success' });
      setPlanOpen(false);
      setPlanReason('');
      invalidate();
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Plan change failed'), { variant: 'error' }),
  });

  if (isLoading) return <LoadingState label="Loading tenant…" minHeight="60vh" />;
  if (isError || !tenant) {
    return (
      <Box>
        <Button startIcon={<BackIcon />} onClick={() => navigate('/platform/tenants')} sx={{ mb: 2 }}>Back to tenants</Button>
        <ErrorState message="This tenant could not be loaded." onRetry={() => refetch()} />
      </Box>
    );
  }

  const openLifecycle = (kind: LifecycleKind) => {
    setReason('');
    setLifecycle(kind);
  };
  const openPlanDialog = () => {
    setPlanId(tenant.planId ?? '');
    setPlanReason('');
    setPlanOpen(true);
  };

  return (
    <Box>
      <Button startIcon={<BackIcon />} onClick={() => navigate('/platform/tenants')} sx={{ mb: 1.5 }} color="inherit">Tenants</Button>
      <PageHeader
        title={tenant.name}
        subtitle="Persisted platform tenant identity and operational links."
        actions={<Stack direction="row" spacing={1}><PlanChip tier={tenant.planCode ?? 'none'} /><TenantStatusChip status={tenant.status} /></Stack>}
      />
      <Grid container spacing={2.5}>
        <Grid size={{ xs: 12, md: 7 }}>
          <Paper sx={{ p: 3, borderRadius: 2 }}>
            <Typography variant="h6" sx={{ fontWeight: 800, mb: 2 }}>Tenant Registry</Typography>
            <Stack spacing={1.5}>
              <RegistryRow label="Platform tenant ID" value={tenant.id} />
              <RegistryRow label="Name" value={tenant.name} />
              <RegistryRow label="Slug" value={tenant.slug} />
              <RegistryRow label="Plan" value={tenant.planCode ?? '—'} />
              <RegistryRow label="Lifecycle status" value={tenant.status} />
              <RegistryRow label="Status reason" value={tenant.statusReason ?? '—'} />
              <RegistryRow label="Created" value={tenant.createdAt ? fmtDateTime(tenant.createdAt) : '—'} />
            </Stack>
          </Paper>
        </Grid>
        <Grid size={{ xs: 12, md: 5 }}>
          <Paper sx={{ p: 3, borderRadius: 2, mb: 2.5 }}>
            <Typography variant="h6" sx={{ fontWeight: 800, mb: 2 }}>Lifecycle & Plan</Typography>
            <Stack spacing={1.5}>
              {tenant.status === 'active' && (
                <Button variant="outlined" color="warning" startIcon={<SuspendIcon />} onClick={() => openLifecycle('suspend')}>
                  Suspend tenant
                </Button>
              )}
              {tenant.status === 'suspended' && (
                <>
                  <Button variant="outlined" color="success" startIcon={<ResumeIcon />} onClick={() => openLifecycle('resume')}>
                    Resume tenant
                  </Button>
                  <Button variant="outlined" color="error" startIcon={<ArchiveIcon />} onClick={() => openLifecycle('archive')}>
                    Archive tenant
                  </Button>
                </>
              )}
              {tenant.status === 'archived' && (
                <Button variant="outlined" color="success" startIcon={<RestoreIcon />} onClick={() => openLifecycle('restore')}>
                  Restore to suspended
                </Button>
              )}
              <Button variant="outlined" startIcon={<PlanIcon />} onClick={openPlanDialog}>
                Change plan
              </Button>
            </Stack>
          </Paper>
          <Paper sx={{ p: 3, borderRadius: 2 }}>
            <Typography variant="h6" sx={{ fontWeight: 800, mb: 2 }}>Operational Evidence</Typography>
            <Stack spacing={1.5}>
              <Button variant="outlined" startIcon={<PipelineIcon />} onClick={() => navigate(`/platform/pipeline?tenant=${tenant.id}`)}>
                View tenant extraction jobs
              </Button>
              <Button variant="outlined" startIcon={<AuditIcon />} onClick={() => navigate(`/platform/audit?tenant=${tenant.id}`)}>
                View tenant audit entries
              </Button>
            </Stack>
          </Paper>
        </Grid>
      </Grid>

      {/* Lifecycle confirm dialog */}
      <Dialog open={!!lifecycle} onClose={() => setLifecycle(null)} maxWidth="xs" fullWidth>
        <DialogTitle sx={{ fontWeight: 800 }}>{lifecycle ? `${LIFECYCLE_VERB[lifecycle]} tenant` : ''}</DialogTitle>
        <DialogContent>
          <DialogContentText>
            {lifecycle === 'suspend' && <>Suspend <strong>{tenant.name}</strong>? Its users lose access until resumed.</>}
            {lifecycle === 'resume' && <>Return <strong>{tenant.name}</strong> to active status?</>}
            {lifecycle === 'archive' && <>Archive <strong>{tenant.name}</strong>? Only suspended tenants can be archived; restore later to bring it back.</>}
            {lifecycle === 'restore' && <>Restore <strong>{tenant.name}</strong> from archive back to suspended?</>}
            {' '}The reason is recorded in the platform audit trail.
          </DialogContentText>
          <TextField
            autoFocus
            fullWidth
            required
            label="Audit reason"
            value={reason}
            onChange={(event) => setReason(event.target.value)}
            sx={{ mt: 2 }}
          />
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setLifecycle(null)} color="inherit">Cancel</Button>
          <Button
            variant="contained"
            color={lifecycle === 'suspend' ? 'warning' : lifecycle === 'archive' ? 'error' : 'success'}
            onClick={() => lifecycle && lifecycleMutation.mutate(lifecycle)}
            disabled={lifecycleMutation.isPending || reason.trim().length < 3}
            sx={{ fontWeight: 700 }}
          >
            {lifecycleMutation.isPending ? 'Working…' : lifecycle ? LIFECYCLE_VERB[lifecycle] : ''}
          </Button>
        </DialogActions>
      </Dialog>

      {/* Plan change dialog */}
      <Dialog open={planOpen} onClose={() => setPlanOpen(false)} maxWidth="xs" fullWidth>
        <DialogTitle sx={{ fontWeight: 800 }}>Change plan</DialogTitle>
        <DialogContent>
          <DialogContentText sx={{ mb: 2 }}>
            Assign a different persisted plan to <strong>{tenant.name}</strong>. Quotas and scheduling weight apply from the next dispatch.
          </DialogContentText>
          <Stack spacing={2}>
            <TextField
              select
              fullWidth
              label="Plan"
              value={planId}
              onChange={(event) => setPlanId(event.target.value)}
            >
              {(plans ?? []).map((plan) => (
                <MenuItem key={plan.id} value={plan.id}>
                  {plan.name} ({plan.code})
                </MenuItem>
              ))}
            </TextField>
            <TextField
              fullWidth
              label="Reason (optional)"
              value={planReason}
              onChange={(event) => setPlanReason(event.target.value)}
            />
          </Stack>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setPlanOpen(false)} color="inherit">Cancel</Button>
          <Button
            variant="contained"
            onClick={() => planMutation.mutate()}
            disabled={planMutation.isPending || !planId || planId === (tenant.planId ?? '')}
            sx={{ fontWeight: 700 }}
          >
            {planMutation.isPending ? 'Changing…' : 'Change plan'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}

function RegistryRow({ label, value }: { label: string; value: string }) {
  return (
    <Stack direction={{ xs: 'column', sm: 'row' }} justifyContent="space-between" spacing={1}>
      <Typography variant="body2" color="text.secondary">{label}</Typography>
      <Typography variant="body2" sx={{ fontWeight: 700, overflowWrap: 'anywhere' }}>{value}</Typography>
    </Stack>
  );
}
