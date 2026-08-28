import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Box,
  Button,
  Chip,
  Collapse,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  MenuItem,
  Paper,
  Pagination,
  Stack,
  Tab,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Tabs,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import { ExpandLess, ExpandMore, OpenInNew, Refresh } from '@mui/icons-material';
import { useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import commercialIntelligenceService, {
  type CoachingFindingDTO,
  type CoachingRecoveryDTO,
  type CommercialIntelligenceEvidenceDTO,
  type RecoveryOpportunityDTO,
} from '../../api/services/commercialIntelligenceService';
import opportunityPriorityService, {
  createOpportunityCommandIdentity,
  type OpportunityPriorityItem,
} from '../../api/services/opportunityPriorityService';
import { useAuth } from '../../context/AuthContext';
import { MetricGrid, PageShell, QueryState, ResponsiveTable, formatDateTime } from './CommercialPagePrimitives';
import { statusLabel } from '../../utils/statusLabels';

const percentage = (value: number) => `${value <= 1 ? Math.round(value * 100) : Math.round(value)}%`;
const money = (value?: number | null, currency?: string | null) => value == null
  ? 'Not measured'
  : `${currency ?? ''} ${value.toLocaleString(undefined, { maximumFractionDigits: 2 })}`.trim();
const priorityPageSize = 10;

const reportingWindow = () => {
  const to = new Date();
  const from = new Date(to);
  from.setUTCDate(from.getUTCDate() - 90);
  return { from: from.toISOString().slice(0, 10), to: to.toISOString().slice(0, 10) };
};

const severityColor = (severity: string): 'error' | 'warning' | 'info' | 'default' => {
  const normalized = severity.toLowerCase();
  if (normalized === 'critical' || normalized === 'high') return 'error';
  if (normalized === 'medium') return 'warning';
  if (normalized === 'low') return 'info';
  return 'default';
};

function SourceEvidence({ evidence, identity }: { evidence: CommercialIntelligenceEvidenceDTO[]; identity: string }) {
  const [open, setOpen] = useState(false);
  const evidenceId = `commercial-evidence-${identity.replace(/[^a-zA-Z0-9_-]/g, '-')}`;
  return <Box>
    <Button size="small" color="inherit" endIcon={open ? <ExpandLess /> : <ExpandMore />} aria-expanded={open} aria-controls={evidenceId} onClick={() => setOpen(current => !current)}>
      {open ? 'Hide evidence' : `Evidence (${evidence.length})`}
    </Button>
    <Collapse in={open}>
      <Stack id={evidenceId} component="ul" spacing={0.75} sx={{ pl: 2.5, my: 1 }}>
        {evidence.length ? evidence.map((item, index) => <Typography component="li" variant="body2" key={`${item.recordType}-${item.recordId}-${index}`}>
          <strong>{item.role}:</strong> {item.reference} ({statusLabel(item.recordType)}){item.occurredOn ? `, ${formatDateTime(item.occurredOn)}` : ''}
        </Typography>) : <Typography component="li" variant="body2" color="text.secondary">No source evidence was returned.</Typography>}
      </Stack>
    </Collapse>
  </Box>;
}

function CoachingFindingRow({ item, canAcknowledge, onOpen, onAcknowledge }: {
  item: CoachingFindingDTO;
  canAcknowledge: boolean;
  onOpen: () => void;
  onAcknowledge: () => void;
}) {
  return <Box sx={{ py: 2, borderBottom: '1px solid', borderColor: 'divider', minWidth: 0, '&:last-child': { borderBottom: 0 } }}>
    <Stack spacing={1}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ justifyContent: 'space-between', alignItems: { xs: 'flex-start', sm: 'center' } }}>
        <Box sx={{ minWidth: 0 }}>
          <Typography sx={{ fontWeight: 900, overflowWrap: 'anywhere' }}>{item.salesRepName}: {item.recommendation}</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ overflowWrap: 'anywhere' }}>{item.customerName || item.reference}</Typography>
        </Box>
        <Chip size="small" label={statusLabel(item.severity)} color={severityColor(item.severity)} />
      </Stack>
      <Typography variant="body2">
        {item.observedValue == null ? 'Observed value not available' : `Observed ${item.observedValue}${item.observedUnit ? ` ${item.observedUnit}` : ''}`}
        {item.thresholdValue == null ? '' : `; policy threshold ${item.thresholdValue}`}. Sample {item.sampleSize}; confidence {percentage(item.confidence)}.
      </Typography>
      <Typography variant="caption" color="text.secondary">Policy {item.policyVersion} | Evidence through {formatDateTime(item.asOf)}</Typography>
      {item.latestAcknowledgement && <Alert severity="success" sx={{ py: 0 }}>{statusLabel(item.latestAcknowledgement.disposition)}: {item.latestAcknowledgement.reason}</Alert>}
      <SourceEvidence evidence={item.evidence} identity={`finding-${item.findingKey}`} />
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1}>
        <Button variant="outlined" endIcon={<OpenInNew />} onClick={onOpen} disabled={!item.actionRoute.startsWith('/')}>Open source</Button>
        {canAcknowledge && !item.latestAcknowledgement && <Button variant="contained" onClick={onAcknowledge}>Acknowledge finding</Button>}
      </Stack>
    </Stack>
  </Box>;
}

