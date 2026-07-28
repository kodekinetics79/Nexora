import { useQuery } from '@tanstack/react-query';
import { Box, Grid, IconButton, Paper, Tooltip, Typography } from '@mui/material';
import Stack from '../components/Flex';
import {
  Article as DocsIcon,
  AttachMoney as CostIcon,
  CheckCircleOutlined as SuccessIcon,
  Layers as QueueIcon,
  Refresh as RefreshIcon,
  Workspaces as TenantsIcon,
} from '@mui/icons-material';
import {
  Area,
  AreaChart,
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  Line,
  LineChart,
  ResponsiveContainer,
  Tooltip as RTooltip,
  XAxis,
  YAxis,
} from 'recharts';
import { useAppTheme } from '../../context/ThemeContext';
import { platformApi } from '../api/client';
import { platformKeys } from '../api/queryKeys';
import type { HealthStatus, PlanTier } from '../types';
import PageHeader from '../components/PageHeader';
import StatTile from '../components/StatTile';
import { ErrorState, LoadingState, TilesSkeleton } from '../components/States';
import { fmtCompact, fmtCurrency, fmtNumber, fmtPercent } from '../components/format';

const HEALTH_COLOR: Record<HealthStatus, string> = {
  healthy: '#10b981',
  degraded: '#f59e0b',
  down: '#ef4444',
};

const PLAN_COLOR: Record<PlanTier, string> = {
  free: '#94a3b8',
  pro: '#3b82f6',
  enterprise: '#10b981',
  unassigned: '#64748b',
};

