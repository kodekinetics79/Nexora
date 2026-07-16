import { useMemo, useState } from 'react';
import Stack from '../components/Flex';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Box,
  Chip,
  Divider,
  Grid,
  MenuItem,
  Paper,
  Switch,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from '@mui/material';
import { CheckCircle, RemoveCircleOutlined as RemoveCircleOutline, WorkspacePremium } from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import { useAppTheme } from '../../context/ThemeContext';
import { platformApi } from '../api/client';
import { platformKeys } from '../api/queryKeys';
import type { Plan, PlanTier } from '../types';
import PageHeader from '../components/PageHeader';
import { PlanChip } from '../components/StatusChip';
import { LoadingState } from '../components/States';
import { fmtCurrency, fmtNumber } from '../components/format';

const PLAN_ACCENT: Record<PlanTier, string> = {
  free: '#94a3b8',
  pro: '#3b82f6',
  enterprise: '#10b981',
};

export default function PlansFlagsPage() {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { mode } = useAppTheme();
  const [selectedTenantId, setSelectedTenantId] = useState<string>('');

  const { data: plans, isLoading: plansLoading } = useQuery({
    queryKey: platformKeys.plans(),
    queryFn: () => platformApi.listPlans(),
  });
  const { data: flags, isLoading: flagsLoading } = useQuery({
    queryKey: platformKeys.flags(),
    queryFn: () => platformApi.listFeatureFlags(),
  });
  const { data: tenants } = useQuery({
    queryKey: platformKeys.tenants(),
    queryFn: () => platformApi.listTenants(),
  });

  // Default the per-tenant flag panel to the first tenant once loaded.
  const activeTenantId = selectedTenantId || tenants?.[0]?.id || '';

  const { data: tenantDetail, isLoading: detailLoading } = useQuery({
    queryKey: platformKeys.tenant(activeTenantId),
    queryFn: () => platformApi.getTenant(activeTenantId),
    enabled: !!activeTenantId,
  });

  const flagMutation = useMutation({
    mutationFn: ({ flagKey, enabled }: { flagKey: string; enabled: boolean }) => platformApi.setTenantFlag(activeTenantId, flagKey, enabled),
    onSuccess: (_r, vars) => {
      enqueueSnackbar(`Flag "${vars.flagKey}" ${vars.enabled ? 'enabled' : 'disabled'}`, { variant: 'success' });
      queryClient.invalidateQueries({ queryKey: platformKeys.tenant(activeTenantId) });
    },
    onError: () => enqueueSnackbar('Failed to update flag', { variant: 'error' }),
  });

  // Count how many tenants each plan tier holds, for the plan cards.
  const tenantsByTier = useMemo(() => {
    const map: Record<string, number> = {};
    for (const t of tenants ?? []) map[t.planTier] = (map[t.planTier] ?? 0) + 1;
    return map;
  }, [tenants]);

  if (plansLoading || flagsLoading || !plans || !flags) {
    return <LoadingState label="Loading plans & flags…" minHeight="60vh" />;
  }

  const formatQuota = (value: number | null, unit: string) => (value == null ? 'Unlimited' : `${fmtNumber(value)} ${unit}`);

  return (
    <Box>
      <PageHeader title="Plans & Feature Flags" subtitle="Plan tiers, quotas, and per-tenant feature entitlements." />

      {/* Plan tier cards */}
      <Grid container spacing={2.5} sx={{ mb: 3 }}>
        {plans.map((plan: Plan) => {
          const accent = PLAN_ACCENT[plan.tier];
          return (
            <Grid size={{ xs: 12, md: 4 }} key={plan.id}>
              <Paper sx={{ p: 3, borderRadius: 3, height: '100%', position: 'relative', overflow: 'hidden' }}>
                <Box sx={{ position: 'absolute', top: 0, left: 0, right: 0, height: 4, bgcolor: accent }} />
                <Stack direction="row" justifyContent="space-between" alignItems="flex-start" sx={{ mb: 1 }}>
                  <Box>
                    <Stack direction="row" spacing={1} alignItems="center">
                      <WorkspacePremium sx={{ color: accent }} />
                      <Typography variant="h6" sx={{ fontWeight: 800 }}>
                        {plan.name}
                      </Typography>
                    </Stack>
                    <Typography variant="h4" sx={{ fontWeight: 800, mt: 1 }}>
                      {plan.priceMonthlyUsd === 0 ? 'Free' : fmtCurrency(plan.priceMonthlyUsd)}
                      {plan.priceMonthlyUsd > 0 && (
                        <Typography component="span" variant="body2" color="text.secondary">
                          /mo
                        </Typography>
                      )}
                    </Typography>
                  </Box>
                  <Chip size="small" label={`${tenantsByTier[plan.tier] ?? 0} tenants`} sx={{ fontWeight: 700 }} />
                </Stack>

                <Divider sx={{ my: 2 }} />

                <Stack spacing={1.25}>
                  {[
                    { label: 'Dispatcher weight', value: `×${plan.weight}` },
                    { label: 'Concurrency cap', value: `${plan.concurrencyCap} jobs` },
                    { label: 'Monthly doc quota', value: formatQuota(plan.monthlyDocQuota, 'docs') },
                    { label: 'Seat quota', value: formatQuota(plan.seatQuota, 'seats') },
                  ].map((row) => (
                    <Stack key={row.label} direction="row" justifyContent="space-between">
                      <Typography variant="body2" color="text.secondary">
                        {row.label}
                      </Typography>
                      <Typography variant="body2" sx={{ fontWeight: 700 }}>
                        {row.value}
                      </Typography>
                    </Stack>
                  ))}
                </Stack>

                <Divider sx={{ my: 2 }} />

                <Typography variant="overline" sx={{ color: 'text.secondary', fontWeight: 700 }}>
                  Included entitlements
                </Typography>
                <Stack spacing={0.75} sx={{ mt: 1 }}>
                  {flags
                    .filter((f) => f.category === 'entitlement')
                    .map((f) => {
                      const included = plan.entitlements.includes(f.key);
                      return (
                        <Stack key={f.key} direction="row" spacing={1} alignItems="center">
                          {included ? (
                            <CheckCircle sx={{ fontSize: 17, color: accent }} />
                          ) : (
                            <RemoveCircleOutline sx={{ fontSize: 17, color: 'text.disabled' }} />
                          )}
                          <Typography variant="body2" sx={{ color: included ? 'text.primary' : 'text.disabled', fontWeight: included ? 600 : 400 }}>
                            {f.label}
                          </Typography>
                        </Stack>
                      );
                    })}
                </Stack>
              </Paper>
            </Grid>
          );
        })}
      </Grid>

      {/* Plan comparison matrix */}
      <Paper sx={{ p: 3, borderRadius: 3, mb: 3, overflowX: 'auto' }}>
        <Typography variant="h6" sx={{ fontWeight: 800, mb: 2 }}>
          Quota Matrix
        </Typography>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell sx={{ fontWeight: 700 }}>Attribute</TableCell>
              {plans.map((p) => (
                <TableCell key={p.id} sx={{ fontWeight: 700 }} align="right">
                  <PlanChip tier={p.tier} />
                </TableCell>
              ))}
            </TableRow>
          </TableHead>
          <TableBody>
            {[
              { label: 'Price / month', get: (p: Plan) => (p.priceMonthlyUsd === 0 ? 'Free' : fmtCurrency(p.priceMonthlyUsd)) },
              { label: 'Dispatcher weight', get: (p: Plan) => `×${p.weight}` },
              { label: 'Concurrency cap', get: (p: Plan) => `${p.concurrencyCap} jobs` },
              { label: 'Monthly docs', get: (p: Plan) => formatQuota(p.monthlyDocQuota, '') },
              { label: 'Seats', get: (p: Plan) => formatQuota(p.seatQuota, '') },
              { label: 'Entitlements', get: (p: Plan) => `${p.entitlements.length}` },
            ].map((row) => (
              <TableRow key={row.label} hover>
                <TableCell sx={{ fontWeight: 600 }}>{row.label}</TableCell>
                {plans.map((p) => (
                  <TableCell key={p.id} align="right">
                    {row.get(p)}
                  </TableCell>
                ))}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Paper>

      {/* Per-tenant feature flags */}
      <Paper sx={{ p: 3, borderRadius: 3 }}>
        <Stack direction={{ xs: 'column', sm: 'row' }} justifyContent="space-between" alignItems={{ sm: 'center' }} spacing={1.5} sx={{ mb: 2 }}>
          <Box>
            <Typography variant="h6" sx={{ fontWeight: 800 }}>
              Per-Tenant Feature Flags
            </Typography>
            <Typography variant="body2" color="text.secondary">
              Override plan entitlements or toggle operational flags for a specific tenant.
            </Typography>
          </Box>
          <TextField size="small" select label="Tenant" value={activeTenantId} onChange={(e) => setSelectedTenantId(e.target.value)} sx={{ minWidth: 240 }}>
            {(tenants ?? []).map((t) => (
              <MenuItem key={t.id} value={t.id}>
                {t.name}
              </MenuItem>
            ))}
          </TextField>
        </Stack>

        {detailLoading || !tenantDetail ? (
          <LoadingState label="Loading tenant flags…" minHeight={200} />
        ) : (
          <Stack divider={<Divider flexItem />}>
            {flags.map((flag) => {
              const enabled = tenantDetail.flags[flag.key] ?? false;
              return (
                <Stack
                  key={flag.key}
                  direction="row"
                  alignItems="center"
                  justifyContent="space-between"
                  sx={{ py: 1.5, px: 1, borderRadius: 2, '&:hover': { bgcolor: mode === 'dark' ? 'rgba(255,255,255,0.02)' : 'rgba(0,0,0,0.01)' } }}
                >
                  <Box sx={{ pr: 2 }}>
                    <Stack direction="row" spacing={1} alignItems="center">
                      <Typography variant="body2" sx={{ fontWeight: 700 }}>
                        {flag.label}
                      </Typography>
                      <Chip
                        size="small"
                        label={flag.category}
                        sx={{ height: 20, fontSize: '0.65rem', fontWeight: 700, textTransform: 'capitalize' }}
                        color={flag.category === 'entitlement' ? 'info' : 'default'}
                        variant="outlined"
                      />
                    </Stack>
                    <Typography variant="caption" color="text.secondary">
                      {flag.description}
                    </Typography>
                  </Box>
                  <Switch checked={enabled} onChange={(e) => flagMutation.mutate({ flagKey: flag.key, enabled: e.target.checked })} disabled={flagMutation.isPending} />
                </Stack>
              );
            })}
          </Stack>
        )}
      </Paper>
    </Box>
  );
}