function RecoveryOpportunityRow({ item, onOpen }: { item: RecoveryOpportunityDTO; onOpen: () => void }) {
  return <Box sx={{ py: 2, borderBottom: '1px solid', borderColor: 'divider', minWidth: 0, '&:last-child': { borderBottom: 0 } }}>
    <Stack spacing={1}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ justifyContent: 'space-between', alignItems: { xs: 'flex-start', sm: 'center' } }}>
        <Box sx={{ minWidth: 0 }}>
          <Typography sx={{ fontWeight: 900, overflowWrap: 'anywhere' }}>{item.title}</Typography>
          <Typography variant="body2" color="text.secondary">{item.customerName || item.nexoraSerial || item.sourceType} | {item.ownerName || 'Unassigned'}</Typography>
        </Box>
        <Chip size="small" label={statusLabel(item.severity)} color={severityColor(item.severity)} />
      </Stack>
      <Typography variant="body2">{item.explanation}</Typography>
      <Typography variant="body2"><strong>Recommended action:</strong> {item.recommendedAction}</Typography>
      <Typography variant="caption" color="text.secondary">{item.dueAt ? `Due ${formatDateTime(item.dueAt)} | ` : ''}Sample {item.sampleSize} | Confidence {percentage(item.confidence)}</Typography>
      <SourceEvidence evidence={item.evidence} identity={`recovery-${item.recoveryKey}`} />
      <Box><Button variant="outlined" endIcon={<OpenInNew />} onClick={onOpen} disabled={!item.actionRoute.startsWith('/')}>Open source</Button></Box>
    </Stack>
  </Box>;
}

function RecommendationEvidence({ item, idPrefix }: { item: OpportunityPriorityItem; idPrefix: 'mobile' | 'desktop' }) {
  const [open, setOpen] = useState(false);
  const evidenceId = `${idPrefix}-priority-evidence-${item.recommendationId}`;
  return (
    <Box>
      <Button
        size="small"
        color="inherit"
        endIcon={open ? <ExpandLess /> : <ExpandMore />}
        aria-expanded={open}
        aria-controls={evidenceId}
        onClick={() => setOpen((current) => !current)}
      >
        {open ? 'Hide rationale' : 'Show rationale'}
      </Button>
      <Collapse in={open}>
        <Stack id={evidenceId} component="ul" spacing={0.5} sx={{ pl: 2.5, my: 1 }}>
          {item.reasons.length ? item.reasons.map((reason) => (
            <Typography component="li" variant="body2" key={reason}>{reason}</Typography>
          )) : <Typography component="li" variant="body2" color="text.secondary">No rationale was supplied.</Typography>}
        </Stack>
      </Collapse>
    </Box>
  );
}

