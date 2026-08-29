import React from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Box,
  Breadcrumbs,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Link,
  Paper,
  Stack,
  TablePagination,
  Typography,
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  NavigateNext as NextIcon,
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
import RfqRevisionImpactResolutionDialog from './RfqRevisionImpactResolutionDialog';
import {
  WorkbenchStagePanel,
  WorkbenchStageTabs,
  workbenchStageFromValue,
  workbenchStageSearchParams,
  type WorkbenchStage,
  type WorkbenchStageStatuses,
} from './WorkbenchStageNavigation';
import WorkbenchStageActions from './WorkbenchStageActions';
import { retryOperation, type RetryOperation } from './retryIdempotency';
import FeatureHelp from '../../../components/common/FeatureHelp';
import {
  blockerAction,
  countDecisions,
  decisionRecordIsLocked,
  deduplicateDisplayedPromotionBlockers,
  decisionsEqual,
  initializeDecisionMap,
  bidCommercialValuesReady,
  promotionBlockers,
  promotionPanelMode,
  terminalDecisionClosedValidation,
  validGovernedDecision,
  type DecisionMap,
} from './workbenchRules';
import { commercialActionPermissions } from '../../../utils/commercialActionPermissions';
import { useUnsavedWorkGuard } from '../../../hooks/useUnsavedWorkGuard';
import { catalogPolicyLabel, catalogWarningSummary } from './catalogWarningPresentation';
import ParticipationHandoffGuidance from './ParticipationHandoffGuidance';

const CountChip = ({ label, count, color = 'default' }: { label: string; count: number; color?: 'default' | 'success' | 'warning' | 'info' }) => (
  <Chip size="small" label={`${label} ${count}`} color={color} variant={count > 0 ? 'filled' : 'outlined'} sx={{ fontWeight: 800 }} />
);

const LeadDecisionWorkbenchPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const leadId = Number(id);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { hasPermission, userData } = useAuth();
  const commercialAccess = commercialActionPermissions(hasPermission);
  // This flag is returned by the server from the stored RoleRank. Role names and broad module
  // grants are deliberately not authority to commit a participation decision.
  const isManager = userData.isManager === true;
  const canEdit = commercialAccess.canEditLeadDecision;
  const canCommitParticipation = canEdit && isManager;
  const canPromote = commercialAccess.canPromoteLeadToRfq && isManager;
  const canResolveRfqRevisionImpact = commercialAccess.canResolveRfqRevisionImpact && isManager;
  const [searchParams, setSearchParams] = useSearchParams();
  const stage = workbenchStageFromValue(searchParams.get('stage'));
  const changeStage = React.useCallback((nextStage: WorkbenchStage) => {
    setSearchParams((current) => workbenchStageSearchParams(current, nextStage));
  }, [setSearchParams]);
  const [decisions, setDecisions] = React.useState<DecisionMap>({});
  const [baselineDecisions, setBaselineDecisions] = React.useState<DecisionMap>({});
  const [fitAssessment, setFitAssessment] = React.useState<FitAssessmentDTO | null>(null);
  const [fullNoBidDialogOpen, setFullNoBidDialogOpen] = React.useState(false);
  const [bidCommitReviewOpen, setBidCommitReviewOpen] = React.useState(false);
  const [bidCommitReviewPage, setBidCommitReviewPage] = React.useState(0);
  const [rfqImpactReviewOpen, setRfqImpactReviewOpen] = React.useState(false);
  const fitRetryOperation = React.useRef<RetryOperation | null>(null);
  const participationRetryOperation = React.useRef<RetryOperation | null>(null);
  const promotionKey = React.useRef<string | null>(null);
  const promotionRevision = React.useRef<number | null>(null);
  const rfqImpactResolutionKey = React.useRef<string | null>(null);
  const rfqImpactResolutionRevision = React.useRef<number | null>(null);
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
    if (rfqImpactResolutionRevision.current !== workbenchQuery.data.leadRevisionId) {
      rfqImpactResolutionKey.current = `rfq-impact-review:${leadId}:${workbenchQuery.data.leadRevisionId}:${crypto.randomUUID()}`;
      rfqImpactResolutionRevision.current = workbenchQuery.data.leadRevisionId;
    }
  }, [leadId, workbenchQuery.data]);

  const decisionGuard = useUnsavedWorkGuard({
    storageKey: workbenchQuery.data
      ? `nexora.lead-participation.${leadId}.revision.${workbenchQuery.data.leadRevisionId}`
      : '',
    value: decisions,
    // decisionSeed is set only after the authoritative revision has populated the form. This
    // prevents the empty pre-load map becoming the baseline and immediately reporting a false
    // unsaved change.
    enabled: Boolean(workbenchQuery.data && decisionSeed.current),
  });

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
      decisionGuard.markSaved(decisions);
      enqueueSnackbar(
        command.commit
          ? result.participationStatus === 'COMMITTED' ? 'Participation decision committed.' : 'Participation decision saved.'
          : 'Participation draft saved.',
        { variant: 'success' },
      );
      await refresh();
      if (command.commit) changeStage('promote');
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
      if (commercialAccess.canViewPromotedRfq) {
        navigate(`/procurement/rfqs/view/${receipt.rfqId}`);
      }
    },
    onError: (error: unknown) => enqueueSnackbar(
      presentableErrorMessage(error, 'The approved lines could not be promoted. No second RFQ was created.'),
      { variant: 'error' },
    ),
  });

  const rfqImpactResolutionMutation = useMutation({
    mutationFn: (reconciliationReason: string) => {
      const workbench = workbenchQuery.data!;
      if (!workbench.promotion || !rfqImpactResolutionKey.current) {
        throw new Error('The promoted RFQ amendment review is no longer available.');
      }
      return leadDecisionService.resolveRfqRevisionImpact(leadId, {
        rfqId: workbench.promotion.rfqId,
        expectedLeadRevisionId: workbench.leadRevisionId,
        reconciliationReason,
        confirmedHistoricalRfqUnchanged: true,
      }, rfqImpactResolutionKey.current);
    },
    onSuccess: async (result) => {
      setRfqImpactReviewOpen(false);
      enqueueSnackbar(
        result.resolvedImpactCount > 0
          ? 'RFQ amendment review recorded. Historical RFQ lineage was preserved.'
          : 'This RFQ amendment review was already recorded.',
        { variant: 'success' },
      );
      await refresh();
    },
    onError: (error: unknown) => enqueueSnackbar(
      presentableErrorMessage(error, 'The RFQ amendment review could not be recorded. Nothing was changed.'),
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
  const rfqRevisionBlocker = workbench.blockers.find((blocker) => blocker.code === 'RFQ_REVISION_REQUIRED');
  const counts = countDecisions(decisions);
  const dirty = !decisionsEqual(decisions, baselineDecisions);
  const governed = Object.values(decisions).every(validGovernedDecision);
  const allDecided = counts.total > 0 && counts.pending === 0;
  const fullNoBid = counts.total > 0 && counts.noBid === counts.total;
  const fullNoBidClosed = fullNoBid && workbench.participationStatus === 'COMMITTED';
  const decisionMutationPending = fitMutation.isPending || participationMutation.isPending || promotionMutation.isPending;
  const decisionRecordLocked = decisionRecordIsLocked(workbench, decisions);
  const bidValuesReady = bidCommercialValuesReady(
    decisions,
    workbench.unitOptions ?? [],
    workbench.currencyOptions ?? [],
    Object.fromEntries(workbench.lines.map((line) => [line.revisionLineId, line.needsAttention])),
  );
  const fitActionable = Boolean(fitAssessment && fitAssessment.version > 0
    && fitAssessment.overallDecision !== 'NOT_FIT'
    && fitAssessment.criteria.every((criterion) => criterion.decision === 'PASS' || criterion.decision === 'NOT_APPLICABLE'));
  const sourceAndLifecycleReady = Boolean(workbench.customerId)
    && workbench.verificationStatus === 'VERIFIED'
    && !workbench.blockers.some((blocker) => ['SOURCE_UNAVAILABLE', 'SOURCE_LINEAGE_INCOMPLETE', 'LEAD_NOT_ELIGIBLE'].includes(blocker.code));
  // Clarify is a persisted draft decision, not a final participation outcome. It stays editable
  // and saveable, but cannot be labelled COMMITTED or become an RFQ promotion input.
  const canCommit = canCommitParticipation && allDecided && counts.clarify === 0 && governed
    && (fullNoBid ? (fitAssessment?.version ?? 0) > 0 : bidValuesReady && fitActionable && sourceAndLifecycleReady)
    && !decisionMutationPending
    && !fullNoBidClosed
    && (dirty || workbench.participationStatus !== 'COMMITTED');
  const fullNoBidParticipationReady = canEdit
    && fullNoBid
    && allDecided
    && counts.clarify === 0
    && governed
    && (fitAssessment?.version ?? 0) > 0;
  const blockers = promotionBlockers({
    workbench,
    decisions,
    fitAssessment,
    dirty,
    participationStatus: workbench.participationStatus,
    participationVersion: workbench.participationVersion,
  });
  const displayedBlockers = deduplicateDisplayedPromotionBlockers(blockers);
  const promotionMode = promotionPanelMode({
    hasPromotion: Boolean(workbench.promotion),
    fullNoBidClosed,
    canPromote,
    blockerCount: displayedBlockers.length,
  });
  const promotionPermissionBlocker = !canPromote && counts.bid > 0
    ? !canEdit
      ? 'Lead edit permission is required to change or promote this decision record.'
      : !isManager
        ? 'A Manager, Admin, or Owner must commit and promote this commercial decision.'
        : 'RFQ creation permission is required. Hand this committed decision to an authorized RFQ owner.'
    : null;
  const primaryBlocker = promotionPermissionBlocker ?? displayedBlockers[0] ?? null;
  const actionableBlockers = workbench.blockers
    .map((blocker) => ({ code: blocker.code, action: blockerAction(blocker, leadId) }))
    .filter((item): item is { code: string; action: { label: string; path: string } } => Boolean(item.action));
  const sourceEvidenceAvailable = workbench.evidence.some((evidence) => evidence.sourceAvailable);
  const sourceCoverageComplete = !workbench.sourceCoverage
    || workbench.sourceCoverage.totalLines === 0
    || workbench.sourceCoverage.coveredLines >= workbench.sourceCoverage.totalLines;
  const sourceEvidenceBlocked = !sourceEvidenceAvailable
    || workbench.blockers.some((blocker) => ['SOURCE_UNAVAILABLE', 'SOURCE_LINEAGE_INCOMPLETE'].includes(blocker.code));
  const validationClosedByTerminalDecision = terminalDecisionClosedValidation(workbench, fullNoBidClosed);
  const stageStatuses: WorkbenchStageStatuses = {
    evidence: sourceEvidenceBlocked
      ? { progress: 'blocked', detail: 'Source evidence is unavailable or incomplete. Recover the source before making a commercial decision.' }
      : sourceCoverageComplete
        ? { progress: 'complete', detail: 'Source email and line evidence are available for review.' }
        : { progress: 'needs-action', detail: 'Evidence is available, but one or more Lead lines still need a source link.' },
    validate: validationClosedByTerminalDecision
      ? { progress: 'complete', detail: workbench.promotion
        ? 'Validation was completed for the immutable Lead revision promoted to this RFQ.'
        : 'The committed full no-bid closes this Lead without requiring RFQ promotion.' }
      : !sourceEvidenceAvailable
      ? { progress: 'blocked', detail: 'Source evidence must be available before the transformed Lead can be validated.' }
      : sourceAndLifecycleReady
        ? { progress: 'complete', detail: 'Customer identity, Lead lifecycle, and transformed values are verified.' }
        : { progress: 'needs-action', detail: 'Resolve the customer, lifecycle, or transformation review before participation.' },
    participation: workbench.participationStatus === 'COMMITTED'
      ? { progress: 'complete', detail: fullNoBidClosed ? 'The full no-bid decision is committed. No RFQ will be created.' : 'Participation is committed against this immutable Lead revision.' }
      : !canEdit || (!sourceAndLifecycleReady && !fullNoBidParticipationReady)
        ? { progress: 'blocked', detail: !canEdit ? 'Your role can review this stage but cannot change the participation decision.' : 'Complete source validation before committing participation.' }
        : { progress: 'needs-action', detail: fullNoBidParticipationReady
          ? 'The full no-bid decision is ready to commit. Source validation is not required because no RFQ will be created.'
          : !fitActionable ? 'Save a complete human fit assessment for this Lead revision.'
            : counts.pending > 0 ? 'Choose Bid, No-bid, or clarification for every current revision line.'
              : counts.clarify > 0 ? 'Resolve every clarification before committing participation.'
                : counts.bid > 0 && !bidValuesReady ? 'Complete quantity, UOM, currency, and any warning acknowledgement for every Bid line.'
                  : dirty ? 'Review and save or commit the participation changes.' : 'Commit the saved participation scope.' },
    promote: rfqRevisionBlocker
      ? { progress: 'blocked', detail: rfqRevisionBlocker.message }
      : workbench.promotion || fullNoBidClosed
      ? { progress: 'complete', detail: workbench.promotion ? 'This Lead revision already has a durable RFQ promotion receipt.' : 'Full no-bid is complete; promotion is intentionally not available.' }
      : primaryBlocker
        ? { progress: 'blocked', detail: primaryBlocker }
        : { progress: 'needs-action', detail: `${counts.bid} approved line${counts.bid === 1 ? '' : 's'} ready for governed RFQ promotion.` },
  };

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
              <Typography component="h1" variant="h5" sx={{ fontWeight: 950, overflowWrap: 'anywhere' }}>{workbench.customerRfqReference || `Lead #${leadId}`}</Typography>
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

      {decisionGuard.recoveredDraft ? (
        <Alert
          severity="warning"
          sx={{ mb: 1.5 }}
          action={(
            <Stack direction="row" spacing={1}>
              <Button
                color="inherit"
                onClick={() => {
                  setDecisions(decisionGuard.recoveredDraft!.value);
                  decisionGuard.acceptRecovered();
                }}
              >
                Restore
              </Button>
              <Button color="inherit" onClick={decisionGuard.discardRecovered}>Discard</Button>
            </Stack>
          )}
        >
          <AlertTitle>Unsaved participation work recovered</AlertTitle>
          Restore the decisions saved in this browser for Lead revision {workbench.leadRevisionNumber},
          or discard them and keep the server version.
        </Alert>
      ) : null}

      {workbench.promotion ? (
        <Alert
          severity="success"
          sx={{ mb: 1.5 }}
          action={commercialAccess.canViewPromotedRfq
            ? <Button color="inherit" onClick={() => navigate(`/procurement/rfqs/view/${workbench.promotion!.rfqId}`)}>Open RFQ</Button>
            : undefined}
        >
          <AlertTitle>Already promoted</AlertTitle>
          Revision {workbench.promotion.leadRevisionNumber} promoted {workbench.promotion.promotedLineCount} approved line{workbench.promotion.promotedLineCount === 1 ? '' : 's'} to {workbench.promotion.rfqNumber || `RFQ #${workbench.promotion.rfqId}`}.
        </Alert>
      ) : null}

      {rfqRevisionBlocker && workbench.promotion ? (
        <Alert
          severity="error"
          sx={{ mb: 1.5 }}
          action={(
            <Stack direction="row" spacing={0.75} sx={{ alignItems: 'center' }}>
              {commercialAccess.canViewPromotedRfq ? (
                <Button color="inherit" onClick={() => navigate(`/procurement/rfqs/view/${workbench.promotion!.rfqId}`)}>
                  Open RFQ
                </Button>
              ) : null}
              {canResolveRfqRevisionImpact ? (
                <Button color="inherit" variant="outlined" onClick={() => setRfqImpactReviewOpen(true)}>
                  Complete review
                </Button>
              ) : null}
            </Stack>
          )}
        >
          <AlertTitle>Customer amendment requires RFQ review</AlertTitle>
          {rfqRevisionBlocker.message}
          {!canResolveRfqRevisionImpact ? (
            <Typography variant="body2" sx={{ mt: 0.5 }}>
              A Manager, Admin, or Owner with Lead edit and RFQ edit permission must record the reconciliation outcome.
            </Typography>
          ) : null}
        </Alert>
      ) : null}

      {!canEdit ? <Alert severity="info" sx={{ mb: 1.5 }}>This decision record is read-only for your role.</Alert> : null}
      <ParticipationHandoffGuidance
        canEdit={canEdit}
        isManager={isManager}
        participationStatus={workbench.participationStatus}
      />

      <WorkbenchStageTabs value={stage} onChange={changeStage} statuses={stageStatuses} />

      <WorkbenchStagePanel stage="evidence" activeStage={stage}>
        <SourceEvidencePanel workbench={workbench} />
      </WorkbenchStagePanel>

      <WorkbenchStagePanel stage="validate" activeStage={stage}>
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', xl: 'minmax(300px, 0.36fr) minmax(0, 1fr)' }, gap: 1.5 }}>
          <Box sx={{ display: { xs: 'none', xl: 'block' } }}><SourceEvidencePanel workbench={workbench} compact /></Box>
          <Box sx={{ minWidth: 0 }}>
            <Stack direction="row" spacing={1} sx={{ justifyContent: 'space-between', alignItems: 'end', mb: 1, flexWrap: 'wrap' }}>
              <Box>
                <Stack direction="row" spacing={0.25} sx={{ alignItems: 'center' }}>
                  <Typography component="h2" variant="h6" sx={{ fontWeight: 900 }}>Review transformed Lead lines</Typography>
                  <FeatureHelp
                    label="Lead transformation review"
                    description="Compare what Nexora extracted with the original email or attachment. Correcting canonical Lead data creates a new immutable revision; participation choices do not rewrite the source."
                  />
                </Stack>
                <Typography variant="caption" color="text.secondary">Exact source values remain beside canonical values and RFQ participation inputs.</Typography>
              </Box>
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
              mode="validation" readOnly onDecisionsChange={setDecisions} />
          </Box>
        </Box>
      </WorkbenchStagePanel>

      <WorkbenchStagePanel stage="participation" activeStage={stage}>
        <Stack spacing={2}>
          <FitAssessmentPanel
            assessment={fitAssessment}
            leadRevisionId={workbench.leadRevisionId}
            decisionVersion={workbench.decisionVersion}
            saving={fitMutation.isPending}
            readOnly={!canEdit || decisionMutationPending || decisionRecordLocked}
            onSave={(request) => fitMutation.mutate(request)}
          />
          <Paper component="section" aria-labelledby="participation-lines-heading" variant="outlined" sx={{ p: 2, borderRadius: 2 }}>
            <Stack direction="row" spacing={0.25} sx={{ alignItems: 'center' }}>
              <Typography id="participation-lines-heading" component="h2" variant="h6" sx={{ fontWeight: 900 }}>Participation by line</Typography>
              <FeatureHelp
                label="participation by line"
                description="Decide Bid, No-bid, or Clarify for each requested line. Only committed Bid lines can enter the formal RFQ; excluded lines remain recorded against this Lead revision."
              />
            </Stack>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>
              Every line starts undecided. No-bid and clarification require a governed reason. These decisions belong to the Lead revision, before any RFQ exists.
            </Typography>
            <LeadValidationGrid
              lines={workbench.lines}
              decisions={decisions}
              reasonCodes={workbench.reasonCodes}
              unitOptions={workbench.unitOptions ?? []}
              currencyOptions={workbench.currencyOptions ?? []}
              mode="participation"
              readOnly={!canEdit || decisionMutationPending || decisionRecordLocked}
              onDecisionsChange={setDecisions}
            />
          </Paper>
        </Stack>
      </WorkbenchStagePanel>

      <WorkbenchStagePanel stage="promote" activeStage={stage}>
        <Paper variant="outlined" component="section" aria-labelledby="promotion-heading" sx={{ p: { xs: 2, md: 3 }, borderRadius: 2 }}>
          <Stack direction="row" spacing={0.25} sx={{ alignItems: 'center' }}>
            <Typography id="promotion-heading" component="h2" variant="h6" sx={{ fontWeight: 900 }}>RFQ promotion</Typography>
            <FeatureHelp
              label="RFQ promotion"
              description="The only governed action that creates a formal RFQ from a Lead. It copies only committed Bid lines and is idempotent, so retrying cannot create a duplicate RFQ."
            />
          </Stack>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
            Promotion creates one formal RFQ from only the lines committed as Bid. It cannot qualify the Lead or invent a participation decision.
          </Typography>
          <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', mb: 2 }}>
            <CountChip label="Approved for RFQ" count={counts.bid} color="success" />
            <CountChip label="Excluded as no-bid" count={counts.noBid} />
            <CountChip label="Awaiting clarification" count={counts.clarify} color="info" />
          </Stack>
          {promotionMode === 'promoted' && workbench.promotion ? (
            <Alert
              severity="success"
              action={commercialAccess.canViewPromotedRfq
                ? <Button color="inherit" onClick={() => navigate(`/procurement/rfqs/view/${workbench.promotion!.rfqId}`)}>Open RFQ</Button>
                : undefined}
            >
              <AlertTitle>Promotion completed</AlertTitle>
              This immutable Lead revision has a durable receipt for {workbench.promotion.promotedLineCount} approved line{workbench.promotion.promotedLineCount === 1 ? '' : 's'} promoted to {workbench.promotion.rfqNumber || `RFQ #${workbench.promotion.rfqId}`}.
            </Alert>
          ) : promotionMode === 'full-no-bid' ? (
            <Alert severity="info"><AlertTitle>Full no-bid committed</AlertTitle>No RFQ will be created for this Lead revision.</Alert>
          ) : promotionMode === 'authority-required' ? (
            <Alert severity="warning">
              <AlertTitle>Authorized RFQ owner required</AlertTitle>
              This decision is ready, but your role cannot create RFQs. An authorized RFQ owner must perform the promotion.
            </Alert>
          ) : promotionMode === 'blocked' ? (
            <Alert severity="warning">
              <AlertTitle>Promotion is not ready</AlertTitle>
              <Stack component="ul" spacing={0.5} sx={{ my: 0, pl: 2.5 }}>
                {displayedBlockers.map((blocker) => <Typography component="li" variant="body2" key={blocker}>{blocker}</Typography>)}
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
      </WorkbenchStagePanel>

      <WorkbenchStageActions
        stage={stage}
        status={stageStatuses[stage]}
        onStageChange={changeStage}
        canContinueEvidence={!sourceEvidenceBlocked}
        canContinueValidation={sourceAndLifecycleReady}
        canEdit={canEdit}
        dirty={dirty}
        hasSavedFitAssessment={(fitAssessment?.version ?? 0) > 0}
        decisionPending={decisionMutationPending}
        decisionRecordLocked={decisionRecordLocked}
        canCommit={canCommit}
        participationCommitted={workbench.participationStatus === 'COMMITTED'}
        participationStatus={workbench.participationStatus}
        fullNoBid={fullNoBid}
        fullNoBidClosed={fullNoBidClosed}
        draftForManagerReview={canEdit && !isManager}
        onSaveDraft={() => participationMutation.mutate({ commit: false })}
        onCommit={() => {
          if (fullNoBid) setFullNoBidDialogOpen(true);
          else {
            setBidCommitReviewPage(0);
            setBidCommitReviewOpen(true);
          }
        }}
        canPromote={canPromote}
        promotionBlocked={blockers.length > 0}
        promotionPending={promotionMutation.isPending}
        alreadyPromoted={Boolean(workbench.promotion)}
        approvedLineCount={counts.bid}
        onPromote={() => promotionMutation.mutate()}
      />
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
      <RfqRevisionImpactResolutionDialog
        key={workbench.leadRevisionId}
        open={rfqImpactReviewOpen}
        rfqLabel={workbench.promotion?.rfqNumber || `RFQ #${workbench.promotion?.rfqId ?? ''}`}
        leadRevisionNumber={workbench.leadRevisionNumber}
        saving={rfqImpactResolutionMutation.isPending}
        onCancel={() => setRfqImpactReviewOpen(false)}
        onConfirm={(reason) => rfqImpactResolutionMutation.mutate(reason)}
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
                const warningSummary = catalogWarningSummary(warningSnapshot, line.attentionReason);
                const policyLabel = catalogPolicyLabel(line.participation?.catalogPolicyVersion || line.catalogPolicyVersion);
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
                      <Stack spacing={0.25} sx={{ mt: 0.75 }}>
                        <Typography variant="caption" color="text.secondary">
                          Catalog review: {warningSummary}
                        </Typography>
                        <Typography variant="caption" color="text.secondary">
                          {policyLabel}
                        </Typography>
                      </Stack>
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
