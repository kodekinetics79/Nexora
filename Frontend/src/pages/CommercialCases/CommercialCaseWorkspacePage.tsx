import React from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { isAxiosError } from 'axios';
import {
  Box,
  Breadcrumbs,
  Button,
  Chip,
  Divider,
  Grid,
  Link,
  List,
  ListItemButton,
  Paper,
  Stack,
  Tab,
  Tabs,
  Typography,
  CircularProgress,
  Alert,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControl,
  InputLabel,
  MenuItem,
  Select,
  TextField,
} from '@mui/material';
import {
  ArrowForward as OpenIcon,
  NavigateNext as NextIcon,
  Search as SearchIcon,
  Assignment as LeadIcon,
  ReceiptLong as RfqIcon,
  Description as QuoteIcon,
  AssignmentTurnedIn as OrderIcon,
  LocalShipping as ShipmentIcon,
  Storefront as SupplierIcon,
  ShoppingCartCheckout as PurchaseIcon,
  SyncAlt as HandoffIcon,
} from '@mui/icons-material';
import commercialCaseService, {
  type CommercialCaseDetail,
  type CommercialCaseDocument,
  type CommercialCaseTraceabilityGap,
} from '../../api/services/commercialCaseService';
import SearchField from '../../components/common/SearchField';
import { formatDateSafe } from '../../utils/dates';
import procurementService from '../../api/services/procurementService';
import commercialIntelligenceService from '../../api/services/commercialIntelligenceService';
import commercialLearningService from '../../api/services/commercialLearningService';
import opportunityPriorityService, {
  createOpportunityCommandIdentity,
  type OpportunityFeedbackCode,
} from '../../api/services/opportunityPriorityService';
import { useAuth } from '../../context/AuthContext';
import { statusLabel } from '../../utils/statusLabels';

const DOC_ORDER: CommercialCaseDocument['documentType'][] = [
  'Lead', 'RFQ', 'SourcingCase', 'SupplierRFQ', 'SupplierQuote',
  'Quote', 'ClientPO', 'Order', 'SupplierPO', 'ProcurementHandoff', 'Shipment',
];

const GAP_HEADLINE: Record<CommercialCaseTraceabilityGap['gapKind'], string> = {
  UnlinkedDocument: 'States no commercial case',
  ConflictingCase: 'States a different commercial case',
  ChainBroken: 'Document chain no longer reaches it',
  CustomerOriginMissing: 'Names no customer document it was bought for',
};

/**
 * Traceability gaps are shown, never hidden. The timeline is assembled from what each document
 * DECLARES, so a record that is reachable through the old foreign-key chain but carries no case
 * — or the wrong one — is genuinely not part of this case. Omitting it silently would make an
 * incomplete spine look complete, which is the failure this panel exists to prevent.
 */
const TraceabilityGapPanel: React.FC<{ gaps: CommercialCaseTraceabilityGap[] }> = ({ gaps }) => {
  if (gaps.length === 0) return null;
  return (
    <Alert severity="warning" sx={{ borderRadius: 2 }}>
      <Typography sx={{ fontWeight: 900, mb: 0.5 }}>
        {gaps.length} traceability {gaps.length === 1 ? 'gap' : 'gaps'}
      </Typography>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
        This case is assembled from the documents that name it. The records below were reached by
        following the old document chain and disagree with what they state.
      </Typography>
      <Stack spacing={1}>
        {gaps.map(gap => (
          <Box key={`${gap.gapKind}-${gap.documentType}-${gap.documentId}`}>
            <Typography variant="body2" sx={{ fontWeight: 800 }}>
              {gap.documentType} {gap.reference} — {GAP_HEADLINE[gap.gapKind] ?? gap.gapKind}
            </Typography>
            <Typography variant="caption" color="text.secondary">{gap.detail}</Typography>
          </Box>
        ))}
      </Stack>
    </Alert>
  );
};

const DataField: React.FC<{ label: string; value: React.ReactNode }> = ({ label, value }) => (
  <Box>
    <Typography
      variant="caption"
      sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', display: 'block', mb: 0.35, fontSize: '0.65rem' }}
    >
      {label}
    </Typography>
    <Typography sx={{ fontWeight: 700, color: 'text.primary', lineHeight: 1.45 }}>
      {value}
    </Typography>
  </Box>
);

const typeIcon = (type: CommercialCaseDocument['documentType']) => {
  switch (type) {
    case 'Lead': return <LeadIcon fontSize="small" />;
    case 'RFQ': return <RfqIcon fontSize="small" />;
    case 'Quote': return <QuoteIcon fontSize="small" />;
    case 'Order': return <OrderIcon fontSize="small" />;
    case 'Shipment': return <ShipmentIcon fontSize="small" />;
    case 'SourcingCase':
    case 'SupplierRFQ':
    case 'SupplierQuote': return <SupplierIcon fontSize="small" />;
    case 'ClientPO':
    case 'SupplierPO': return <PurchaseIcon fontSize="small" />;
    case 'ProcurementHandoff': return <HandoffIcon fontSize="small" />;
    default: return <LeadIcon fontSize="small" />;
  }
};

