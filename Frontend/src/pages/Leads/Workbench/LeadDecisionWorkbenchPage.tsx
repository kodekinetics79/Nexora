import React from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Box,
  Breadcrumbs,
  Button,
  Chip,
  CircularProgress,
  Divider,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Link,
  Paper,
  Stack,
  Tab,
  Tabs,
  TablePagination,
  Typography,
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  CheckCircleOutlined as PromoteIcon,
  NavigateNext as NextIcon,
  SaveOutlined as SaveIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import leadDecisionService, {
  type FitAssessmentDTO,
  type SaveFitAssessmentRequest,
} from '../../../api/services/leadDecisionService';
import { useAuth } from '../../../context/AuthContext';
import { presentableErrorMessage } from '../../../utils/apiErrors';
import { formatDateSafe } from '../../../utils/dates';
import FitAssessmentPanel from './FitAssessmentPanel';
import FullNoBidCommitDialog from './FullNoBidCommitDialog';
import LeadValidationGrid from './LeadValidationGrid';
import SourceEvidencePanel from './SourceEvidencePanel';
import { retryOperation, type RetryOperation } from './retryIdempotency';
import {
  blockerAction,
  countDecisions,
  decisionRecordIsLocked,
  decisionsEqual,
  initializeDecisionMap,
  promotionBlockers,
  validGovernedDecision,
  type DecisionMap,
} from './workbenchRules';

type WorkbenchStage = 'evidence' | 'validate' | 'participation' | 'promote';

const stageLabel: Record<WorkbenchStage, string> = {
  evidence: '1. Evidence',
  validate: '2. Review transformation',
  participation: '3. Fit & Participation',
  promote: '4. Promote',
};

const CountChip = ({ label, count, color = 'default' }: { label: string; count: number; color?: 'default' | 'success' | 'warning' | 'info' }) => (
  <Chip size="small" label={`${label} ${count}`} color={color} variant={count > 0 ? 'filled' : 'outlined'} sx={{ fontWeight: 800 }} />
);

const LeadDecisionWorkbenchPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const leadId = Number(id);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { hasPermission } = useAuth();
  const canEdit = hasPermission('Leads', 'edit');
  const canPromote = canEdit && hasPermission('RFQ Management', 'create');
  const [stage, setStage] = React.useState<WorkbenchStage>('evidence');
  const [decisions, setDecisions] = React.useState<DecisionMap>({});
  const [baselineDecisions, setBaselineDecisions] = React.useState<DecisionMap>({});
  const [fitAssessment, setFitAssessment] = React.useState<FitAssessmentDTO | null>(null);
  const [fullNoBidDialogOpen, setFullNoBidDialogOpen] = React.useState(false);
  const [bidCommitReviewOpen, setBidCommitReviewOpen] = React.useState(false);
  const [bidCommitReviewPage, setBidCommitReviewPage] = React.useState(0);
  const fitRetryOperation = React.useRef<RetryOperation | null>(null);
  const participationRetryOperation = React.useRef<RetryOperation | null>(null);
  const promotionKey = React.useRef<string | null>(null);
  const promotionRevision = React.useRef<number | null>(null);
  const decisionSeed = React.useRef<string | null>(null);

  const workbenchQuery = useQuery({
    queryKey: ['lead-decision-workbench', leadId],
    queryFn: () => leadDecisionService.getWorkbench(leadId),
    enabled: Number.isFinite(leadId) && leadId > 0,
    retry: 1,
  });

  React.useEffect(() => {
    if (!workbenchQuery.data) return;
    const seed = [
      workbenchQuery.data.leadRevisionId,
      workbenchQuery.data.participationVersion ?? 'none',
      workbenchQuery.data.participationStatus,
    ].join(':');
    // A fit-assessment save refreshes the envelope. Preserve unsaved participation edits unless
    // the authoritative Lead revision or participation snapshot itself changed.
    if (decisionSeed.current !== seed) {
      const initial = initializeDecisionMap(workbenchQuery.data);
      setDecisions(initial);
      setBaselineDecisions(initial);
      decisionSeed.current = seed;
    }
    setFitAssessment(workbenchQuery.data.fitAssessment ?? null);
    if (promotionRevision.current !== workbenchQuery.data.leadRevisionId) {
      promotionKey.current = `lead-promotion:${leadId}:${workbenchQuery.data.leadRevisionId}:${crypto.randomUUID()}`;
      promotionRevision.current = workbenchQuery.data.leadRevisionId;
    }
  }, [leadId, workbenchQuery.data]);

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['lead-decision-workbench', leadId] });
    await queryClient.invalidateQueries({ queryKey: ['lead-detail', leadId] });
  };

  const fitMutation = useMutation({
    mutationFn: (request: SaveFitAssessmentRequest) => {
      const operation = retryOperation(fitRetryOperation.current, 'lead-fit', leadId, request);
      fitRetryOperation.current = operation;
      return leadDecisionService.saveFitAssessment(leadId, request, operation.key);
    },
    onSuccess: async (assessment) => {
      fitRetryOperation.current = null;
      setFitAssessment(assessment);
      enqueueSnackbar('Fit assessment saved against this Lead revision.', { variant: 'success' });
      await refresh();
    },
    onError: (error: unknown) => enqueueSnackbar(
      presentableErrorMessage(error, 'The fit assessment could not be saved. Nothing was changed.'),
      { variant: 'error' },
    ),
  });

  const participationMutation = useMutation({
    mutationFn: (command: { commit: boolean; reasonCode?: string; notes?: string }) => {
      const workbench = workbenchQuery.data!;
      const request = {
        expectedLeadRevisionId: workbench.leadRevisionId,
        expectedDecisionVersion: workbench.decisionVersion,
        expectedParticipationVersion: workbench.participationVersion,
        commit: command.commit,
        reasonCode: command.reasonCode,
        notes: command.notes,
        lines: workbench.lines.map((line) => ({
          revisionLineId: line.revisionLineId,
          decision: decisions[line.revisionLineId]?.decision ?? 'Pending',
          reasonCode: decisions[line.revisionLineId]?.reasonCode,
          note: decisions[line.revisionLineId]?.note,
          productId: decisions[line.revisionLineId]?.productId,
          quantity: decisions[line.revisionLineId]?.quantity,
          unitOfMeasure: decisions[line.revisionLineId]?.unitOfMeasure,
          currency: decisions[line.revisionLineId]?.currency,
        })),
      };
      const scope = command.commit ? 'lead-participation-commit' : 'lead-participation-draft';
      const operation = retryOperation(participationRetryOperation.current, scope, leadId, request);
      participationRetryOperation.current = operation;
      return leadDecisionService.saveParticipation(leadId, request, operation.key);
    },
    onSuccess: async (result, command) => {
      participationRetryOperation.current = null;
      setBaselineDecisions(decisions);
      enqueueSnackbar(
        command.commit
          ? result.participationStatus === 'COMMITTED' ? 'Participation decision committed.' : 'Participation decision saved.'
          : 'Participation draft saved.',
        { variant: 'success' },
      );
      await refresh();
      if (command.commit) setStage('promote');
    },
    onError: (error: unknown) => enqueueSnackbar(
      presentableErrorMessage(error, 'The participation decision could not be saved. Nothing was changed.'),
      { variant: 'error' },
    ),
  });

  const promotionMutation = useMutation({
    mutationFn: () => {
      const workbench = workbenchQuery.data!;
      if (!workbench.participationVersion || !promotionKey.current) throw new Error('A committed participation version is required.');
      return leadDecisionService.promoteToRfq(leadId, {
        expectedLeadRevisionId: workbench.leadRevisionId,
        expectedDecisionVersion: workbench.decisionVersion,
        expectedParticipationVersion: workbench.participationVersion,
        idempotencyKey: promotionKey.current,
      });
    },
    onSuccess: async (receipt) => {
      enqueueSnackbar(`${receipt.promotedLineCount} approved line${receipt.promotedLineCount === 1 ? '' : 's'} promoted to one RFQ.`, { variant: 'success' });
      await refresh();
      navigate(`/procurement/rfqs/view/${receipt.rfqId}`);
    },
    onError: (error: unknown) => enqueueSnackbar(
      presentableErrorMessage(error, 'The approved lines could not be promoted. No second RFQ was created.'),
      { variant: 'error' },
    ),
  });

  if (workbenchQuery.isLoading) {
    return (
      <Box sx={{ minHeight: '60vh', display: 'grid', placeItems: 'center' }}>
        <Stack spacing={1.5} sx={{ alignItems: 'center' }}><CircularProgress /><Typography color="text.secondary">Loading the Lead decision record…</Typography></Stack>
      </Box>
    );
  }

  if (workbenchQuery.isError || !workbenchQuery.data) {
    return (
      <Box sx={{ p: { xs: 1, sm: 3 }, maxWidth: 760, mx: 'auto' }}>
        <Alert severity="error" action={<Button color="inherit" onClick={() => workbenchQuery.refetch()}>Retry</Button>}>
          <AlertTitle>Decision workbench unavailable</AlertTitle>
          The Lead remains unchanged. Retry, or return to the Lead record.
        </Alert>
        <Button startIcon={<BackIcon />} onClick={() => navigate(`/procurement/leads/view/${leadId}`)} sx={{ mt: 2 }}>Back to Lead</Button>
      </Box>
    );
  }

  const workbench = workbenchQuery.data;
  const counts = countDecisions(decisions);
  const dirty = !decisionsEqual(decisions, baselineDecisions);
  const governed = Object.values(decisions).every(validGovernedDecision);
  const allDecided = counts.total > 0 && counts.pending === 0;
  const fullNoBid = counts.total > 0 && counts.noBid === counts.total;
  const fullNoBidClosed = fullNoBid && workbench.participationStatus === 'COMMITTED';
  const decisionMutationPending = fitMutation.isPending || participationMutation.isPending || promotionMutation.isPending;
  const decisionRecordLocked = decisionRecordIsLocked(workbench, decisions);
  const validUnitCodes = new Set((workbench.unitOptions ?? []).map((option) => option.code.toUpperCase()));
  const validCurrencyCodes = new Set((workbench.currencyOptions ?? []).map((option) => option.code.toUpperCase()));
  const bidValuesReady = workbench.lines.every((line) => {
    const decision = decisions[line.revisionLineId];
    if (decision?.decision !== 'Bid') return true;
    return Boolean(decision.quantity && Number.isInteger(decision.quantity) && decision.quantity > 0
      && decision.unitOfMeasure && validUnitCodes.has(decision.unitOfMeasure.toUpperCase())
      && decision.currency && validCurrencyCodes.has(decision.currency.toUpperCase())
      && (!line.needsAttention || (decision.note?.trim().length ?? 0) >= 5));
  });
  const fitActionable = Boolean(fitAssessment && fitAssessment.version > 0
    && fitAssessment.overallDecision !== 'NOT_FIT'
    && fitAssessment.criteria.every((criterion) => criterion.decision === 'PASS' || criterion.decision === 'NOT_APPLICABLE'));
  const sourceAndLifecycleReady = Boolean(workbench.customerId)
    && workbench.verificationStatus === 'VERIFIED'
    && !workbench.blockers.some((blocker) => ['SOURCE_UNAVAILABLE', 'SOURCE_LINEAGE_INCOMPLETE', 'LEAD_NOT_ELIGIBLE'].includes(blocker.code));
  // Clarify is a persisted draft decision, not a final participation outcome. It stays editable
  // and saveable, but cannot be labelled COMMITTED or become an RFQ promotion input.
  const canCommit = canEdit && allDecided && counts.clarify === 0 && governed
    && (fullNoBid ? (fitAssessment?.version ?? 0) > 0 : bidValuesReady && fitActionable && sourceAndLifecycleReady)
    && !decisionMutationPending
    && !fullNoBidClosed
    && (dirty || workbench.participationStatus !== 'COMMITTED');
  const blockers = promotionBlockers({
    workbench,
    decisions,
    fitAssessment,
    dirty,
    participationStatus: workbench.participationStatus,
    participationVersion: workbench.participationVersion,
  });
  const promotionPermissionBlocker = !canPromote && counts.bid > 0
    ? 'RFQ creation permission is required. Hand this committed decision to an authorized RFQ owner.'
    : null;
  const primaryBlocker = promotionPermissionBlocker ?? blockers[0] ?? null;
  const actionableBlockers = workbench.blockers
    .map((blocker) => ({ code: blocker.code, action: blockerAction(blocker, leadId) }))
    .filter((item): item is { code: string; action: { label: string; path: string } } => Boolean(item.action));

  return (
    <Box sx={{ p: { xs: 1, sm: 2 }, maxWidth: 1920, mx: 'auto', pb: 2 }}>
      <Breadcrumbs separator={<NextIcon sx={{ fontSize: 14 }} />} sx={{ mb: 1.5 }}>
        <Link component="button" variant="caption" onClick={() => navigate('/procurement/leads/all')}>Leads</Link>
        <Link component="button" variant="caption" onClick={() => navigate(`/procurement/leads/view/${leadId}`)}>{workbench.customerRfqReference || `Lead #${leadId}`}</Link>
        <Typography variant="caption" color="primary">Decision workbench</Typography>
      </Breadcrumbs>

      <Paper variant="outlined" sx={{ p: { xs: 1.5, md: 2 }, borderRadius: 2, mb: 1.5 }}>
        <Stack direction={{ xs: 'column', lg: 'row' }} sx={{ justifyContent: 'space-between', gap: 2 }}>
          <Box sx={{ minWidth: 0 }}>
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
              <Typography variant="h5" sx={{ fontWeight: 950, overflowWrap: 'anywhere' }}>{workbench.customerRfqReference || `Lead #${leadId}`}</Typography>
              <Chip size="small" label={workbench.lifecycleStatusLabel || workbench.lifecycleStatusCode} variant="outlined" />
              <Chip size="small" label={`Revision ${workbench.leadRevisionNumber}`} color="primary" variant="outlined" />
              <Chip size="small" label={workbench.verificationStatus.replaceAll('_', ' ')} color={workbench.verificationStatus === 'VERIFIED' ? 'success' : 'warning'} variant="outlined" />
            </Stack>
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
              {[workbench.customerName || 'Customer unresolved', workbench.buyerName, formatDateSafe(workbench.bidClosingDate ?? null)].filter(Boolean).join(' · ')}
            </Typography>
            {workbench.nexoraSerial ? <Typography variant="caption" color="text.secondary" sx={{ fontFamily: 'monospace' }}>Nexora {workbench.nexoraSerial}</Typography> : null}
          </Box>
          <Stack direction="row" spacing={0.75} sx={{ flexWrap: 'wrap', alignItems: 'flex-start' }} aria-label="Participation decision summary">
            <CountChip label="Bid" count={counts.bid} color="success" />
            <CountChip label="No-bid" count={counts.noBid} />
            <CountChip label="Clarify" count={counts.clarify} color="info" />
            <CountChip label="Undecided" count={counts.pending} color="warning" />
          </Stack>
        </Stack>
      </Paper>

      {workbench.promotion ? (
        <Alert severity="success" sx={{ mb: 1.5 }} action={<Button color="inherit" onClick={() => navigate(`/procurement/rfqs/view/${workbench.promotion!.rfqId}`)}>Open RFQ</Button>}>
          <AlertTitle>Already promoted</AlertTitle>
          Revision {workbench.promotion.leadRevisionNumber} promoted {workbench.promotion.promotedLineCount} approved line{workbench.promotion.promotedLineCount === 1 ? '' : 's'} to {workbench.promotion.rfqNumber || `RFQ #${workbench.promotion.rfqId}`}.
        </Alert>
      ) : null}

      {!canEdit ? <Alert severity="info" sx={{ mb: 1.5 }}>This decision record is read-only for your role.</Alert> : null}

      <Paper variant="outlined" sx={{ mb: 1.5, borderRadius: 2, overflow: 'hidden' }}>
        <Tabs
          value={stage}
          onChange={(_event, value: WorkbenchStage) => setStage(value)}
          variant="scrollable"
          scrollButtons="auto"
          aria-label="Lead decision stages"
        >
          {(Object.keys(stageLabel) as WorkbenchStage[]).map((key) => <Tab key={key} value={key} label={stageLabel[key]} />)}
        </Tabs>
      </Paper>

      {stage === 'evidence' ? <SourceEvidencePanel workbench={workbench} /> : null}

      {stage === 'validate' ? (
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', xl: 'minmax(300px, 0.36fr) minmax(0, 1fr)' }, gap: 1.5 }}>
          <Box sx={{ display: { xs: 'none', xl: 'block' } }}><SourceEvidencePanel workbench={workbench} compact /></Box>
          <Box sx={{ minWidth: 0 }}>
            <Stack direction="row" spacing={1} sx={{ justifyContent: 'space-between', alignItems: 'end', mb: 1, flexWrap: 'wrap' }}>
              <Box><Typography variant="h6" sx={{ fontWeight: 900 }}>Review transformed Lead lines</Typography><Typography variant="caption" color="text.secondary">Exact source values remain beside canonical values and RFQ participation inputs.</Typography></Box>
              <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                <Chip size="small" label={`${workbench.lines.filter((line) => line.verificationStatus !== 'VERIFIED').length} need review`} color={workbench.lines.every((line) => line.verificationStatus === 'VERIFIED') ? 'success' : 'warning'} variant="outlined" />
                <Button size="small" variant="outlined" onClick={() => navigate(`/procurement/extraction/review/${leadId}`)}>
                  {workbench.verificationStatus === 'VERIFIED' ? 'Correct canonical Lead' : 'Open extraction review'}
                </Button>
              </Stack>
            </Stack>
            <Alert severity="info" sx={{ mb: 1 }}>
              Canonical Lead corrections create a new immutable revision in Extraction Review. Choices made in this grid are participation inputs only.
            </Alert>
            <LeadValidationGrid lines={workbench.lines} decisions={decisions} reasonCodes={workbench.reasonCodes}
              unitOptions={workbench.unitOptions ?? []} currencyOptions={workbench.currencyOptions ?? []}
              readOnly={!canEdit || decisionMutationPending || decisionRecordLocked} onDecisionsChange={setDecisions} />
          </Box>
        </Box>
      ) : null}

      {stage === 'participation' ? (
        <Stack spacing={2}>
          <FitAssessmentPanel
            assessment={fitAssessment}
            leadRevisionId={workbench.leadRevisionId}
            decisionVersion={workbench.decisionVersion}
            saving={fitMutation.isPending}
            readOnly={!canEdit || decisionMutationPending || decisionRecordLocked}
            onSave={(request) => fitMutation.mutate(request)}
          />
          <Paper variant="outlined" sx={{ p: 2, borderRadius: 2 }}>
            <Typography variant="h6" sx={{ fontWeight: 900 }}>Participation by line</Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>
              Every line starts undecided. No-bid and clarification require a governed reason. These decisions belong to the Lead revision, before any RFQ exists.
            </Typography>
            <LeadValidationGrid
              lines={workbench.lines}
              decisions={decisions}
              reasonCodes={workbench.reasonCodes}
              unitOptions={workbench.unitOptions ?? []}
              currencyOptions={workbench.currencyOptions ?? []}
              readOnly={!canEdit || decisionMutationPending || decisionRecordLocked}
              onDecisionsChange={setDecisions}
            />
          </Paper>
        </Stack>
      ) : null}

      {stage === 'promote' ? (
        <Paper variant="outlined" component="section" aria-labelledby="promotion-heading" sx={{ p: { xs: 2, md: 3 }, borderRadius: 2 }}>
          <Typography id="promotion-heading" variant="h6" sx={{ fontWeight: 900 }}>RFQ promotion</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
            Promotion creates one formal RFQ from only the lines committed as Bid. It cannot qualify the Lead or invent a participation decision.
          </Typography>
          <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', mb: 2 }}>
            <CountChip label="Approved for RFQ" count={counts.bid} color="success" />
            <CountChip label="Excluded as no-bid" count={counts.noBid} />
            <CountChip label="Awaiting clarification" count={counts.clarify} color="info" />
          </Stack>
          {fullNoBidClosed ? (
            <Alert severity="info"><AlertTitle>Full no-bid committed</AlertTitle>No RFQ will be created for this Lead revision.</Alert>
          ) : !canPromote && blockers.length === 0 ? (
            <Alert severity="warning">
              <AlertTitle>Authorized RFQ owner required</AlertTitle>
              This decision is ready, but your role cannot create RFQs. An authorized RFQ owner must perform the promotion.
            </Alert>
          ) : blockers.length > 0 ? (
            <Alert severity="warning">
              <AlertTitle>Promotion is not ready</AlertTitle>
              <Stack component="ul" spacing={0.5} sx={{ my: 0, pl: 2.5 }}>
                {blockers.map((blocker) => <Typography component="li" variant="body2" key={blocker}>{blocker}</Typography>)}
              </Stack>
              {actionableBlockers.length > 0 ? (
                <Stack direction="row" spacing={1} sx={{ mt: 1.5, flexWrap: 'wrap' }}>
                  {actionableBlockers.map(({ code, action }) => (
                    <Button key={`${code}-${action.path}`} size="small" variant="outlined" color="inherit" onClick={() => navigate(action.path)}>
                      {action.label}
                    </Button>
                  ))}
                </Stack>
              ) : null}
            </Alert>
          ) : (
            <Alert severity="success">The committed participation decision is ready to promote.</Alert>
          )}
        </Paper>
      ) : null}

      <Paper
        elevation={6}
        component="footer"
        sx={{ position: 'sticky', bottom: 12, zIndex: 10, mt: 2, p: 1.5, borderRadius: 2, width: '100%' }}
      >
        <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.25} sx={{ alignItems: { xs: 'stretch', md: 'center' } }}>
          <Box sx={{ flex: 1, minWidth: 0 }}>
            <Typography variant="body2" sx={{ fontWeight: 800 }}>
              {workbench.promotion ? 'This Lead revision already has a promotion receipt.' : primaryBlocker || `${counts.bid} approved line${counts.bid === 1 ? '' : 's'} ready for RFQ promotion.`}
            </Typography>
            <Typography variant="caption" color="text.secondary">
              {dirty ? 'Unsaved participation changes' : `Participation ${workbench.participationStatus.toLowerCase()}`}
            </Typography>
          </Box>
          <Divider orientation="vertical" flexItem sx={{ display: { xs: 'none', md: 'block' } }} />
          <Button
            variant="outlined"
            startIcon={<SaveIcon />}
            disabled={!canEdit || !dirty || (fitAssessment?.version ?? 0) <= 0 || decisionMutationPending || decisionRecordLocked}
            onClick={() => participationMutation.mutate({ commit: false })}
          >
            Save draft
          </Button>
          <Button
            variant="contained"
            color={fullNoBid ? 'warning' : 'primary'}
            disabled={!canCommit || decisionRecordLocked}
            onClick={() => {
              if (fullNoBid) setFullNoBidDialogOpen(true);
              else {
                setBidCommitReviewPage(0);
                setBidCommitReviewOpen(true);
              }
            }}
            sx={{ fontWeight: 800 }}
          >
            {participationMutation.isPending ? 'Saving…' : fullNoBid ? 'Commit full no-bid' : 'Commit participation'}
          </Button>
          <Button
            variant="contained"
            color="success"
            startIcon={promotionMutation.isPending ? <CircularProgress size={16} color="inherit" /> : <PromoteIcon />}
            disabled={!canPromote || blockers.length > 0 || promotionMutation.isPending || Boolean(workbench.promotion)}
            onClick={() => promotionMutation.mutate()}
            sx={{ fontWeight: 900 }}
          >
            {promotionMutation.isPending ? 'Promoting…' : `Promote ${counts.bid} line${counts.bid === 1 ? '' : 's'} to RFQ`}
          </Button>
        </Stack>
      </Paper>
      <FullNoBidCommitDialog
        open={fullNoBidDialogOpen}
        lineCount={counts.total}
        reasonCodes={workbench.reasonCodes}
        lines={workbench.lines}
        decisions={decisions}
        onCancel={() => setFullNoBidDialogOpen(false)}
        onConfirm={(reasonCode, notes) => {
          setFullNoBidDialogOpen(false);
          participationMutation.mutate({ commit: true, reasonCode, notes });
        }}
      />
      <Dialog open={bidCommitReviewOpen} onClose={() => setBidCommitReviewOpen(false)} maxWidth="sm" fullWidth>
        <DialogTitle>Commit participation scope</DialogTitle>
        <DialogContent>
          <Stack spacing={1.5} sx={{ pt: 1 }}>
            <Alert severity={counts.noBid > 0 ? 'warning' : 'info'}>
              You are committing {counts.bid} line{counts.bid === 1 ? '' : 's'} to Bid and excluding {counts.noBid} as No-bid.
              Only the Bid lines can be promoted to the formal RFQ.
            </Alert>
            <Typography variant="body2">
              Confirm that product choices, quantities, units, currencies, warning acknowledgements, and exclusion reasons match the customer's source evidence.
            </Typography>
            <Stack spacing={1} sx={{ maxHeight: 420, overflowY: 'auto' }} aria-label="Exact participation scope by line">
              {workbench.lines.slice(bidCommitReviewPage * 25, (bidCommitReviewPage + 1) * 25).map((line) => {
                const decision = decisions[line.revisionLineId] ?? { decision: 'Pending' as const };
                const reason = workbench.reasonCodes.find((item) => item.code === decision.reasonCode);
                const chosenProduct = line.catalogMatches?.find((match) => match.productId === decision.productId);
                const warningSnapshot = line.participation?.warningSnapshotJson || line.warningSnapshotJson;
                return (
                  <Paper key={line.revisionLineId} variant="outlined" sx={{ p: 1.25 }}>
                    <Stack direction="row" spacing={1} sx={{ alignItems: 'center', justifyContent: 'space-between' }}>
                      <Typography variant="subtitle2" sx={{ fontWeight: 900 }}>Line {line.lineItemNo || line.id}</Typography>
                      <Chip size="small" label={decision.decision === 'NoBid' ? 'No-bid' : decision.decision}
                        color={decision.decision === 'Bid' ? 'success' : decision.decision === 'NoBid' ? 'warning' : 'default'} />
                    </Stack>
                    <Typography variant="body2" sx={{ mt: 0.5 }}>
                      {decision.decision === 'Bid'
                        ? `${decision.productId ? chosenProduct?.productName || chosenProduct?.materialCode || `Selected product #${decision.productId} is not in the current candidate list` : 'No catalog product selected'} · ${decision.quantity ?? 'Missing quantity'} ${decision.unitOfMeasure || 'Missing UOM'} · ${decision.currency || 'Missing currency'}`
                        : `${reason?.label || decision.reasonCode || 'Reason missing'}${decision.note ? ` · ${decision.note}` : ''}`}
                    </Typography>
                    {decision.decision === 'Bid' && line.needsAttention ? (
                      <Alert severity="warning" sx={{ mt: 0.75 }}>
                        {line.attentionReason || 'Catalog warning'} · Acknowledgement: {decision.note || 'Missing'}
                      </Alert>
                    ) : null}
                    {decision.decision === 'Bid' ? (
                      <Typography component="pre" variant="caption" color="text.secondary" sx={{ mt: 0.75, mb: 0, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>
                        Policy {line.participation?.catalogPolicyVersion || line.catalogPolicyVersion || 'not supplied'} · warning snapshot {warningSnapshot || 'not supplied'}
                      </Typography>
                    ) : null}
                  </Paper>
                );
              })}
            </Stack>
            <TablePagination
              component="div"
              count={workbench.lines.length}
              page={Math.min(bidCommitReviewPage, Math.max(0, Math.ceil(workbench.lines.length / 25) - 1))}
              onPageChange={(_, page) => setBidCommitReviewPage(page)}
              rowsPerPage={25}
              rowsPerPageOptions={[25]}
              labelRowsPerPage="Lines per page"
            />
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setBidCommitReviewOpen(false)}>Back to review</Button>
          <Button variant="contained" onClick={() => {
            setBidCommitReviewOpen(false);
            participationMutation.mutate({ commit: true });
          }}>Commit exact scope</Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default LeadDecisionWorkbenchPage;
