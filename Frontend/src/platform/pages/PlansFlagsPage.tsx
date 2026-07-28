import Stack from '../components/Flex';
import { useQuery } from '@tanstack/react-query';
import { Box, Chip, Divider, Grid, Paper, Table, TableBody, TableCell, TableHead, TableRow, Typography } from '@mui/material';
import { WorkspacePremium } from '@mui/icons-material';
import { platformApi } from '../api/client';
import { platformKeys } from '../api/queryKeys';
import type { Plan, PlanTier } from '../types';
import PageHeader from '../components/PageHeader';
import { PlanChip } from '../components/StatusChip';
import { ErrorState, LoadingState } from '../components/States';
import { fmtCurrency, fmtNumber } from '../components/format';

const PLAN_ACCENT: Record<PlanTier, string> = {
  free: '#64748b',
  pro: '#2563eb',
  enterprise: '#059669',
  unassigned: '#64748b',
};

export default function PlansFlagsPage() {
  const { data: plans, isLoading, isError, refetch } = useQuery({
    queryKey: platformKeys.plans(),
    queryFn: () => platformApi.listPlans(),
  });

  if (isLoading) return <LoadingState label="Loading plans…" minHeight="60vh" />;
  if (isError || !plans) return <ErrorState message="The persisted plan catalog could not be loaded." onRetry={() => refetch()} />;

  const formatQuota = (value: number | null, unit: string) =>
    value == null ? 'Unlimited' : `${fmtNumber(value)}${unit ? ` ${unit}` : ''}`;
  const formatPrice = (plan: Plan) =>
    plan.priceMonthlyUsd == null ? 'Not recorded' : plan.priceMonthlyUsd === 0 ? 'Free' : `${fmtCurrency(plan.priceMonthlyUsd)}/mo`;

  return (
    <Box>
      <PageHeader title="Plans" subtitle="Persisted scheduling, quota, and entitlement configuration." />

      <Grid container spacing={2.5} sx={{ mb: 3 }}>
        {plans.map((plan) => {
          const accent = PLAN_ACCENT[plan.tier];
          return (
            <Grid size={{ xs: 12, md: 4 }} key={plan.id}>
              <Paper sx={{ p: 3, borderRadius: 2, height: '100%', borderTop: `4px solid ${accent}` }}>
                <Stack direction="row" spacing={1} alignItems="center">
                  <WorkspacePremium sx={{ color: accent }} />
                  <Typography variant="h6" sx={{ fontWeight: 800 }}>{plan.name}</Typography>
                </Stack>
                <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>{formatPrice(plan)}</Typography>
                <Divider sx={{ my: 2 }} />
                <Stack spacing={1.25}>
                  <PlanRow label="Dispatcher weight" value={`${plan.weight}x`} />
                  <PlanRow label="Concurrency cap" value={`${plan.concurrencyCap} jobs`} />
                  <PlanRow label="Monthly document quota" value={formatQuota(plan.monthlyDocQuota, 'docs')} />
                  <PlanRow label="Seat quota" value={formatQuota(plan.seatQuota, 'seats')} />
                </Stack>
                <Divider sx={{ my: 2 }} />
                <Typography variant="overline" color="text.secondary" sx={{ fontWeight: 700 }}>Enabled entitlements</Typography>
                <Stack direction="row" useFlexGap spacing={0.75} sx={{ mt: 1, flexWrap: 'wrap' }}>
                  {plan.entitlements.length === 0 ? (
                    <Typography variant="body2" color="text.secondary">None configured</Typography>
                  ) : plan.entitlements.map((entitlement) => (
                    <Chip key={entitlement} size="small" variant="outlined" label={entitlement} />
                  ))}
                </Stack>
              </Paper>
            </Grid>
          );
        })}
      </Grid>

      <Paper sx={{ p: 3, borderRadius: 2, overflowX: 'auto' }}>
        <Typography variant="h6" sx={{ fontWeight: 800, mb: 2 }}>Quota Matrix</Typography>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell sx={{ fontWeight: 700 }}>Attribute</TableCell>
              {plans.map((plan) => <TableCell key={plan.id} align="right"><PlanChip tier={plan.tier} /></TableCell>)}
            </TableRow>
          </TableHead>
          <TableBody>
            {[
              { label: 'Price / month', get: formatPrice },
              { label: 'Dispatcher weight', get: (plan: Plan) => `${plan.weight}x` },
              { label: 'Concurrency cap', get: (plan: Plan) => `${plan.concurrencyCap} jobs` },
              { label: 'Monthly documents', get: (plan: Plan) => formatQuota(plan.monthlyDocQuota, '') },
              { label: 'Seats', get: (plan: Plan) => formatQuota(plan.seatQuota, '') },
              { label: 'Entitlements', get: (plan: Plan) => `${plan.entitlements.length}` },
            ].map((row) => (
              <TableRow key={row.label} hover>
                <TableCell sx={{ fontWeight: 600 }}>{row.label}</TableCell>
                {plans.map((plan) => <TableCell key={plan.id} align="right">{row.get(plan)}</TableCell>)}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Paper>
    </Box>
  );
}

function PlanRow({ label, value }: { label: string; value: string }) {
  return (
    <Stack direction="row" justifyContent="space-between" spacing={2}>
      <Typography variant="body2" color="text.secondary">{label}</Typography>
      <Typography variant="body2" sx={{ fontWeight: 700, textAlign: 'right' }}>{value}</Typography>
    </Stack>
  );
}
