import Stack from '../components/Flex';
import { useNavigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { ArrowBack as BackIcon, Dns as PipelineIcon, ReceiptLong as AuditIcon } from '@mui/icons-material';
import { Box, Button, Grid, Paper, Typography } from '@mui/material';
import { platformApi } from '../api/client';
import { platformKeys } from '../api/queryKeys';
import PageHeader from '../components/PageHeader';
import { PlanChip, TenantStatusChip } from '../components/StatusChip';
import { ErrorState, LoadingState } from '../components/States';
import { fmtDateTime } from '../components/format';

export default function TenantDetailPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const { data: tenant, isLoading, isError, refetch } = useQuery({
    queryKey: platformKeys.tenant(id),
    queryFn: () => platformApi.getTenant(id),
    enabled: !!id,
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

  return (
    <Box>
      <Button startIcon={<BackIcon />} onClick={() => navigate('/platform/tenants')} sx={{ mb: 1.5 }} color="inherit">Tenants</Button>
      <PageHeader
        title={tenant.name}
        subtitle="Persisted platform tenant identity and operational links."
        actions={<Stack direction="row" spacing={1}><PlanChip tier={tenant.planTier} /><TenantStatusChip status={tenant.status} /></Stack>}
      />
      <Grid container spacing={2.5}>
        <Grid size={{ xs: 12, md: 7 }}>
          <Paper sx={{ p: 3, borderRadius: 2 }}>
            <Typography variant="h6" sx={{ fontWeight: 800, mb: 2 }}>Tenant Registry</Typography>
            <Stack spacing={1.5}>
              <RegistryRow label="Platform tenant ID" value={tenant.id} />
              <RegistryRow label="Name" value={tenant.name} />
              <RegistryRow label="Slug" value={tenant.slug} />
              <RegistryRow label="Plan" value={tenant.planTier} />
              <RegistryRow label="Lifecycle status" value={tenant.status} />
              <RegistryRow label="Created" value={fmtDateTime(tenant.createdAt)} />
            </Stack>
          </Paper>
        </Grid>
        <Grid size={{ xs: 12, md: 5 }}>
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
