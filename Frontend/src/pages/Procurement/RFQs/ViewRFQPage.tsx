import React from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Grid, Stack, Chip,
  Table, TableHead, TableRow, TableCell, TableBody,
  CircularProgress, Divider, Breadcrumbs, Link, Alert, ButtonBase,
  Drawer, IconButton, LinearProgress, Tooltip,
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  Edit as EditIcon,
  CheckCircle as ApproveIcon,
  RequestQuote as QuoteDraftIcon,
  Download as ExportIcon,
  NavigateNext as NextIcon,
  Schedule as HistoryIcon,
  OpenInNew as WorkspaceIcon,
  TravelExplore as SourcingIcon,
  Close as CloseIcon,
  Inventory2 as InventoryIcon,
  PriceCheck as PricingIcon,
  WarningAmber as BlockerIcon,
  Description as EvidenceIcon,
} from '@mui/icons-material';
import rfqService from '../../../api/services/rfqService';
import { useAuth } from '../../../context/AuthContext';
import EmailPromptDialog from '../../../components/common/EmailPromptDialog';
import { useSnackbar } from 'notistack';
import LifecycleActions from '../../../components/common/LifecycleActions';
import lifecycleService from '../../../api/services/commercialLifecycleService';
import CommercialLineIntelligence from '../../../components/common/CommercialLineIntelligence';
import procurementService from '../../../api/services/procurementService';
import commercialLearningService from '../../../api/services/commercialLearningService';
import CommercialProcessingEvidence from '../../../components/common/CommercialProcessingEvidence';
import commercialIntelligenceService from '../../../api/services/commercialIntelligenceService';

const DataField: React.FC<{ label: string; value: string | number | null; bold?: boolean; color?: string }> = ({ label, value, bold = true, color = 'text.primary' }) => (
  <Box sx={{ mb: 1.5 }}>
    <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', display: 'block', mb: 0.2, fontSize: '0.65rem' }}>
      {label}
    </Typography>
    <Typography sx={{ fontWeight: bold ? 800 : 500, fontSize: '0.85rem', color: color, whiteSpace: 'pre-wrap' }}>
      {value || '—'}
    </Typography>
  </Box>
);

const formatScenarioMoney = (value: number, currencyCode?: string | null, currencyId?: number | null) => {
  if (currencyCode) {
    try {
      return new Intl.NumberFormat('en-US', { style: 'currency', currency: currencyCode }).format(value);
    } catch {
      return `${currencyCode} ${value.toLocaleString()}`;
    }
  }
  return `${value.toLocaleString()} (currency ${currencyId ?? 'unverified'})`;
};

