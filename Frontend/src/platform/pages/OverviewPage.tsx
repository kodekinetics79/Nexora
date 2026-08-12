import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  Box,
  Chip,
  Grid,
  IconButton,
  Paper,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  ToggleButton,
  ToggleButtonGroup,
  Tooltip,
  Typography,
} from '@mui/material';
import Stack from '../components/Flex';
import {
  Article as DocsIcon,
  AttachMoney as CostIcon,
  CheckCircleOutlined as SuccessIcon,
  ChevronRight as ArrowIcon,
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
import { OVERVIEW_WINDOWS, type HealthStatus, type OverviewWindow, type TenantLifecycle } from '../types';
import PageHeader from '../components/PageHeader';
import StatTile from '../components/StatTile';
import { ErrorState, LoadingState, TilesSkeleton } from '../components/States';
import { fmtCompact, fmtCurrency, fmtDateTime, fmtNumber, fmtPercent } from '../components/format';

const HEALTH_COLOR: Record<HealthStatus, string> = {
  healthy: '#10b981',
  degraded: '#f59e0b',
  down: '#ef4444',
};

// The buckets are REAL plan codes (plus "none" for plan-less tenants), so any
// unknown code needs a deterministic fallback colour.
const PLAN_COLOR: Record<string, string> = {
  free: '#94a3b8',
  pro: '#3b82f6',
  enterprise: '#10b981',
  none: '#64748b',
};
const planColor = (tier: string) => PLAN_COLOR[tier] ?? '#8b5cf6';

// Every lifecycle state a tenant can be in. Provisioning and PastDue are the two an
// operator most needs to see and the two the old overview folded into "not active".
const LIFECYCLE_COLOR: Record<TenantLifecycle, string> = {
  Active: '#10b981',
  Provisioning: '#3b82f6',
  PastDue: '#f59e0b',
  Suspended: '#ef4444',
  Archived: '#94a3b8',
};

/** Minutes → the coarsest unit that still reads honestly. */
const fmtAge = (minutes: number | null): string => {
  if (minutes == null) return '—';
  if (minutes < 60) return `${Math.round(minutes)}m`;
  if (minutes < 1440) return `${(minutes / 60).toFixed(1)}h`;
  return `${(minutes / 1440).toFixed(1)}d`;
};

/**
 * Shown INSTEAD of a chart whose every point is zero.
 *
 * A flat line pinned to the axis is indistinguishable from a broken feed, and it reads as
 * data — a reader sees a rendered chart and believes something was measured. Saying "nothing
 * happened in this window" is both true and less work to understand.
 */
function NoSeriesData({ height, label }: { height: number; label: string }) {
  return (
    <Box
      sx={{
        height,
        mt: 2,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        border: '1px dashed',
        borderColor: 'divider',
        borderRadius: 2.5,
      }}
    >
      <Typography variant="body2" sx={{ color: 'text.secondary', fontWeight: 600 }}>
        {label}
      </Typography>
    </Box>
  );
}

/** One stage of the commercial spine, with the conversion into it when there is one. */
function SpineStage({
  label,
  value,
  caption,
  color,
  first,
}: {
  label: string;
  value: number;
  caption?: string;
  color: string;
  first?: boolean;
}) {
  return (
    <Stack direction="row" alignItems="center" spacing={{ xs: 1, md: 2 }} sx={{ flex: 1, minWidth: 0 }}>
      {!first && <ArrowIcon sx={{ color: 'text.disabled', flexShrink: 0 }} />}
      <Box sx={{ minWidth: 0 }}>
        <Typography variant="overline" sx={{ color: 'text.secondary', fontWeight: 700, letterSpacing: 0.6 }}>
          {label}
        </Typography>
        <Typography variant="h5" sx={{ fontWeight: 800, color, lineHeight: 1.2 }}>
          {fmtNumber(value)}
        </Typography>
        <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block' }}>
          {caption ?? ' '}
        </Typography>
      </Box>
    </Stack>
  );
}

export default function OverviewPage() {
  const { mode, primaryColor } = useAppTheme();
  const navigate = useNavigate();
  const [windowDays, setWindowDays] = useState<OverviewWindow>(14);

  const { data, isLoading, isError, refetch, isFetching } = useQuery({
    queryKey: platformKeys.overview(windowDays),
    queryFn: () => platformApi.getOverview(windowDays),
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
        <Stack direction="row" alignItems="center" spacing={1.5}>
          <ToggleButtonGroup
            size="small"
            exclusive
            value={windowDays}
            onChange={(_, next: OverviewWindow | null) => next && setWindowDays(next)}
            aria-label="Metrics window"
          >
            {OVERVIEW_WINDOWS.map((days) => (
              <ToggleButton key={days} value={days} aria-label={`${days} day window`} sx={{ px: 1.5, fontWeight: 700 }}>
                {days}d
              </ToggleButton>
            ))}
          </ToggleButtonGroup>
          {/* The span is required: MUI's disabled button swallows pointer events, so a Tooltip on a
              bare disabled IconButton never fires — the operator got a control that looked broken
              with no way to find out why. The title now says what is happening, too. */}
          <Tooltip title={isFetching ? 'Refreshing…' : 'Refresh'}>
            <span>
              <IconButton
                onClick={() => refetch()}
                aria-label="Refresh platform overview"
                sx={{ bgcolor: 'action.hover', borderRadius: 2 }}
                disabled={isFetching}
              >
                <RefreshIcon fontSize="small" />
              </IconButton>
            </span>
          </Tooltip>
        </Stack>
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

  const windowLabel = `${data.windowDays} days`;
  const lifecycle = data.tenantsByStatus.filter((b) => b.count > 0);
  const notActive = lifecycle.filter((b) => b.status !== 'Active');
  const hasThroughput = data.throughput.some((d) => d.docs > 0 || d.failures > 0);
  const hasCost = data.costTrend.some((d) => d.costUsd > 0);
  const { commercial } = data;
  const spineIsEmpty =
    commercial.leadsCaptured + commercial.rfqsCaptured + commercial.quotesIssued + commercial.ordersWon === 0;

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
            caption={
              // "5 tenants, 0 active" is the single most important thing a fleet can be, and
              // the old caption said it in a way that read like a rounding note. The
              // non-active states are named.
              notActive.length > 0
                ? `${data.activeTenants} active · ${notActive.map((b) => `${b.count} ${b.status.toLowerCase()}`).join(' · ')}`
                : `${data.activeTenants} active · ${fmtCompact(data.activeUsersFleetWide)} users fleet-wide`
            }
          />
        </Grid>
        <Grid size={{ xs: 12, sm: 6, md: 4, lg: 2.4 }}>
          <StatTile
            label={`Docs Processed (${windowLabel})`}
            value={fmtCompact(data.docsProcessedInWindow)}
            icon={<DocsIcon />}
            color="#3b82f6"
            caption={`${fmtNumber(data.failuresInWindow)} failed · ${fmtCompact(data.docsProcessedMtd)} MTD`}
          />
        </Grid>
        <Grid size={{ xs: 12, sm: 6, md: 4, lg: 2.4 }}>
          <StatTile
            label="Extraction Success"
            value={fmtPercent(data.extractionSuccessRateWindow)}
            icon={<SuccessIcon />}
            color="#10b981"
            caption={
              data.extractionSuccessRateWindow == null
                ? `no jobs finished in ${windowLabel}`
                : `${windowLabel} · ${fmtPercent(data.extractionSuccessRate)} all-time`
            }
          />
        </Grid>
        <Grid size={{ xs: 12, sm: 6, md: 4, lg: 2.4 }}>
          <StatTile
            label="Queue Depth"
            value={fmtNumber(data.queueDepth)}
            icon={<QueueIcon />}
            color="#f59e0b"
            caption={
              // A depth alone cannot distinguish a busy minute from a stalled worker; the age
              // of the oldest waiting job can.
              `${data.inFlight} in-flight · ${data.deadLetter} dead-letter${
                data.oldestPendingMinutes != null ? ` · oldest ${fmtAge(data.oldestPendingMinutes)}` : ''
              }`
            }
          />
        </Grid>
        <Grid size={{ xs: 12, sm: 6, md: 4, lg: 2.4 }}>
          <StatTile
            label="LLM Cost (MTD)"
            value={fmtCurrency(data.llmCostMtdUsd, true)}
            icon={<CostIcon />}
            color="#8b5cf6"
            deltaPct={data.llmCostTrendPct ?? undefined}
            caption={data.llmCostTrendPct == null ? 'no comparable prior period' : undefined}
          />
        </Grid>
      </Grid>

      {/* The commercial spine — what the product is actually for */}
      <Paper sx={{ p: 3, borderRadius: 3, mb: 3 }}>
        <Stack direction="row" alignItems="baseline" spacing={1.5} sx={{ mb: 0.5, flexWrap: 'wrap' }}>
          <Typography variant="h6" sx={{ fontWeight: 800 }}>
            Commercial Spine
          </Typography>
          <Typography variant="caption" color="text.secondary">
            Fleet-wide RFQ-to-order flow · created in the last {windowLabel}
          </Typography>
        </Stack>

        {spineIsEmpty ? (
          <NoSeriesData height={110} label={`No commercial activity anywhere in the fleet in the last ${windowLabel}.`} />
        ) : (
          <>
            <Stack
              direction={{ xs: 'column', md: 'row' }}
              alignItems={{ xs: 'flex-start', md: 'center' }}
              spacing={{ xs: 2, md: 1 }}
              sx={{ mt: 2 }}
            >
              <SpineStage first label="Leads captured" value={commercial.leadsCaptured} color={primaryColor} />
              <SpineStage label="RFQs raised" value={commercial.rfqsCaptured} color="#3b82f6" />
              <SpineStage
                label="Quotes issued"
                value={commercial.quotesIssued}
                color="#8b5cf6"
                // Cohort conversion computed server-side on LINKED records, so it stays true
                // when the quote lands days after the RFQ.
                caption={
                  commercial.rfqsQuotedPct == null
                    ? 'no RFQs to convert'
                    : `${fmtPercent(commercial.rfqsQuotedPct)} of those RFQs quoted`
                }
              />
              <SpineStage
                label="Orders won"
                value={commercial.ordersWon}
                color="#10b981"
                caption={
                  commercial.quotesOrderedPct == null
                    ? 'no quotes to convert'
                    : `${fmtPercent(commercial.quotesOrderedPct)} of those quotes ordered`
                }
              />
            </Stack>

            {commercial.orderValueByCurrency.length > 0 && (
              <Stack direction="row" spacing={1} sx={{ mt: 2.5, flexWrap: 'wrap', gap: 1 }}>
                <Typography variant="caption" sx={{ color: 'text.secondary', alignSelf: 'center', fontWeight: 600 }}>
                  {/* Never blended into one number: the fleet trades in more than one currency
                      and adding the numerals would invent a total nobody could reconcile. */}
                  Order value, by currency:
                </Typography>
                {commercial.orderValueByCurrency.map((v) => (
                  <Chip
                    key={v.currency}
                    size="small"
                    label={`${v.currency} ${new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 }).format(v.amount)} · ${v.orders} order${v.orders === 1 ? '' : 's'}`}
                    sx={{ fontWeight: 700 }}
                  />
                ))}
              </Stack>
            )}
          </>
        )}
      </Paper>

      {/* System health */}
      <Paper sx={{ p: 3, borderRadius: 3, mb: 3 }}>
        <Stack direction="row" alignItems="center" spacing={1.5} sx={{ mb: 2, flexWrap: 'wrap' }}>
          <Typography variant="h6" sx={{ fontWeight: 800 }}>
            System Health
          </Typography>
          {/* A roll-up, because eleven cards of which one is red is not a thing you should
              have to notice by scanning. */}
          <Chip
            size="small"
            label={
              data.health.worst === 'healthy'
                ? `All ${data.health.healthy} services healthy`
                : `${data.health.down} down · ${data.health.degraded} degraded · ${data.health.healthy} healthy`
            }
            sx={{
              fontWeight: 700,
              color: HEALTH_COLOR[data.health.worst],
              bgcolor: `${HEALTH_COLOR[data.health.worst]}1f`,
            }}
          />
        </Stack>
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
              Documents processed vs failures · trailing {windowLabel}
            </Typography>
            {hasThroughput ? (
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
            ) : (
              <NoSeriesData height={270} label={`No documents processed in the last ${windowLabel}.`} />
            )}
          </Paper>
        </Grid>

        <Grid size={{ xs: 12, lg: 4 }}>
          <Paper sx={{ p: 3, borderRadius: 3, height: 360, display: 'flex', flexDirection: 'column' }}>
            <Typography variant="h6" sx={{ fontWeight: 800, mb: 0.5 }}>
              Fleet Composition
            </Typography>
            <Typography variant="caption" color="text.secondary">
              Lifecycle and plan mix · {fmtNumber(data.newTenantsInWindow)} new in {windowLabel}
            </Typography>

            {/* Lifecycle first: which states the fleet is IN outranks which plans it is on. */}
            <Stack direction="row" spacing={1} sx={{ mt: 2, mb: 1.5, flexWrap: 'wrap', gap: 1 }}>
              {lifecycle.length === 0 ? (
                <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                  No tenants yet.
                </Typography>
              ) : (
                lifecycle.map((bucket) => (
                  <Chip
                    key={bucket.status}
                    size="small"
                    label={`${bucket.status} ${bucket.count}`}
                    sx={{
                      fontWeight: 700,
                      color: LIFECYCLE_COLOR[bucket.status],
                      bgcolor: `${LIFECYCLE_COLOR[bucket.status]}1f`,
                    }}
                  />
                ))
              )}
            </Stack>

            {data.tenantsByPlan.length > 0 && (
              <Box sx={{ flex: 1, minHeight: 0 }}>
                <ResponsiveContainer width="100%" height="100%">
                  <BarChart data={data.tenantsByPlan} margin={{ top: 8, right: 8, left: -18, bottom: 0 }}>
                    <CartesianGrid strokeDasharray="3 3" stroke={gridColor} vertical={false} />
                    <XAxis dataKey="tier" stroke={axisColor} fontSize={11} tickLine={false} axisLine={false} tickFormatter={(t: string) => (t ? t[0].toUpperCase() + t.slice(1) : '—')} />
                    <YAxis stroke={axisColor} fontSize={11} tickLine={false} axisLine={false} allowDecimals={false} />
                    <RTooltip contentStyle={tooltipStyle} cursor={{ fill: gridColor }} />
                    <Bar dataKey="count" radius={[6, 6, 0, 0]} barSize={44}>
                      {data.tenantsByPlan.map((entry) => (
                        <Cell key={entry.tier} fill={planColor(entry.tier)} />
                      ))}
                    </Bar>
                  </BarChart>
                </ResponsiveContainer>
              </Box>
            )}
          </Paper>
        </Grid>

        {/* Which tenants the fleet-wide numbers came from. "Global KPIs across all tenants"
            with no way to see a tenant is a dead end on the screen an operator lands on. */}
        <Grid size={{ xs: 12 }}>
          <Paper sx={{ p: 3, borderRadius: 3 }}>
            <Typography variant="h6" sx={{ fontWeight: 800, mb: 0.5 }}>
              Tenant Activity
            </Typography>
            <Typography variant="caption" color="text.secondary">
              Busiest tenants · last {windowLabel} · select a row for the tenant record
            </Typography>
            {data.topTenants.length === 0 ? (
              <NoSeriesData height={120} label="No tenants have been provisioned yet." />
            ) : (
              <Box sx={{ overflowX: 'auto', mt: 1 }}>
                <Table size="small">
                  <TableHead>
                    <TableRow>
                      <TableCell sx={{ fontWeight: 700 }}>Tenant</TableCell>
                      <TableCell sx={{ fontWeight: 700 }}>Status</TableCell>
                      <TableCell sx={{ fontWeight: 700 }}>Plan</TableCell>
                      <TableCell align="right" sx={{ fontWeight: 700 }}>Docs</TableCell>
                      <TableCell align="right" sx={{ fontWeight: 700 }}>Failures</TableCell>
                      <TableCell align="right" sx={{ fontWeight: 700 }}>RFQs</TableCell>
                      <TableCell align="right" sx={{ fontWeight: 700 }}>Quotes</TableCell>
                      <TableCell align="right" sx={{ fontWeight: 700 }}>Orders</TableCell>
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {data.topTenants.map((t) => (
                      <TableRow
                        key={t.tenantId}
                        hover
                        sx={{ cursor: 'pointer' }}
                        onClick={() => navigate(`/platform/tenants/${t.tenantId}`)}
                      >
                        <TableCell>
                          <Typography variant="body2" sx={{ fontWeight: 700 }}>
                            {t.name}
                          </Typography>
                          <Typography variant="caption" sx={{ color: 'text.secondary' }}>
                            {t.slug}
                          </Typography>
                        </TableCell>
                        <TableCell>
                          <Chip
                            size="small"
                            label={t.status}
                            sx={{
                              fontWeight: 700,
                              color: LIFECYCLE_COLOR[t.status],
                              bgcolor: `${LIFECYCLE_COLOR[t.status]}1f`,
                            }}
                          />
                        </TableCell>
                        <TableCell sx={{ textTransform: 'capitalize' }}>{t.plan ?? '—'}</TableCell>
                        <TableCell align="right">{fmtNumber(t.docs)}</TableCell>
                        <TableCell align="right" sx={{ color: t.failures > 0 ? '#ef4444' : undefined, fontWeight: t.failures > 0 ? 700 : undefined }}>
                          {fmtNumber(t.failures)}
                        </TableCell>
                        <TableCell align="right">{fmtNumber(t.rfqs)}</TableCell>
                        <TableCell align="right">{fmtNumber(t.quotes)}</TableCell>
                        <TableCell align="right">{fmtNumber(t.orders)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </Box>
            )}
          </Paper>
        </Grid>

        <Grid size={{ xs: 12 }}>
          <Paper sx={{ p: 3, borderRadius: 3, height: 300 }}>
            <Typography variant="h6" sx={{ fontWeight: 800, mb: 0.5 }}>
              LLM Spend
            </Typography>
            <Typography variant="caption" color="text.secondary">
              Daily gateway cost (USD) · trailing {windowLabel}
            </Typography>
            {hasCost ? (
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
            ) : (
              <NoSeriesData height={210} label={`No metered gateway spend in the last ${windowLabel}.`} />
            )}
          </Paper>
        </Grid>
      </Grid>

      {/* A stale tab should be identifiable as one. */}
      <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block', mt: 2 }}>
        Measured at {fmtDateTime(data.asOfUtc)} · window opened {fmtDateTime(data.windowStartUtc)}
      </Typography>
    </Box>
  );
}
