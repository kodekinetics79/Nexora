import type { ReactNode } from 'react';
import Stack from '../components/Flex';
import { useNavigate, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Avatar,
  Box,
  Button,
  Chip,
  Divider,
  Grid,
  LinearProgress,
  Paper,
  Switch,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Tooltip,
  Typography,
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  Bolt as QueueIcon,
  Group as UsersIcon,
  ReceiptLong as AuditIcon,
  Speed as HealthIcon,
  Toll as UsageIcon,
  Tune as FlagsIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import { useAppTheme } from '../../context/ThemeContext';
import { platformApi } from '../api/client';
import { platformKeys } from '../api/queryKeys';
import PageHeader from '../components/PageHeader';
import { HealthChip, PlanChip, SoftChip, TenantStatusChip } from '../components/StatusChip';
import { ErrorState, LoadingState } from '../components/States';
import { fmtCurrency, fmtDate, fmtDateTime, fmtNumber, fmtPercent, fmtRelative } from '../components/format';
import type { FeatureFlag } from '../types';

function SectionCard({ title, icon, children, action }: { title: string; icon: ReactNode; children: ReactNode; action?: ReactNode }) {
  return (
    <Paper sx={{ p: 3, borderRadius: 3, height: '100%' }}>
      <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 2 }}>
        <Stack direction="row" alignItems="center" spacing={1}>
          <Box sx={{ color: 'primary.main', display: 'flex' }}>{icon}</Box>
          <Typography variant="h6" sx={{ fontWeight: 800 }}>
            {title}
          </Typography>
        </Stack>
        {action}
      </Stack>
      {children}
    </Paper>
  );
}

