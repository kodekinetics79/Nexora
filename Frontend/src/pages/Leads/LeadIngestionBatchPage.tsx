import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams } from 'react-router-dom';
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Grid,
  LinearProgress,
  Paper,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  CheckCircleOutlined as NewIcon,
  ContentCopy as DuplicateIcon,
  ErrorOutlined as RejectedIcon,
  FactCheck as ReviewIcon,
  History as RevisionIcon,
  OpenInNew as OpenIcon,
  Refresh as RefreshIcon,
  Replay as RetryIcon,
  Security as SecurityIcon,
  UploadFile as FilesIcon,
} from '@mui/icons-material';
import dayjs from 'dayjs';
import leadService from '../../api/services/leadService';
import type { BatchReconciliationItemDTO, LeadMatchCandidateDTO, MatchReviewDecisionAction } from '../../api/services/leadService';

type ChipColor = 'default' | 'primary' | 'success' | 'warning' | 'error' | 'info';

const readable = (value: string): string => value
  .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
  .replaceAll('_', ' ')
  .replace(/\b\w/g, (character) => character.toUpperCase());

const classificationMeta = (classification: string): { label: string; color: ChipColor } => {
  const normalized = classification.replaceAll('_', '').toLowerCase();
  if (normalized === 'new') return { label: 'New lead', color: 'success' };
  if (normalized === 'exactduplicate') return { label: 'Exact duplicate', color: 'info' };
  if (normalized === 'revision') return { label: 'Revision', color: 'primary' };
  if (normalized === 'possiblematchreviewrequired') return { label: 'Possible match review', color: 'warning' };
  if (normalized === 'rejectedorunprocessable') return { label: 'Rejected or unsupported', color: 'error' };
  return { label: readable(classification || 'Pending'), color: 'default' };
};

const confidenceLabel = (confidence: number): string => `${Math.round(confidence * 100)}% confidence`;

const timestampLabel = (value?: string | null): string => {
  if (!value || !dayjs(value).isValid()) return 'time unavailable';
  const zone = Intl.DateTimeFormat().resolvedOptions().timeZone || 'local time';
  return `${dayjs(value).format('DD MMM YYYY, HH:mm')} (${zone})`;
};

const evidenceObject = (value: string): Record<string, unknown> | null => {
  try {
    const parsed = JSON.parse(value) as unknown;
    return typeof parsed === 'object' && parsed !== null ? parsed as Record<string, unknown> : null;
  } catch {
    return null;
  }
};

const itemLabels = (value: unknown): string[] => !Array.isArray(value) ? [] : value.map((item) => {
  if (typeof item !== 'object' || item === null) return 'Unlabelled line item';
  const row = item as Record<string, unknown>;
  const part = String(row.part || row.description || 'Unlabelled item');
  const quantity = Number(row.Quantity ?? row.quantity);
  return Number.isFinite(quantity) ? `${part} (quantity ${quantity})` : part;
});

const matchEvidenceLines = (value: string): string[] => {
  const evidence = evidenceObject(value);
  if (!evidence) return [value || 'Match evidence is unavailable.'];
  const overlap = Number(evidence.lineOverlap);
  return Number.isFinite(overlap)
    ? [`${Math.round(overlap * 100)}% of line items overlap with this candidate.`]
    : ['Customer and RFQ evidence indicate a credible possible match.'];
};

const differenceLines = (value: string): string[] => {
  const evidence = evidenceObject(value);
  if (!evidence) return [value || 'Material differences are unavailable.'];
  const current = evidence.current as Record<string, unknown> | undefined;
  const previous = evidence.previous as Record<string, unknown> | undefined;
  const currentItems = itemLabels(current?.items);
  const previousItems = itemLabels(previous?.items);
  const added = currentItems.filter((item) => !previousItems.includes(item));
  const removed = previousItems.filter((item) => !currentItems.includes(item));
  const lines: string[] = [];
  if (added.length) lines.push(`Added or changed: ${added.join(', ')}.`);
  if (removed.length) lines.push(`Removed or changed: ${removed.join(', ')}.`);
  return lines.length ? lines : ['No material line-item differences were detected.'];
};

