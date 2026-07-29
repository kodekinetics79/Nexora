import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Box,
  Button,
  Chip,
  Collapse,
  Paper,
  Pagination,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Tooltip,
  Typography,
} from '@mui/material';
import { ExpandLess, ExpandMore, OpenInNew, Refresh } from '@mui/icons-material';
import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import commercialIntelligenceService from '../../api/services/commercialIntelligenceService';
import opportunityPriorityService, {
  createOpportunityCommandIdentity,
  type OpportunityPriorityItem,
} from '../../api/services/opportunityPriorityService';
import { useAuth } from '../../context/AuthContext';
import { MetricGrid, PageShell, QueryState, ResponsiveTable, formatDateTime } from './CommercialPagePrimitives';

const percentage = (value: number) => `${value <= 1 ? Math.round(value * 100) : Math.round(value)}%`;
const money = (value?: number | null, currency?: string | null) => value == null
  ? 'Not measured'
  : `${currency ?? ''} ${value.toLocaleString(undefined, { maximumFractionDigits: 2 })}`.trim();
const businessLabel = (value: string) => value.replaceAll('_', ' ').replace(/\b\w/g, letter => letter.toUpperCase());
const priorityPageSize = 10;

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
  const [priorityPage, setPriorityPage] = useState(1);
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
  const items = query.data?.attentionItems ?? [];
  const priorityItems = priorities.data?.items ?? [];

  return <PageShell title="Sales today" subtitle={query.data?.scope === 'tenant' ? 'Team-wide commercial work that needs attention now.' : 'Your assigned commercial work that needs attention now.'}>
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
            Scope: {businessLabel(priorities.data.accessScope)} | Generated {formatDateTime(priorities.data.generatedAtUtc)}
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
                    <TableCell><Typography variant="body2">{item.priorityBand} | Score {item.priorityScore}</Typography><Typography variant="caption" color="text.secondary">Advisory ECV {money(item.expectedCommercialValue, item.expectedCommercialValueCurrency)} | {businessLabel(item.expectedCommercialValueStatus)}</Typography><br /><Typography variant="caption" color="text.secondary">Not used in cross-currency rank | Completeness {percentage(item.completeness)} | Sample {item.sampleSize}</Typography></TableCell>
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
  </PageShell>;
}
