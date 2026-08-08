import { useQuery } from '@tanstack/react-query';
import { Alert, AlertTitle, Box, Chip, Grid, Typography } from '@mui/material';
import Stack from '../../components/Flex';
import PageSection from '../../components/PageSection';
import { EmptyState, ErrorState, LoadingState } from '../../components/States';
import { platformApi } from '../../api/client';
import { platformErrorMessage } from '../../api/apiError';
import { platformKeys } from '../../api/queryKeys';
import type { Tenant } from '../../types';

const quota = (value: number | null): string => value == null ? 'Unlimited' : value.toLocaleString();

export default function EntitlementsTab({ tenant }: { tenant: Tenant }) {
  const plans = useQuery({ queryKey: platformKeys.plans(), queryFn: () => platformApi.listPlans() });

  if (plans.isLoading) return <LoadingState label="Reading plan entitlements…" />;
  if (plans.isError) {
    return <ErrorState message={platformErrorMessage(plans.error, 'Plan entitlements could not be read.')} onRetry={() => plans.refetch()} />;
  }
  if (tenant.planId === null) {
    return (
      <Alert severity="error">
        <AlertTitle>This tenant has no plan</AlertTitle>
        No quotas or feature entitlements can be claimed until commercial authority assigns a plan.
      </Alert>
    );
  }

  const plan = plans.data?.find((candidate) => candidate.id === tenant.planId);
  if (!plan) {
    return (
      <Alert severity="error">
        <AlertTitle>Assigned plan could not be resolved</AlertTitle>
        Tenant plan id {tenant.planId} is not present in the platform plan catalogue. Entitlements remain unknown;
        this screen will not guess from the plan code.
      </Alert>
    );
  }

  return (
    <Stack spacing={2.5}>
      {!plan.isActive && (
        <Alert severity="warning">
          <AlertTitle>Assigned plan is inactive</AlertTitle>
          {tenant.name} still references {plan.name}. Review the commercial assignment before promising access or capacity.
        </Alert>
      )}
      <PageSection title={`${plan.name} entitlements`} subtitle={`Effective catalogue entry ${plan.code} · plan id ${plan.id}`}>
        <Grid container spacing={2}>
          <Grid size={{ xs: 12, sm: 4 }}><Metric label="Concurrent extractions" value={quota(plan.concurrencyCap)} /></Grid>
          <Grid size={{ xs: 12, sm: 4 }}><Metric label="Documents per month" value={quota(plan.monthlyDocQuota)} /></Grid>
          <Grid size={{ xs: 12, sm: 4 }}><Metric label="Seats" value={quota(plan.seatQuota)} /></Grid>
        </Grid>
      </PageSection>
      <PageSection title="Enabled features" subtitle="Only feature keys set to true in the server-owned plan catalogue appear here.">
        {plan.entitlements.length === 0 ? (
          <EmptyState title="No feature entitlements enabled" message="This is an empty server response, not an inferred default." minHeight={140} />
        ) : (
          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1 }}>
            {plan.entitlements.map((entitlement) => <Chip key={entitlement} label={entitlement} color="primary" variant="outlined" />)}
          </Box>
        )}
      </PageSection>
      <Alert severity="info">
        This page shows contracted plan capacity, not live consumption. Usage and billing evidence remain on the
        Commercial tab; extraction execution remains on Pipeline.
      </Alert>
    </Stack>
  );
}

function Metric({ label, value }: { label: string; value: string }) {
  return <Box><Typography variant="caption" color="text.secondary">{label}</Typography><Typography variant="h6" sx={{ fontWeight: 800 }}>{value}</Typography></Box>;
}