const impactLines = (value: string): string[] => {
  const impact = evidenceObject(value);
  if (!impact) return [value || 'Downstream impact is unavailable.'];
  const rfqCount = Number(impact.rfqCount || 0);
  const orderCount = Number(impact.orderCount || 0);
  if (rfqCount === 0 && orderCount === 0)
    return ['No downstream RFQs or orders would be changed by this decision.'];
  return [`Review ${rfqCount} downstream RFQ${rfqCount === 1 ? '' : 's'} and ${orderCount} order${orderCount === 1 ? '' : 's'} before merging.`];
};

const EvidenceLines = ({ lines }: { lines: string[] }) => (
  <Stack spacing={0.5} sx={{ mt: 0.25 }}>
    {lines.map((line) => <Typography key={line} variant="body2">{line}</Typography>)}
  </Stack>
);

const responseStatus = (error: unknown): number | undefined => {
  if (typeof error !== 'object' || error === null || !('response' in error)) return undefined;
  const response = (error as { response?: { status?: unknown } }).response;
  return typeof response?.status === 'number' ? response.status : undefined;
};

const batchErrorMessage = (status: number | undefined): { severity: 'warning' | 'error'; message: string } => {
  if (status === 401) return { severity: 'error', message: 'Your session is no longer authorized. Sign in again to view this batch.' };
  if (status === 403) return { severity: 'error', message: 'You do not have permission to view this reconciliation batch.' };
  if (status === 404) return { severity: 'warning', message: 'This reconciliation batch was not found for your organization.' };
  if (status === 409) return { severity: 'warning', message: 'This batch changed while it was being reviewed. Refresh to load the current state.' };
  return { severity: 'error', message: 'The reconciliation service is unavailable. The source remains safely recorded; retry when the service recovers.' };
};

const MatchReviewPanel = ({ occurrenceId, candidate }: { occurrenceId: number; candidate: LeadMatchCandidateDTO }) => {
  const queryClient = useQueryClient();
  const [action, setAction] = useState<MatchReviewDecisionAction | null>(null);
  const [reason, setReason] = useState('');
  const mutation = useMutation({
    mutationFn: () => leadService.decideMatchReview(occurrenceId, {
      action: action!, candidateLeadId: candidate.candidateLeadId, expectedVersion: candidate.version,
      reason: reason.trim(), idempotencyKey: crypto.randomUUID(),
    }),
    onSuccess: async () => {
      setAction(null); setReason('');
      await queryClient.invalidateQueries({ queryKey: ['lead-ingestion-batch'] });
    },
  });
  const choose = (next: MatchReviewDecisionAction) => { setReason(''); setAction(next); };

  return (
    <Box sx={{ mt: 2, p: 2, border: 1, borderColor: 'warning.light', borderRadius: 1 }}>
      <Typography sx={{ fontWeight: 800 }}>Candidate {candidate.nexoraSerial}</Typography>
      <Typography variant="body2" color="text.secondary">
        {candidate.customerRfqReference || 'Customer RFQ reference unavailable'} | {confidenceLabel(candidate.confidence)}
      </Typography>
      <Stack spacing={1} sx={{ mt: 1 }}>
        <Box><Typography variant="caption" sx={{ fontWeight: 800 }}>Match reasons and line-item overlap</Typography><EvidenceLines lines={matchEvidenceLines(candidate.matchEvidenceJson)} /></Box>
        <Box><Typography variant="caption" sx={{ fontWeight: 800 }}>Material differences</Typography><EvidenceLines lines={differenceLines(candidate.differencesJson)} /></Box>
        <Box><Typography variant="caption" sx={{ fontWeight: 800 }}>Downstream commercial impact</Typography><EvidenceLines lines={impactLines(candidate.downstreamImpactJson)} /></Box>
      </Stack>
      {candidate.reviewState === 'Pending' && (
        <Stack direction="row" spacing={1} sx={{ mt: 1.5, flexWrap: 'wrap', gap: 1 }}>
          <Button size="small" variant="contained" onClick={() => choose('revision')}>Treat as revision</Button>
          <Button size="small" onClick={() => choose('create_new')}>Create new lead</Button>
          <Button size="small" color="error" onClick={() => choose('reject')}>Reject</Button>
          <Button size="small" onClick={() => choose('defer')}>Return for review</Button>
        </Stack>
      )}
      <Dialog open={action !== null} onClose={() => !mutation.isPending && setAction(null)} fullWidth maxWidth="sm">
        <DialogTitle>{action ? readable(action) : 'Match decision'}</DialogTitle>
        <DialogContent><TextField autoFocus fullWidth multiline minRows={3} sx={{ mt: 1 }} label="Decision reason" value={reason} onChange={(event) => setReason(event.target.value)} /></DialogContent>
        <DialogActions>
          <Button onClick={() => setAction(null)} disabled={mutation.isPending}>Cancel</Button>
          <Button variant="contained" onClick={() => mutation.mutate()} disabled={!reason.trim() || mutation.isPending}>Record decision</Button>
        </DialogActions>
      </Dialog>
      {mutation.isError && <Alert severity="error" sx={{ mt: 1 }}>The decision was not recorded. Refresh and try again.</Alert>}
    </Box>
  );
};

