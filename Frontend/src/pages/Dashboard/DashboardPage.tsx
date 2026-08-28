import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useLocation, useNavigate } from 'react-router-dom';
import dayjs from 'dayjs';
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogContent,
  DialogTitle,
  Divider,
  IconButton,
  List,
  ListItem,
  ListItemButton,
  ListItemText,
  Paper,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import {
  ArrowForward as DrillDownIcon,
  Refresh as RefreshIcon,
  Schedule as FreshnessIcon,
  Close as CloseIcon,
} from '@mui/icons-material';
import dashboardService, {
  type Release01KpiDTO,
  type Release01KpiUnit,
} from '../../api/services/dashboardService';
import commercialIntelligenceService from '../../api/services/commercialIntelligenceService';
import GrossMarginPanel from './GrossMarginPanel';
import { useAuth } from '../../context/AuthContext';

const formatValue = (kpi: Release01KpiDTO): string => {
  if (kpi.state === 'insufficient_data' || kpi.value === null) {
    return 'Insufficient data';
  }

  if (kpi.unit === 'percentage') return `${kpi.value.toLocaleString('en-US', { maximumFractionDigits: 1 })}%`;
  const formats: Record<Exclude<Release01KpiUnit, 'percentage'>, Intl.NumberFormatOptions> = {
    count: { maximumFractionDigits: 0 },
    currency: { maximumFractionDigits: 2 },
    hours: { maximumFractionDigits: 1 },
    score: { maximumFractionDigits: 1 },
    weighted_work: { maximumFractionDigits: 1 },
  };
  const formatted = new Intl.NumberFormat('en-US', formats[kpi.unit]).format(kpi.value);
  return kpi.unit === 'hours' ? `${formatted} h` : formatted;
};

const drillDownRoute = (recordType: string, recordId: number): string | null => {
  if (recordType === 'lead') return `/procurement/leads/view/${recordId}`;
  if (recordType === 'rfq') return `/procurement/rfqs/view/${recordId}`;
  if (recordType === 'quote') return `/sales/quotes/view/${recordId}`;
  return null;
};