export default function OverviewPage() {
  const { mode, primaryColor } = useAppTheme();
  const { data, isLoading, isError, refetch, isFetching } = useQuery({
    queryKey: platformKeys.overview(),
    queryFn: () => platformApi.getOverview(),
  });

  const axisColor = mode === 'dark' ? '#64748b' : '#94a3b8';
  const gridColor = mode === 'dark' ? 'rgba(255,255,255,0.06)' : 'rgba(0,0,0,0.05)';
  const tooltipStyle = {
    backgroundColor: mode === 'dark' ? '#0f172a' : '#fff',
    border: '1px solid',
    borderColor: mode === 'dark' ? '#334155' : '#e2e8f0',
    borderRadius: 10,
    fontSize: 12,
  } as const;

  const header = (
    <PageHeader
      title="Platform Overview"
      subtitle="System health and global KPIs across all tenants."
      actions={
        <Tooltip title="Refresh">
          <IconButton onClick={() => refetch()} sx={{ bgcolor: 'action.hover', borderRadius: 2 }} disabled={isFetching}>
            <RefreshIcon fontSize="small" />
          </IconButton>
        </Tooltip>
      }
    />
  );

  if (isLoading) {
    return (
      <Box>
        {header}
        <TilesSkeleton count={5} />
        <Box sx={{ mt: 3 }}>
          <LoadingState label="Loading platform metrics…" />
        </Box>
      </Box>
    );
  }

  if (isError || !data) {
    return (
      <Box>
        {header}
        <ErrorState message="The platform metrics service did not respond." onRetry={() => refetch()} />
      </Box>
    );
  }

  return (
    <Box>
      {header}

      {/* KPI tiles */}
      <Grid container spacing={2.5} sx={{ mb: 3 }}>
        <Grid size={{ xs: 12, sm: 6, md: 4, lg: 2.4 }}>
          <StatTile
            label="Tenants"
            value={fmtNumber(data.tenantCount)}
            icon={<TenantsIcon />}
            color={primaryColor}
            caption={`${data.activeTenants} active`}
          />
        </Grid>
        <Grid size={{ xs: 12, sm: 6, md: 4, lg: 2.4 }}>
          <StatTile
            label="Docs Processed (MTD)"
            value={fmtCompact(data.docsProcessedMtd)}
            icon={<DocsIcon />}
            color="#3b82f6"
            caption="successful extraction jobs"
          />
        </Grid>
        <Grid size={{ xs: 12, sm: 6, md: 4, lg: 2.4 }}>
          <StatTile
            label="Extraction Success"
            value={fmtPercent(data.extractionSuccessRate)}
            icon={<SuccessIcon />}
            color="#10b981"
            caption="all terminal jobs"
          />
        </Grid>
        <Grid size={{ xs: 12, sm: 6, md: 4, lg: 2.4 }}>
          <StatTile
            label="Queue Depth"
            value={fmtNumber(data.queueDepth)}
            icon={<QueueIcon />}
            color="#f59e0b"
            caption={`${data.inFlight} in-flight · ${data.deadLetter} dead-letter`}
          />
        </Grid>
        <Grid size={{ xs: 12, sm: 6, md: 4, lg: 2.4 }}>
          <StatTile
            label="LLM Cost (MTD)"
            value={fmtCurrency(data.llmCostMtdUsd, true)}
            icon={<CostIcon />}
            color="#8b5cf6"
            deltaPct={data.llmCostTrendPct ?? undefined}
          />
        </Grid>
      </Grid>

      {/* System health */}
      <Paper sx={{ p: 3, borderRadius: 3, mb: 3 }}>
        <Typography variant="h6" sx={{ fontWeight: 800, mb: 2 }}>
          System Health
        </Typography>
        <Grid container spacing={2}>
          {data.services.map((svc) => (
            <Grid size={{ xs: 12, sm: 6, md: 4, lg: 2 }} key={svc.key}>
              <Box
                sx={{
                  p: 2,
                  borderRadius: 2.5,
                  height: '100%',
                  border: '1px solid',
                  borderColor: 'divider',
                  bgcolor: mode === 'dark' ? 'rgba(255,255,255,0.02)' : 'rgba(0,0,0,0.01)',
                  position: 'relative',
                  overflow: 'hidden',
                }}
              >
                <Box sx={{ position: 'absolute', top: 0, left: 0, width: 4, height: '100%', bgcolor: HEALTH_COLOR[svc.status] }} />
                <Stack direction="row" alignItems="center" spacing={1} sx={{ mb: 0.5 }}>
                  <Box
                    sx={{
                      width: 8,
                      height: 8,
                      borderRadius: '50%',
                      bgcolor: HEALTH_COLOR[svc.status],
                      boxShadow: `0 0 0 3px ${HEALTH_COLOR[svc.status]}33`,
                    }}
                  />
                  <Typography variant="subtitle2" sx={{ fontWeight: 700 }}>
                    {svc.name}
                  </Typography>
                </Stack>
                <Typography variant="caption" sx={{ display: 'block', textTransform: 'capitalize', fontWeight: 700, color: HEALTH_COLOR[svc.status] }}>
                  {svc.status}
                </Typography>
                <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block', mt: 0.5 }}>
                  {svc.detail}
                </Typography>
              </Box>
            </Grid>
          ))}
        </Grid>
      </Paper>

      {/* Charts */}
      <Grid container spacing={2.5}>
        <Grid size={{ xs: 12, lg: 8 }}>
          <Paper sx={{ p: 3, borderRadius: 3, height: 360 }}>
            <Typography variant="h6" sx={{ fontWeight: 800, mb: 0.5 }}>
              Document Throughput
            </Typography>
            <Typography variant="caption" color="text.secondary">
              Documents processed vs failures · trailing 14 days
            </Typography>
            <Box sx={{ height: 270, mt: 2 }}>
              <ResponsiveContainer width="100%" height="100%">
                <AreaChart data={data.throughput} margin={{ top: 8, right: 8, left: -12, bottom: 0 }}>
                  <defs>
                    <linearGradient id="ovDocs" x1="0" y1="0" x2="0" y2="1">
                      <stop offset="5%" stopColor={primaryColor} stopOpacity={0.35} />
                      <stop offset="95%" stopColor={primaryColor} stopOpacity={0} />
                    </linearGradient>
                    <linearGradient id="ovFail" x1="0" y1="0" x2="0" y2="1">
                      <stop offset="5%" stopColor="#ef4444" stopOpacity={0.3} />
                      <stop offset="95%" stopColor="#ef4444" stopOpacity={0} />
                    </linearGradient>
                  </defs>
                  <CartesianGrid strokeDasharray="3 3" stroke={gridColor} vertical={false} />
                  <XAxis dataKey="date" stroke={axisColor} fontSize={11} tickLine={false} axisLine={false} tickFormatter={(d: string) => d.slice(5)} />
                  <YAxis stroke={axisColor} fontSize={11} tickLine={false} axisLine={false} />
                  <RTooltip contentStyle={tooltipStyle} />
                  <Area type="monotone" dataKey="docs" name="Docs" stroke={primaryColor} strokeWidth={2.5} fill="url(#ovDocs)" />
                  <Area type="monotone" dataKey="failures" name="Failures" stroke="#ef4444" strokeWidth={2} fill="url(#ovFail)" />
                </AreaChart>
              </ResponsiveContainer>
            </Box>
          </Paper>
        </Grid>

        <Grid size={{ xs: 12, lg: 4 }}>
          <Paper sx={{ p: 3, borderRadius: 3, height: 360 }}>
            <Typography variant="h6" sx={{ fontWeight: 800, mb: 0.5 }}>
              Tenants by Plan
            </Typography>
            <Typography variant="caption" color="text.secondary">
              Distribution across plan tiers
            </Typography>
            <Box sx={{ height: 240, mt: 2 }}>
              <ResponsiveContainer width="100%" height="100%">
                <BarChart data={data.tenantsByPlan} margin={{ top: 8, right: 8, left: -18, bottom: 0 }}>
                  <CartesianGrid strokeDasharray="3 3" stroke={gridColor} vertical={false} />
                  <XAxis dataKey="tier" stroke={axisColor} fontSize={11} tickLine={false} axisLine={false} tickFormatter={(t: string) => t[0].toUpperCase() + t.slice(1)} />
                  <YAxis stroke={axisColor} fontSize={11} tickLine={false} axisLine={false} allowDecimals={false} />
                  <RTooltip contentStyle={tooltipStyle} cursor={{ fill: gridColor }} />
                  <Bar dataKey="count" radius={[6, 6, 0, 0]} barSize={44}>
                    {data.tenantsByPlan.map((entry) => (
                      <Cell key={entry.tier} fill={PLAN_COLOR[entry.tier]} />
                    ))}
                  </Bar>
                </BarChart>
              </ResponsiveContainer>
            </Box>
            <Stack direction="row" spacing={2} justifyContent="center" sx={{ mt: 1 }}>
              {data.tenantsByPlan.map((e) => (
                <Stack key={e.tier} direction="row" alignItems="center" spacing={0.5}>
                  <Box sx={{ width: 9, height: 9, borderRadius: '50%', bgcolor: PLAN_COLOR[e.tier] }} />
                  <Typography variant="caption" sx={{ fontWeight: 600, textTransform: 'capitalize' }}>
                    {e.tier}
                  </Typography>
                </Stack>
              ))}
            </Stack>
          </Paper>
        </Grid>

        <Grid size={{ xs: 12 }}>
          <Paper sx={{ p: 3, borderRadius: 3, height: 300 }}>
            <Typography variant="h6" sx={{ fontWeight: 800, mb: 0.5 }}>
              LLM Spend
            </Typography>
            <Typography variant="caption" color="text.secondary">
              Daily gateway cost (USD) · trailing 14 days
            </Typography>
            <Box sx={{ height: 210, mt: 2 }}>
              <ResponsiveContainer width="100%" height="100%">
                <LineChart data={data.costTrend} margin={{ top: 8, right: 8, left: -8, bottom: 0 }}>
                  <CartesianGrid strokeDasharray="3 3" stroke={gridColor} vertical={false} />
                  <XAxis dataKey="date" stroke={axisColor} fontSize={11} tickLine={false} axisLine={false} tickFormatter={(d: string) => d.slice(5)} />
                  <YAxis stroke={axisColor} fontSize={11} tickLine={false} axisLine={false} tickFormatter={(v: number) => `$${v}`} />
                  <RTooltip contentStyle={tooltipStyle} formatter={(v) => [fmtCurrency(Number(v)), 'Cost'] as [string, string]} />
                  <Line type="monotone" dataKey="costUsd" stroke="#8b5cf6" strokeWidth={2.5} dot={false} activeDot={{ r: 5 }} />
                </LineChart>
              </ResponsiveContainer>
            </Box>
          </Paper>
        </Grid>
      </Grid>
    </Box>
  );
}