export default function TenantDetailPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { mode } = useAppTheme();

  const { data: tenant, isLoading, isError, refetch } = useQuery({
    queryKey: platformKeys.tenant(id),
    queryFn: () => platformApi.getTenant(id),
    enabled: !!id,
  });

  const { data: flags } = useQuery({
    queryKey: platformKeys.flags(),
    queryFn: () => platformApi.listFeatureFlags(),
  });

  const flagMutation = useMutation({
    mutationFn: ({ flagKey, enabled }: { flagKey: string; enabled: boolean }) => platformApi.setTenantFlag(id, flagKey, enabled),
    onSuccess: (_res, vars) => {
      enqueueSnackbar(`Flag "${vars.flagKey}" ${vars.enabled ? 'enabled' : 'disabled'}`, { variant: 'success' });
      queryClient.invalidateQueries({ queryKey: platformKeys.tenant(id) });
    },
    onError: () => enqueueSnackbar('Failed to update flag', { variant: 'error' }),
  });

  if (isLoading) return <LoadingState label="Loading tenant…" minHeight="60vh" />;
  if (isError || !tenant) {
    return (
      <Box>
        <Button startIcon={<BackIcon />} onClick={() => navigate('/platform/tenants')} sx={{ mb: 2 }}>
          Back to tenants
        </Button>
        <ErrorState message="This tenant could not be loaded." onRetry={() => refetch()} />
      </Box>
    );
  }

  const q = tenant.queue;

  return (
    <Box>
      <Button startIcon={<BackIcon />} onClick={() => navigate('/platform/tenants')} sx={{ mb: 1.5 }} color="inherit">
        Tenants
      </Button>

      <PageHeader
        title={tenant.name}
        subtitle={`${tenant.slug} · ${tenant.region} · created ${fmtDate(tenant.createdAt)}`}
        actions={
          <Stack direction="row" spacing={1} alignItems="center">
            <PlanChip tier={tenant.planTier} />
            <TenantStatusChip status={tenant.status} />
            <HealthChip status={tenant.pipelineHealth} />
          </Stack>
        }
      />

      <Grid container spacing={2.5}>
        {/* Usage meters */}
        <Grid size={{ xs: 12, lg: 8 }}>
          <SectionCard title="Usage & Entitlements" icon={<UsageIcon />}>
            <Grid container spacing={2}>
              {tenant.usageMeters.map((m) => {
                const pct = m.limit ? Math.min((m.used / m.limit) * 100, 100) : null;
                return (
                  <Grid size={{ xs: 12, sm: 6 }} key={m.metric}>
                    <Box sx={{ p: 2, borderRadius: 2.5, border: '1px solid', borderColor: 'divider', bgcolor: mode === 'dark' ? 'rgba(255,255,255,0.02)' : 'rgba(0,0,0,0.01)' }}>
                      <Stack direction="row" justifyContent="space-between" alignItems="baseline">
                        <Typography variant="caption" sx={{ fontWeight: 700, color: 'text.secondary', textTransform: 'uppercase', letterSpacing: 0.4 }}>
                          {m.label}
                        </Typography>
                        <Typography variant="caption" color="text.secondary">
                          {m.period}
                        </Typography>
                      </Stack>
                      <Typography variant="h5" sx={{ fontWeight: 800, mt: 0.5 }}>
                        {m.unit === 'USD' ? fmtCurrency(m.used) : fmtNumber(m.used)}
                        <Typography component="span" variant="body2" color="text.secondary" sx={{ ml: 0.5 }}>
                          {m.limit ? ` / ${m.unit === 'USD' ? fmtCurrency(m.limit) : fmtNumber(m.limit)}` : m.unit === 'USD' ? '' : ` ${m.unit}`}
                          {m.limit == null && m.metric !== 'llm_cost' ? ' · unlimited' : ''}
                        </Typography>
                      </Typography>
                      {pct != null && (
                        <LinearProgress variant="determinate" value={pct} color={pct > 90 ? 'error' : pct > 75 ? 'warning' : 'primary'} sx={{ height: 6, borderRadius: 3, mt: 1 }} />
                      )}
                    </Box>
                  </Grid>
                );
              })}
            </Grid>
          </SectionCard>
        </Grid>

        {/* Pipeline health */}
        <Grid size={{ xs: 12, lg: 4 }}>
          <SectionCard title="Pipeline Health" icon={<HealthIcon />}>
            <Stack spacing={1.5}>
              {[
                { label: 'Queue depth', value: fmtNumber(q.queueDepth), icon: <QueueIcon fontSize="small" /> },
                { label: 'In-flight', value: fmtNumber(q.inFlight) },
                { label: 'Dead-letter', value: fmtNumber(q.deadLetter), danger: q.deadLetter > 0 },
                { label: 'Processed 24h', value: fmtNumber(q.processedLast24h) },
                { label: 'Avg latency', value: `${(q.avgLatencyMs / 1000).toFixed(1)}s` },
                { label: 'Success rate', value: fmtPercent(q.successRate) },
              ].map((row) => (
                <Stack key={row.label} direction="row" justifyContent="space-between" alignItems="center">
                  <Typography variant="body2" color="text.secondary">
                    {row.label}
                  </Typography>
                  <Typography variant="body2" sx={{ fontWeight: 800, color: row.danger ? 'error.main' : 'text.primary' }}>
                    {row.value}
                  </Typography>
                </Stack>
              ))}
              <Divider />
              <Button variant="outlined" size="small" onClick={() => navigate(`/platform/pipeline?tenant=${tenant.id}`)} sx={{ fontWeight: 700 }}>
                Open in pipeline
              </Button>
            </Stack>
          </SectionCard>
        </Grid>

        {/* Feature flags */}
        <Grid size={{ xs: 12, lg: 6 }}>
          <SectionCard title="Feature Flags" icon={<FlagsIcon />}>
            <Stack divider={<Divider flexItem />} spacing={0}>
              {(flags ?? []).map((flag: FeatureFlag) => {
                const enabled = tenant.flags[flag.key] ?? false;
                return (
                  <Stack key={flag.key} direction="row" alignItems="center" justifyContent="space-between" sx={{ py: 1.25 }}>
                    <Box sx={{ pr: 2 }}>
                      <Stack direction="row" spacing={1} alignItems="center">
                        <Typography variant="body2" sx={{ fontWeight: 700 }}>
                          {flag.label}
                        </Typography>
                        <SoftChip label={flag.category} tone={flag.category === 'entitlement' ? 'info' : 'neutral'} dot={false} />
                      </Stack>
                      <Typography variant="caption" color="text.secondary">
                        {flag.description}
                      </Typography>
                    </Box>
                    <Switch
                      checked={enabled}
                      onChange={(e) => flagMutation.mutate({ flagKey: flag.key, enabled: e.target.checked })}
                      disabled={flagMutation.isPending}
                    />
                  </Stack>
                );
              })}
            </Stack>
          </SectionCard>
        </Grid>

        {/* Users & seats */}
        <Grid size={{ xs: 12, lg: 6 }}>
          <SectionCard
            title="Users & Seats"
            icon={<UsersIcon />}
            action={
              <Chip
                size="small"
                label={`${tenant.usage.seatsUsed}${tenant.usage.seatQuota ? ` / ${tenant.usage.seatQuota}` : ''} seats`}
                sx={{ fontWeight: 700 }}
              />
            }
          >
            <Box sx={{ maxHeight: 320, overflow: 'auto' }}>
              <Table size="small" stickyHeader>
                <TableHead>
                  <TableRow>
                    <TableCell sx={{ fontWeight: 700 }}>User</TableCell>
                    <TableCell sx={{ fontWeight: 700 }}>Role</TableCell>
                    <TableCell sx={{ fontWeight: 700 }}>Status</TableCell>
                    <TableCell sx={{ fontWeight: 700 }} align="right">
                      Last active
                    </TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {tenant.users.map((u) => (
                    <TableRow key={u.id} hover>
                      <TableCell>
                        <Stack direction="row" spacing={1.25} alignItems="center">
                          <Avatar sx={{ width: 28, height: 28, fontSize: 12, bgcolor: 'primary.main' }}>
                            {u.name.split(' ').map((p) => p[0]).slice(0, 2).join('')}
                          </Avatar>
                          <Box sx={{ lineHeight: 1.1 }}>
                            <Typography variant="body2" sx={{ fontWeight: 600 }}>
                              {u.name}
                            </Typography>
                            <Typography variant="caption" color="text.secondary">
                              {u.email}
                            </Typography>
                          </Box>
                        </Stack>
                      </TableCell>
                      <TableCell sx={{ textTransform: 'capitalize' }}>{u.role}</TableCell>
                      <TableCell>
                        <SoftChip
                          label={u.status}
                          tone={u.status === 'active' ? 'success' : u.status === 'invited' ? 'warning' : 'neutral'}
                          dot={false}
                        />
                      </TableCell>
                      <TableCell align="right">
                        <Typography variant="caption" color="text.secondary">
                          {fmtRelative(u.lastActiveAt)}
                        </Typography>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </Box>
          </SectionCard>
        </Grid>

        {/* Recent audit */}
        <Grid size={{ xs: 12 }}>
          <SectionCard
            title="Recent Audit"
            icon={<AuditIcon />}
            action={
              <Tooltip title="View full audit log filtered to this tenant">
                <Button size="small" onClick={() => navigate(`/platform/audit?tenant=${tenant.id}`)} sx={{ fontWeight: 700 }}>
                  View all
                </Button>
              </Tooltip>
            }
          >
            {tenant.recentAudit.length === 0 ? (
              <Typography variant="body2" color="text.secondary" sx={{ py: 2, textAlign: 'center' }}>
                No audit activity recorded for this tenant.
              </Typography>
            ) : (
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell sx={{ fontWeight: 700 }}>When</TableCell>
                    <TableCell sx={{ fontWeight: 700 }}>Actor</TableCell>
                    <TableCell sx={{ fontWeight: 700 }}>Action</TableCell>
                    <TableCell sx={{ fontWeight: 700 }}>Result</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {tenant.recentAudit.map((a) => (
                    <TableRow key={a.id} hover>
                      <TableCell>
                        <Typography variant="caption" color="text.secondary">
                          {fmtDateTime(a.timestamp)}
                        </Typography>
                      </TableCell>
                      <TableCell>{a.actor}</TableCell>
                      <TableCell>
                        <Box component="code" sx={{ fontSize: 12, fontWeight: 700 }}>
                          {a.action}
                        </Box>
                      </TableCell>
                      <TableCell>
                        <SoftChip label={a.result} tone={a.result === 'success' ? 'success' : 'error'} dot={false} />
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </SectionCard>
        </Grid>
      </Grid>
    </Box>
  );
}