const ReconciliationRow = ({ item }: { item: BatchReconciliationItemDTO }) => {
  const navigate = useNavigate();
  const awaitingScan = item.intakeStatus === 'AwaitingSecurityScan';
  const meta = awaitingScan
    ? { label: 'Awaiting Security Scan', color: 'warning' as ChipColor }
    : classificationMeta(item.classification);
  const canOpenLead = typeof item.leadId === 'number' && item.leadId > 0;

  return (
    <Paper component="article" variant="outlined" sx={{ p: { xs: 2, md: 2.5 }, borderRadius: 2 }}>
      <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ alignItems: { xs: 'stretch', md: 'center' } }}>
        <Box sx={{ flex: 1, minWidth: 0 }}>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap', mb: 0.75 }}>
            <Chip label={meta.label} color={meta.color} size="small" variant="outlined" sx={{ fontWeight: 800 }} />
            {item.revisionNumber != null && (
              <Chip label={`Revision ${item.revisionNumber}`} size="small" sx={{ fontWeight: 700 }} />
            )}
            {item.nexoraSerial && (
              <Typography variant="body2" sx={{ fontFamily: 'monospace', fontWeight: 800 }}>
                {item.nexoraSerial}
              </Typography>
            )}
          </Stack>
          <Typography sx={{ fontWeight: 800, overflowWrap: 'anywhere' }}>
            {item.fileName || `Ingestion occurrence ${item.occurrenceId}`}
          </Typography>
          <Typography variant="caption" color="text.secondary">
            Received {timestampLabel(item.ingestedAtUtc)}
            {' | '}{readable(item.processingPath)}{' | '}{confidenceLabel(item.confidence)}
          </Typography>
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
            Security {readable(item.securityStatus || 'Pending')} updated {timestampLabel(item.securityScanUpdatedAtUtc || item.lastUpdatedAtUtc)}
            {item.extractionStatus ? ` | Extraction ${readable(item.extractionStatus)} updated ${timestampLabel(item.extractionUpdatedAtUtc)}` : ''}
          </Typography>
          {item.intakeStatus && item.intakeStatus !== 'Reconciled' && (
            <Typography variant="body2" color={meta.color === 'error' ? 'error.main' : 'text.secondary'} sx={{ mt: 0.75 }}>
              Intake: {readable(item.intakeStatus)}{item.errorCode ? ` (${readable(item.errorCode)})` : ''}
            </Typography>
          )}
          <Typography variant="body2" sx={{ mt: 1 }}>
            Customer: {readable(item.customerResolutionStatus || 'Awaiting customer resolution')}
            {' | '}Owner: {item.assignedOpportunityOwner || 'Not assigned'}
          </Typography>
          {item.reasons.length > 0 && (
            <Stack spacing={0.25} sx={{ mt: 1 }}>
              {item.reasons.map((reason) => (
                <Typography key={reason} variant="body2" color="text.secondary">{reason}</Typography>
              ))}
            </Stack>
          )}
          {item.matchCandidates.map((candidate) => (
            <MatchReviewPanel key={candidate.candidateId} occurrenceId={item.occurrenceId} candidate={candidate} />
          ))}
        </Box>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', justifyContent: { xs: 'space-between', md: 'flex-end' } }}>
          <Chip
            size="small"
            label={item.externalAiUsed ? 'External provider used' : 'Local-first'}
            color={item.externalAiUsed ? 'warning' : 'default'}
            variant="outlined"
          />
          {canOpenLead && (
            <>
              <Button
                variant="outlined"
                size="small"
                endIcon={<OpenIcon />}
                onClick={() => navigate(`/procurement/leads/view/${item.leadId}`)}
              >
                Review inquiry
              </Button>
              <Button
                variant="contained"
                size="small"
                onClick={() => navigate(`/procurement/leads/${item.leadId}/convert`)}
              >
                Prepare RFQ
              </Button>
            </>
          )}
        </Stack>
      </Stack>
    </Paper>
  );
};

