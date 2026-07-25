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
      <Typography variant="caption" component="pre" sx={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', mt: 1 }}>
        {candidate.matchEvidenceJson}{'\n'}Differences: {candidate.differencesJson}{'\n'}Commercial impact: {candidate.downstreamImpactJson}
      </Typography>
      {candidate.reviewState === 'Pending' && (
        <Stack direction="row" spacing={1} sx={{ mt: 1.5, flexWrap: 'wrap', gap: 1 }}>
          <Button size="small" variant="contained" onClick={() => choose('revision')}>Confirm revision</Button>
          <Button size="small" onClick={() => choose('exact_duplicate')}>Exact duplicate</Button>
          <Button size="small" onClick={() => choose('create_new')}>Create new lead</Button>
          <Button size="small" onClick={() => choose('defer')}>Defer</Button>
          <Button size="small" color="error" onClick={() => choose('reject')}>Reject source</Button>
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
  const meta = classificationMeta(item.classification);
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
            Ingested {dayjs(item.ingestedAtUtc).isValid() ? dayjs(item.ingestedAtUtc).format('DD MMM YYYY, HH:mm') : 'time unavailable'}
            {' | '}{readable(item.processingPath)}{' | '}{confidenceLabel(item.confidence)}
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
            label={item.externalAiUsed ? 'External processing used' : 'No external processing'}
            color={item.externalAiUsed ? 'warning' : 'default'}
            variant="outlined"
          />
          {canOpenLead && (
            <Button
              variant="outlined"
              size="small"
              endIcon={<OpenIcon />}
              onClick={() => navigate(`/procurement/leads/view/${item.leadId}`)}
            >
              Open lead
            </Button>
          )}
        </Stack>
      </Stack>
    </Paper>
  );
};

export default function LeadIngestionBatchPage() {
  const { batchId = '' } = useParams<{ batchId: string }>();
  const navigate = useNavigate();
  const batchQuery = useQuery({
    queryKey: ['lead-ingestion-batch', batchId],
    queryFn: () => leadService.getIngestionBatch(batchId),
    enabled: Boolean(batchId),
    retry: 8,
    retryDelay: 1500,
    refetchInterval: (query) => query.state.data?.items.some((item) =>
      item.classification.replaceAll('_', '').toLowerCase() === 'pending') ? 4000 : false,
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
    return (
      <Box sx={{ maxWidth: 900, mx: 'auto', p: 3 }}>
        <Alert
          severity="info"
          action={<Button color="inherit" startIcon={<RefreshIcon />} onClick={() => batchQuery.refetch()}>Check again</Button>}
        >
          Reconciliation results are not available yet. The batch may still be entering the processing queue.
        </Alert>
      </Box>
    );
  }

  const batch = batchQuery.data;
  const metrics = [
    { label: 'Files received', value: batch.filesReceived, icon: <FilesIcon color="action" /> },
    { label: 'Logical inquiries', value: batch.logicalInquiries, icon: <ReviewIcon color="action" /> },
    { label: 'New leads', value: batch.newLeads, icon: <NewIcon color="success" /> },
    { label: 'Exact duplicates', value: batch.exactDuplicates, icon: <DuplicateIcon color="info" /> },
    { label: 'Revisions', value: batch.revisions, icon: <RevisionIcon color="primary" /> },
    { label: 'Possible matches', value: batch.possibleMatches, icon: <ReviewIcon color="warning" /> },
    { label: 'Rejected', value: batch.rejected, icon: <RejectedIcon color="error" /> },
  ];

  return (
    <Box sx={{ maxWidth: 1400, mx: 'auto', p: { xs: 2, md: 3 } }}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { xs: 'stretch', sm: 'center' }, mb: 3 }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 900 }}>Batch reconciliation</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ overflowWrap: 'anywhere' }}>Batch {batch.batchId}</Typography>
        </Box>
        <Stack direction="row" spacing={1}>
          <Button startIcon={<BackIcon />} onClick={() => navigate('/procurement/leads/manual-upload')}>New upload</Button>
          <Button variant="outlined" startIcon={batchQuery.isFetching ? <CircularProgress size={16} /> : <RefreshIcon />} onClick={() => batchQuery.refetch()} disabled={batchQuery.isFetching}>
            Refresh
          </Button>
        </Stack>
      </Stack>

      <Grid container spacing={1.5} sx={{ mb: 3 }}>
        {metrics.map((metric) => (
          <Grid key={metric.label} size={{ xs: 6, sm: 4, lg: 12 / 7 }}>
            <Paper variant="outlined" sx={{ p: 2, borderRadius: 2, minHeight: 104 }}>
              <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center' }}>
                <Typography variant="h5" sx={{ fontWeight: 900 }}>{metric.value}</Typography>
                {metric.icon}
              </Stack>
              <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>{metric.label}</Typography>
            </Paper>
          </Grid>
        ))}
      </Grid>

      <Alert severity={batch.externalOccurrences > 0 ? 'warning' : 'success'} sx={{ mb: 3 }}>
        {batch.externalOccurrences > 0
          ? `${batch.externalOccurrences} occurrence${batch.externalOccurrences === 1 ? '' : 's'} used external processing. Recorded external cost: ${batch.externalCost.toFixed(4)}.`
          : 'No external processing is recorded for this batch.'}
      </Alert>

      <Stack direction={{ xs: 'column', sm: 'row' }} sx={{ justifyContent: 'space-between', alignItems: { xs: 'stretch', sm: 'center' }, mb: 1.5 }}>
        <Typography variant="h6" sx={{ fontWeight: 900 }}>Reconciled inquiries</Typography>
        <Typography variant="body2" color="text.secondary">{batch.items.length} recorded occurrence{batch.items.length === 1 ? '' : 's'}</Typography>
      </Stack>
      {batch.items.length === 0 ? (
        <Alert severity="info">No ingestion occurrences have been recorded for this batch yet.</Alert>
      ) : (
        <Stack spacing={1.5}>
          {batch.items.map((item) => <ReconciliationRow key={item.occurrenceId} item={item} />)}
        </Stack>
      )}
    </Box>
  );
}