const openDocument = (navigate: ReturnType<typeof useNavigate>, doc: CommercialCaseDocument) => {
  const routes: Record<string, string> = {
    Lead: `/procurement/leads/view/${doc.documentId}`,
    RFQ: `/procurement/rfqs/view/${doc.documentId}`,
    Quote: `/sales/quotes/view/${doc.documentId}`,
    Order: `/sales/orders/${doc.documentId}`,
    Shipment: `/sales/shipments/${doc.documentId}`,
    SourcingCase: `/procurement/sourcing-cases/${doc.documentId}`,
    SupplierRFQ: doc.parentDocumentId ? `/procurement/rfqs/${doc.parentDocumentId}/sourcing` : '/procurement/rfqs/all?state=requires-sourcing',
    SupplierQuote: `/procurement/supplier-quotes/${doc.documentId}`,
    ClientPO: `/sales/client-pos/${doc.documentId}`,
    SupplierPO: '/suppliers/purchase-orders',
    ProcurementHandoff: '/procurement/handoffs',
  };
  const target = routes[doc.documentType];
  if (target) navigate(target);
};

const caseAge = (createdOn: string) => {
  const created = new Date(createdOn);
  if (Number.isNaN(created.getTime())) return '—';
  const days = Math.max(0, Math.floor((Date.now() - created.getTime()) / (1000 * 60 * 60 * 24)));
  if (days === 0) return 'Today';
  if (days === 1) return '1 day old';
  return `${days} days old`;
};

const percent = (value: number) => `${value <= 1 ? Math.round(value * 100) : Math.round(value)}%`;

const feedbackLabels: Record<OpportunityFeedbackCode, string> = {
  Accepted: 'Agree with recommendation',
  Rejected: 'Reject recommendation',
  Replaced: 'Suggest another action',
  Deferred: 'Defer assessment',
  Reverted: 'Revert latest feedback',
};

