import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams } from 'react-router-dom';
import {
  Alert,
  AlertTitle,
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
  Tooltip,
  Typography,
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  CheckCircleOutlined as NewIcon,
  CloudOff as OfflineIcon,
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
import { clientStatusLabel } from './ClientCell';
import { useAuth } from '../../context/AuthContext';
import ApiErrorNotice from '../../components/common/ApiErrorNotice';
import { presentableServerText } from '../../utils/apiErrors';
import {
  explainIntakeItem,
  explainSecurityRetryOutcome,
  isInfrastructureHold,
  summariseSecurityRetryHolds,
} from '../../utils/intakeErrors';

type ChipColor = 'default' | 'primary' | 'success' | 'warning' | 'error' | 'info';

/**
 * Prettifies machine tokens for LABELS ONLY (statuses, classifications, processing paths).
 *
 * It must never be used on an error code: title-casing `document_quarantined` into "Document
 * Quarantined" restates the code without explaining anything. Error codes go through
 * `explainIntakeItem` in src/utils/intakeErrors.ts.
 */
const readable = (value: string): string => value
  .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
  .replaceAll('_', ' ')
  .replace(/\b\w/g, (character) => character.toUpperCase());

/**
 * `reasons` is the literal path by which backend exception text becomes product copy: the read
 * model falls back to the extraction job's raw `LastError`
 * (LeadIdentity/LeadIdentityApplicationService.cs:399-400). Anything carrying operator signal is
 * withheld here — `explainIntakeItem` already provides the user-facing explanation, and the raw
 * text stays available through the support disclosure.
 */
const presentableReasons = (reasons: string[]): string[] => reasons
  // "Intake stopped: document quarantined." is the code echoed back; the error map says it better.
  .filter((reason) => !/^\s*Intake (?:stopped|status):/i.test(reason))
  // One gate, shared with every other server string in the product (src/utils/apiErrors.ts):
  // bounded length, printable, no markup, no hostnames, no stack frames, no bare reason phrases.
  .map((reason) => presentableServerText(reason))
  .filter((reason): reason is string => reason !== null);

/**
 * The exact complement of {@link presentableReasons}: everything the gate refused, so the "detail
 * was recorded for support" note appears whenever something real was held back — not only for the
 * technical-marker case it used to check.
 */
const withheldReasons = (reasons: string[]): string[] => reasons
  .map((reason) => reason.trim())
  .filter((reason) => reason.length > 0)
  .filter((reason) => !/^Intake (?:stopped|status):/i.test(reason))
  .filter((reason) => presentableServerText(reason) === null);

/**
 * Poll backoff. The old loop stopped the moment nothing was Pending or Awaiting, so a scanner that
 * recovered minutes later was never noticed. We keep watching while infrastructure holds remain,
 * and widen the interval so a long outage is not a tight loop against the API.
 */
export const pollIntervalFor = (completedFetches: number): number =>
  Math.min(2000 * 2 ** Math.floor(Math.max(completedFetches, 0) / 3), 30000);

const classificationMeta = (classification: string): { label: string; color: ChipColor } => {
  const normalized = classification.replaceAll('_', '').toLowerCase();
  if (normalized === 'new') return { label: 'New lead', color: 'success' };
  if (normalized === 'exactduplicate') return { label: 'Exact duplicate', color: 'info' };
  if (normalized === 'revision') return { label: 'Revision', color: 'primary' };
  if (normalized === 'possiblematchreviewrequired') return { label: 'Possible match review', color: 'warning' };
  if (normalized === 'rejectedorunprocessable') return { label: 'Rejected or unsupported', color: 'error' };
  return { label: readable(classification || 'Pending'), color: 'default' };
};

const confidenceLabel = (confidence: number, scored = true): string => scored
  ? `${Math.round(confidence * 100)}% confidence`
  : 'Not yet scored';

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
  // Number(null) is 0 and Number.isFinite(0) is true, so an unstated quantity used to render
  // as "(quantity 0)" — a line the buyer never quantified, reported as an order for none.
  const raw = row.Quantity ?? row.quantity;
  if (raw === null || raw === undefined || raw === '') return `${part} (quantity not stated)`;
  const quantity = Number(raw);
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

const BATCH_LOAD_FALLBACK =
  'The reconciliation service is not answering. Every source document remains safely recorded — retry when the service recovers.';

const MatchReviewPanel = ({ occurrenceId, candidate }: { occurrenceId: number; candidate: LeadMatchCandidateDTO }) => {
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const canEditLeads = hasPermission('Leads', 'edit');
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
      {candidate.reviewState === 'Pending' && canEditLeads && (
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
      {mutation.isError && (
        <ApiErrorNotice
          error={mutation.error}
          fallbackMessage="The decision was not recorded. Nothing changed — refresh and try again."
          sx={{ mt: 1 }}
        />
      )}
    </Box>
  );
};

interface ReconciliationRowProps {
  item: BatchReconciliationItemDTO;
  /** Present only when the signed-in user may release held files. */
  onRetryHold?: () => void;
  retrying?: boolean;
  /** Outcome recorded for this occurrence by the most recent retry sweep. */
  retryOutcome?: { status: string; errorCode?: string | null };
}

const ReconciliationRow = ({ item, onRetryHold, retrying, retryOutcome }: ReconciliationRowProps) => {
  const navigate = useNavigate();
  const { hasPermission } = useAuth();
  const held = isInfrastructureHold(item);
  const classificationPending = item.classification.replaceAll('_', '').toLowerCase() === 'pending';
  const hasExtractionScore = !held && !classificationPending;
  const canPrepareRfq = hasPermission('Leads', 'create') && hasPermission('RFQ Management', 'create');
  const failed = held || item.classification.replaceAll('_', '').toLowerCase() === 'rejectedorunprocessable';
  const explanation = failed ? explainIntakeItem(item) : null;
  const meta = held
    ? { label: 'Held — scanning offline', color: 'warning' as ChipColor }
    : classificationMeta(item.classification);
  const canOpenLead = typeof item.leadId === 'number' && item.leadId > 0;
  // `explainIntakeItem` promotes the recorded reason to `whatHappened` whenever the code is a
  // bucket, so listing it again below the explanation would print the same sentence twice.
  const shownReasons = presentableReasons(item.reasons)
    .filter((reason) => reason !== explanation?.whatHappened);
  const hiddenReasons = withheldReasons(item.reasons);

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
            {' | '}{readable(item.processingPath)}{' | '}{confidenceLabel(item.confidence, hasExtractionScore)}
          </Typography>
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
            Security {readable(item.securityStatus || 'Pending')} updated {timestampLabel(item.securityScanUpdatedAtUtc || item.lastUpdatedAtUtc)}
            {item.extractionStatus ? ` | Extraction ${readable(item.extractionStatus)} updated ${timestampLabel(item.extractionUpdatedAtUtc)}` : ''}
          </Typography>
          {explanation && (
            <Alert
              severity={explanation.category === 'infrastructure' ? 'warning' : 'error'}
              icon={explanation.category === 'infrastructure' ? <OfflineIcon fontSize="inherit" /> : undefined}
              variant="outlined"
              sx={{ mt: 1.25, borderRadius: 1.5 }}
            >
              <AlertTitle sx={{ fontWeight: 800, mb: 0.25 }}>{explanation.title}</AlertTitle>
              <Typography variant="body2">{explanation.whatHappened}</Typography>
              <Typography variant="body2" sx={{ mt: 0.5, fontWeight: 700 }}>{explanation.nextAction}</Typography>
              {shownReasons.length > 0 && (
                <Stack spacing={0.25} sx={{ mt: 0.75 }}>
                  {shownReasons.map((reason) => (
                    <Typography key={reason} variant="caption" color="text.secondary">{reason}</Typography>
                  ))}
                </Stack>
              )}
              {hiddenReasons.length > 0 && (
                <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.75 }}>
                  Diagnostic detail was recorded for support and is not shown here.
                </Typography>
              )}
              {retryOutcome && (
                <Typography variant="caption" sx={{ display: 'block', mt: 0.75, fontWeight: 700 }}>
                  {/* One vocabulary for every outcome the sweep can report, including the
                      email-component refusals. The old branch said "scanning is still offline"
                      for ALL of them, which is wrong for a part that is already queued (benign)
                      and wrong again for one whose ownership could not be resolved (a person has
                      to act). The raw code is never shown. */}
                  Last retry: {explainSecurityRetryOutcome(retryOutcome).sentence}
                </Typography>
              )}
            </Alert>
          )}
          {!explanation && item.intakeStatus && item.intakeStatus !== 'Reconciled' && (
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.75 }}>
              Intake: {readable(item.intakeStatus)}
            </Typography>
          )}
          <Typography variant="body2" sx={{ mt: 1 }}>
            {/* Plain language, not the raw match enum: "AUTO_MATCHED_CONTACT_UNRESOLVED"
                told an operator nothing. Same vocabulary as the leads grid. */}
            Client: {clientStatusLabel(item.customerResolutionStatus)}
            {' | '}Owner: {item.assignedOpportunityOwner || 'Not assigned'}
          </Typography>
          {!explanation && shownReasons.length > 0 && (
            <Stack spacing={0.25} sx={{ mt: 1 }}>
              {shownReasons.map((reason) => (
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
          {held && onRetryHold && (
            <Tooltip title="Replays this file from its stored original. Releases every held file in this batch — no re-upload needed.">
              <span>
                <Button
                  variant="contained"
                  color="warning"
                  size="small"
                  startIcon={retrying ? <CircularProgress size={16} color="inherit" /> : <RetryIcon />}
                  onClick={onRetryHold}
                  disabled={retrying}
                >
                  {retrying ? 'Retrying…' : 'Retry security scan'}
                </Button>
              </span>
            </Tooltip>
          )}
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
              {canPrepareRfq && (
                <Button
                  variant="contained"
                  size="small"
                  onClick={() => navigate(`/procurement/leads/${item.leadId}/workbench`)}
                >
                  Open decision workbench
                </Button>
              )}
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
  const { hasPermission } = useAuth();
  const canCreateLeads = hasPermission('Leads', 'create');
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
      const stillArriving = batch.items.length < batch.filesReceived;
      const stillProcessing = batch.items.some((item) =>
        item.classification.replaceAll('_', '').toLowerCase() === 'pending');
      // Infrastructure holds keep the loop alive: the scanner may recover at any point, and the
      // user must see that happen without reloading the page.
      const stillHeld = batch.items.some(isInfrastructureHold);
      if (!stillArriving && !stillProcessing && !stillHeld) return false;
      return stillArriving || stillProcessing
        ? 2000
        : pollIntervalFor(query.state.dataUpdateCount);
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
    return (
      <Box sx={{ maxWidth: 900, mx: 'auto', p: 3 }}>
        <ApiErrorNotice
          error={batchQuery.error}
          fallbackMessage={BATCH_LOAD_FALLBACK}
          onRetry={() => batchQuery.refetch()}
          retryLabel="Check again"
        />
      </Box>
    );
  }

  const batch = batchQuery.data;
  // Derived from the items, NOT from `batch.awaitingSecurityScan`. That counter only tallies intake
  // occurrences that were never reconciled, so it reaches 0 the moment a hold is recorded against a
  // reconciled occurrence — which is exactly when the retry control used to vanish and strand the
  // files. `recoverableSecurityHold` is per-item and durable.
  const heldItems = batch.items.filter(isInfrastructureHold);
  const heldCount = Math.max(heldItems.length, batch.awaitingSecurityScan ?? 0);
  const canRetryHolds = heldCount > 0 && canCreateLeads;
  const retryOutcomes = new Map(
    (retryMutation.data?.items ?? []).map((entry) => [entry.sourceDocumentOccurrenceId, entry]),
  );
  const retryStillAwaiting = summariseSecurityRetryHolds(retryMutation.data ?? {});
  const metrics = [
    { label: 'Files received', value: batch.filesReceived, classification: null, icon: <FilesIcon color="action" /> },
    { label: 'Logical inquiries', value: batch.logicalInquiries, classification: null, icon: <ReviewIcon color="action" /> },
    { label: 'New leads', value: batch.newLeads, classification: 'new', icon: <NewIcon color="success" /> },
    { label: 'Exact duplicates', value: batch.exactDuplicates, classification: 'exactduplicate', icon: <DuplicateIcon color="info" /> },
    { label: 'Revisions', value: batch.revisions, classification: 'revision', icon: <RevisionIcon color="primary" /> },
    { label: 'Possible matches', value: batch.possibleMatches, classification: 'possiblematchreviewrequired', icon: <ReviewIcon color="warning" /> },
    { label: 'Held by scanning', value: heldCount, classification: 'awaitingsecurityscan', icon: <SecurityIcon color="warning" /> },
    { label: 'Rejected', value: batch.rejected, classification: 'rejectedorunprocessable', icon: <RejectedIcon color="error" /> },
  ];
  const pendingCount = Math.max(batch.filesReceived - batch.items.length, 0) + batch.items.filter((item) =>
    item.classification.replaceAll('_', '').toLowerCase() === 'pending').length;
  const visibleItems = activeClassification === null ? batch.items : batch.items.filter((item) =>
    activeClassification === 'awaitingsecurityscan'
      ? isInfrastructureHold(item)
      : item.classification.replaceAll('_', '').toLowerCase() === activeClassification);

  return (
    <Box sx={{ maxWidth: 1400, mx: 'auto', p: { xs: 2, md: 3 } }}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { xs: 'stretch', sm: 'center' }, mb: 3 }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 900 }}>Batch reconciliation</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ overflowWrap: 'anywhere' }}>Batch {batch.batchId}</Typography>
        </Box>
        <Stack direction="row" spacing={1}>
          {canCreateLeads && <Button startIcon={<BackIcon />} onClick={() => navigate('/procurement/leads/manual-upload')}>New upload</Button>}
          {canRetryHolds && (
            <Button
              variant="contained"
              color="warning"
              startIcon={retryMutation.isPending ? <CircularProgress size={16} /> : <RetryIcon />}
              onClick={() => retryMutation.mutate()}
              disabled={retryMutation.isPending}
            >
              {retryMutation.isPending ? 'Retrying…' : `Retry ${heldCount} held file${heldCount === 1 ? '' : 's'}`}
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

      {/*
        Degraded-service banner. An infrastructure outage is not a rejection: it is our fault, the
        files are intact, and they resume without the user doing anything. It is deliberately styled
        as an outage notice (warning + offline icon) so it never reads like the per-file content
        rejections below it.
      */}
      {heldCount > 0 && (
        <Alert
          severity="warning"
          icon={<OfflineIcon fontSize="inherit" />}
          sx={{ mb: 2, borderLeft: 4, borderColor: 'warning.main' }}
          action={canRetryHolds ? (
            <Button
              color="inherit"
              size="small"
              startIcon={retryMutation.isPending ? <CircularProgress size={16} color="inherit" /> : <RetryIcon />}
              onClick={() => retryMutation.mutate()}
              disabled={retryMutation.isPending}
            >
              Retry now
            </Button>
          ) : undefined}
        >
          <AlertTitle sx={{ fontWeight: 800 }}>Malware scanning is offline</AlertTitle>
          {/* NOT "will process automatically". Nothing in this deployment sweeps a held file back
              into processing — the recovery service runs only when someone presses Retry — so the
              banner names the action instead of promising one that never comes. */}
          {heldCount} file{heldCount === 1 ? ' is' : 's are'} held safely, and {heldCount === 1 ? 'it stays' : 'they stay'} held
          until someone releases {heldCount === 1 ? 'it' : 'them'}. Nothing is wrong with {heldCount === 1 ? 'this document' : 'these documents'},
          and no re-upload is needed — the originals are stored exactly as you sent them. Use
          &quot;Retry now&quot; once scanning is back.
        </Alert>
      )}

      <Box aria-live="polite" sx={{ mb: retryMutation.isSuccess || retryMutation.isError ? 2 : 0 }}>
        {retryMutation.isSuccess && (
          <Alert severity={retryMutation.data.queued > 0 ? 'success' : 'warning'}>
            <AlertTitle sx={{ fontWeight: 800 }}>
              {retryMutation.data.queued > 0
                ? `${retryMutation.data.queued} file${retryMutation.data.queued === 1 ? '' : 's'} released for processing`
                : 'No files could be released yet'}
            </AlertTitle>
            {/* Split by what each outcome actually MEANS, because "still awaiting" is not one
                situation. A part already in the queue needs nobody; a part whose link to its
                email could not be resolved needs an administrator. One sentence covering both
                is guaranteed to be wrong for one of them. */}
            {retryStillAwaiting.resuming > 0 && (
              <>{retryStillAwaiting.resuming} already in the processing queue — {retryStillAwaiting.resuming === 1 ? 'it finishes' : 'they finish'} without you. </>
            )}
            {retryStillAwaiting.needsAPerson > 0 && (
              <>{retryStillAwaiting.needsAPerson} still held, and {retryStillAwaiting.needsAPerson === 1 ? 'it will not' : 'they will not'} be released on their own — see each file below for what it needs. </>
            )}
            {retryMutation.data.rejected > 0 && (
              <>{retryMutation.data.rejected} did not pass inspection on this attempt. </>
            )}
            {retryMutation.data.sourceObjectUnavailable > 0 && (
              <>{retryMutation.data.sourceObjectUnavailable} could not be read from storage — contact support with this batch reference. </>
            )}
            {retryMutation.data.eligible === 0 && 'No file in this batch is currently eligible for replay.'}
            {retryMutation.data.moreRemaining && ' More files remain — run the retry again to continue.'}
          </Alert>
        )}
        {retryMutation.isError && (
          <ApiErrorNotice
            error={retryMutation.error}
            fallbackMessage="Held files could not be retried. Their stored originals and current status are unchanged — try again shortly."
            onRetry={() => retryMutation.mutate()}
          />
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
              onRetryHold={canCreateLeads ? () => retryMutation.mutate() : undefined}
              retrying={retryMutation.isPending}
              retryOutcome={item.sourceDocumentOccurrenceId != null
                ? retryOutcomes.get(item.sourceDocumentOccurrenceId)
                : undefined}
            />
          ))}
        </Stack>
      )}
    </Box>
  );
}
