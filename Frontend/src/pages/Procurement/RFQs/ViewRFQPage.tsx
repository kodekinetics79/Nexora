import React from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Grid, Stack, Chip,
  Table, TableHead, TableRow, TableCell, TableBody,
  CircularProgress, Divider, Breadcrumbs, Link, Alert, ButtonBase,
  Drawer, IconButton, LinearProgress, Tooltip, Autocomplete, TextField,
  Dialog, DialogActions, DialogContent, DialogTitle,
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  Edit as EditIcon,
  CheckCircle as ApproveIcon,
  RequestQuote as QuoteDraftIcon,
  NavigateNext as NextIcon,
  Schedule as HistoryIcon,
  OpenInNew as WorkspaceIcon,
  TravelExplore as SourcingIcon,
  Close as CloseIcon,
  Inventory2 as InventoryIcon,
  PriceCheck as PricingIcon,
  Handshake as WorkbenchIcon,
  WarningAmber as BlockerIcon,
  Description as EvidenceIcon,
  ErrorOutlined as UnavailableIcon,
  HelpOutlined as UndecidedIcon,
  HourglassEmpty as WaitingIcon,
} from '@mui/icons-material';
import rfqService from '../../../api/services/rfqService';
import { useAuth } from '../../../context/AuthContext';
import { useSnackbar } from 'notistack';
import LifecycleActions from '../../../components/common/LifecycleActions';
import lifecycleService from '../../../api/services/commercialLifecycleService';
import CommercialLineIntelligence from '../../../components/common/CommercialLineIntelligence';
import procurementService from '../../../api/services/procurementService';
import commercialLearningService from '../../../api/services/commercialLearningService';
import CommercialProcessingEvidence from '../../../components/common/CommercialProcessingEvidence';
import commercialIntelligenceService from '../../../api/services/commercialIntelligenceService';
import { formatMoney } from '../../../utils/currency';
import { formatDateSafe, parseDateSafe } from '../../../utils/dates';
import { statusLabel } from '../../../utils/statusLabels';
import { commercialActionPermissions } from '../../../utils/commercialActionPermissions';
import productService, { type ProductDTO } from '../../../api/services/productService';

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

const MAX_EXTRA_FIELDS = 6;
const MAX_EXTRA_FIELD_VALUE_LENGTH = 120;

interface ExtraFieldsPresentation {
  fields: Array<{ label: string; value: string }>;
  hiddenCount: number;
  invalid: boolean;
}

const extraFieldLabel = (key: string): string => {
  const spaced = key.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replaceAll(/[_-]+/g, ' ').trim();
  return (spaced || 'Customer field').slice(0, 60);
};

const boundedExtraFieldValue = (value: unknown): string => {
  if (value === null || value === undefined || value === '') return 'Not stated';
  if (typeof value === 'object') return 'Structured value retained in the source record';
  const text = String(value).trim();
  return text.length > MAX_EXTRA_FIELD_VALUE_LENGTH
    ? `${text.slice(0, MAX_EXTRA_FIELD_VALUE_LENGTH - 1)}…`
    : text;
};

/**
 * ExtraFields is source JSON, not application markup. Render only a bounded primitive summary;
 * React escapes the text and nested/unbounded structures remain available in the source record
 * without flooding the operational table.
 */
export const presentRfqExtraFields = (json: string | null | undefined): ExtraFieldsPresentation => {
  if (!json?.trim()) return { fields: [], hiddenCount: 0, invalid: false };
  try {
    const parsed = JSON.parse(json) as unknown;
    if (!parsed || Array.isArray(parsed) || typeof parsed !== 'object') {
      return { fields: [], hiddenCount: 0, invalid: true };
    }
    const entries = Object.entries(parsed as Record<string, unknown>);
    return {
      fields: entries.slice(0, MAX_EXTRA_FIELDS).map(([key, value]) => ({
        label: extraFieldLabel(key),
        value: boundedExtraFieldValue(value),
      })),
      hiddenCount: Math.max(0, entries.length - MAX_EXTRA_FIELDS),
      invalid: false,
    };
  } catch {
    return { fields: [], hiddenCount: 0, invalid: true };
  }
};

/**
 * Scenario money.
 *
 * This used to be a third private copy of `formatMoney`, hardcoded to `en-US` — so the same SAR
 * figure was grouped one way here and another way on the quote screen — and, when the record
 * carried no currency code, it printed the database foreign key: "1,240 (currency 7)". A
 * currency id tells the reader nothing; `formatMoney` already renders a bare grouped number for
 * a record with no stated currency, which is the honest output, so the note beside it only has
 * to say the currency is missing.
 */
const formatScenarioMoney = (value: number, currencyCode?: string | null) =>
  currencyCode ? formatMoney(value, currencyCode) : `${formatMoney(value)} (currency not stated)`;

