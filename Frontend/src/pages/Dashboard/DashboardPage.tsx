import { useMemo, useState } from 'react';
import { Collapse } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import dayjs from 'dayjs';
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Divider,
  List,
  ListItemButton,
  ListItemText,
  Paper,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import {
  ArrowForward as DrillDownIcon,
  Refresh as RefreshIcon,
  Schedule as FreshnessIcon,
  Handshake as WonIcon,
  TrendingUp as PipelineIcon,
  Schedule as WaitingIcon,
  MarkEmailReadOutlined as RequestsIcon,
} from '@mui/icons-material';
import dashboardService, { type PipelineStageDTO } from '../../api/services/dashboardService';
import commercialIntelligenceService from '../../api/services/commercialIntelligenceService';
import { useAuth } from '../../context/AuthContext';
import { formatMoney } from '../../utils/currency';
import GrossMarginPanel from './GrossMarginPanel';
import HeroTile from './executive/HeroTile';
import FunnelPanel from './executive/FunnelPanel';
import TrendPanel from './executive/TrendPanel';
import WorkloadPanel from './executive/WorkloadPanel';
import KpiCard, { drillDownRoute } from './executive/KpiCard';

/**
 * The executive view.
 *
 * One screen for the person who reads the numbers rather than works a single deal (owner
 * decision, 2026-09-05). It is laid out in the order a director scans:
 *
 *   1. Four figures at a glance — won, weighted pipeline, waiting on the customer, requests
 *      received — each a pressable key that opens the records behind it, each with its
 *      denominator on its face and a sparkline where a series exists.
 *   2. The funnel and six months of volume against value.
 *   3. Gross margin with its sample, and who is carrying what (manager tier only).
 *   4. The verified Release 01 snapshot — the evidence row — and what needs a decision.
 *
 * Nothing on this screen is computed here. Every figure is a server aggregate with its own scope
 * and freshness, and a figure the server cannot state is shown as "not available" with the
 * server's reason, never as zero and never as a dash. The panels do not fetch through one
 * another: a failed funnel does not blank the margin.
 */
const stageRoute = (stage: PipelineStageDTO): string => {
  if (stage.key === 'leads') return '/procurement/leads';
  if (stage.key === 'accepted') return '/procurement/leads';
  if (stage.key === 'quoted') return '/sales/quotes';
  return '/sales/quotes';
};

const scopeWords = (scope: { scope: string; accountTeamIds?: number[] } | undefined): string | null => {
  if (!scope) return null;
  if (scope.scope === 'tenant') return 'Company-wide';
  if (scope.scope === 'managed_scope') return `Your managed scope — ${scope.accountTeamIds?.length ?? 0} account team(s)`;
  if (scope.scope === 'assigned_accounts') return `Your assigned accounts — ${scope.accountTeamIds?.length ?? 0} account team(s)`;
  return scope.scope;
};