function PriorityMobileCard({ item, onOpen }: { item: OpportunityPriorityItem; onOpen: () => void }) {
  return (
    <Paper variant="outlined" sx={{ p: 2, minWidth: 0 }}>
      <Stack spacing={1.25}>
        <Stack direction="row" spacing={1} sx={{ justifyContent: 'space-between', alignItems: 'flex-start' }}>
          <Box sx={{ minWidth: 0 }}>
            <Typography variant="caption" color="text.secondary">Rank {item.rank}</Typography>
            <Typography sx={{ fontWeight: 900, overflowWrap: 'anywhere' }}>{item.nexoraSerial}</Typography>
            <Typography variant="body2" color="text.secondary">{item.ownerName || 'Unassigned'}</Typography>
          </Box>
          <Chip size="small" label="Shadow" variant="outlined" />
        </Stack>
        <Typography sx={{ fontWeight: 800 }}>{item.recommendedActionLabel}</Typography>
        <Typography variant="body2"><strong>Expected commercial value:</strong> {money(item.expectedCommercialValue, item.expectedCommercialValueCurrency)}</Typography>
        <Typography variant="caption" color="text.secondary">Blocker: {item.currentBlocker}</Typography>
        <Typography variant="caption" color="text.secondary">Deadline: {item.responseDeadline ? formatDateTime(item.responseDeadline) : 'Not available'}</Typography>
        <Box sx={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 1 }}>
          <Typography variant="body2"><strong>Priority:</strong> {item.priorityBand} ({item.priorityScore})</Typography>
          <Typography variant="body2"><strong>Confidence:</strong> {percentage(item.confidence)}</Typography>
          <Typography variant="body2"><strong>Completeness:</strong> {percentage(item.completeness)}</Typography>
          <Typography variant="body2"><strong>Sample:</strong> {item.sampleSize}</Typography>
        </Box>
        <Typography variant="caption" color="text.secondary">
          Policy {item.policyVersion} | Evidence through {formatDateTime(item.evidenceCutoffAtUtc)}
        </Typography>
        <RecommendationEvidence item={item} idPrefix="mobile" />
        <Button variant="outlined" endIcon={<OpenInNew />} onClick={onOpen} aria-label={`Open opportunity ${item.nexoraSerial}`}>
          Open opportunity
        </Button>
      </Stack>
    </Paper>
  );
}