const ViewRFQPage: React.FC = () => {
  const { t } = useTranslation();
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { userData, hasPermission } = useAuth();
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();

  const [approvalDialogOpen, setApprovalDialogOpen] = React.useState(false);
  const [lineFilter, setLineFilter] = React.useState('all');
  const [evidenceItemId, setEvidenceItemId] = React.useState<number | null>(null);

  const { data: rfq, isLoading, isError, refetch } = useQuery({
    queryKey: ['rfq-detail', Number(id)],
    queryFn: () => rfqService.getById(Number(id), userData?.businessUnitId || 0),
    enabled: !!id && !!userData?.businessUnitId,
  });
  const { data: lifecycle } = useQuery({
    queryKey: ['lifecycle', 'rfqs', Number(id)],
    queryFn: () => lifecycleService.getState('rfqs', Number(id)),
    enabled: !!id,
  });
  const sourcingQuery = useQuery({
    queryKey: ['procurement-sourcing-workbench', Number(id)],
    queryFn: () => procurementService.getWorkbench(Number(id)),
    enabled: !!id && hasPermission('RFQ Management'),
    retry: 1,
  });
  const intelligenceQuery = useQuery({
    queryKey: ['rfq-commercial-intelligence', Number(id)],
    queryFn: () => commercialLearningService.getRfqIntelligence(Number(id)),
    enabled: !!id && hasPermission('RFQ Management') && hasPermission('Quotations'),
    retry: 1,
  });
  const lineResolutionQuery = useQuery({
    queryKey: ['commercial-line-resolutions', 'rfq', Number(id)],
    queryFn: () => commercialIntelligenceService.getRfqLineResolutions(Number(id)),
    enabled: !!id && hasPermission('RFQ Management'),
    retry: 1,
  });

  const approveMutation = useMutation({
    mutationFn: (payload: { id: number; approvedBy: string; email?: string; subject?: string; body?: string; customerId?: number }) =>
      rfqService.approve(payload.id, payload.approvedBy, payload.email, payload.subject, payload.body, payload.customerId),
    onSuccess: () => {
      enqueueSnackbar('RFQ Approved and Sent successfully!', { variant: 'success' });
      setApprovalDialogOpen(false);
      queryClient.invalidateQueries({ queryKey: ['rfq-detail', Number(id)] });
    },
    onError: () => enqueueSnackbar('Failed to approve RFQ', { variant: 'error' }),
  });

  const quoteDraftMutation = useMutation({
    mutationFn: () => rfqService.prepareQuoteDraft(Number(id)),
    onSuccess: (quote) => {
      enqueueSnackbar('Quote Draft prepared with pricing and commercial terms pending.', { variant: 'success' });
      navigate(`/sales/quotes/view/${quote.id}`);
    },
    onError: (error: any) => enqueueSnackbar(
      error?.response?.data?.message || 'The RFQ still has fields that require review.',
      { variant: 'error' },
    ),
  });

  const sourcingCaseMutation = useMutation({
    mutationFn: (rfqItemId: number) => procurementService.createOrOpenSourcingCase(Number(id), rfqItemId, 10),
    onSuccess: (sourcingCase) => navigate(`/procurement/sourcing-cases/${sourcingCase.id}`),
    onError: (error: any) => enqueueSnackbar(
      error?.response?.data?.detail || error?.response?.data?.message || 'The Sourcing Case could not be created.',
      { variant: 'error' },
    ),
  });

  const formatDate = (dateStr: string | null) => {
    if (!dateStr) return '—';
    const d = new Date(dateStr);
    return isNaN(d.getTime()) ? '—' : d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  };

  if (isLoading) return <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: '60vh' }}><CircularProgress /></Box>;
  if (isError) return <Box sx={{ p: 4 }}><Alert severity="error" action={<Button color="inherit" onClick={() => refetch()}>Retry</Button>}>We couldn't load this RFQ.</Alert></Box>;
  if (!rfq) return <Box sx={{ p: 4 }}><Typography>RFQ not found.</Typography></Box>;

  const isDraft = lifecycle?.currentStatusCode === 'DRAFT';
  const intelligence = intelligenceQuery.data;
  const canPrepareQuote = intelligence?.commercialDecision === 'VIABLE_READY';
  const canOpenRecommendedAction = Boolean(intelligence?.nextBestAction.userOverrideAllowed &&
    intelligence.nextBestAction.overrideAction.startsWith('/') && hasPermission('RFQ Management'));
  const sourcingLines = new Map((sourcingQuery.data?.lines ?? []).map((line) => [line.id, line]));
  const offersByLine = new Map<number, number>();
  for (const offer of sourcingQuery.data?.offers ?? []) {
    offersByLine.set(offer.rfqItemId, (offersByLine.get(offer.rfqItemId) ?? 0) + 1);
  }
  const intelligenceLines = new Map((intelligence?.lines ?? []).map((line) => [line.rfqItemId, line]));
  const persistedResolutions = new Map(
    (lineResolutionQuery.data ?? []).filter(line => line.rfqItemId != null).map(line => [line.rfqItemId!, line]),
  );
  const lineMatches = (itemId: number, filter: string) => {
    const line = sourcingLines.get(itemId);
    const decisionLine = intelligenceLines.get(itemId);
    const quoteCount = offersByLine.get(itemId) ?? 0;
    const sourcingRequired = Boolean(line && line.shortfallQuantity > 0 && line.resolution !== 'INCOMING');
    const ready = Boolean(decisionLine && decisionLine.unfulfilledQuantity <= 0 && decisionLine.blockers.length === 0);
    if (filter === 'stock') return line?.resolution === 'IN_STOCK';
    if (filter === 'partial') return line?.resolution === 'PARTIAL';
    if (filter === 'sourcing') return sourcingRequired;
    if (filter === 'quoted') return quoteCount > 0;
    if (filter === 'unresolved') return line?.resolution === 'UNKNOWN' || line?.resolution === 'POSSIBLE_MATCH';
    if (filter === 'pricing') return Boolean(decisionLine && decisionLine.unfulfilledQuantity > 0 && decisionLine.offerCount > 0);
    if (filter === 'ready') return ready;
    return true;
  };
  const visibleItems = rfq.rfqitems.filter((item) => lineMatches(item.id, lineFilter));
  const summary = [
    { key: 'all', label: 'Total lines', value: rfq.rfqitems.length, icon: <EvidenceIcon fontSize="small" /> },
    { key: 'stock', label: 'Ready from stock', value: sourcingQuery.isError ? '—' : rfq.rfqitems.filter((x) => lineMatches(x.id, 'stock')).length, icon: <InventoryIcon fontSize="small" /> },
    { key: 'partial', label: 'Partially available', value: sourcingQuery.isError ? '—' : rfq.rfqitems.filter((x) => lineMatches(x.id, 'partial')).length, icon: <BlockerIcon fontSize="small" /> },
    { key: 'sourcing', label: 'Sourcing required', value: sourcingQuery.isError ? '—' : rfq.rfqitems.filter((x) => lineMatches(x.id, 'sourcing')).length, icon: <SourcingIcon fontSize="small" /> },
    { key: 'quoted', label: 'Lines with supplier quotes', value: sourcingQuery.isError ? '—' : rfq.rfqitems.filter((x) => lineMatches(x.id, 'quoted')).length, icon: <QuoteDraftIcon fontSize="small" /> },
    { key: 'unresolved', label: 'Unresolved matches', value: sourcingQuery.isError ? '—' : rfq.rfqitems.filter((x) => lineMatches(x.id, 'unresolved')).length, icon: <BlockerIcon fontSize="small" /> },
    { key: 'pricing', label: 'Pricing pending', value: intelligenceQuery.isError ? '—' : rfq.rfqitems.filter((x) => lineMatches(x.id, 'pricing')).length, icon: <PricingIcon fontSize="small" /> },
    { key: 'ready', label: 'Ready for quote', value: intelligenceQuery.isError ? '—' : rfq.rfqitems.filter((x) => lineMatches(x.id, 'ready')).length, icon: <ApproveIcon fontSize="small" /> },
  ];
  const readinessPercent = intelligence?.readinessScore ?? 0;
  const primaryBlocker = intelligence?.nextBestAction.explanation ?? (intelligenceQuery.isLoading ? 'Calculating from current commercial evidence' : 'Commercial intelligence unavailable');
  const evidenceItem = rfq.rfqitems.find((item) => item.id === evidenceItemId);

  return (
    <Box sx={{ p: 3, maxWidth: 1800, mx: 'auto' }}>
      {/* Header */}
      <Box sx={{ mb: 3 }}>
        <Breadcrumbs separator={<NextIcon sx={{ fontSize: 14 }} />} sx={{ mb: 2 }}>
          <Link component="button" variant="caption" onClick={() => navigate('/procurement/rfqs/all')} sx={{ color: 'text.secondary', fontWeight: 700, textDecoration: 'none', textTransform: 'uppercase' }}>
            {t('rfq_management')}
          </Link>
          <Typography variant="caption" sx={{ color: 'primary.main', fontWeight: 900, textTransform: 'uppercase' }}>
            {rfq.rfqno}
          </Typography>
        </Breadcrumbs>

        <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: { xs: 'flex-start', md: 'center' }, gap: 2, flexDirection: { xs: 'column', md: 'row' } }}>
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 2 }}>
            <Typography variant="h4" sx={{ fontWeight: 950, color: 'text.primary', letterSpacing: 0 }}>
              {rfq.rfqno}
            </Typography>
            {(rfq.nexoraSerial || rfq.commercialCaseReference) && (
              <Chip
                label={`Nexora Serial: ${rfq.nexoraSerial || rfq.commercialCaseReference}`}
                size="small"
                variant="outlined"
                sx={{ fontWeight: 900, fontFamily: 'monospace' }}
              />
            )}
            <Chip 
              label={rfq.rfqstatusValue || 'Unknown'} 
              color={isDraft ? "warning" : "success"}
              size="small"
              sx={{ fontWeight: 900, fontSize: '0.65rem', textTransform: 'uppercase' }}
            />
          </Box>
          <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap', position: { md: 'sticky' }, top: 8, zIndex: 2 }}>
            {rfq.commercialCaseId && (
              <Button
                variant="outlined"
                startIcon={<WorkspaceIcon />}
                onClick={() => navigate(`/commercial-cases/${rfq.commercialCaseId}`)}
                sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}
              >
                Workspace
              </Button>
            )}
            {hasPermission('Quotations') && (
              <Button
                variant="outlined"
                startIcon={<PricingIcon />}
                onClick={() => navigate(`/procurement/rfqs/${rfq.id}/pricing`)}
                sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}
              >
                Smart Pricing
              </Button>
            )}
            {hasPermission('RFQ Management', 'edit') && <LifecycleActions aggregate="rfqs" id={rfq.id} onChanged={() => queryClient.invalidateQueries({ queryKey: ['rfq-detail', Number(id)] })} />}
            <Button
              variant="outlined"
              startIcon={<BackIcon />}
              onClick={() => navigate(-1)}
              sx={{ fontWeight: 800, borderRadius: 2, px: 3, borderColor: 'divider', color: 'text.secondary' }}
            >
              Back
            </Button>
            {isDraft && hasPermission('RFQ Management', 'edit') && (
              <>
                <Button
                  variant="outlined"
                  color="primary"
                  startIcon={<EditIcon />}
                  sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}
                >
                  Edit Draft
                </Button>
              </>
            )}
            {hasPermission('Quotations', 'create') && <Button variant="contained" color="success" startIcon={<QuoteDraftIcon />}
              onClick={() => quoteDraftMutation.mutate()} disabled={!canPrepareQuote || quoteDraftMutation.isPending}
              sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}>
              Prepare Quote Draft
            </Button>}
            <Button
              variant="outlined"
              startIcon={<ExportIcon />}
              sx={{ fontWeight: 800, borderRadius: 2, px: 3, borderColor: 'divider', color: 'text.secondary' }}
            >
              Export
            </Button>
          </Stack>
        </Box>
        <Paper sx={{ mt: 2, p: 2, borderRadius: 1, border: '1px solid', borderColor: 'divider', bgcolor: 'background.paper' }}>
          <Grid container spacing={2} sx={{ alignItems: 'center' }}>
            <Grid size={{ xs: 12, md: 3 }}><DataField label="Customer / contact" value={`${rfq.customerName || rfq.buyersName || 'Unresolved'}${rfq.contactName ? ` · ${rfq.contactName}` : ''}`} /></Grid>
            <Grid size={{ xs: 6, md: 2 }}><DataField label="Account Owner" value={rfq.accountOwnerName || 'Unassigned'} /></Grid>
            <Grid size={{ xs: 6, md: 2 }}><DataField label="Opportunity Owner" value={rfq.opportunityOwnerName || 'Unassigned'} /></Grid>
            <Grid size={{ xs: 6, md: 2 }}><DataField label="Customer deadline" value={formatDate(rfq.bidClosingDate || null)} color={rfq.bidClosingDate && new Date(rfq.bidClosingDate) < new Date() ? 'error.main' : 'text.primary'} /></Grid>
            <Grid size={{ xs: 6, md: 3 }}>
              <Typography variant="caption" color="text.secondary">Commercial readiness</Typography>
              <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}><LinearProgress variant="determinate" value={readinessPercent} sx={{ flex: 1, height: 8, borderRadius: 1 }} /><Typography sx={{ fontWeight: 800 }}>{readinessPercent}%</Typography></Stack>
              <Typography variant="caption" color={intelligence?.commercialDecision === 'VIABLE_READY' ? 'success.main' : 'warning.main'}>{primaryBlocker}</Typography>
            </Grid>
          </Grid>
        </Paper>
      </Box>

      <CommercialProcessingEvidence resource="rfqs" id={rfq.id} />

      <Grid container spacing={1.5} sx={{ mb: 3 }}>
        {summary.map((item) => (
          <Grid key={item.key} size={{ xs: 6, sm: 4, md: 3, xl: 1.5 }}>
            <ButtonBase onClick={() => setLineFilter(item.key)} sx={{ width: '100%', textAlign: 'left', borderRadius: 1 }} aria-pressed={lineFilter === item.key}>
              <Paper sx={{ width: '100%', minHeight: 88, p: 1.5, borderRadius: 1, border: '1px solid', borderColor: lineFilter === item.key ? 'primary.main' : 'divider', bgcolor: lineFilter === item.key ? 'action.selected' : 'background.paper' }}>
                <Stack direction="row" sx={{ justifyContent: 'space-between', color: 'text.secondary' }}>{item.icon}<Typography variant="h6" sx={{ fontWeight: 900 }}>{item.value}</Typography></Stack>
                <Typography variant="caption" sx={{ fontWeight: 700 }}>{item.label}</Typography>
              </Paper>
            </ButtonBase>
          </Grid>
        ))}
      </Grid>

      <Grid container spacing={3}>
        {/* RFQ Details */}
        <Grid size={{ xs: 12, lg: 9 }}>
          <Stack spacing={3}>
            {/* General Info */}
            <Paper sx={{ p: 3, borderRadius: 3, border: '1px solid', borderColor: 'divider' }}>
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 3 }}>
                <Typography sx={{ fontWeight: 900, fontSize: '1rem', color: 'text.primary', textTransform: 'uppercase', letterSpacing: '0.025em' }}>
                  General Information
                </Typography>
              </Box>
              <Grid container spacing={2}>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="RFQ #" value={rfq.rfqno} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Active Lead Revision" value={`Revision ${rfq.activeLeadRevision || 1}`} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Readiness" value={rfq.readiness} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}>
                  <Button size="small" variant="outlined" onClick={() => navigate(`/procurement/leads/view/${rfq.leadId}`)}>Open Canonical Lead</Button>
                </Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Customer / Buyer" value={rfq.buyersName || rfq.customerName || 'N/A'} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Customer Email" value={rfq.customerEmail || rfq.leadEmail || 'N/A'} /></Grid>
                
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Received Date" value={formatDate(rfq.recDate)} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Bid Closing Date" value={formatDate(rfq.bidClosingDate || null)} color="error.main" /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="RFQ Type" value={rfq.rfqtype || 'Agreement'} /></Grid>

                <Grid size={{ xs: 12, md: 4 }}><DataField label="Created By" value={rfq.createdBy} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Created On" value={formatDate(rfq.createdDate)} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Business Unit" value={rfq.businessUnitName || null} /></Grid>
              </Grid>

              {rfq.headerRemarks && (
                <Box sx={{ mt: 2 }}>
                  <Divider sx={{ mb: 2 }} />
                  <DataField label="Header Remarks" value={rfq.headerRemarks} bold={false} />
                </Box>
              )}
            </Paper>

            <Box sx={{ mb: 2 }}><CommercialLineIntelligence stage="rfq" recordId={rfq.id} /></Box>

            {intelligenceQuery.isError && <Alert severity="error" action={<Button color="inherit" onClick={() => intelligenceQuery.refetch()}>Retry</Button>}>Commercial intelligence could not be reconciled.</Alert>}
            {intelligence && <Paper variant="outlined" sx={{ p: 2.5, borderRadius: 1 }}>
              <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', mb: 2 }}>
                <Box>
                  <Typography sx={{ fontWeight: 900 }}>Opportunity Digital Twin</Typography>
                  <Typography variant="body2" color="text.secondary">{intelligence.nextBestAction.label}: {intelligence.nextBestAction.explanation}</Typography>
                  <Typography variant="caption" color="text.secondary">Confidence {Math.round(intelligence.nextBestAction.confidence * 100)}% · {intelligence.digitalTwin.validity}</Typography>
                </Box>
                {canOpenRecommendedAction && <Button variant="outlined" onClick={() => navigate(intelligence.nextBestAction.overrideAction)}>Open recommended action</Button>}
              </Stack>
              <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, 1fr)', xl: 'repeat(4, 1fr)' }, gap: 1.5 }}>
                {intelligence.digitalTwin.scenarios.map((scenario) => <Box key={scenario.code} sx={{ border: '1px solid', borderColor: 'divider', p: 1.5, minHeight: 132 }}>
                  <Stack direction="row" spacing={1} sx={{ justifyContent: 'space-between', alignItems: 'center' }}><Typography variant="subtitle2" sx={{ fontWeight: 800 }}>{scenario.label}</Typography><Chip size="small" color={scenario.eligible ? 'success' : 'default'} label={scenario.eligible ? 'Eligible' : 'Evidence needed'} /></Stack>
                  <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>{scenario.explanation}</Typography>
                  {scenario.estimatedLandedCost != null && <Typography variant="body2" sx={{ mt: 1, fontWeight: 800 }}>Landed total {formatScenarioMoney(scenario.estimatedLandedCost, scenario.currencyCode, scenario.currencyId)} · {scenario.estimatedLeadTimeDays ?? '—'} days</Typography>}
                  <Typography variant="caption" sx={{ display: 'block', mt: 0.75, fontWeight: 700 }}>Risk {scenario.riskBand.replaceAll('_', ' ')} · Confidence {Math.round(scenario.confidence * 100)}%</Typography>
                  {scenario.validUntil && <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>Valid to {formatDate(scenario.validUntil)}</Typography>}
                  {scenario.quantities.length > 0 && <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>Stock {scenario.quantities.reduce((sum, line) => sum + line.immediateStockQuantity, 0)} · Supplier {scenario.quantities.reduce((sum, line) => sum + line.supplierQuantity, 0)}</Typography>}
                  <Box component="details" sx={{ mt: 1 }}>
                    <Typography component="summary" variant="caption" sx={{ fontWeight: 800, cursor: 'pointer' }}>Evidence and assumptions</Typography>
                    <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.75 }}>{scenario.riskExplanation}</Typography>
                    {scenario.costSources.map((source, index) => <Typography key={`${source.sourceType}-${index}`} variant="caption" sx={{ display: 'block' }}>{source.label}: {source.amount != null ? formatScenarioMoney(source.amount, scenario.currencyCode, source.currencyId) : source.status.replaceAll('_', ' ')}</Typography>)}
                    {scenario.assumptions.map((assumption) => <Typography key={assumption} variant="caption" color="text.secondary" sx={{ display: 'block' }}>Assumption: {assumption}</Typography>)}
                    {scenario.approvalRequirements.map((approval) => <Typography key={approval} variant="caption" color="warning.main" sx={{ display: 'block' }}>Approval: {approval}</Typography>)}
                    {scenario.evidence.map((item) => <Typography key={`${item.recordType}-${item.recordId}-${item.role}`} variant="caption" color="text.secondary" sx={{ display: 'block' }}>Evidence: {item.reference}</Typography>)}
                  </Box>
                </Box>)}
              </Box>
              <Divider sx={{ my: 2 }} />
              <Stack direction={{ xs: 'column', md: 'row' }} spacing={3} sx={{ justifyContent: 'space-between' }}>
                <Box>
                  <Typography variant="subtitle2" sx={{ fontWeight: 900 }}>Predictive pricing · shadow mode</Typography>
                  <Typography variant="caption" color="text.secondary">{intelligence.digitalTwin.backtest.cohort}</Typography>
                  <Typography variant="body2" sx={{ mt: 0.5 }}>{intelligence.digitalTwin.predictivePricing.filter(line => line.status === 'READY_SHADOW').length} of {intelligence.digitalTwin.predictivePricing.length} lines have sufficient order-backed evidence.</Typography>
                  {intelligence.digitalTwin.predictivePricing.filter(line => line.status === 'READY_SHADOW').map((line) => <Box key={line.rfqItemId} sx={{ mt: 1 }}>
                    <Typography variant="caption" sx={{ display: 'block', fontWeight: 800 }}>Line {line.rfqItemId}: {line.recommendedUnitPrice != null ? formatScenarioMoney(line.recommendedUnitPrice, line.currencyCode, line.currencyId) : 'withheld'} · {line.customerOrderSampleSize} order outcomes</Typography>
                    <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>Winning range {line.winningRangeLow != null && line.winningRangeHigh != null ? `${formatScenarioMoney(line.winningRangeLow, line.currencyCode, line.currencyId)} to ${formatScenarioMoney(line.winningRangeHigh, line.currencyCode, line.currencyId)}` : 'insufficient'} · walk-forward MAPE {line.backtestMeanAbsolutePercentError != null ? `${line.backtestMeanAbsolutePercentError}% (${line.backtestHoldoutCount} holdouts)` : 'not measured'} · decided-cohort conversion baseline {line.cohortConversionBaseline != null ? `${Math.round(line.cohortConversionBaseline * 100)}%` : 'withheld'}</Typography>
                  </Box>)}
                </Box>
                <Box>
                  <Typography variant="subtitle2" sx={{ fontWeight: 900 }}>Customer target bridge</Typography>
                  <Typography variant="body2">{intelligence.digitalTwin.customerTargetBridges.filter(bridge => bridge.status === 'VERIFIED_SOURCING_DECISION').length} verified · {intelligence.digitalTwin.customerTargetBridges.filter(bridge => bridge.status !== 'VERIFIED_SOURCING_DECISION').length} need attention</Typography>
                  <Typography variant="caption" color="text.secondary">Margins and maximum Supplier costs are never inferred without a persisted decision.</Typography>
                </Box>
              </Stack>
            </Paper>}

            {/* Line Items */}
            <Paper sx={{ borderRadius: 1, border: '1px solid', borderColor: 'divider', overflow: 'hidden' }}>
              <Box sx={{ p: 2.5, bgcolor: '#fafafa', borderBottom: '1px solid', borderColor: 'divider' }}>
                <Typography sx={{ fontWeight: 950, fontSize: '0.9rem', color: 'text.primary', textTransform: 'uppercase' }}>
                  RFQ Lines ({rfq.rfqitems.length})
                </Typography>
              </Box>
              <Box sx={{ overflowX: 'auto' }}><Table size="small" sx={{ minWidth: 1100 }}>
                <TableHead sx={{ bgcolor: '#fcfcfc' }}>
                  <TableRow>
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }}>#</TableCell>
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }}>Product / Description</TableCell>
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }}>Manufacturer / Part #</TableCell>
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }} align="center">Qty</TableCell>
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }}>Resolution</TableCell>
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }}>Inventory</TableCell>
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }}>Supplier Quotes</TableCell>
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }}>Next Action</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {visibleItems.map((item, idx) => (
                    <TableRow key={item.id} hover sx={{ '&:last-child td': { border: 0 } }}>
                      <TableCell sx={{ fontSize: '0.8rem', fontWeight: 700, color: 'text.secondary' }}>{idx + 1}</TableCell>
                      <TableCell>
                        <Typography sx={{ fontSize: '0.8rem', fontWeight: 800, color: 'primary.main' }}>
                          {item.productName || item.productShortName || 'Unknown Product'}
                        </Typography>
                        <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block' }}>
                          {item.productShortDescription || 'No description'}
                        </Typography>
                      </TableCell>
                      <TableCell>
                        <Typography sx={{ fontSize: '0.8rem', fontWeight: 700 }}>
                          {item.manufacturerName || 'N/A'}
                        </Typography>
                        <Typography variant="caption" sx={{ color: 'text.disabled' }}>
                          {item.manufacturerPartNumber || 'N/A'}
                        </Typography>
                      </TableCell>
                      <TableCell align="center" sx={{ fontSize: '0.85rem', fontWeight: 900 }}>
                        {item.quantity} {item.unitOfMeasure || 'EA'}
                      </TableCell>
                      <TableCell>{(() => {
                        const resolution = persistedResolutions.get(item.id);
                        if (lineResolutionQuery.isLoading) return <Chip size="small" label="Resolution checking" variant="outlined" />;
                        if (lineResolutionQuery.isError) return <Chip size="small" label="Resolution unavailable" color="error" variant="outlined" />;
                        if (!resolution) return <Chip size="small" label="Resolution not recorded" color="warning" variant="outlined" />;
                        const reviewed = resolution.productResolution?.decisionState?.toLowerCase().includes('approved') || resolution.classification === 'KnownInStock' || resolution.classification === 'KnownIncoming' || resolution.classification === 'KnownShortage';
                        return <Tooltip title={`Evidence: ${resolution.evidenceReference || 'not recorded'}`}><Chip size="small" label={reviewed ? 'Persisted resolution' : resolution.classification.replace(/([a-z])([A-Z])/g, '$1 $2')} color={reviewed ? 'success' : 'warning'} variant="outlined" /></Tooltip>;
                      })()}</TableCell>
                      <TableCell>
                        <Typography variant="caption" sx={{ fontWeight: 700 }}>{sourcingLines.get(item.id)?.resolution.replaceAll('_', ' ') || 'Checking'}</Typography>
                        <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>ATP {sourcingLines.get(item.id)?.availableQuantity ?? '—'} · Short {sourcingLines.get(item.id)?.shortfallQuantity ?? '—'}</Typography>
                      </TableCell>
                      <TableCell><Stack spacing={0.5} sx={{ alignItems: 'flex-start' }}><Chip size="small" label={`${intelligenceLines.get(item.id)?.eligibleOfferCount ?? 0}/${offersByLine.get(item.id) ?? 0} eligible`} color={(intelligenceLines.get(item.id)?.eligibleOfferCount ?? 0) > 0 ? 'success' : 'default'} />{(intelligenceLines.get(item.id)?.bidQualityFlags.length ?? 0) > 0 && <Typography variant="caption" color="warning.main">{intelligenceLines.get(item.id)?.bidQualityFlags.length} quality finding(s)</Typography>}</Stack></TableCell>
                      <TableCell>
                        {(() => {
                          const sourcingLine = sourcingLines.get(item.id);
                          if (sourcingQuery.isLoading) {
                            return <Typography variant="caption" color="text.secondary">Checking company availability…</Typography>;
                          }
                          if (sourcingQuery.isError || !sourcingLine) {
                            return <Typography variant="caption" color="error.main">Inventory check unavailable</Typography>;
                          }
                          if (sourcingLine.shortfallQuantity <= 0 || sourcingLine.resolution === 'IN_STOCK') {
                            return <Chip size="small" color="success" variant="outlined" label="Use company inventory" />;
                          }
                          return (
                            <Stack spacing={0.75} sx={{ alignItems: 'flex-start' }}>
                              <Typography variant="caption" color="error.main" sx={{ fontWeight: 700 }}>
                                {sourcingLine.shortfallQuantity} to source · {sourcingLine.resolution.replaceAll('_', ' ')}
                              </Typography>
                              {hasPermission('RFQ Management', 'edit') && (
                                <Button
                                  size="small"
                                  variant="contained"
                                  startIcon={<SourcingIcon />}
                                  disabled={sourcingCaseMutation.isPending}
                                  onClick={() => sourcingCaseMutation.mutate(item.id)}
                                >
                                  {sourcingLine.sourcingCaseId ? 'Open Sourcing Case' : 'Create / Open Sourcing Case'}
                                </Button>
                              )}
                              <Tooltip title="Inspect persisted source and normalization evidence"><Button size="small" variant="text" startIcon={<EvidenceIcon />} onClick={() => setEvidenceItemId(item.id)}>Evidence</Button></Tooltip>
                            </Stack>
                          );
                        })()}
                      </TableCell>
                    </TableRow>
                  ))}
                  {rfq.rfqitems.length === 0 && (
                    <TableRow>
                      <TableCell colSpan={8} sx={{ py: 4, textAlign: 'center', color: 'text.disabled' }}>
                        No RFQ lines match this filter.
                      </TableCell>
                    </TableRow>
                  )}
                </TableBody>
              </Table></Box>
              <Box sx={{ p: 2, display: 'flex', justifyContent: 'flex-end', bgcolor: '#fafafa' }}>
                <Stack direction="row" spacing={4}>
                   <Box sx={{ textAlign: 'right' }}>
                      <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase' }}>Total Items</Typography>
                      <Typography sx={{ fontWeight: 900, fontSize: '1.1rem' }}>{rfq.rfqitems.length}</Typography>
                   </Box>
                   <Chip label="Commercial Review Required" color="warning" variant="outlined" />
                </Stack>
              </Box>
            </Paper>
          </Stack>
        </Grid>

        {/* Sidebar */}
        <Grid size={{ xs: 12, lg: 3 }}>
          <Stack spacing={3}>
            {/* Lead Connection */}
            {rfq.leadId && (
              <Paper sx={{ p: 2.5, borderRadius: 3, border: '1px solid', borderColor: 'divider', bgcolor: 'primary.lighter' }}>
                <Typography variant="caption" sx={{ fontWeight: 900, color: 'primary.main', textTransform: 'uppercase', mb: 1, display: 'block' }}>
                  Connected Lead
                </Typography>
                <Typography sx={{ fontWeight: 800, fontSize: '0.9rem', mb: 1.5 }}>
                  This RFQ was generated from Lead #{rfq.leadId}
                </Typography>
                <Button 
                  fullWidth variant="contained" size="small" 
                  onClick={() => navigate(`/procurement/leads/view/${rfq.leadId}`)}
                  sx={{ textTransform: 'none', fontWeight: 800, borderRadius: 2 }}
                >
                  View Original Lead
                </Button>
              </Paper>
            )}

            {/* Audit / Timeline */}
            <Paper sx={{ p: 2.5, borderRadius: 3, border: '1px solid', borderColor: 'divider' }}>
               <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 2 }}>
                 <HistoryIcon sx={{ color: 'text.secondary', fontSize: 20 }} />
                 <Typography sx={{ fontWeight: 900, fontSize: '0.85rem', textTransform: 'uppercase' }}>Workflow History</Typography>
               </Box>
               <Stack spacing={2}>
                  {[
                    { title: 'RFQ Created', user: rfq.createdBy, date: rfq.createdDate, icon: <EditIcon sx={{ fontSize: 14 }} /> },
                    rfq.modifiedBy && { title: 'Last Modified', user: rfq.modifiedBy, date: rfq.modifiedDate, icon: <HistoryIcon sx={{ fontSize: 14 }} /> },
                  ].filter(Boolean).map((log: any, i) => (
                    <Box key={i} sx={{ display: 'flex', gap: 1.5 }}>
                       <Box sx={{ mt: 0.5, p: 0.5, borderRadius: '50%', bgcolor: 'action.hover', display: 'flex' }}>
                         {log.icon}
                       </Box>
                       <Box>
                          <Typography sx={{ fontSize: '0.75rem', fontWeight: 800 }}>{log.title}</Typography>
                          <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block' }}>by {log.user}</Typography>
                          <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.65rem' }}>{formatDate(log.date)}</Typography>
                       </Box>
                    </Box>
                  ))}
               </Stack>
            </Paper>
          </Stack>
        </Grid>
      </Grid>

      {rfq && (
        <EmailPromptDialog
          open={approvalDialogOpen}
          initialEmail={rfq.customerEmail || rfq.leadEmail}
          initialSubject={`Quote for RFQ #${rfq.rfqno}`}
          initialBody={`Dear Customer,\n\nPlease find the quote for your RFQ #${rfq.rfqno} attached.\n\nBest Regards,\n${userData?.userName}`}
          businessUnitId={userData?.businessUnitId || 0}
          customerId={rfq.customerId}
          loading={approveMutation.isPending}
          onCancel={() => setApprovalDialogOpen(false)}
          onConfirm={(email, subject, body, customerId) => {
            approveMutation.mutate({
              id: rfq.id,
              approvedBy: userData?.userName || 'System',
              email,
              subject,
              body,
              customerId
            });
          }}
        />
      )}
      <Drawer anchor="right" open={Boolean(evidenceItem)} onClose={() => setEvidenceItemId(null)} slotProps={{ paper: { sx: { width: { xs: '100%', sm: 440 }, p: 3 } } }}>
        <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 2 }}>
          <Box><Typography variant="overline">Source evidence</Typography><Typography variant="h6" sx={{ fontWeight: 900 }}>{evidenceItem?.manufacturerPartNumber || evidenceItem?.itemMaterialCode || `Line ${evidenceItem?.lineItemNo || ''}`}</Typography></Box>
          <IconButton aria-label="Close evidence" onClick={() => setEvidenceItemId(null)}><CloseIcon /></IconButton>
        </Stack>
        {/* This alert used to lead with an extraction-confidence percentage.
            Nexora does not measure extraction accuracy, so no figure is shown;
            what is true — and actionable — is that a person checks every line. */}
        <Alert severity="info" sx={{ mb: 2 }}>
          Every extracted line is verified by a reviewer before it moves downstream. Cross-check the values below against the
          original customer document.
        </Alert>
        <DataField label="Original customer value" value={evidenceItem?.itemText || evidenceItem?.materialPotext || evidenceItem?.productShortDescription || 'Source text not linked'} bold={false} />
        <DataField label="Normalized part" value={evidenceItem?.manufacturerPartNumber || evidenceItem?.itemMaterialCode || 'Unresolved'} />
        <DataField label="Normalized quantity / UOM" value={evidenceItem ? `${evidenceItem.quantity} ${evidenceItem.unitOfMeasure || 'EA'}` : null} />
        <DataField label="Extraction location" value="Open Canonical Lead to inspect document, page, sheet, row, or bounding-box evidence." bold={false} />
        {Boolean(rfq.leadId) && <Button variant="outlined" startIcon={<EvidenceIcon />} onClick={() => navigate(`/procurement/leads/view/${rfq.leadId}`)}>Open Canonical Lead evidence</Button>}
      </Drawer>
    </Box>
  );
};

export default ViewRFQPage;