export default function DashboardPage() {
  const { hasPermission, userData } = useAuth();
  const navigate = useNavigate();
  const initialTo = useMemo(() => dayjs().startOf('day').format('YYYY-MM-DD'), []);
  const initialFrom = useMemo(() => dayjs(initialTo).subtract(30, 'day').format('YYYY-MM-DD'), [initialTo]);
  const [from, setFrom] = useState(initialFrom);
  const [to, setTo] = useState(initialTo);
  const invalidWindow = !from || !to || !dayjs(from).isBefore(dayjs(to));
  const managerTier = Boolean(userData.isManager || userData.isSuperAdmin || userData.hasModuleAuthorityByRank);
  const businessUnitId = userData.businessUnitId;

  const release = useQuery({
    queryKey: ['dashboard', 'release-01', from, to],
    queryFn: () => dashboardService.getRelease01({ from, to }),
    refetchInterval: 60_000,
    retry: 1,
    enabled: !invalidWindow,
  });
  const pipeline = useQuery({
    queryKey: ['dashboard', 'pipeline-analytics'],
    queryFn: dashboardService.getPipelineAnalytics,
    refetchInterval: 60_000,
    retry: 1,
  });
  const workload = useQuery({
    queryKey: ['dashboard', 'team-workload'],
    queryFn: dashboardService.getTeamWorkload,
    refetchInterval: 60_000,
    enabled: managerTier,
    retry: (failureCount, error: any) => error?.response?.status !== 403 && failureCount < 2,
  });
  // The tenant's six-month series (requests priced, order value). A separate, older aggregate:
  // it feeds the trend chart and the sparklines only, never a headline figure.
  const series = useQuery({
    queryKey: ['dashboard', 'monthly-series', businessUnitId],
    queryFn: () => dashboardService.getDashboard(businessUnitId as number),
    enabled: typeof businessUnitId === 'number' && businessUnitId > 0,
    staleTime: 5 * 60_000,
    retry: 1,
  });
  const salesToday = useQuery({
    queryKey: ['commercial-intelligence', 'sales-today'],
    queryFn: commercialIntelligenceService.getSalesToday,
    retry: 1,
  });

  const data = release.data;
  const generatedAt = data?.generatedAt ? dayjs(data.generatedAt) : null;
  const funnel = pipeline.data;
  const won = funnel?.funnel.find((s) => s.key === 'won');
  const leadsReceived = data?.kpis.find((k) => k.key === 'leads_received');
  const evidenceKpis = data?.kpis.filter((k) => k.key !== 'leads_received') ?? [];
  const monthly = series.data?.volumeTrend ?? [];
  const valueSeries = monthly.map((m) => m.value);
  const countSeries = monthly.map((m) => m.count);
  const workloadForbidden = (workload.error as any)?.response?.status === 403;
  const funnelForbidden = (pipeline.error as any)?.response?.status === 403;
  const funnelDown = pipeline.isLoading ? 'Loading…' : funnelForbidden ? 'Available to managers and administrators.' : pipeline.isError ? 'The funnel could not be loaded.' : undefined;
  const measurable = evidenceKpis.filter((k) => k.state === 'available');
  const notYet = evidenceKpis.filter((k) => k.state !== 'available');
  const [showNotYet, setShowNotYet] = useState(false);
  const attention = salesToday.data?.attentionItems?.slice(0, 5) ?? [];

  const refreshAll = () => {
    void release.refetch();
    void pipeline.refetch();
    if (managerTier) void workload.refetch();
    void series.refetch();
    void salesToday.refetch();
  };
  const busy = release.isFetching || pipeline.isFetching || series.isFetching;

  return (
    <Box sx={{ maxWidth: 1440, mx: 'auto', p: { xs: 1, sm: 2, md: 3 } }}>
      <Stack
        direction={{ xs: 'column', md: 'row' }}
        spacing={2}
        sx={{ alignItems: { md: 'flex-end' }, justifyContent: 'space-between', mb: 2.5 }}
      >
        <Box sx={{ minWidth: 0 }}>
          <Typography
            variant="h4"
            component="h1"
            sx={{ fontWeight: 900, fontFamily: '"Cambay", "Source Sans 3", sans-serif', letterSpacing: '-0.02em' }}
          >
            Executive view
          </Typography>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mt: 0.5, flexWrap: 'wrap', gap: 0.5 }}>
            {data?.roleScope && <Chip size="small" variant="outlined" label={scopeWords(data.roleScope)} />}
            <Chip size="small" variant="outlined" label={data?.definitionVersion ?? 'release-01'} />
            <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center', color: 'text.secondary' }}>
              <FreshnessIcon sx={{ fontSize: 16 }} />
              <Typography variant="caption">
                {generatedAt?.isValid() ? `Generated ${generatedAt.format('DD MMM YYYY, HH:mm')}` : 'Awaiting a verified snapshot'}
              </Typography>
            </Stack>
          </Stack>
        </Box>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ width: { xs: '100%', md: 'auto' } }}>
          <TextField
            type="date"
            label="From"
            size="small"
            value={from}
            onChange={(event) => setFrom(event.target.value)}
            slotProps={{ inputLabel: { shrink: true } }}
          />
          <TextField
            type="date"
            label="To"
            size="small"
            value={to}
            onChange={(event) => setTo(event.target.value)}
            error={invalidWindow}
            slotProps={{ inputLabel: { shrink: true } }}
          />
          <Button
            variant="outlined"
            startIcon={busy ? <CircularProgress size={16} aria-label="Refreshing" /> : <RefreshIcon />}
            onClick={refreshAll}
            disabled={busy || invalidWindow}
          >
            Refresh
          </Button>
        </Stack>
      </Stack>

      {invalidWindow && <Alert severity="warning" sx={{ mb: 2 }}>The start date must be earlier than the end date.</Alert>}

      {/* 1. At a glance */}
      <Box
        component="section"
        aria-label="At a glance"
        sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, 1fr)', lg: 'repeat(4, 1fr)' }, gap: 2 }}
      >
        <HeroTile
          index={0}
          label="Won"
          icon={<WonIcon />}
          value={pipeline.isLoading ? null : won ? (won.value !== null ? formatMoney(won.value, won.valueCurrency) : won.count.toLocaleString('en-US')) : null}
          basis={won ? `${won.count.toLocaleString('en-US')} won quote${won.count === 1 ? '' : 's'}, all time${won.value === null && won.valueUnavailableReason ? ` · ${won.valueUnavailableReason}` : ''}` : 'Won quotes, all time'}
          unavailableReason={funnelDown}
          series={valueSeries.some((v) => v > 0) ? valueSeries : null}
          seriesLabel="Order value by month"
          definition="Quotes the customer accepted, valued in the tenant's base currency when every one can be converted."
          onOpen={() => navigate('/sales/quotes')}
          openLabel="Quotes"
        />
        <HeroTile
          index={1}
          label="Weighted pipeline"
          icon={<PipelineIcon />}
          value={funnel ? (funnel.weightedForecast !== null ? formatMoney(funnel.weightedForecast, funnel.forecastCurrency) : null) : null}
          basis={funnel ? `${(funnel.awaitingResponseQuotes + funnel.respondedQuotes).toLocaleString('en-US')} open quotes, weighted by stage` : 'Open quotes, weighted by stage'}
          unavailableReason={funnelDown ?? funnel?.forecastUnavailableReason ?? undefined}
          definition="Open quote value multiplied by the likelihood of each stage. Not a forecast of cash."
          onOpen={() => navigate('/sales/quotes')}
          openLabel="Quotes"
        />
        <HeroTile
          index={2}
          label="Waiting on the customer"
          icon={<WaitingIcon />}
          value={funnel ? funnel.awaitingResponseQuotes.toLocaleString('en-US') : null}
          basis={funnel ? (funnel.awaitingResponseValue !== null ? `${formatMoney(funnel.awaitingResponseValue, funnel.forecastCurrency)} on the table` : 'Sent quotes with no answer yet') : 'Sent quotes with no answer yet'}
          unavailableReason={funnelDown}
          definition="Quotes sent and neither accepted, declined nor expired."
          onOpen={() => navigate('/sales/quotes?state=sent')}
          openLabel="Sent quotes"
        />
        <HeroTile
          index={3}
          label="Requests received"
          icon={<RequestsIcon />}
          value={leadsReceived && leadsReceived.state === 'available' && leadsReceived.value !== null ? leadsReceived.value.toLocaleString('en-US') : null}
          basis={`${dayjs(from).format('D MMM')} – ${dayjs(to).format('D MMM YYYY')}${data?.roleScope ? ` · ${scopeWords(data.roleScope)}` : ''}`}
          unavailableReason={release.isLoading ? 'Loading…' : leadsReceived?.insufficientDataReason ?? (release.isError ? 'The verified snapshot is unavailable.' : undefined)}
          series={countSeries.some((v) => v > 0) ? countSeries : null}
          seriesLabel="Requests priced by month"
          definition={leadsReceived?.definition}
          onOpen={() => navigate('/procurement/leads')}
          openLabel="Leads"
        />
      </Box>

      {/* 2. The funnel, and volume against value */}
      <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', lg: '7fr 5fr' }, gap: 2, mt: 2 }}>
        <FunnelPanel
          data={funnel}
          loading={pipeline.isLoading}
          error={pipeline.isError && !funnelForbidden}
          forbidden={funnelForbidden}
          onStage={(stage) => navigate(stageRoute(stage))}
        />
        <TrendPanel
          trend={monthly}
          loading={series.isLoading}
          unavailable={series.isError ? 'The monthly series could not be read for this workspace.' : null}
          currencyCode={funnel?.forecastCurrency ?? null}
        />
      </Box>

      {/* 3. The money and the team — manager tier */}
      {managerTier && !invalidWindow && (
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', lg: workloadForbidden ? '1fr' : '1fr 1fr' }, gap: 2, mt: 2 }}>
          <GrossMarginPanel from={from} to={to} />
          <WorkloadPanel
            data={workload.data}
            loading={workload.isLoading}
            forbidden={workloadForbidden}
            error={workload.isError && !workloadForbidden}
            onOpen={() => navigate('/dashboard/team')}
          />
        </Box>
      )}

      {/* 4. Evidence and decisions */}
      {release.isError && (
        <Alert
          severity="error"
          action={<Button color="inherit" size="small" onClick={() => release.refetch()}>Retry</Button>}
          sx={{ mt: 3 }}
        >
          The verified Release 01 dashboard snapshot is unavailable. No legacy totals are shown in its place.
        </Alert>
      )}
      <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', lg: attention.length ? '8fr 4fr' : '1fr' }, gap: 2, mt: 3 }}>
        <Box component="section" aria-label="Verified performance">
          <Typography variant="h6" sx={{ fontWeight: 900, mb: 1.5 }}>Verified performance</Typography>
          {release.isLoading ? (
            <Box sx={{ minHeight: 160, display: 'grid', placeItems: 'center' }}><CircularProgress aria-label="Loading dashboard" /></Box>
          ) : evidenceKpis.length ? (
            <>
              {measurable.length > 0 ? (
                <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, 1fr)', xl: 'repeat(3, 1fr)' }, gap: 2 }}>
                  {measurable.map((kpi, index) => <KpiCard key={kpi.key} kpi={kpi} index={index} />)}
                </Box>
              ) : (
                <Alert severity="info">Nothing in the verified snapshot can be measured yet for this window and scope.</Alert>
              )}
              {/* The figures the snapshot cannot yet state are one line, not a wall of grey cards:
                  the reason each is unmeasurable is worth reading once, not at every glance. */}
              {notYet.length > 0 && (
                <Box sx={{ mt: 1.5 }}>
                  <Button size="small" onClick={() => setShowNotYet((v) => !v)} sx={{ fontWeight: 700, px: 1 }}>
                    {showNotYet ? 'Hide' : 'Show'} {notYet.length} not yet measurable
                  </Button>
                  <Collapse in={showNotYet}>
                    <Box component="dl" sx={{ mt: 1, mb: 0, display: 'grid', gridTemplateColumns: { xs: '1fr', md: 'auto 1fr' }, columnGap: 2, rowGap: 0.75 }}>
                      {notYet.map((kpi) => (
                        <Box key={kpi.key} sx={{ display: 'contents' }}>
                          <Typography component="dt" variant="body2" sx={{ fontWeight: 700 }}>{kpi.label}</Typography>
                          <Typography component="dd" variant="body2" sx={{ m: 0, color: 'text.secondary' }}>{kpi.insufficientDataReason ?? kpi.definition}</Typography>
                        </Box>
                      ))}
                    </Box>
                  </Collapse>
                </Box>
              )}
            </>
          ) : !release.isError ? (
            <Alert severity="info">No KPI definitions are available for this period and role scope.</Alert>
          ) : null}
        </Box>
        {attention.length > 0 && (
          <Paper component="section" aria-label="Needs a decision" variant="outlined" className="nx-glass" sx={{ p: { xs: 1.5, sm: 2 }, borderRadius: 3, alignSelf: 'start' }}>
            <Typography variant="subtitle1" sx={{ fontWeight: 900 }}>Needs a decision</Typography>
            <List dense disablePadding sx={{ mt: 0.5 }}>
              {attention.map((item) => {
                const route = drillDownRoute(item.recordType.toLowerCase(), item.recordId);
                return (
                  <ListItemButton
                    key={item.id}
                    disabled={!route}
                    onClick={() => route && navigate(route)}
                    sx={{ borderRadius: 2, px: 1 }}
                  >
                    <ListItemText
                      primary={`${item.reference}${item.customerName ? ` · ${item.customerName}` : ''}`}
                      secondary={`${item.reason}${item.dueAt ? ` · due ${dayjs(item.dueAt).format('D MMM')}` : ''}${item.ownerName ? ` · ${item.ownerName}` : ''}`}
                      slotProps={{ primary: { sx: { fontWeight: 700, fontSize: 14 } }, secondary: { sx: { fontSize: 12 } } }}
                    />
                    <DrillDownIcon sx={{ fontSize: 16, color: 'text.secondary' }} />
                  </ListItemButton>
                );
              })}
            </List>
          </Paper>
        )}
      </Box>

      <Divider sx={{ my: 3 }} />
      <Stack direction="row" spacing={1.5} sx={{ flexWrap: 'wrap', gap: 1 }}>
        <Button variant="outlined" endIcon={<DrillDownIcon />} onClick={() => navigate('/analytics/deadlines')} sx={{ fontWeight: 800 }}>
          Deadline board
        </Button>
        {managerTier && (
          <Button variant="outlined" endIcon={<DrillDownIcon />} onClick={() => navigate('/analytics/brand-demand')} sx={{ fontWeight: 800 }}>
            Brand demand
          </Button>
        )}
        <Button variant="outlined" endIcon={<DrillDownIcon />} onClick={() => navigate('/sales/performance')} sx={{ fontWeight: 800 }}>
          Performance by rep
        </Button>
        {hasPermission('Leads') && (
          <Button variant="outlined" endIcon={<DrillDownIcon />} onClick={() => navigate('/procurement/extraction/review')} sx={{ fontWeight: 800 }}>
            Extraction review queue
          </Button>
        )}
      </Stack>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1.5 }}>
        Nexora does not publish an extraction accuracy figure. Accuracy is measured from your reviewers' own corrections and
        published per field once enough approved documents exist for your tenant. Until then every extraction is reviewed by your
        team.
      </Typography>
    </Box>
  );
}