export default function SalesTodayPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { hasPermission, userData } = useAuth();
  const canReconcile = (userData.isManager === true || userData.isSuperAdmin === true)
    && hasPermission('Leads', 'edit');
  const canAcknowledgeCoaching = (userData.isManager === true || userData.isSuperAdmin === true)
    && (hasPermission('Sales Coaching', 'edit') || hasPermission('Leads', 'edit'));
  const [priorityPage, setPriorityPage] = useState(1);
  const [coachingTab, setCoachingTab] = useState<'findings' | 'recovery'>('findings');
  const [acknowledgementTarget, setAcknowledgementTarget] = useState<CoachingFindingDTO | null>(null);
  const [acknowledgementDisposition, setAcknowledgementDisposition] = useState('ACKNOWLEDGED');
  const [acknowledgementReason, setAcknowledgementReason] = useState('');
  const [acknowledgementKey, setAcknowledgementKey] = useState('');
  const acknowledgementReasonInputRef = useRef<HTMLTextAreaElement>(null);
  const period = useMemo(reportingWindow, []);
  const query = useQuery({ queryKey: ['commercial-intelligence', 'sales-today'], queryFn: commercialIntelligenceService.getSalesToday, refetchInterval: 60_000 });
  const priorities = useQuery({
    queryKey: ['opportunity-priorities', priorityPage],
    queryFn: () => opportunityPriorityService.getPriorities(priorityPage, priorityPageSize),
    retry: 1,
  });
  const reconcile = useMutation({
    mutationFn: () => opportunityPriorityService.reconcileAll(createOpportunityCommandIdentity()),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['opportunity-priorities'] }),
  });
  const coaching = useQuery({
    queryKey: ['commercial-intelligence', 'coaching-recovery', period.from, period.to],
    queryFn: () => commercialIntelligenceService.getCoachingRecovery(period.from, period.to),
    retry: 1,
  });
  const acknowledge = useMutation({
    mutationFn: () => {
      if (!acknowledgementTarget) throw new Error('Select a coaching finding first.');
      return commercialIntelligenceService.acknowledgeCoachingFinding(
        acknowledgementTarget.findingKey,
        acknowledgementDisposition,
        acknowledgementReason.trim(),
        period.from,
        period.to,
        acknowledgementKey,
      );
    },
    onSuccess: async acknowledgement => {
      const findingKey = acknowledgementTarget?.findingKey;
      if (findingKey) {
        queryClient.setQueryData<CoachingRecoveryDTO>(
          ['commercial-intelligence', 'coaching-recovery', period.from, period.to],
          current => current ? {
            ...current,
            coachingFindings: current.coachingFindings.map(finding => finding.findingKey === findingKey
              ? { ...finding, latestAcknowledgement: acknowledgement }
              : finding),
          } : current,
        );
      }
      setAcknowledgementTarget(null);
      setAcknowledgementReason('');
      setAcknowledgementKey('');
      await queryClient.invalidateQueries({ queryKey: ['commercial-intelligence', 'coaching-recovery'] });
    },
  });
  const items = query.data?.attentionItems ?? [];
  const priorityItems = priorities.data?.items ?? [];

  const scopeSubtitle = query.data?.scope === 'tenant'
    ? 'Tenant-wide commercial work that needs attention now.'
    : query.data?.scope === 'managed_scope'
      ? 'Commercial work across your managed teams that needs attention now.'
      : 'Your assigned commercial work that needs attention now.';

  return <PageShell title="Sales today" subtitle={scopeSubtitle}>
    <MetricGrid metrics={query.data?.metrics ?? []} />

    <Stack spacing={1.5} sx={{ mb: 3 }}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ justifyContent: 'space-between', alignItems: { xs: 'stretch', sm: 'center' } }}>
        <Box>
          <Typography variant="h6" sx={{ fontWeight: 900 }}>Opportunity priority</Typography>
          <Typography variant="body2" color="text.secondary">
            Server-ranked guidance in shadow mode. Opening a recommendation does not execute or change commercial workflow.
          </Typography>
        </Box>
        {canReconcile && <Tooltip title="Reconcile persisted opportunity evidence">
          <span>
            <Button
              variant="outlined"
              startIcon={<Refresh />}
              disabled={reconcile.isPending}
              onClick={() => reconcile.mutate()}
            >
              {reconcile.isPending ? 'Reconciling...' : 'Reconcile'}
            </Button>
          </span>
        </Tooltip>}
      </Stack>
      {reconcile.isError && <Alert severity="error">{reconcile.error instanceof Error ? reconcile.error.message : 'Opportunity evidence could not be reconciled. Retry safely.'}</Alert>}
      {reconcile.isSuccess && <Alert severity="success">Reconciliation completed for {reconcile.data.evaluated} opportunities across all available batches.</Alert>}
      {priorities.data && (
        <Stack spacing={0.25}>
          <Typography variant="caption" color="text.secondary">
            Scope: {statusLabel(priorities.data.accessScope)} | Generated {formatDateTime(priorities.data.generatedAtUtc)}
          </Typography>
          <Typography variant="caption" color="text.secondary">
            Cohort: {priorities.data.cohort.eligibleRecommendations} eligible | {priorities.data.cohort.insufficientEvidenceRecommendations} insufficient evidence | {priorities.data.cohort.recommendationsWithObservedOutcome} with observed outcomes. Accuracy: {priorities.data.cohort.accuracyStatus}
          </Typography>
        </Stack>
      )}
      <QueryState
        loading={priorities.isLoading}
        error={priorities.isError}
        empty={!priorityItems.length}
        onRetry={() => void priorities.refetch()}
        emptyText="No persisted shadow priorities are available for this scope."
      >
        <Box sx={{ display: { xs: 'grid', md: 'none' }, gap: 1.5 }}>
          {priorityItems.map((item) => <PriorityMobileCard key={item.recommendationId} item={item} onOpen={() => navigate(`/commercial-cases/${item.commercialCaseId}`)} />)}
        </Box>
        <Box sx={{ display: { xs: 'none', md: 'block' } }}>
          <ResponsiveTable label="Opportunity priority shadow queue">
            <Table size="small">
              <TableHead><TableRow><TableCell>Rank</TableCell><TableCell>Opportunity</TableCell><TableCell>Recommendation</TableCell><TableCell>Priority evidence</TableCell><TableCell>Confidence</TableCell><TableCell>Evidence version</TableCell><TableCell align="right">Action</TableCell></TableRow></TableHead>
              <TableBody>
                {priorityItems.map((item) => (
                  <TableRow hover key={item.recommendationId}>
                    <TableCell><Typography sx={{ fontWeight: 900 }}>#{item.rank}</Typography><Chip size="small" label="Shadow" variant="outlined" /></TableCell>
                    <TableCell><Typography sx={{ fontWeight: 800 }}>{item.nexoraSerial}</Typography><Typography variant="caption" color="text.secondary">{item.ownerName || 'Unassigned'}</Typography></TableCell>
                    <TableCell><Typography sx={{ fontWeight: 800 }}>{item.recommendedActionLabel}</Typography><Typography variant="caption" color="text.secondary">Blocker: {item.currentBlocker}</Typography><br /><Typography variant="caption" color="text.secondary">Deadline: {item.responseDeadline ? formatDateTime(item.responseDeadline) : 'Not available'}</Typography><RecommendationEvidence item={item} idPrefix="desktop" /></TableCell>
                    <TableCell><Typography variant="body2">{item.priorityBand} | Score {item.priorityScore}</Typography><Typography variant="caption" color="text.secondary">Advisory ECV {money(item.expectedCommercialValue, item.expectedCommercialValueCurrency)} | {statusLabel(item.expectedCommercialValueStatus)}</Typography><br /><Typography variant="caption" color="text.secondary">Not used in cross-currency rank | Completeness {percentage(item.completeness)} | Sample {item.sampleSize}</Typography></TableCell>
                    <TableCell>{percentage(item.confidence)}</TableCell>
                    <TableCell><Typography variant="body2">Policy {item.policyVersion}</Typography><Typography variant="caption" color="text.secondary">Through {formatDateTime(item.evidenceCutoffAtUtc)}</Typography></TableCell>
                    <TableCell align="right"><Button size="small" endIcon={<OpenInNew />} onClick={() => navigate(`/commercial-cases/${item.commercialCaseId}`)} aria-label={`Open opportunity ${item.nexoraSerial}`}>Open opportunity</Button></TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </ResponsiveTable>
        </Box>
        {priorities.data && priorities.data.total > priorities.data.pageSize && (
          <Stack sx={{ alignItems: 'center', mt: 1.5 }}>
            <Pagination
              page={priorities.data.pageNumber}
              count={Math.ceil(priorities.data.total / priorities.data.pageSize)}
              onChange={(_event, page) => setPriorityPage(page)}
              aria-label="Opportunity priority pages"
            />
          </Stack>
        )}
      </QueryState>
    </Stack>

    <Typography variant="h6" sx={{ fontWeight: 900, mb: 1.5 }}>Attention queue</Typography>
    <QueryState loading={query.isLoading} error={query.isError} empty={!items.length} onRetry={() => void query.refetch()} emptyText="Nothing requires sales attention right now.">
      <ResponsiveTable label="Sales attention queue"><Table size="small"><TableHead><TableRow><TableCell>Priority</TableCell><TableCell>Reference</TableCell><TableCell>Customer</TableCell><TableCell>Owner</TableCell><TableCell>Why it needs attention</TableCell><TableCell>Due</TableCell><TableCell align="right">Action</TableCell></TableRow></TableHead><TableBody>
        {items.map(item => { const target = item.recordType.toLowerCase() === 'quote' ? `/sales/quotes/view/${item.recordId}` : item.recordType.toLowerCase() === 'lead' ? `/procurement/leads/view/${item.recordId}` : item.nexoraSerial ? `/commercial-cases?search=${encodeURIComponent(item.nexoraSerial)}` : null; return <TableRow hover key={`${item.recordType}-${item.id}`}><TableCell><Chip size="small" label={item.priority} color={item.priority.toLowerCase() === 'critical' ? 'error' : 'warning'} /></TableCell><TableCell>{item.nexoraSerial || item.reference}</TableCell><TableCell>{item.customerName || 'Customer unresolved'}</TableCell><TableCell>{item.ownerName || 'Unassigned'}</TableCell><TableCell>{item.reason}</TableCell><TableCell>{formatDateTime(item.dueAt)}</TableCell><TableCell align="right">{target && <Button size="small" endIcon={<OpenInNew />} onClick={() => navigate(target)}>Open</Button>}</TableCell></TableRow>; })}
      </TableBody></Table></ResponsiveTable>
    </QueryState>

    <Box component="section" aria-labelledby="coaching-recovery-heading" sx={{ mt: 4, minWidth: 0 }}>
      <Typography id="coaching-recovery-heading" variant="h6" sx={{ fontWeight: 900 }}>Coaching and recovery</Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>Evidence-backed coaching and recoverable commercial work for the selected 90-day cohort.</Typography>
      {coaching.data && <Typography variant="caption" color="text.secondary">
        Scope: {statusLabel(coaching.data.scope)} | Policy {coaching.data.policyVersion} | Generated {formatDateTime(coaching.data.generatedAt)}
      </Typography>}
      {coaching.data?.dataCompleteness.status === 'partial' && <Alert severity="warning" sx={{ mt: 1 }}>
        This cohort is partial because the bounded source limit was reached for: {coaching.data.dataCompleteness.incompleteSources.join(', ')}.
      </Alert>}
      <Tabs
        value={coachingTab}
        onChange={(_event, value: 'findings' | 'recovery') => setCoachingTab(value)}
        aria-label="Coaching and recovery views"
        variant="scrollable"
        allowScrollButtonsMobile
        selectionFollowsFocus
        sx={{ borderBottom: '1px solid', borderColor: 'divider', mt: 1 }}
      >
        <Tab value="findings" label={`Coaching findings (${coaching.data?.coachingFindings.length ?? 0})`} />
        <Tab value="recovery" label={`Recovery opportunities (${coaching.data?.recoveryOpportunities.length ?? 0})`} />
      </Tabs>
      {coaching.isLoading ? <Box sx={{ minHeight: 180, display: 'grid', placeItems: 'center' }}><CircularProgress aria-label="Loading coaching and recovery" /></Box>
        : coaching.isError ? <Alert severity="error" action={<Button color="inherit" startIcon={<Refresh />} onClick={() => void coaching.refetch()}>Retry</Button>}>This persisted view could not be loaded. No empty result has been assumed.</Alert>
        : (coachingTab === 'findings' ? !coaching.data?.coachingFindings.length : !coaching.data?.recoveryOpportunities.length)
          ? <Typography color="text.secondary" sx={{ py: 4, textAlign: 'center' }}>{coachingTab === 'findings' ? 'No coaching findings require attention for this cohort.' : 'No recovery opportunities require attention for this cohort.'}</Typography>
          : (
        <Box role="tabpanel" aria-label={coachingTab === 'findings' ? 'Coaching findings' : 'Recovery opportunities'}>
          {coachingTab === 'findings' ? coaching.data?.coachingFindings.map(item => <CoachingFindingRow
            key={item.findingKey}
            item={item}
            canAcknowledge={canAcknowledgeCoaching}
            onOpen={() => navigate(item.actionRoute)}
            onAcknowledge={() => {
              setAcknowledgementTarget(item);
              setAcknowledgementDisposition('ACKNOWLEDGED');
              setAcknowledgementReason('');
              setAcknowledgementKey(crypto.randomUUID());
            }}
          />) : coaching.data?.recoveryOpportunities.map(item => <RecoveryOpportunityRow key={item.recoveryKey} item={item} onOpen={() => navigate(item.actionRoute)} />)}
        </Box>
      )}
    </Box>

    <Dialog open={Boolean(acknowledgementTarget)} onClose={() => !acknowledge.isPending && setAcknowledgementTarget(null)} fullWidth maxWidth="sm" aria-labelledby="coaching-acknowledgement-title" slotProps={{ transition: { onEntered: () => acknowledgementReasonInputRef.current?.focus() } }}>
      <DialogTitle id="coaching-acknowledgement-title">Acknowledge coaching finding</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          <Typography variant="body2">This records a manager decision against the evidence. It does not change commercial workflow state.</Typography>
          <TextField label="Decision reason" value={acknowledgementReason} onChange={event => setAcknowledgementReason(event.target.value)} multiline minRows={3} inputRef={acknowledgementReasonInputRef} required slotProps={{ htmlInput: { maxLength: 1000 } }} />
          <TextField select label="Disposition" value={acknowledgementDisposition} onChange={event => setAcknowledgementDisposition(event.target.value)} fullWidth>
            <MenuItem value="ACKNOWLEDGED">Acknowledged</MenuItem>
            <MenuItem value="RESOLVED">Resolved</MenuItem>
            <MenuItem value="DISMISSED">Dismissed</MenuItem>
          </TextField>
          {acknowledge.isError && <Alert severity="error">{acknowledge.error instanceof Error ? acknowledge.error.message : 'The acknowledgement could not be recorded.'}</Alert>}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={() => setAcknowledgementTarget(null)} disabled={acknowledge.isPending}>Cancel</Button>
        <Button variant="contained" onClick={() => acknowledge.mutate()} disabled={acknowledge.isPending || acknowledgementReason.trim().length < 10}>
          {acknowledge.isPending ? 'Recording...' : 'Record acknowledgement'}
        </Button>
      </DialogActions>
    </Dialog>
  </PageShell>;
}