const ViewRFQPage: React.FC = () => {
  const { t } = useTranslation();
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { userData, hasPermission } = useAuth();
  const commercialAccess = commercialActionPermissions(hasPermission);
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();

  const [lineFilter, setLineFilter] = React.useState('all');
  const [evidenceItemId, setEvidenceItemId] = React.useState<number | null>(null);
  const [productResolutionItemId, setProductResolutionItemId] = React.useState<number | null>(null);
  const [productSearch, setProductSearch] = React.useState('');
  const [selectedProduct, setSelectedProduct] = React.useState<ProductDTO | null>(null);
  const [productResolutionReason, setProductResolutionReason] = React.useState('');

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
  const productSearchQuery = useQuery({
    queryKey: ['rfq-product-resolution-search', userData?.businessUnitId, productSearch],
    queryFn: () => productService.getAll({
      businessUnitId: userData?.businessUnitId || 0,
      pageNumber: 1,
      pageSize: 20,
      search: productSearch.trim() || undefined,
      isActive: true,
    }).then((response) => response.items),
    enabled: productResolutionItemId !== null && Boolean(userData?.businessUnitId),
    staleTime: 30_000,
  });

  const productResolutionMutation = useMutation({
    mutationFn: async () => {
      if (!productResolutionItemId || !selectedProduct || !productResolutionReason.trim()) {
        throw new Error('Choose a catalogue product and record the resolution reason.');
      }
      return rfqService.resolveLineProduct(
        Number(id), productResolutionItemId, selectedProduct.id, productResolutionReason.trim(),
      );
    },
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['rfq-detail', Number(id)] }),
        queryClient.invalidateQueries({ queryKey: ['procurement-sourcing-workbench', Number(id)] }),
        queryClient.invalidateQueries({ queryKey: ['commercial-line-resolutions', 'rfq', Number(id)] }),
        queryClient.invalidateQueries({ queryKey: ['rfq-commercial-intelligence', Number(id)] }),
      ]);
      setProductResolutionItemId(null);
      setSelectedProduct(null);
      setProductResolutionReason('');
      enqueueSnackbar('Catalogue product resolved. Reopen sourcing to refresh governed Supplier candidates.', { variant: 'success' });
    },
    onError: (error: any) => enqueueSnackbar(
      error?.response?.data?.detail || error?.response?.data?.message || error?.message || 'The product resolution could not be saved.',
      { variant: 'error' },
    ),
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

  if (isLoading) return <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: '60vh' }}><CircularProgress /></Box>;
  if (isError) return <Box sx={{ p: 4 }}><Alert severity="error" action={<Button color="inherit" onClick={() => refetch()}>Retry</Button>}>We couldn't load this RFQ.</Alert></Box>;
  if (!rfq) return <Box sx={{ p: 4 }}><Typography>RFQ not found.</Typography></Box>;

  const isDraft = lifecycle?.currentStatusCode === 'DRAFT';
  const intelligence = intelligenceQuery.data;
  // Matches the server. QuoteService.PrepareQuoteDraftAsync blocks a draft ONLY on
  // NO_QUOTE_REVIEW, and says why in a comment worth repeating: demanding VIABLE_READY
  // "made the draft unreachable for any line needing sourcing — the normal case. Supply
  // coverage is a condition of quote RELEASE, not of starting one."
  //
  // The explanation beside this button (prepareQuoteReason) was added upstream and is kept —
  // but it explained a block that should not have been there. Confirmed on the live tenant:
  // RFQs 58 and 62 both showed the button disabled with a reason the rep could not act on.
  //
  // Enabled unless we positively KNOW the decision is NO_QUOTE_REVIEW: the intelligence query
  // is advisory, and blocking a rep because advice failed to load is the same defect in a
  // different costume.
  const canPrepareQuote = intelligence?.commercialDecision !== 'NO_QUOTE_REVIEW';
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
  const productResolutionItem = rfq.rfqitems.find((item) => item.id === productResolutionItemId) ?? null;
  const openProductResolution = (item: typeof rfq.rfqitems[number]) => {
    setProductResolutionItemId(item.id);
    setProductSearch(item.manufacturerPartNumber || item.itemMaterialCode || item.productShortDescription || '');
    setSelectedProduct(null);
    setProductResolutionReason('');
  };
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
  // Participation is governed on the immutable Lead revision. Every line on a promoted RFQ is
  // already in the approved scope; RFQ-line participation flags are a legacy artifact and must
  // not create a second, conflicting denominator on this screen.
  const judgedCount = rfq.rfqitems.length;
  const judgedScope = `of ${judgedCount} line${judgedCount === 1 ? '' : 's'}`;
  const summary = [
    { key: 'all', label: 'Total lines', value: rfq.rfqitems.length, icon: <EvidenceIcon fontSize="small" /> },
    { key: 'stock', label: 'Ready from stock', value: sourcingQuery.isError ? '—' : rfq.rfqitems.filter((x) => lineMatches(x.id, 'stock')).length, icon: <InventoryIcon fontSize="small" /> },
    { key: 'partial', label: 'Partially available', value: sourcingQuery.isError ? '—' : rfq.rfqitems.filter((x) => lineMatches(x.id, 'partial')).length, icon: <BlockerIcon fontSize="small" /> },
    { key: 'sourcing', label: 'Sourcing required', value: sourcingQuery.isError ? '—' : rfq.rfqitems.filter((x) => lineMatches(x.id, 'sourcing')).length, icon: <SourcingIcon fontSize="small" /> },
    { key: 'quoted', label: 'Lines with supplier quotes', value: sourcingQuery.isError ? '—' : rfq.rfqitems.filter((x) => lineMatches(x.id, 'quoted')).length, icon: <QuoteDraftIcon fontSize="small" /> },
    { key: 'unresolved', label: 'Unresolved matches', value: sourcingQuery.isError ? '—' : rfq.rfqitems.filter((x) => lineMatches(x.id, 'unresolved')).length, icon: <BlockerIcon fontSize="small" /> },
    { key: 'pricing', label: `Pricing pending ${judgedScope}`, value: intelligenceQuery.isError ? '—' : rfq.rfqitems.filter((x) => lineMatches(x.id, 'pricing')).length, icon: <PricingIcon fontSize="small" /> },
    { key: 'ready', label: `Ready for quote ${judgedScope}`, value: intelligenceQuery.isError ? '—' : rfq.rfqitems.filter((x) => lineMatches(x.id, 'ready')).length, icon: <ApproveIcon fontSize="small" /> },
  ];
  // The score is a heuristic rounded to two decimals by the server. Rendering "62.75%" claims a
  // precision the model does not have.
  const readinessPercent = intelligence ? Math.round(intelligence.readinessScore) : 0;
  const primaryBlocker = intelligence?.nextBestAction.explanation ?? (intelligenceQuery.isLoading ? 'Calculating from current commercial evidence' : 'Commercial intelligence unavailable');
  const readinessNarrative = intelligence && canPrepareQuote && intelligence.commercialDecision !== 'VIABLE_READY'
    ? 'Draft can start now. Resolve the highlighted commercial blockers before releasing it to the customer.'
    : primaryBlocker;
  // Why the screen's primary action is unavailable. `disabled` with no reason is the state a
  // single transient intelligence failure leaves the page in, and the panel-level alert further
  // down explains the panel, not the button.
  const prepareQuoteReason = quoteDraftMutation.isPending ? 'Preparing the quote draft…'
    : intelligenceQuery.isLoading ? 'Commercial readiness is still being calculated.'
    : intelligenceQuery.isError ? 'Commercial readiness is unavailable. You may start a draft; the server revalidates every release gate before the quote can be issued.'
    : !intelligence ? 'Commercial readiness has not been reported for this RFQ.'
    // Three states, not two. Matching the client gate to the server means a quote can now be
    // STARTED while lines still lack a fulfilment route — that is the normal case for a
    // distributor. Saying "every line has an evidence-backed fulfilment route" there would be
    // false, so the enabled-with-blockers case gets its own sentence: supply coverage is a
    // condition of quote RELEASE, not of starting one, and the reason now says exactly that.
    : !canPrepareQuote ? intelligence.nextBestAction.explanation
    : intelligence.commercialDecision === 'VIABLE_READY'
      ? 'Every line being quoted has an evidence-backed fulfilment route.'
      : `You can start the quote now. ${intelligence.nextBestAction.explanation}`;
  // A deadline is overdue only if it is a real date. `new Date('0001-01-01') < new Date()` is
  // perfectly true, which is how a sentinel used to be coloured and presented as a passed
  // customer deadline — the leak utils/dates.ts exists to close.
  const deadline = parseDateSafe(rfq.bidClosingDate);
  const overdue = deadline !== null && deadline < new Date();
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

        <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: { xs: 'flex-start', lg: 'center' }, gap: 2, flexDirection: { xs: 'column', lg: 'row' } }}>
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, flexWrap: 'wrap', minWidth: 0 }}>
            <Typography variant="h4" sx={{ fontWeight: 950, color: 'text.primary', letterSpacing: 0, whiteSpace: 'nowrap' }}>
              {rfq.rfqno}
            </Typography>
            {rfq.nexoraSerial ? (
              <Chip
                label={`Nexora Serial: ${rfq.nexoraSerial}`}
                size="small"
                variant="outlined"
                sx={{ fontWeight: 900, fontFamily: 'monospace' }}
              />
            ) : (
              // The API no longer borrows the lead's case for an RFQ that carries none, so this
              // state is now reachable and is stated plainly instead of leaving a blank header.
              <Chip
                label="Not linked to a case"
                size="small"
                color="warning"
                variant="outlined"
                sx={{ fontWeight: 900 }}
              />
            )}
            <Chip 
              label={rfq.rfqstatusValue || 'Unknown'} 
              color={isDraft ? "warning" : "success"}
              size="small"
              sx={{ fontWeight: 900, fontSize: '0.65rem', textTransform: 'uppercase' }}
            />
          </Box>
          <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap', position: { lg: 'sticky' }, top: 8, zIndex: 2 }}>
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
            {/*
              The sourcing workbench for THIS RFQ had no door on this page. It was reachable only
              by creating a sourcing case from one line, or through a server-supplied
              "Open recommended action" whose label never said where it went — so the sourcing step
              of the journey was, in navigation terms, undiscoverable from the RFQ it belongs to.
            */}
            {hasPermission('Supplier History') && (
              <Button
                variant="outlined"
                startIcon={<WorkbenchIcon />}
                onClick={() => navigate(`/procurement/rfqs/${rfq.id}/sourcing`)}
                sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}
              >
                Sourcing
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
            {hasPermission('Quotations', 'create') && (
              // A disabled button wrapped in a Tooltip needs a focusable element between them,
              // otherwise the reason is unreachable by pointer and by keyboard alike.
              <Tooltip title={prepareQuoteReason}>
                <Box
                  component="span"
                  role={canPrepareQuote ? undefined : 'button'}
                  aria-disabled={canPrepareQuote ? undefined : 'true'}
                  tabIndex={canPrepareQuote ? -1 : 0}
                  aria-label={prepareQuoteReason}
                  sx={{
                    display: 'inline-flex',
                    borderRadius: 2,
                    '&:focus-visible': { outline: '3px solid', outlineColor: 'primary.main', outlineOffset: 2 },
                  }}
                >
                  <Button variant="contained" color="success" startIcon={<QuoteDraftIcon />}
                    onClick={() => quoteDraftMutation.mutate()} disabled={!canPrepareQuote || quoteDraftMutation.isPending}
                    sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}>
                    Prepare Quote Draft
                  </Button>
                </Box>
              </Tooltip>
            )}
          </Stack>
        </Box>
        <Paper sx={{ mt: 2, p: 2, borderRadius: 1, border: '1px solid', borderColor: 'divider', bgcolor: 'background.paper' }}>
          <Grid container spacing={2} sx={{ alignItems: 'center' }}>
            <Grid size={{ xs: 12, md: 3 }}><DataField label="Customer / contact" value={`${rfq.customerName || rfq.buyersName || 'Unresolved'}${rfq.contactName ? ` · ${rfq.contactName}` : ''}`} /></Grid>
            <Grid size={{ xs: 6, md: 2 }}><DataField label="Account Owner" value={rfq.accountOwnerName || 'Unassigned'} /></Grid>
            <Grid size={{ xs: 6, md: 2 }}><DataField label="Opportunity Owner" value={rfq.opportunityOwnerName || 'Unassigned'} /></Grid>
            <Grid size={{ xs: 6, md: 2 }}><DataField label="Customer deadline" value={formatDateSafe(rfq.bidClosingDate || null)} color={overdue ? 'error.main' : 'text.primary'} /></Grid>
            <Grid size={{ xs: 6, md: 3 }}>
              <Typography variant="caption" color="text.secondary">Commercial readiness</Typography>
              {/* A determinate bar pinned at 0 while the request is in flight is an assertion,
                  and it is indistinguishable from an RFQ that genuinely scores zero. */}
              <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}><LinearProgress variant={intelligence ? 'determinate' : 'indeterminate'} value={readinessPercent} sx={{ flex: 1, height: 8, borderRadius: 1 }} /><Typography sx={{ fontWeight: 800, fontVariantNumeric: 'tabular-nums' }}>{intelligence ? `${readinessPercent}%` : '—'}</Typography></Stack>
              <Typography variant="caption" color={intelligence?.commercialDecision === 'VIABLE_READY' ? 'success.main' : 'warning.main'} sx={{ display: 'block' }}>{readinessNarrative}</Typography>
            </Grid>
          </Grid>
        </Paper>
      </Box>

      <CommercialProcessingEvidence resource="rfqs" id={rfq.id} />

      <Stack direction="row" spacing={1} sx={{ mb: 1, alignItems: 'center', flexWrap: 'wrap' }}>
        {/* Clicking a tile silently swapped the table contents with no indication of which filter
            was applied and no labelled way back. The live region announces the change for a
            screen reader; the same sentence is the visible half, with a labelled reset beside
            it — the Total tile was the only way back and is not labelled as one. */}
        <Typography variant="caption" role="status" aria-live="polite" sx={{ fontWeight: 700, color: 'text.secondary' }}>
          {lineFilter === 'all'
            ? `Showing all ${rfq.rfqitems.length} line${rfq.rfqitems.length === 1 ? '' : 's'}`
            : `Showing ${visibleItems.length} of ${rfq.rfqitems.length} lines · ${summary.find((tile) => tile.key === lineFilter)?.label ?? statusLabel(lineFilter)}`}
        </Typography>
        {lineFilter !== 'all' && (
          <Button size="small" variant="text" onClick={() => setLineFilter('all')} sx={{ fontWeight: 700 }}>
            Show all lines
          </Button>
        )}
      </Stack>

      <Grid container spacing={1.5} sx={{ mb: 3 }}>
        {summary.map((item) => (
          <Grid key={item.key} size={{ xs: 6, sm: 4, md: 3, xl: 1.5 }}>
            <ButtonBase onClick={() => setLineFilter(item.key)} sx={{ width: '100%', textAlign: 'left', borderRadius: 1 }} aria-pressed={lineFilter === item.key}>
              <Paper sx={{ width: '100%', minHeight: 88, p: 1.5, borderRadius: 1, border: '1px solid', borderColor: lineFilter === item.key ? 'primary.main' : 'divider', bgcolor: lineFilter === item.key ? 'action.selected' : 'background.paper' }}>
                <Stack direction="row" sx={{ justifyContent: 'space-between', color: 'text.secondary' }}>{item.icon}<Typography variant="h6" sx={{ fontWeight: 900, fontVariantNumeric: 'tabular-nums' }}>{item.value}</Typography></Stack>
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
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Current Lead revision" value={`Revision ${rfq.activeLeadRevision || 1}`} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}>
                  {rfq.leadId && commercialAccess.canViewLeadEvidence ? (
                    <Button size="small" variant="outlined" onClick={() => navigate(`/procurement/leads/view/${rfq.leadId}`)}>
                      Open Canonical Lead
                    </Button>
                  ) : null}
                </Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Customer / Buyer" value={rfq.buyersName || rfq.customerName || 'N/A'} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Customer Email" value={rfq.customerEmail || rfq.leadEmail || 'N/A'} /></Grid>
                
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Received Date" value={formatDateSafe(rfq.recDate)} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Bid Closing Date" value={formatDateSafe(rfq.bidClosingDate || null)} color={overdue ? 'error.main' : 'text.primary'} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="RFQ Type" value={rfq.rfqtype || 'Agreement'} /></Grid>

                <Grid size={{ xs: 12, md: 4 }}><DataField label="Created By" value={rfq.createdBy} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Created On" value={formatDateSafe(rfq.createdDate)} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Business Unit" value={rfq.businessUnitName || null} /></Grid>
              </Grid>

              {rfq.headerRemarks && (
                <Box sx={{ mt: 2 }}>
                  <Divider sx={{ mb: 2 }} />
                  <DataField label="Header Remarks" value={rfq.headerRemarks} bold={false} />
                </Box>
              )}
            </Paper>

            <Paper component="section" aria-labelledby="customer-request-terms-heading" sx={{ p: 3, borderRadius: 3, border: '1px solid', borderColor: 'divider' }}>
              <Typography id="customer-request-terms-heading" component="h2" sx={{ fontWeight: 900, fontSize: '1rem', color: 'text.primary', textTransform: 'uppercase', letterSpacing: '0.025em', mb: 0.5 }}>
                Customer request terms
              </Typography>
              <Typography variant="body2" color="text.secondary" sx={{ mb: 2.5 }}>
                Read-only values preserved from the immutable Lead revision used to create this RFQ.
              </Typography>
              <Grid container spacing={2}>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Customer RFQ reference" value={rfq.customerRfqReference ?? null} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Required delivery date" value={formatDateSafe(rfq.requiredDeliveryDate ?? null)} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Delivery location" value={rfq.deliveryLocation ?? null} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Agreement reference" value={rfq.agreementReference ?? null} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Closing date (Hijri)" value={rfq.bidClosingDateHijri ?? null} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Inquiry type" value={rfq.inquiryType ?? null} /></Grid>
              </Grid>
            </Paper>

            <Box sx={{ mb: 2 }}><CommercialLineIntelligence stage="rfq" recordId={rfq.id} /></Box>

            {intelligenceQuery.isError && <Alert severity="error" action={<Button color="inherit" onClick={() => intelligenceQuery.refetch()}>Retry</Button>}>Commercial intelligence could not be reconciled.</Alert>}
            {intelligence && <Paper variant="outlined" sx={{ p: 2.5, borderRadius: 1 }}>
              <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', mb: 2 }}>
                <Box>
                  <Typography sx={{ fontWeight: 900 }}>Opportunity Digital Twin</Typography>
                  <Typography variant="body2" color="text.secondary">{intelligence.nextBestAction.label}</Typography>
                  <Typography variant="caption" color="text.secondary">Confidence {Math.round(intelligence.nextBestAction.confidence * 100)}% · {intelligence.digitalTwin.validity}</Typography>
                </Box>
                {canOpenRecommendedAction && <Button variant="outlined" onClick={() => navigate(intelligence.nextBestAction.overrideAction)}>Open recommended action</Button>}
              </Stack>
              <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, 1fr)', xl: 'repeat(4, 1fr)' }, gap: 1.5 }}>
                {intelligence.digitalTwin.scenarios.map((scenario) => <Box key={scenario.code} sx={{ border: '1px solid', borderColor: 'divider', p: 1.5, minHeight: 132 }}>
                  <Stack direction="row" spacing={1} sx={{ justifyContent: 'space-between', alignItems: 'center' }}><Typography variant="subtitle2" sx={{ fontWeight: 800 }}>{scenario.label}</Typography><Chip size="small" icon={scenario.eligible ? <ApproveIcon /> : <BlockerIcon />} color={scenario.eligible ? 'success' : 'default'} label={scenario.eligible ? 'Eligible' : 'Evidence needed'} /></Stack>
                  <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>{scenario.explanation}</Typography>
                  {scenario.estimatedLandedCost != null && <Typography variant="body2" sx={{ mt: 1, fontWeight: 800 }}>Landed total {formatScenarioMoney(scenario.estimatedLandedCost, scenario.currencyCode)} · {scenario.estimatedLeadTimeDays ?? '—'} days</Typography>}
                  <Typography variant="caption" sx={{ display: 'block', mt: 0.75, fontWeight: 700 }}>Risk {statusLabel(scenario.riskBand)} · Confidence {Math.round(scenario.confidence * 100)}%</Typography>
                  {scenario.validUntil && <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>Valid to {formatDateSafe(scenario.validUntil)}</Typography>}
                  {scenario.quantities.length > 0 && <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>Stock {scenario.quantities.reduce((sum, line) => sum + line.immediateStockQuantity, 0)} · Supplier {scenario.quantities.reduce((sum, line) => sum + line.supplierQuantity, 0)}</Typography>}
                  <Box component="details" sx={{ mt: 1 }}>
                    <Typography component="summary" variant="caption" sx={{ fontWeight: 800, cursor: 'pointer' }}>Evidence and assumptions</Typography>
                    <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.75 }}>{scenario.riskExplanation}</Typography>
                    {scenario.costSources.map((source, index) => <Typography key={`${source.sourceType}-${index}`} variant="caption" sx={{ display: 'block' }}>{source.label}: {source.amount != null ? formatScenarioMoney(source.amount, scenario.currencyCode) : statusLabel(source.status)}</Typography>)}
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
                    <Typography variant="caption" sx={{ display: 'block', fontWeight: 800 }}>Line {line.rfqItemId}: {line.recommendedUnitPrice != null ? formatScenarioMoney(line.recommendedUnitPrice, line.currencyCode) : 'withheld'} · {line.customerOrderSampleSize} order outcomes</Typography>
                    <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>Winning range {line.winningRangeLow != null && line.winningRangeHigh != null ? `${formatScenarioMoney(line.winningRangeLow, line.currencyCode)} to ${formatScenarioMoney(line.winningRangeHigh, line.currencyCode)}` : 'insufficient'} · walk-forward MAPE {line.backtestMeanAbsolutePercentError != null ? `${line.backtestMeanAbsolutePercentError}% (${line.backtestHoldoutCount} holdouts)` : 'not measured'} · decided-cohort conversion baseline {line.cohortConversionBaseline != null ? `${Math.round(line.cohortConversionBaseline * 100)}%` : 'withheld'}</Typography>
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
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }} align="right">Qty</TableCell>
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }}>Resolution</TableCell>
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }}>Inventory</TableCell>
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }}>Supplier Quotes</TableCell>
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }}>Source Lead line</TableCell>
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }}>Next Action</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {visibleItems.map((item, idx) => {
                    const extraFields = presentRfqExtraFields(item.extraFields);
                    return (
                    <TableRow key={item.id} hover sx={{ '&:last-child td': { border: 0 } }}>
                      {/* The buyer's line number is the identifier they will quote back at you, so
                          a renumbering index is actively wrong — and it shifted every time a tile
                          filter was applied. Falls back to the position only when the document
                          carried no line number. */}
                      <TableCell sx={{ fontSize: '0.8rem', fontWeight: 700, color: 'text.secondary', fontVariantNumeric: 'tabular-nums' }}>{item.lineItemNo || idx + 1}</TableCell>
                      <TableCell>
                        <Typography sx={{ fontSize: '0.8rem', fontWeight: 800, color: 'primary.main' }}>
                          {item.productName || item.productShortName || 'Unknown Product'}
                        </Typography>
                        <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block' }}>
                          {item.productShortDescription || 'No description'}
                        </Typography>
                        {extraFields.fields.length > 0 ? (
                          <Box component="details" sx={{ mt: 0.75, maxWidth: 360 }}>
                            <Typography component="summary" variant="caption" sx={{ cursor: 'pointer', fontWeight: 800 }}>
                              Customer fields ({extraFields.fields.length + extraFields.hiddenCount})
                            </Typography>
                            <Stack spacing={0.25} sx={{ mt: 0.5 }}>
                              {extraFields.fields.map((field, fieldIndex) => (
                                <Typography key={`${field.label}-${fieldIndex}`} variant="caption" sx={{ overflowWrap: 'anywhere' }}>
                                  <Box component="span" sx={{ fontWeight: 800 }}>{field.label}:</Box> {field.value}
                                </Typography>
                              ))}
                              {extraFields.hiddenCount > 0 ? (
                                <Typography variant="caption" color="text.secondary">
                                  {extraFields.hiddenCount} additional customer field{extraFields.hiddenCount === 1 ? '' : 's'} retained in the source record.
                                </Typography>
                              ) : null}
                            </Stack>
                          </Box>
                        ) : extraFields.invalid ? (
                          <Typography variant="caption" color="warning.main" sx={{ display: 'block', mt: 0.5 }}>
                            Customer fields could not be displayed safely.
                          </Typography>
                        ) : null}
                      </TableCell>
                      <TableCell>
                        <Typography sx={{ fontSize: '0.8rem', fontWeight: 700 }}>
                          {item.manufacturerName || 'N/A'}
                        </Typography>
                        <Typography variant="caption" sx={{ color: 'text.disabled' }}>
                          {item.manufacturerPartNumber || 'N/A'}
                        </Typography>
                      </TableCell>
                      <TableCell align="right" sx={{ fontSize: '0.85rem', fontWeight: 900, fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' }}>
                        {item.quantity?.toLocaleString()} {item.unitOfMeasure || 'EA'}
                      </TableCell>
                      <TableCell>
                        <Stack spacing={0.75} sx={{ alignItems: 'flex-start' }}>
                          {(() => {
                            const resolution = persistedResolutions.get(item.id);
                            // A human catalogue decision is authoritative even when the earlier
                            // inventory-resolution snapshot did not contain an automatic match.
                            if (item.productResolvedOn && item.productId) {
                              return (
                                <Tooltip title={`${item.productName || `Product ${item.productId}`} · ${item.productResolutionReason || 'Reason not returned'}`}>
                                  <Chip size="small" icon={<ApproveIcon />} label="Human-resolved product" color="success" variant="outlined" />
                                </Tooltip>
                              );
                            }
                            // Colour alone does not carry a status: each state also has a shape.
                            if (lineResolutionQuery.isLoading) return <Chip size="small" icon={<WaitingIcon />} label="Resolution checking" variant="outlined" />;
                            if (lineResolutionQuery.isError) return <Chip size="small" icon={<UnavailableIcon />} label="Resolution unavailable" color="error" variant="outlined" />;
                            if (!resolution) return <Chip size="small" icon={<UndecidedIcon />} label="Resolution not recorded" color="warning" variant="outlined" />;
                            const reviewed = resolution.productResolution?.decisionState?.toLowerCase().includes('approved') || resolution.classification === 'KnownInStock' || resolution.classification === 'KnownIncoming' || resolution.classification === 'KnownShortage';
                            return <Tooltip title={`Evidence: ${resolution.evidenceReference || 'not recorded'}`}><Chip size="small" icon={reviewed ? <ApproveIcon /> : <BlockerIcon />} label={reviewed ? 'Persisted resolution' : statusLabel(resolution.classification)} color={reviewed ? 'success' : 'warning'} variant="outlined" /></Tooltip>;
                          })()}
                          {commercialAccess.canResolveRfqProduct && (
                            <Button size="small" variant="text" onClick={() => openProductResolution(item)}>
                              {item.productResolvedOn ? 'Change product' : 'Resolve catalogue product'}
                            </Button>
                          )}
                        </Stack>
                      </TableCell>
                      <TableCell>
                        <Typography variant="caption" sx={{ fontWeight: 700 }}>{sourcingLines.get(item.id) ? statusLabel(sourcingLines.get(item.id)!.resolution) : 'Checking'}</Typography>
                        <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>ATP {sourcingLines.get(item.id)?.availableQuantity ?? '—'} · Short {sourcingLines.get(item.id)?.shortfallQuantity ?? '—'}</Typography>
                      </TableCell>
                      <TableCell><Stack spacing={0.5} sx={{ alignItems: 'flex-start' }}><Chip size="small" icon={(intelligenceLines.get(item.id)?.eligibleOfferCount ?? 0) > 0 ? <ApproveIcon /> : <UndecidedIcon />} label={`${intelligenceLines.get(item.id)?.eligibleOfferCount ?? 0}/${offersByLine.get(item.id) ?? 0} eligible`} color={(intelligenceLines.get(item.id)?.eligibleOfferCount ?? 0) > 0 ? 'success' : 'default'} />{(intelligenceLines.get(item.id)?.bidQualityFlags.length ?? 0) > 0 && <Typography variant="caption" color="warning.main">{intelligenceLines.get(item.id)?.bidQualityFlags.length} quality finding(s)</Typography>}</Stack></TableCell>
                      <TableCell>
                        {item.sourceLeadItemRevisionId ? (
                          <Stack spacing={0.25}>
                            <Typography variant="caption" sx={{ fontWeight: 800, fontFamily: 'monospace' }}>Immutable source record #{item.sourceLeadItemRevisionId}</Typography>
                            <Typography variant="caption" color="text.secondary">Lead item revision database identity</Typography>
                          </Stack>
                        ) : (
                          <Typography variant="caption" color="warning.main" sx={{ fontWeight: 700 }}>Trace unavailable</Typography>
                        )}
                      </TableCell>
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
                            return <Chip size="small" icon={<InventoryIcon />} color="success" variant="outlined" label="Use company inventory" />;
                          }
                          return (
                            <Stack spacing={0.75} sx={{ alignItems: 'flex-start' }}>
                              <Typography variant="caption" color="error.main" sx={{ fontWeight: 700 }}>
                                {sourcingLine.shortfallQuantity} to source · {statusLabel(sourcingLine.resolution)}
                              </Typography>
                              {commercialAccess.canCreateOrOpenSourcingCase && (
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
                  );})}
                  {/* The body renders `visibleItems`, so this has to test `visibleItems`. Keyed on
                      `rfq.rfqitems` it fired only for an RFQ with no lines at all — the one case
                      where "match this filter" is meaningless — and a filter that genuinely
                      matched nothing produced a table body with no rows and no explanation. */}
                  {visibleItems.length === 0 && (
                    <TableRow>
                      <TableCell colSpan={9} sx={{ py: 4, textAlign: 'center', color: 'text.disabled' }}>
                        {rfq.rfqitems.length === 0
                          ? 'This RFQ has no lines yet.'
                          : `No line matches "${summary.find((tile) => tile.key === lineFilter)?.label ?? statusLabel(lineFilter)}". ${rfq.rfqitems.length} line${rfq.rfqitems.length === 1 ? '' : 's'} on this RFQ.`}
                      </TableCell>
                    </TableRow>
                  )}
                </TableBody>
              </Table></Box>
              <Box sx={{ p: 2, display: 'flex', justifyContent: 'flex-end', bgcolor: '#fafafa' }}>
                <Stack direction="row" spacing={4}>
                   <Box sx={{ textAlign: 'right' }}>
                      <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase' }}>Total lines</Typography>
                      <Typography sx={{ fontWeight: 900, fontSize: '1.1rem', fontVariantNumeric: 'tabular-nums' }}>{rfq.rfqitems.length}</Typography>
                   </Box>
                   <Box sx={{ textAlign: 'right' }}>
                      <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase' }}>Total quantity</Typography>
                      <Typography sx={{ fontWeight: 900, fontSize: '1.1rem', fontVariantNumeric: 'tabular-nums' }}>{rfq.rfqitems.reduce((sum, item) => sum + (item.quantity ?? 0), 0).toLocaleString()}</Typography>
                   </Box>
                </Stack>
              </Box>
            </Paper>
          </Stack>
        </Grid>

        {/* Sidebar */}
        <Grid size={{ xs: 12, lg: 3 }}>
          <Stack spacing={3}>
            {/* Immutable Lead-to-RFQ lineage; no participation is editable after promotion. */}
            <Paper component="section" aria-labelledby="promotion-lineage-heading" sx={{ p: 2.5, borderRadius: 3, border: '1px solid', borderColor: 'divider', bgcolor: 'primary.lighter' }}>
                <Typography variant="caption" sx={{ fontWeight: 900, color: 'primary.main', textTransform: 'uppercase', mb: 1, display: 'block' }}>
                  RFQ promotion lineage
                </Typography>
                <Typography id="promotion-lineage-heading" sx={{ fontWeight: 900, fontSize: '0.95rem', mb: 1.5 }}>
                  {rfq.promotionId ? 'Governed promotion receipt' : 'Promotion receipt unavailable'}
                </Typography>
                {rfq.promotionId ? (
                  <>
                    <DataField label="Lead" value={rfq.leadId ? `#${rfq.leadId}` : null} />
                    <DataField
                      label="Immutable Lead revision"
                      value={rfq.sourceLeadRevisionNumber
                        ? `Revision ${rfq.sourceLeadRevisionNumber}`
                        : rfq.sourceLeadRevisionId ? `Revision record #${rfq.sourceLeadRevisionId}` : null}
                    />
                    <DataField label="Participation decision" value={rfq.participationDecisionId ? `#${rfq.participationDecisionId}` : null} />
                    <DataField label="Participation version" value={rfq.participationVersion ? `Version ${rfq.participationVersion}` : null} />
                    <DataField label="Promotion receipt" value={`#${rfq.promotionId}`} />
                    <DataField label="Promoted by" value={rfq.promotedBy ?? null} />
                    <DataField label="Promoted on" value={formatDateSafe(rfq.promotedAtUtc ?? null)} />
                  </>
                ) : (
                  <Alert severity="warning" sx={{ mb: 1.5 }}>
                    This RFQ has no governed promotion receipt in the response. It may be a legacy or manually created record; immutable source lineage cannot be claimed.
                  </Alert>
                )}
                {rfq.leadId && commercialAccess.canViewLeadEvidence ? (
                  <Button
                    fullWidth variant="contained" size="small"
                    onClick={() => navigate(`/procurement/leads/${rfq.leadId}/workbench`)}
                    sx={{ textTransform: 'none', fontWeight: 800, borderRadius: 2 }}
                  >
                    Open Lead decision record
                  </Button>
                ) : null}
            </Paper>

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
                          <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.65rem' }}>{formatDateSafe(log.date)}</Typography>
                       </Box>
                    </Box>
                  ))}
               </Stack>
            </Paper>
          </Stack>
        </Grid>
      </Grid>

      <Dialog
        open={Boolean(productResolutionItem)}
        onClose={() => !productResolutionMutation.isPending && setProductResolutionItemId(null)}
        maxWidth="sm"
        fullWidth
      >
        <DialogTitle>Resolve catalogue product</DialogTitle>
        <DialogContent dividers>
          <Stack spacing={2}>
            <Alert severity="warning">
              This is a governed commercial decision. Choose the product that the customer line actually requests;
              Supplier discovery, inventory coverage and pricing will use this identity.
            </Alert>
            <Paper variant="outlined" sx={{ p: 2 }}>
              <Typography variant="caption" color="text.secondary">Customer line</Typography>
              <Typography sx={{ fontWeight: 800 }}>
                {productResolutionItem?.manufacturerPartNumber || productResolutionItem?.itemMaterialCode || 'No customer part number'}
              </Typography>
              <Typography variant="body2" color="text.secondary">
                {productResolutionItem?.productShortDescription || productResolutionItem?.itemText || productResolutionItem?.materialPotext || 'No description recorded'}
              </Typography>
            </Paper>
            <Autocomplete
              openOnFocus
              options={productSearchQuery.data ?? []}
              loading={productSearchQuery.isLoading}
              value={selectedProduct}
              onChange={(_, value) => {
                setSelectedProduct(value);
                if (value) setProductSearch(`${value.partNo} · ${value.productName || 'Unnamed product'}`);
              }}
              inputValue={productSearch}
              onInputChange={(_, value, reason) => (reason === 'input' || reason === 'clear') && setProductSearch(value)}
              isOptionEqualToValue={(option, value) => option.id === value.id}
              getOptionLabel={(option) => `${option.partNo} · ${option.productName || 'Unnamed product'}`}
              noOptionsText={productSearchQuery.isError
                ? 'Product search is unavailable. No catalogue decision was saved.'
                : 'No tenant catalogue product matches this search.'}
              renderInput={(params) => (
                <TextField {...params} label="Tenant catalogue product" required helperText="Search by part number or product name." />
              )}
              renderOption={(props, option) => {
                const { key, ...optionProps } = props;
                return (
                  <li key={key} {...optionProps}>
                    <Box>
                      <Typography variant="body2" sx={{ fontWeight: 800 }}>{option.partNo}</Typography>
                      <Typography variant="caption" color="text.secondary">
                        {option.productName || 'Unnamed product'} · On hand {option.qtyOnHand ?? 0}
                      </Typography>
                    </Box>
                  </li>
                );
              }}
            />
            <TextField
              label="Resolution reason"
              required
              multiline
              minRows={3}
              value={productResolutionReason}
              onChange={(event) => setProductResolutionReason(event.target.value)}
              helperText="Record the customer part, drawing, specification or catalogue evidence used for this decision."
            />
            {productResolutionItem?.productResolvedOn && (
              <Alert severity="info">
                Current product: {productResolutionItem.productName || `Product ${productResolutionItem.productId}`}.
                A correction is allowed until Supplier outreach has been prepared.
              </Alert>
            )}
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setProductResolutionItemId(null)} disabled={productResolutionMutation.isPending}>Cancel</Button>
          <Button
            variant="contained"
            onClick={() => productResolutionMutation.mutate()}
            disabled={!selectedProduct || !productResolutionReason.trim() || productResolutionMutation.isPending}
          >
            {productResolutionMutation.isPending ? 'Saving resolution…' : 'Save product resolution'}
          </Button>
        </DialogActions>
      </Dialog>

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
        <DataField label="Extraction location" value="Open the Lead decision record's Evidence stage to inspect document, page, sheet, row, or bounding-box evidence." bold={false} />
        {Boolean(rfq.leadId) && commercialAccess.canViewLeadEvidence && (
          <Button
            variant="outlined"
            startIcon={<EvidenceIcon />}
            onClick={() => navigate(`/procurement/leads/${rfq.leadId}/workbench?stage=evidence`)}
          >
            Open exact source evidence
          </Button>
        )}
      </Drawer>
    </Box>
  );
};

export default ViewRFQPage;