const CommercialCaseWorkspacePage: React.FC = () => {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { hasPermission, userData } = useAuth();
  const { id } = useParams<{ id?: string }>();
  const [searchParams] = useSearchParams();
  const [query, setQuery] = React.useState(() => searchParams.get('search') ?? '');
  const [tab, setTab] = React.useState(0);
  const [feedbackOpen, setFeedbackOpen] = React.useState(false);
  const [feedbackCode, setFeedbackCode] = React.useState<OpportunityFeedbackCode>('Accepted');
  const [feedbackReason, setFeedbackReason] = React.useState('');
  const [replacementActionCode, setReplacementActionCode] = React.useState('');
  const [feedbackRecorded, setFeedbackRecorded] = React.useState(false);
  const feedbackReasonInputRef = React.useRef<HTMLTextAreaElement | null>(null);

  const searchTerm = query.trim();
  const { data: searchResults, isLoading: searchLoading, isError: searchError, refetch: retrySearch } = useQuery({
    queryKey: ['commercial-cases', 'search', searchTerm],
    queryFn: () => commercialCaseService.search(searchTerm, 25),
    enabled: searchTerm.length >= 2,
  });

  const selectedCaseId = React.useMemo(() => {
    if (id && Number.isFinite(Number(id))) return Number(id);
    return searchResults?.[0]?.id;
  }, [id, searchResults]);

  const { data: detail, isLoading: detailLoading, isError: detailError, refetch: retryDetail } = useQuery({
    queryKey: ['commercial-case', selectedCaseId],
    queryFn: () => commercialCaseService.getById(selectedCaseId ?? 0),
    enabled: !!selectedCaseId,
  });
  const priorityQueryKey = ['opportunity-priority', 'commercial-case', selectedCaseId] as const;
  const opportunityPriority = useQuery({
    queryKey: priorityQueryKey,
    queryFn: () => opportunityPriorityService.getForCommercialCase(selectedCaseId ?? 0),
    enabled: !!selectedCaseId,
    retry: (failureCount, error) => !isAxiosError(error) || error.response?.status !== 404 ? failureCount < 1 : false,
  });
  const feedbackMutation = useMutation({
    mutationFn: () => {
      if (!opportunityPriority.data) throw new Error('No current recommendation is available.');
      const identity = createOpportunityCommandIdentity();
      return opportunityPriorityService.recordFeedback(opportunityPriority.data.recommendationId, {
        ...identity,
        expectedRecommendationId: opportunityPriority.data.recommendationId,
        decision: feedbackCode,
        replacementActionCode: feedbackCode === 'Replaced' ? replacementActionCode.trim() : undefined,
        reason: feedbackReason.trim(),
        supersedesFeedbackId: feedbackCode === 'Reverted'
          ? opportunityPriority.data.latestFeedback?.id
          : undefined,
      });
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: priorityQueryKey });
      setFeedbackOpen(false);
      setFeedbackReason('');
      setReplacementActionCode('');
      setFeedbackRecorded(true);
    },
    onError: (error) => {
      if (isAxiosError(error) && error.response?.status === 409) {
        void queryClient.invalidateQueries({ queryKey: priorityQueryKey });
      }
    },
  });
  const primaryRfqId = detail?.documents
    .filter(document => document.documentType === 'RFQ')
    .sort((left, right) => {
      const rightOccurredOn = right.occurredOn ? new Date(right.occurredOn).getTime() : 0;
      const leftOccurredOn = left.occurredOn ? new Date(left.occurredOn).getTime() : 0;
      return rightOccurredOn - leftOccurredOn || right.documentId - left.documentId;
    })[0]
    ?.documentId;
  const workbench = useQuery({
    queryKey: ['commercial-case', selectedCaseId, 'rfq-workbench', primaryRfqId],
    queryFn: () => procurementService.getWorkbench(primaryRfqId),
    enabled: !!primaryRfqId && hasPermission('RFQ Management'),
  });
  const primaryRfqLineId = workbench.data?.lines[0]?.id;
  const memory = useQuery({
    queryKey: ['commercial-case', selectedCaseId, 'memory', primaryRfqLineId],
    queryFn: () => commercialLearningService.getLineCard(primaryRfqLineId ?? 0),
    enabled: !!primaryRfqLineId && hasPermission('Dashboard') && hasPermission('Quotations'),
  });
  const ownership = useQuery({
    queryKey: ['commercial-case', selectedCaseId, 'ownership', workbench.data?.customerName],
    queryFn: async () => {
      const rows = await commercialIntelligenceService.getAccountOwnership({ search: workbench.data?.customerName ?? '' });
      return rows.find(row => row.customerName === workbench.data?.customerName) ?? null;
    },
    enabled: !!workbench.data?.customerName && hasPermission('Customers'),
  });

  const commandSummary = React.useMemo(() => {
    const lines = workbench.data?.lines ?? [];
    const deadline = lines.map(line => line.requiredOn).filter((value): value is string => !!value).sort()[0];
    const unresolved = lines.filter(line => ['UNKNOWN', 'POSSIBLE_MATCH'].includes(line.resolution)).length;
    const short = lines.filter(line => line.shortfallQuantity > 0).length;
    const readiness = unresolved > 0 ? `${unresolved} line(s) need Product review` : short > 0 ? `${short} line(s) require sourcing` : lines.length ? 'All lines have ATP coverage' : 'No requested lines available';
    const nextAction = unresolved > 0 ? 'Resolve uncertain Product matches' :
      short > 0 && !(workbench.data?.solicitations.length) ? 'Create Supplier RFQs' :
      (workbench.data?.offers.length ?? 0) > 0 && !(workbench.data?.awards.length) ? 'Compare and select Supplier offers' :
      (workbench.data?.awards.length ?? 0) > 0 && !workbench.data?.customerQuoteDraft ? 'Prepare the Customer Quote' :
      workbench.data?.customerQuoteDraft ? 'Review the Customer Quote and follow-up state' : 'Review opportunity evidence';
    return { deadline, readiness, nextAction };
  }, [workbench.data]);

  const selectedResult = React.useMemo(
    () => searchResults?.find(item => item.id === selectedCaseId) ?? null,
    [searchResults, selectedCaseId]
  );

  React.useEffect(() => {
    setTab(0);
    setFeedbackOpen(false);
    setFeedbackRecorded(false);
  }, [selectedCaseId]);

  const canRecordFeedback = hasPermission('Leads', 'edit');
  const canRevertFeedback = canRecordFeedback &&
    (userData.isManager === true || userData.isSuperAdmin === true) &&
    !!opportunityPriority.data?.latestFeedback;
  const priorityMissing = isAxiosError(opportunityPriority.error) && opportunityPriority.error.response?.status === 404;
  const feedbackValid = feedbackReason.trim().length > 0
    && (feedbackCode !== 'Replaced' || replacementActionCode.trim().length > 0);

  const documentsByType = React.useMemo(() => {
    const entries = new Map<CommercialCaseDocument['documentType'], CommercialCaseDocument[]>();
    for (const type of DOC_ORDER) entries.set(type, []);
    for (const doc of detail?.documents ?? []) {
      const current = entries.get(doc.documentType) ?? [];
      current.push(doc);
      entries.set(doc.documentType, current);
    }
    return entries;
  }, [detail]);

  const counts = React.useMemo(() => {
    const docs = detail?.documents ?? [];
    return {
      leads: docs.filter(doc => doc.documentType === 'Lead').length,
      rfqs: docs.filter(doc => doc.documentType === 'RFQ').length,
      quotes: docs.filter(doc => doc.documentType === 'Quote').length,
      orders: docs.filter(doc => doc.documentType === 'Order').length,
      shipments: docs.filter(doc => doc.documentType === 'Shipment').length,
    };
  }, [detail]);

  const renderWorkspaceDetail = (current: CommercialCaseDetail) => (
    <Stack spacing={2.5}>
      <Paper sx={{ p: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
        <Stack direction={{ xs: 'column', lg: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { xs: 'flex-start', lg: 'center' } }}>
          <Box>
            <Stack direction="row" spacing={1.25} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
              <Typography sx={{ fontSize: '1.45rem', fontWeight: 950, letterSpacing: 0 }}>
                {current.masterReference}
              </Typography>
              <Chip
                label={current.currentStatus ?? 'Open'}
                size="small"
                sx={{ fontWeight: 900, textTransform: 'uppercase', height: 24 }}
                color="primary"
                variant="outlined"
              />
              <Chip label={caseAge(current.createdOn)} size="small" sx={{ fontWeight: 800, height: 24 }} />
            </Stack>
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.75, fontWeight: 600 }}>
              {current.buyerName || 'Unknown buyer'}
              {current.customerEmail ? ` · ${current.customerEmail}` : ''}
            </Typography>
          </Box>
          <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap' }}>
            <Chip icon={<LeadIcon />} label={`Lead ${counts.leads}`} size="small" />
            <Chip icon={<RfqIcon />} label={`RFQs ${counts.rfqs}`} size="small" />
            <Chip icon={<QuoteIcon />} label={`Quotes ${counts.quotes}`} size="small" />
            <Chip icon={<OrderIcon />} label={`Orders ${counts.orders}`} size="small" />
            <Chip icon={<ShipmentIcon />} label={`Shipments ${counts.shipments}`} size="small" />
          </Stack>
        </Stack>

        <Box sx={{ mt: 2.5 }}>
          <Tabs value={tab} onChange={(_, value) => setTab(value)} sx={{ minHeight: 40 }}>
            <Tab label={`Overview (${current.documents.length})`} />
            <Tab label={`Documents (${current.documents.length})`} />
            <Tab label={`Activity (${current.statusHistory.length})`} />
            <Tab label={`Traceability (${current.traceabilityGaps.length})`} />
          </Tabs>
        </Box>
      </Paper>

      {tab === 0 && (
        <Stack spacing={2}>
        <TraceabilityGapPanel gaps={current.traceabilityGaps} />
        <Paper sx={{ p: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
          <Grid container spacing={2.5}>
            <Grid size={{ xs: 12, md: 4 }}>
              <DataField label="Nexora Serial" value={current.masterReference} />
            </Grid>
            <Grid size={{ xs: 12, md: 4 }}>
              <DataField label="Lead ID" value={current.leadId} />
            </Grid>
            <Grid size={{ xs: 12, md: 4 }}>
              <DataField label="Business Unit" value={current.businessUnitId} />
            </Grid>
            <Grid size={{ xs: 12, md: 4 }}>
              <DataField label="Buyer" value={current.buyerName ?? '—'} />
            </Grid>
            <Grid size={{ xs: 12, md: 4 }}>
              <DataField label="Customer Email" value={current.customerEmail ?? '—'} />
            </Grid>
            <Grid size={{ xs: 12, md: 4 }}>
              <DataField label="Opportunity" value={current.opportunityNumber ?? '—'} />
            </Grid>
            <Grid size={{ xs: 12, md: 4 }}>
              <DataField label="Received" value={formatDateSafe(current.createdOn)} />
            </Grid>
            <Grid size={{ xs: 12, md: 4 }}>
              <DataField label="Customer RFQ" value={current.customerRfqNumber ?? '—'} />
            </Grid>
            <Grid size={{ xs: 12, md: 4 }}>
              <DataField label="Allocation Number" value={current.allocationNumber} />
            </Grid>
          </Grid>
        </Paper>
        {feedbackRecorded && <Alert severity="success">Recommendation feedback recorded. No commercial workflow state was changed.</Alert>}
        {opportunityPriority.isLoading && <Paper variant="outlined" sx={{ p: 3, textAlign: 'center' }}><CircularProgress size={24} aria-label="Loading shadow recommendation" /></Paper>}
        {!opportunityPriority.isLoading && priorityMissing && (
          <Paper variant="outlined" sx={{ p: 3 }}>
            <Typography sx={{ fontWeight: 900 }}>Opportunity priority</Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.75 }}>No persisted shadow recommendation is available for this commercial case.</Typography>
          </Paper>
        )}
        {!opportunityPriority.isLoading && opportunityPriority.isError && !priorityMissing && (
          <Alert severity="error" action={<Button color="inherit" onClick={() => void opportunityPriority.refetch()}>Retry</Button>}>
            The persisted shadow recommendation could not be loaded. No recommendation has been assumed.
          </Alert>
        )}
        {opportunityPriority.data && (
          <Paper component="section" aria-labelledby="opportunity-priority-title" sx={{ p: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
            <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { xs: 'stretch', md: 'flex-start' } }}>
              <Box sx={{ minWidth: 0 }}>
                <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
                  <Typography id="opportunity-priority-title" sx={{ fontWeight: 900 }}>Opportunity priority</Typography>
                  <Chip size="small" label="Shadow" variant="outlined" />
                  <Chip size="small" label={`${opportunityPriority.data.priorityBand} priority`} />
                </Stack>
                <Typography variant="h6" sx={{ fontWeight: 900, mt: 1 }}>{opportunityPriority.data.recommendedActionLabel}</Typography>
                <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
                  Advisory guidance only. Feedback records your assessment and does not execute an action or change workflow state.
                </Typography>
              </Box>
              {canRecordFeedback && <Button variant="outlined" onClick={() => { feedbackMutation.reset(); setFeedbackRecorded(false); setFeedbackOpen(true); }}>Record feedback</Button>}
            </Stack>
            <Grid container spacing={2} sx={{ mt: 1 }}>
              <Grid size={{ xs: 6, md: 2.4 }}><DataField label="Rank" value={`#${opportunityPriority.data.rank}`} /></Grid>
              <Grid size={{ xs: 6, md: 2.4 }}><DataField label="Score" value={opportunityPriority.data.priorityScore} /></Grid>
              <Grid size={{ xs: 6, md: 2.4 }}><DataField label="Confidence" value={percent(opportunityPriority.data.confidence)} /></Grid>
              <Grid size={{ xs: 6, md: 2.4 }}><DataField label="Completeness" value={percent(opportunityPriority.data.completeness)} /></Grid>
              <Grid size={{ xs: 6, md: 2.4 }}><DataField label="Sample size" value={opportunityPriority.data.sampleSize} /></Grid>
            </Grid>
            <Divider sx={{ my: 2 }} />
            <Typography variant="subtitle2" sx={{ fontWeight: 900 }}>Expected Commercial Value components</Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
              {opportunityPriority.data.expectedCommercialValue == null
                ? `Not measured: ${opportunityPriority.data.currentBlocker}`
                : `${opportunityPriority.data.expectedCommercialValueCurrency ?? ''} ${opportunityPriority.data.expectedCommercialValue.toLocaleString(undefined, { maximumFractionDigits: 2 })} in shadow mode.`}
            </Typography>
            <Typography variant="body2" sx={{ mt: 0.75 }}><strong>Current blocker:</strong> {opportunityPriority.data.currentBlocker}</Typography>
            <Typography variant="body2"><strong>Response deadline:</strong> {opportunityPriority.data.responseDeadline ? formatDateSafe(opportunityPriority.data.responseDeadline) : 'Not available'}</Typography>
            <Typography variant="caption" color="text.secondary">Expected Commercial Value is not used to rank opportunities across currencies.</Typography>
            <Grid container spacing={1.5} sx={{ mt: 0.5 }}>
              {opportunityPriority.data.components.map(component => (
                <Grid key={component.code} size={{ xs: 12, sm: 6, md: 4 }}>
                  <Box sx={{ p: 1.5, height: '100%', border: '1px solid', borderColor: 'divider' }}>
                    <Typography variant="caption" color="text.secondary">{component.label}</Typography>
                    <Typography sx={{ fontWeight: 900 }}>
                      {component.value == null ? 'Not measured' : `${component.value.toLocaleString(undefined, { maximumFractionDigits: 2 })}${component.unit === 'percent' ? '%' : component.unit === 'ratio' ? '' : ` ${component.unit ?? ''}`}`}
                    </Typography>
                    <Typography variant="caption" color="text.secondary">Sample {component.sampleSize} | Confidence {percent(component.confidence)} | {statusLabel(component.status)}</Typography>
                    <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>{component.sourceType} | {component.sourceReference} | {formatDateSafe(component.evidenceAsOfUtc)}</Typography>
                    <Typography variant="body2" sx={{ mt: 0.75 }}>{component.evidence}</Typography>
                  </Box>
                </Grid>
              ))}
            </Grid>
            <Divider sx={{ my: 2 }} />
            <Typography variant="subtitle2" sx={{ fontWeight: 900 }}>Recommendation rationale</Typography>
            {opportunityPriority.data.reasons.length ? (
              <Stack component="ul" spacing={0.75} sx={{ pl: 2.5, my: 1 }}>
                {opportunityPriority.data.reasons.map((reason) => <Typography component="li" variant="body2" key={reason}>{reason}</Typography>)}
              </Stack>
            ) : <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>No rationale was supplied.</Typography>}
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ mt: 2, flexWrap: 'wrap' }}>
              <Chip size="small" variant="outlined" label={`Policy ${opportunityPriority.data.policyVersion}`} />
              <Chip size="small" variant="outlined" label={`Evidence through ${formatDateSafe(opportunityPriority.data.evidenceCutoffAtUtc)}`} />
              <Chip size="small" variant="outlined" label={`Generated ${formatDateSafe(opportunityPriority.data.generatedAtUtc)}`} />
              {opportunityPriority.data.latestFeedback && <Chip size="small" variant="outlined" label={`Latest feedback: ${opportunityPriority.data.latestFeedback.decision}`} />}
              {opportunityPriority.data.outcomes.at(-1) && <Chip size="small" variant="outlined" label={`Observed outcome: ${opportunityPriority.data.outcomes.at(-1)?.outcomeCode}`} />}
            </Stack>
          </Paper>
        )}
        {primaryRfqId && !hasPermission('RFQ Management') && <Alert severity="info">RFQ Management view permission is required to see line readiness and sourcing evidence.</Alert>}
        {workbench.isLoading && <Paper variant="outlined" sx={{ p: 4, textAlign: 'center' }}><CircularProgress size={24} /></Paper>}
        {workbench.isError && <Alert severity="error" action={<Button color="inherit" onClick={() => void workbench.refetch()}>Retry</Button>}>RFQ command evidence could not be loaded.</Alert>}
        {workbench.data && <>
          <Paper sx={{ p: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
            <Typography sx={{ fontWeight: 900, mb: 2 }}>Opportunity command view</Typography>
            <Grid container spacing={2.5}>
              <Grid size={{ xs: 12, md: 4 }}><DataField label="Customer" value={workbench.data.customerName ?? 'Unresolved'} /></Grid>
              <Grid size={{ xs: 12, md: 4 }}><DataField label="Account owner" value={ownership.data?.ownerName ?? 'Unassigned'} /></Grid>
              <Grid size={{ xs: 12, md: 4 }}><DataField label="Opportunity owner" value={ownership.data?.ownerName ?? 'Unassigned'} /></Grid>
              <Grid size={{ xs: 12, md: 4 }}><DataField label="Deadline" value={commandSummary.deadline ? formatDateSafe(commandSummary.deadline) : 'Not recorded'} /></Grid>
              <Grid size={{ xs: 12, md: 4 }}><DataField label="Readiness" value={commandSummary.readiness} /></Grid>
              <Grid size={{ xs: 12, md: 4 }}><DataField label="Next action" value={commandSummary.nextAction} /></Grid>
              <Grid size={{ xs: 12, md: 4 }}><DataField label="Supplier RFQs" value={workbench.data.solicitations.length} /></Grid>
              <Grid size={{ xs: 12, md: 4 }}><DataField label="Supplier offers" value={workbench.data.offers.length} /></Grid>
              <Grid size={{ xs: 12, md: 4 }}><DataField label="Selected offers" value={workbench.data.awards.length} /></Grid>
            </Grid>
            <Stack direction="row" spacing={1} sx={{ mt: 2 }}>
              <Button variant="contained" onClick={() => navigate(`/procurement/rfqs/view/${primaryRfqId}`)}>Open RFQ action view</Button>
              <Button variant="outlined" onClick={() => navigate(`/procurement/rfqs/${primaryRfqId}/sourcing`)}>Open sourcing decisions</Button>
            </Stack>
          </Paper>
          <Paper sx={{ p: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider', overflowX: 'auto' }}>
            <Typography sx={{ fontWeight: 900, mb: 2 }}>Requested lines, Product match and ATP</Typography>
            <Table size="small"><TableHead><TableRow><TableCell>Part / description</TableCell><TableCell>Requested</TableCell><TableCell>ATP</TableCell><TableCell>Shortfall</TableCell><TableCell>Resolution</TableCell><TableCell>Evidence checked</TableCell></TableRow></TableHead><TableBody>
              {workbench.data.lines.map(line => <TableRow key={line.id}><TableCell><Typography sx={{ fontWeight: 800 }}>{line.partNumber ?? 'Part unresolved'}</Typography><Typography variant="caption" color="text.secondary">{line.description}</Typography></TableCell><TableCell>{line.requestedQuantity}</TableCell><TableCell>{line.availableQuantity}</TableCell><TableCell>{line.shortfallQuantity}</TableCell><TableCell><Chip size="small" label={statusLabel(line.resolution)} /></TableCell><TableCell>{line.resolutionCheckedOn ? formatDateSafe(line.resolutionCheckedOn) : 'Pending'}</TableCell></TableRow>)}
            </TableBody></Table>
          </Paper>
          {memory.data && <Paper sx={{ p: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
            <Typography sx={{ fontWeight: 900, mb: 2 }}>Commercial memory and evidence</Typography>
            <Grid container spacing={2.5}>
              <Grid size={{ xs: 12, md: 4 }}><DataField label="Part" value={memory.data.product ? `${memory.data.product.partNumber} · ${memory.data.product.productName}` : 'Product unresolved'} /></Grid>
              <Grid size={{ xs: 12, md: 4 }}><DataField label="Demand memory" value={memory.data.inventory?.recommendation ?? 'Insufficient verified demand evidence'} /></Grid>
              <Grid size={{ xs: 12, md: 4 }}><DataField label="Recommended next action" value={memory.data.nextAction} /></Grid>
              <Grid size={{ xs: 12, md: 4 }}><DataField label="Product evidence records" value={memory.data.product?.evidence.length ?? 0} /></Grid>
              <Grid size={{ xs: 12, md: 4 }}><DataField label="Supplier evidence records" value={memory.data.suppliers.reduce((total, supplier) => total + supplier.evidence.length, 0)} /></Grid>
            </Grid>
          </Paper>}
        </>}
        </Stack>
      )}

      {tab === 1 && (
        <Stack spacing={2}>
          {DOC_ORDER.map(type => {
            const docs = documentsByType.get(type) ?? [];
            if (docs.length === 0) return null;
            return (
              <Paper key={type} sx={{ p: 2.5, borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
                <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 2 }}>
                  {typeIcon(type)}
                  <Typography sx={{ fontWeight: 900, textTransform: 'uppercase', letterSpacing: '0.025em' }}>
                    {type}
                  </Typography>
                  <Chip label={docs.length} size="small" sx={{ height: 20, fontWeight: 900 }} />
                </Stack>
                <Stack spacing={1}>
                  {docs.map(doc => (
                    <Stack
                      key={`${doc.documentType}-${doc.documentId}`}
                      direction="row"
                      spacing={1.5}
                      sx={{
                        alignItems: 'center',
                        justifyContent: 'space-between',
                        p: 1.5,
                        border: '1px solid',
                        borderColor: 'divider',
                        borderRadius: 1.5,
                      }}
                    >
                      <Box>
                        <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                          <Typography sx={{ fontWeight: 800 }}>{doc.reference}</Typography>
                          {doc.linkState === 'ChainBroken' && (
                            <Chip
                              label="Chain broken"
                              size="small"
                              color="warning"
                              variant="outlined"
                              sx={{ height: 20, fontWeight: 800 }}
                            />
                          )}
                        </Stack>
                        <Typography variant="caption" color="text.secondary">
                          {doc.status ?? 'Open'}
                          {doc.occurredOn ? ` · ${formatDateSafe(doc.occurredOn)}` : ''}
                        </Typography>
                      </Box>
                      <Button
                        size="small"
                        variant="outlined"
                        startIcon={<OpenIcon />}
                        onClick={() => openDocument(navigate, doc)}
                        sx={{ fontWeight: 800, borderRadius: 2 }}
                      >
                        Open
                      </Button>
                    </Stack>
                  ))}
                </Stack>
              </Paper>
            );
          })}
        </Stack>
      )}

      {tab === 2 && (
        <Stack spacing={1.5}>
          {current.statusHistory.length === 0 && (
            <Alert severity="info" sx={{ borderRadius: 2 }}>
              No workspace activity has been recorded yet.
            </Alert>
          )}
          {current.statusHistory.map(event => (
            <Paper key={event.id} sx={{ p: 2.25, borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
              <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5} sx={{ justifyContent: 'space-between' }}>
                <Box>
                  <Typography sx={{ fontWeight: 900 }}>
                    {event.previousStatus ?? 'None'} → {event.newStatus ?? 'None'}
                  </Typography>
                  <Typography variant="body2" color="text.secondary">
                    {event.eventType} · {event.actorSource}
                    {event.changedBy ? ` · ${event.changedBy}` : ''}
                  </Typography>
                </Box>
                <Typography variant="caption" sx={{ color: 'text.secondary', fontWeight: 700 }}>
                  {formatDateSafe(event.changedOn)}
                </Typography>
              </Stack>
              {event.reason && (
                <Typography variant="body2" sx={{ mt: 1, color: 'text.primary' }}>
                  {event.reason}
                </Typography>
              )}
              <Stack direction="row" spacing={1} sx={{ mt: 1, flexWrap: 'wrap' }}>
                {event.aggregateType && <Chip label={event.aggregateType} size="small" variant="outlined" />}
                {event.correlationId && <Chip label={`Correlation ${event.correlationId}`} size="small" variant="outlined" />}
                {event.requestReference && <Chip label={`Request ${event.requestReference}`} size="small" variant="outlined" />}
                {event.reasonCode && <Chip label={`Reason ${event.reasonCode}`} size="small" variant="outlined" />}
              </Stack>
            </Paper>
          ))}
        </Stack>
      )}

      {tab === 3 && (
        <Stack spacing={1.5}>
          {current.traceabilityGaps.length === 0 ? (
            <Alert severity="success" sx={{ borderRadius: 2 }}>
              Every document reachable from this case states this case, and every document that
              states it is reachable. The declared spine and the document chain agree.
            </Alert>
          ) : (
            <TraceabilityGapPanel gaps={current.traceabilityGaps} />
          )}
        </Stack>
      )}

      <Dialog
        open={feedbackOpen}
        onClose={() => !feedbackMutation.isPending && setFeedbackOpen(false)}
        fullWidth
        maxWidth="sm"
        slotProps={{ transition: { onEntered: () => feedbackReasonInputRef.current?.focus() } }}
      >
        <DialogTitle>Record recommendation feedback</DialogTitle>
        <DialogContent dividers>
          <Stack spacing={2}>
            <Alert severity="info">This feedback is advisory evidence. It does not execute the recommendation or change Lead, RFQ, Quote, Order, ownership, pricing, or inventory state.</Alert>
            <FormControl fullWidth>
              <InputLabel id="opportunity-feedback-label">Decision</InputLabel>
              <Select
                labelId="opportunity-feedback-label"
                label="Decision"
                value={feedbackCode}
                onChange={(event) => setFeedbackCode(event.target.value as OpportunityFeedbackCode)}
              >
                {(['Accepted', 'Rejected', 'Replaced', 'Deferred'] as OpportunityFeedbackCode[]).map((code) => <MenuItem key={code} value={code}>{feedbackLabels[code]}</MenuItem>)}
                {canRevertFeedback && <MenuItem value="Reverted">{feedbackLabels.Reverted}</MenuItem>}
              </Select>
            </FormControl>
            {feedbackCode === 'Replaced' && (
              <FormControl required fullWidth>
                <InputLabel id="replacement-action-label">Suggested action</InputLabel>
                <Select
                  labelId="replacement-action-label"
                  label="Suggested action"
                  value={replacementActionCode}
                  onChange={(event) => setReplacementActionCode(event.target.value)}
                >
                  {opportunityPriority.data?.availableActions.map(action => (
                    <MenuItem key={action.code} value={action.code}>{action.label}</MenuItem>
                  ))}
                </Select>
              </FormControl>
            )}
            <TextField
              required
              autoFocus
              inputRef={feedbackReasonInputRef}
              multiline
              minRows={3}
              label="Reason"
              value={feedbackReason}
              onChange={(event) => setFeedbackReason(event.target.value)}
              slotProps={{ htmlInput: { maxLength: 1000 } }}
            />
            {feedbackMutation.isError && (
              <Alert severity="error">
                {isAxiosError(feedbackMutation.error) && feedbackMutation.error.response?.status === 409
                  ? 'The recommendation changed before feedback was recorded. The latest persisted version is being loaded.'
                  : 'Feedback could not be recorded. No workflow state was changed.'}
              </Alert>
            )}
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setFeedbackOpen(false)} disabled={feedbackMutation.isPending}>Cancel</Button>
          <Button variant="contained" disabled={!feedbackValid || feedbackMutation.isPending} onClick={() => feedbackMutation.mutate()}>
            {feedbackMutation.isPending ? 'Recording...' : 'Record feedback'}
          </Button>
        </DialogActions>
      </Dialog>
    </Stack>
  );

  return (
    <Box sx={{ p: 3, maxWidth: 1800, mx: 'auto' }}>
      <Breadcrumbs separator={<NextIcon sx={{ fontSize: 14 }} />} sx={{ mb: 2 }}>
        <Link component="button" variant="caption" onClick={() => navigate('/dashboard')} sx={{ color: 'text.secondary', fontWeight: 700, textDecoration: 'none', textTransform: 'uppercase' }}>
          Dashboard
        </Link>
        <Typography variant="caption" sx={{ color: 'primary.main', fontWeight: 900, textTransform: 'uppercase' }}>
          Commercial Workspace
        </Typography>
      </Breadcrumbs>

      <Stack direction={{ xs: 'column', lg: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { xs: 'flex-start', lg: 'center' }, mb: 2.5 }}>
        <Box>
          <Typography variant="h4" sx={{ fontWeight: 950, letterSpacing: 0 }}>
            Commercial Workspace
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>
            Search by Nexora Serial, customer, contact, part, supplier, RFQ, Quote, PO or email subject.
          </Typography>
        </Box>
        <Box sx={{ minWidth: { xs: '100%', lg: 420 }, width: { xs: '100%', lg: 420 } }}>
          <SearchField
            width="100%"
            value={query}
            onChange={setQuery}
            placeholder="Search by master reference, buyer, RFQ, quote, order, shipment..."
          />
        </Box>
      </Stack>

      <Grid container spacing={2.5}>
        <Grid size={{ xs: 12, lg: 4 }}>
          <Paper sx={{ p: 2.5, borderRadius: 2, border: '1px solid', borderColor: 'divider', minHeight: 640 }}>
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1.75 }}>
              <SearchIcon fontSize="small" />
              <Typography sx={{ fontWeight: 900, textTransform: 'uppercase', letterSpacing: '0.025em' }}>
                Search Results
              </Typography>
            </Stack>
            <Divider sx={{ mb: 2 }} />

            {searchTerm.length < 2 && (
              <Alert severity="info" sx={{ borderRadius: 2 }}>
                Enter at least two characters to search the workspace.
              </Alert>
            )}

            {searchLoading && (
              <Box sx={{ display: 'flex', justifyContent: 'center', py: 6 }}>
                <CircularProgress size={28} />
              </Box>
            )}

            {searchError && (
              <Alert severity="error" action={<Button color="inherit" size="small" onClick={() => void retrySearch()}>Retry</Button>}>
                Commercial case search could not be loaded.
              </Alert>
            )}

            {!searchLoading && !searchError && searchTerm.length >= 2 && (searchResults?.length ?? 0) === 0 && (
              <Alert severity="warning" sx={{ borderRadius: 2 }}>
                No commercial cases matched your search.
              </Alert>
            )}

            <List sx={{ mt: 1 }}>
              {(searchResults ?? []).map(item => (
                <ListItemButton
                  key={item.id}
                  selected={item.id === selectedCaseId}
                  onClick={() => navigate(`/commercial-cases/${item.id}`)}
                  sx={{
                    mb: 1,
                    borderRadius: 2,
                    border: '1px solid',
                    borderColor: item.id === selectedCaseId ? 'primary.main' : 'divider',
                    alignItems: 'flex-start',
                  }}
                >
                  <Stack spacing={0.75} sx={{ width: '100%' }}>
                    <Stack direction="row" spacing={1} sx={{ justifyContent: 'space-between', alignItems: 'center' }}>
                      <Typography sx={{ fontWeight: 900, fontFamily: 'monospace', color: 'primary.main' }}>
                        {item.masterReference}
                      </Typography>
                      <Chip label={item.status ?? 'Open'} size="small" sx={{ height: 20, fontWeight: 800 }} />
                    </Stack>
                    <Typography sx={{ fontWeight: 700 }}>{item.buyerName ?? 'Unknown buyer'}</Typography>
                    <Typography variant="body2" color="text.secondary">
                      {item.customerRfqNumber ?? 'No customer RFQ'}{item.customerEmail ? ` · ${item.customerEmail}` : ''}
                    </Typography>
                    <Typography variant="caption" sx={{ color: 'primary.main', fontWeight: 800 }}>
                      Matched on {item.matchReason}
                    </Typography>
                    <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', pt: 0.5 }}>
                      <Chip label={`RFQs ${item.rfqCount}`} size="small" variant="outlined" />
                      <Chip label={`Quotes ${item.quoteCount}`} size="small" variant="outlined" />
                      <Chip label={`Orders ${item.orderCount}`} size="small" variant="outlined" />
                      <Chip label={`Shipments ${item.shipmentCount}`} size="small" variant="outlined" />
                    </Stack>
                  </Stack>
                </ListItemButton>
              ))}
            </List>
          </Paper>
        </Grid>

        <Grid size={{ xs: 12, lg: 8 }}>
          {detailLoading && (
            <Paper sx={{ p: 4, borderRadius: 2, border: '1px solid', borderColor: 'divider', minHeight: 640 }}>
              <Box sx={{ display: 'flex', justifyContent: 'center', py: 8 }}>
                <CircularProgress />
              </Box>
            </Paper>
          )}

          {!detailLoading && detail && renderWorkspaceDetail(detail)}

          {!detailLoading && !detailError && !detail && !selectedResult && (
            <Paper sx={{ p: 4, borderRadius: 2, border: '1px solid', borderColor: 'divider', minHeight: 640 }}>
              <Alert severity="info" sx={{ borderRadius: 2 }}>
                Search for a commercial case to open the master reference, document trail, and activity timeline.
              </Alert>
            </Paper>
          )}

          {!detailLoading && detailError && (
            <Paper sx={{ p: 4, borderRadius: 2, border: '1px solid', borderColor: 'divider', minHeight: 640 }}>
              <Alert severity="error" action={<Button color="inherit" size="small" onClick={() => void retryDetail()}>Retry</Button>} sx={{ borderRadius: 2 }}>
                {selectedResult ? `We found ${selectedResult.masterReference}, but its workspace could not be loaded.` : 'The commercial workspace could not be loaded.'}
              </Alert>
            </Paper>
          )}
        </Grid>
      </Grid>
    </Box>
  );
};

export default CommercialCaseWorkspacePage;