export default function LeadIngestionBatchPage() {
  const { batchId = '' } = useParams<{ batchId: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [activeClassification, setActiveClassification] = useState<string | null>(null);
  const batchQuery = useQuery({
    queryKey: ['lead-ingestion-batch', batchId],
    queryFn: () => leadService.getIngestionBatch(batchId),
    enabled: Boolean(batchId),
    retry: (failureCount, error) => {
      const status = responseStatus(error);
      return failureCount < 2 && (status === undefined || status === 408 || status === 429 || status >= 500);
    },
    retryDelay: (attempt) => Math.min(1000 * (attempt + 1), 3000),
    refetchInterval: (query) => {
      const batch = query.state.data;
      if (!batch) return 2000;
      return batch.items.length < batch.filesReceived || batch.items.some((item) =>
        item.classification.replaceAll('_', '').toLowerCase() === 'pending'
        || item.intakeStatus === 'AwaitingSecurityScan') ? 2000 : false;
    },
  });
  const retryMutation = useMutation({
    mutationFn: () => leadService.retryBlockedFiles(batchId),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['lead-ingestion-batch', batchId] });
    },
  });

  if (batchQuery.isLoading) {
    return (
      <Box sx={{ maxWidth: 1200, mx: 'auto', p: 3 }}>
        <LinearProgress sx={{ mb: 2 }} />
        <Typography sx={{ fontWeight: 800 }}>Waiting for reconciliation results...</Typography>
        <Typography variant="body2" color="text.secondary">The documents are safely queued. Results appear as processing records them.</Typography>
      </Box>
    );
  }

  if (batchQuery.isError || !batchQuery.data) {
    const error = batchErrorMessage(responseStatus(batchQuery.error));
    return (
      <Box sx={{ maxWidth: 900, mx: 'auto', p: 3 }}>
        <Alert
          severity={error.severity}
          action={<Button color="inherit" startIcon={<RefreshIcon />} onClick={() => batchQuery.refetch()}>Check again</Button>}
        >
          {error.message}
        </Alert>
      </Box>
    );
  }

  const batch = batchQuery.data;
  const awaitingSecurityScan = batch.awaitingSecurityScan ?? 0;
  const metrics = [
    { label: 'Files received', value: batch.filesReceived, classification: null, icon: <FilesIcon color="action" /> },
    { label: 'Logical inquiries', value: batch.logicalInquiries, classification: null, icon: <ReviewIcon color="action" /> },
    { label: 'New leads', value: batch.newLeads, classification: 'new', icon: <NewIcon color="success" /> },
    { label: 'Exact duplicates', value: batch.exactDuplicates, classification: 'exactduplicate', icon: <DuplicateIcon color="info" /> },
    { label: 'Revisions', value: batch.revisions, classification: 'revision', icon: <RevisionIcon color="primary" /> },
    { label: 'Possible matches', value: batch.possibleMatches, classification: 'possiblematchreviewrequired', icon: <ReviewIcon color="warning" /> },
    { label: 'Awaiting security scan', value: awaitingSecurityScan, classification: 'awaitingsecurityscan', icon: <SecurityIcon color="warning" /> },
    { label: 'Rejected', value: batch.rejected, classification: 'rejectedorunprocessable', icon: <RejectedIcon color="error" /> },
  ];
  const pendingCount = Math.max(batch.filesReceived - batch.items.length, 0) + batch.items.filter((item) =>
    item.classification.replaceAll('_', '').toLowerCase() === 'pending').length;
  const visibleItems = activeClassification === null ? batch.items : batch.items.filter((item) =>
    activeClassification === 'awaitingsecurityscan'
      ? item.intakeStatus === 'AwaitingSecurityScan'
      : item.classification.replaceAll('_', '').toLowerCase() === activeClassification);

  return (
    <Box sx={{ maxWidth: 1400, mx: 'auto', p: { xs: 2, md: 3 } }}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { xs: 'stretch', sm: 'center' }, mb: 3 }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 900 }}>Batch reconciliation</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ overflowWrap: 'anywhere' }}>Batch {batch.batchId}</Typography>
        </Box>
        <Stack direction="row" spacing={1}>
          <Button startIcon={<BackIcon />} onClick={() => navigate('/procurement/leads/manual-upload')}>New upload</Button>
          {awaitingSecurityScan > 0 && (
            <Button
              variant="contained"
              color="warning"
              startIcon={retryMutation.isPending ? <CircularProgress size={16} /> : <RetryIcon />}
              onClick={() => retryMutation.mutate()}
              disabled={retryMutation.isPending}
            >
              Retry Blocked Files
            </Button>
          )}
          <Button variant="outlined" startIcon={batchQuery.isFetching ? <CircularProgress size={16} /> : <RefreshIcon />} onClick={() => batchQuery.refetch()} disabled={batchQuery.isFetching}>
            Refresh
          </Button>
        </Stack>
      </Stack>

      <Grid container spacing={1.5} sx={{ mb: 3 }}>
        {metrics.map((metric) => (
          <Grid key={metric.label} size={{ xs: 6, sm: 4, lg: 1.5 }}>
            <Paper component="button" type="button" variant="outlined" onClick={() => setActiveClassification(metric.classification)}
              aria-pressed={activeClassification === metric.classification}
              sx={{ p: 2, borderRadius: 2, minHeight: 104, width: '100%', textAlign: 'left', cursor: 'pointer', bgcolor: activeClassification === metric.classification ? 'action.selected' : 'background.paper' }}>
              <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center' }}>
                <Typography variant="h5" sx={{ fontWeight: 900 }}>{metric.value}</Typography>
                {metric.icon}
              </Stack>
              <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>{metric.label}</Typography>
            </Paper>
          </Grid>
        ))}
      </Grid>

      <Box aria-live="polite" sx={{ mb: retryMutation.isSuccess || retryMutation.isError ? 2 : 0 }}>
        {retryMutation.isSuccess && (
          <Alert severity={retryMutation.data.stillAwaiting > 0 ? 'warning' : 'success'}>
            Retry complete: {retryMutation.data.queued} queued, {retryMutation.data.stillAwaiting} still awaiting security scan, {retryMutation.data.rejected} rejected.
          </Alert>
        )}
        {retryMutation.isError && (
          <Alert severity="error">Blocked files could not be retried. Their stored evidence and current status are unchanged.</Alert>
        )}
      </Box>

      <Alert severity={pendingCount > 0 ? 'info' : batch.rejected > 0 ? 'warning' : 'success'} sx={{ mb: 2 }}>
        {pendingCount > 0
          ? `${pendingCount} occurrence${pendingCount === 1 ? '' : 's'} still processing. Refresh to see completed classifications.`
          : `Processing complete: ${batch.logicalInquiries} inquiries classified${batch.rejected > 0 ? `, including ${batch.rejected} rejected or unsupported` : ' with no processing failures'}.`}
      </Alert>

      <Alert severity={batch.externalOccurrences > 0 ? 'warning' : 'success'} sx={{ mb: 3 }}>
        {batch.externalOccurrences > 0
          ? `${batch.localFirstOccurrences ?? 0} local-first and ${batch.externalOccurrences} external occurrence${batch.externalOccurrences === 1 ? '' : 's'}. ${batch.externalCost == null ? 'Provider cost is not priced.' : `Recorded external cost: ${batch.externalCost.toFixed(4)}.`}`
          : `${batch.localFirstOccurrences ?? 0} reconciled occurrence${batch.localFirstOccurrences === 1 ? '' : 's'} used local-first processing. No external provider use is recorded.`}
      </Alert>

      <Stack direction={{ xs: 'column', sm: 'row' }} sx={{ justifyContent: 'space-between', alignItems: { xs: 'stretch', sm: 'center' }, mb: 1.5 }}>
        <Typography variant="h6" sx={{ fontWeight: 900 }}>Reconciled inquiries</Typography>
        <Typography variant="body2" color="text.secondary">{visibleItems.length} of {batch.items.length} recorded occurrence{batch.items.length === 1 ? '' : 's'}</Typography>
      </Stack>
      {batch.items.length === 0 ? (
        <Alert severity="info">No ingestion occurrences have been recorded for this batch yet.</Alert>
      ) : visibleItems.length === 0 ? (
        <Alert severity="info">No recorded occurrences match this summary category.</Alert>
      ) : (
        <Stack spacing={1.5}>
          {visibleItems.map((item) => (
            <ReconciliationRow
              key={`${item.sourceDocumentOccurrenceId ?? 'lead'}:${item.occurrenceId}:${item.classification}`}
              item={item}
            />
          ))}
        </Stack>
      )}
    </Box>
  );
}