const KpiCard = ({ kpi }: { kpi: Release01KpiDTO }) => {
  const navigate = useNavigate();
  const [drillDownOpen, setDrillDownOpen] = useState(false);
  const drillDownRecords = kpi.drillDownIdentifiers;
  const canDrillDown = kpi.state === 'available' && drillDownRecords.length > 0;

  return (
    <Paper
      component="article"
      variant="outlined"
      sx={{ p: 2, minHeight: 172, borderRadius: 2, display: 'flex', flexDirection: 'column' }}
    >
      <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start', justifyContent: 'space-between' }}>
        <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>
          {kpi.label}
        </Typography>
        <Chip
          size="small"
          label={kpi.state === 'available' ? 'Available' : 'Insufficient data'}
          color={kpi.state === 'available' ? 'success' : 'default'}
          variant="outlined"
        />
      </Stack>
      <Typography
        variant={kpi.state === 'available' ? 'h4' : 'h6'}
        sx={{ mt: 1.5, fontWeight: 900, color: kpi.state === 'available' ? 'text.primary' : 'text.secondary' }}
      >
        {formatValue(kpi)}
      </Typography>
      <Tooltip title={kpi.definition} placement="top-start">
        <Typography
          variant="body2"
          sx={{ mt: 1, color: 'text.secondary', display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden' }}
        >
          {kpi.definition}
        </Typography>
      </Tooltip>
      {kpi.state === 'insufficient_data' && kpi.insufficientDataReason && (
        <Typography variant="caption" sx={{ mt: 1, color: 'text.secondary' }}>
          {kpi.insufficientDataReason}
        </Typography>
      )}
      <Box sx={{ flexGrow: 1 }} />
      {canDrillDown && (
        <Button
          size="small"
          endIcon={<DrillDownIcon />}
          onClick={() => setDrillDownOpen(true)}
          sx={{ alignSelf: 'flex-start', mt: 1, px: 0 }}
        >
          View {kpi.drillDownIdentifiers.length} record{kpi.drillDownIdentifiers.length === 1 ? '' : 's'}
        </Button>
      )}
      <Dialog open={drillDownOpen} onClose={() => setDrillDownOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
          {kpi.label} records
          <IconButton aria-label="Close drill-down" onClick={() => setDrillDownOpen(false)}>
            <CloseIcon />
          </IconButton>
        </DialogTitle>
        <DialogContent dividers>
          <List disablePadding>
            {drillDownRecords.map((record) => {
              const route = drillDownRoute(record.recordType.toLowerCase(), record.recordId);
              const content = (
                <ListItemText
                  primary={record.nexoraSerial}
                  secondary={`${record.recordType.toUpperCase()} #${record.recordId}${record.classification ? ` | ${record.classification}` : ''}`}
                />
              );
              return route ? (
                <ListItemButton key={`${record.recordType}-${record.recordId}`} onClick={() => navigate(route)}>
                  {content}<DrillDownIcon />
                </ListItemButton>
              ) : (
                <ListItem key={`${record.recordType}-${record.recordId}`}>
                  {content}
                  <Chip size="small" label="No detail route" variant="outlined" />
                </ListItem>
              );
            })}
          </List>
        </DialogContent>
      </Dialog>
    </Paper>
  );
};

export default function DashboardPage() {
  const { hasPermission, userData } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const isExecutiveToday = location.pathname === '/executive/today';
  const initialTo = useMemo(() => dayjs().startOf('day').format('YYYY-MM-DD'), []);
  const initialFrom = useMemo(() => dayjs(initialTo).subtract(30, 'day').format('YYYY-MM-DD'), [initialTo]);
  const [from, setFrom] = useState(initialFrom);
  const [to, setTo] = useState(initialTo);
  const invalidWindow = !from || !to || !dayjs(from).isBefore(dayjs(to));
  const canViewManagementAnalytics = Boolean(
    userData.isManager || userData.isSuperAdmin || userData.hasModuleAuthorityByRank,
  );

  const dashboard = useQuery({
    queryKey: ['dashboard', 'release-01', from, to],
    queryFn: () => dashboardService.getRelease01({ from, to }),
    refetchInterval: 60_000,
    retry: 1,
    enabled: !invalidWindow,
  });
  const salesToday = useQuery({
    queryKey: ['commercial-intelligence', 'sales-today'],
    queryFn: commercialIntelligenceService.getSalesToday,
    retry: 1,
  });

  const data = dashboard.data;
  const generatedAt = data?.generatedAt ? dayjs(data.generatedAt) : null;
  return (
    <Box sx={{ maxWidth: 1440, mx: 'auto', p: { xs: 1, sm: 2, md: 3 } }}>
      <Stack
        direction={{ xs: 'column', md: 'row' }}
        spacing={2}
        sx={{ alignItems: { md: 'flex-end' }, justifyContent: 'space-between', mb: 2.5 }}
      >
        <Box>
          <Typography variant="h4" sx={{ fontWeight: 900 }}>{isExecutiveToday ? 'Executive RFQ-to-Revenue' : 'Dashboard'}</Typography>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mt: 0.5, flexWrap: 'wrap' }}>
            <Chip size="small" label={data?.definitionVersion ?? 'release-01'} variant="outlined" />
            {data?.roleScope && (
              // The tier, in words. A raw 'managed_scope' on a board tile tells a director
              // nothing about whose numbers they are reading.
              <Chip
                size="small"
                variant="outlined"
                label={
                  data.roleScope.scope === 'tenant' ? 'Company-wide'
                    : data.roleScope.scope === 'managed_scope'
                      ? `Your managed scope — ${data.roleScope.accountTeamIds?.length ?? 0} account team(s)`
                      : data.roleScope.scope === 'assigned_accounts'
                        ? `Your assigned accounts — ${data.roleScope.accountTeamIds?.length ?? 0} account team(s)`
                        : data.roleScope.scope
                }
              />
            )}
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
            startIcon={dashboard.isFetching ? <CircularProgress size={16} /> : <RefreshIcon />}
            onClick={() => dashboard.refetch()}
            disabled={dashboard.isFetching || invalidWindow}
          >
            Refresh
          </Button>
        </Stack>
      </Stack>

      {invalidWindow && <Alert severity="warning" sx={{ mb: 2 }}>The start date must be earlier than the end date.</Alert>}

      {dashboard.isError && (
        <Alert
          severity="error"
          action={<Button color="inherit" size="small" onClick={() => dashboard.refetch()}>Retry</Button>}
          sx={{ mb: 2 }}
        >
          The verified Release 01 dashboard snapshot is unavailable. No legacy totals are shown in its place.
        </Alert>
      )}

      {salesToday.data?.metrics.length ? (
        <>
          <Typography variant="h6" sx={{ fontWeight: 900, mb: 1.5 }}>Sales Control</Typography>
          <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(3, 1fr)' }, gap: 1.5, mb: 3 }}>
            {salesToday.data.metrics.map((metric) => (
              <Paper key={metric.key} variant="outlined" sx={{ p: 2 }}>
                <Typography variant="body2" color="text.secondary">{metric.label}</Typography>
                <Typography variant="h5" sx={{ fontWeight: 900 }}>{metric.value}</Typography>
              </Paper>
            ))}
          </Box>
        </>
      ) : null}

      {dashboard.isLoading ? (
        <Box sx={{ minHeight: 320, display: 'grid', placeItems: 'center' }}><CircularProgress aria-label="Loading dashboard" /></Box>
      ) : data?.kpis.length ? (
        <>
          {/* The "Commercial Attention" block that used to sit here was seven
              navigation links laid out as metric tiles — no value, no
              denominator, no source. A link menu dressed as a dashboard teaches
              people to read tiles as facts. It is gone; the sidebar already
              carries every one of those destinations. */}
          <Typography variant="h6" sx={{ fontWeight: 900, mb: 1.5 }}>Verified Performance</Typography>
          <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, 1fr)', lg: 'repeat(4, 1fr)' }, gap: 2 }}>
            {data.kpis.map((kpi) => <KpiCard key={kpi.key} kpi={kpi} />)}
          </Box>
        </>
      ) : !dashboard.isError ? (
        <Alert severity="info">No KPI definitions are available for this period and role scope.</Alert>
      ) : null}

      <Divider sx={{ my: 3 }} />
      {/* FR-DSH-02. Its own panel rather than a KPI tile, because the figure only means
          anything alongside its sample size, its coverage and its cost-basis disclosure, and a
          tile has room for none of those. */}
      {!invalidWindow && canViewManagementAnalytics && <GrossMarginPanel from={from} to={to} />}

      <Divider sx={{ my: 3 }} />
      <Stack direction="row" spacing={1.5} sx={{ flexWrap: 'wrap', gap: 1 }}>
        <Button variant="outlined" endIcon={<DrillDownIcon />} onClick={() => navigate('/analytics/deadlines')} sx={{ fontWeight: 800 }}>
          Deadline board
        </Button>
        {canViewManagementAnalytics && (
          <Button variant="outlined" endIcon={<DrillDownIcon />} onClick={() => navigate('/analytics/brand-demand')} sx={{ fontWeight: 800 }}>
            Brand demand
          </Button>
        )}
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
